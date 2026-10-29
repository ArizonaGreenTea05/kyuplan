using Microsoft.AspNetCore.TestHost;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>Protection against password guessing, plain-text logins and what a browser does with the pages; plus the rules for the allowed hosts.</summary>
public sealed class SecurityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
            configure?.Invoke(builder);
        });

    private static HttpRequestMessage Login(string userName, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader(userName, password));
        return request;
    }

    // --- Password guessing -------------------------------------------------------------------------------------

    [Fact]
    public async Task Too_many_failed_logins_lock_the_address_out_even_for_the_right_password()
    {
        using var host = CreateHost(b => b.UseSetting("Api:MaxLoginFailuresPerAddress", "3"));
        var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(AdminName, "wrong password " + i))).StatusCode);

        var locked = await client.SendAsync(Login(AdminName, AdminPassword));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.Empty(locked.Headers.WwwAuthenticate); // no login dialog: asking again would only extend the wait
    }

    [Fact]
    public async Task Failed_logins_as_one_user_lock_that_name_whatever_the_address()
    {
        using var host = CreateHost(b => b.UseSetting("Api:MaxLoginFailuresPerUser", "2"));
        var client = host.CreateClient();

        for (var i = 0; i < 2; i++) await client.SendAsync(Login(AdminName, "wrong password " + i));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login(AdminName, AdminPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login("somebody else", "wrong password"))).StatusCode);
    }

    [Fact]
    public async Task A_few_failures_do_not_stop_the_right_password()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        await client.SendAsync(Login(AdminName, "wrong password"));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Login(AdminName, AdminPassword))).StatusCode);
    }

    // --- A login that cannot be checked is not a failed login --------------------------------------------------

    private sealed class OutageException() : DbException("database is gone");

    /// <summary>Makes every command of the accounts database fail while <see cref="Down"/> is set.</summary>
    private sealed class OutageInterceptor : DbCommandInterceptor
    {
        public volatile bool Down;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            Down ? throw new OutageException() : result;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            Down ? throw new OutageException() : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task A_database_outage_answers_503_and_does_not_count_as_failed_logins()
    {
        var outage = new OutageInterceptor();
        using var host = CreateHost(b =>
        {
            b.UseSetting("Api:MaxLoginFailuresPerAddress", "2");
            b.UseSetting("Api:MaxLoginFailuresPerUser", "2");
            b.ConfigureTestServices(s => s.ConfigureDbContext<AccountsDbContext>(o => o.AddInterceptors(outage)));
        });
        var client = host.CreateClient(); // starts the host: the first administrator is made before the outage
        outage.Down = true;

        for (var i = 0; i < 5; i++)
        {
            var response = await client.SendAsync(Login(AdminName, AdminPassword));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(response.Headers.WwwAuthenticate); // no login dialog: the password is not the problem
            Assert.DoesNotContain("database is gone", await response.Content.ReadAsStringAsync());
        }

        outage.Down = false;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Login(AdminName, AdminPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login(AdminName, "wrong password"))).StatusCode); // the first real failure
    }

    private sealed class CountingHasher : IPasswordHasher<UserAccount>
    {
        private readonly PasswordHasher<UserAccount> _inner = new();

        public int Verified { get; private set; }

        public string HashPassword(UserAccount user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        {
            Verified++;
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private async Task<(AccountService Service, CountingHasher Hasher, IServiceScope Scope)> CreateServiceAsync(WebApplicationFactory<Program> host)
    {
        await host.CreateClient().GetAsync("/healthz"); // starts the host and makes the administrator
        var scope = host.Services.CreateScope();
        var hasher = new CountingHasher();
        var service = new AccountService(scope.ServiceProvider.GetRequiredService<AccountsDbContext>(), hasher, TimeProvider.System,
            Options.Create(new AccountsOptions()), NullLogger<AccountService>.Instance);
        return (service, hasher, scope);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_a_failed_login()
    {
        using var host = CreateHost();
        var (service, _, scope) = await CreateServiceAsync(host);
        using var _ = scope;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AuthenticateAsync(AdminName, AdminPassword, cancelled.Token));
    }

    [Fact]
    public async Task An_unknown_user_is_still_checked_against_a_hash_and_is_not_found()
    {
        using var host = CreateHost();
        var (service, hasher, scope) = await CreateServiceAsync(host);
        using var _ = scope;

        Assert.Null(await service.AuthenticateAsync("nobody", AdminPassword, CancellationToken.None));
        Assert.Equal(1, hasher.Verified);

        Assert.Null(await service.AuthenticateAsync(AdminName, "wrong password", CancellationToken.None));
        Assert.Equal(2, hasher.Verified);

        Assert.Equal(AdminName, (await service.AuthenticateAsync(AdminName, AdminPassword, CancellationToken.None))?.UserName);
        Assert.Equal(3, hasher.Verified);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void The_lockout_ends_with_its_window()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(Options.Create(new ApiSecurityOptions { MaxLoginFailuresPerAddress = 2, LoginFailureWindow = TimeSpan.FromMinutes(10) }), time);

        throttle.Failed("10.0.0.1", "a");
        Assert.Null(throttle.RetryAfter("10.0.0.1", "a"));
        throttle.Failed("10.0.0.1", "b");
        Assert.Equal(TimeSpan.FromMinutes(10), throttle.RetryAfter("10.0.0.1", "c"));
        Assert.Null(throttle.RetryAfter("10.0.0.2", "c")); // another address is not affected

        time.Now += TimeSpan.FromMinutes(10);
        Assert.Null(throttle.RetryAfter("10.0.0.1", "c"));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    public void Passwords_need_twelve_characters(int length, bool valid) =>
        Assert.Equal(valid, AccountRules.CheckPassword(new string('x', length)) is null);

    // --- Transport and headers ---------------------------------------------------------------------------------

    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                return nextMiddleware();
            });
            next(app);
        };
    }

    [Fact]
    public async Task A_login_from_another_machine_over_plain_http_is_refused_unless_allowed()
    {
        using var refused = CreateHost(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, RemoteAddressFilter>()));
        var response = await refused.CreateClient().SendAsync(Login(AdminName, AdminPassword));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("HTTPS", await response.Content.ReadAsStringAsync());

        using var allowed = CreateHost(b =>
        {
            b.UseSetting("Api:AllowInsecureHttp", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, RemoteAddressFilter>());
        });
        Assert.Equal(HttpStatusCode.OK, (await allowed.CreateClient().SendAsync(Login(AdminName, AdminPassword))).StatusCode);
    }

    [Fact]
    public async Task A_request_that_came_through_a_proxy_is_not_local()
    {
        using var host = CreateHost();
        var request = Login(AdminName, AdminPassword);
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");

        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public void Only_the_configured_proxies_are_believed()
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        TrustedProxies.Apply(options, ["192.0.2.7", "198.51.100.0/24"]);
        Assert.Single(options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);

        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Apply(options, ["not an address"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_list_only_this_machine_is_believed(bool emptyList)
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        TrustedProxies.Apply(options, emptyList ? [] : null);

        Assert.Empty(options.KnownProxies);
        Assert.All(["127.0.0.1", "127.5.6.7", "::1"], a => Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse(a))));
        Assert.All(["10.1.2.3", "172.17.0.2", "192.168.1.5", "fd00::1", "fe80::1", "203.0.113.5"],
            a => Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse(a))));
    }

    [Fact]
    public void A_list_replaces_the_default_completely()
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        TrustedProxies.Apply(options, [" 172.18.0.0/16 "]);

        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("172.18.0.9")));
        Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("127.0.0.1")));
    }

    [Theory]
    [InlineData("true", null, true)]
    [InlineData("True", "", true)]
    [InlineData("true", "10.0.0.5", false)]
    [InlineData("false", null, false)]
    [InlineData(null, null, false)]
    [InlineData("1", null, false)] // not what the host reads as "on"
    public void Enabling_forwarded_headers_without_a_proxy_list_is_warned_about(string? enabled, string? proxy, bool warn) =>
        Assert.Equal(warn, TrustedProxies.NeedsWarning(enabled, proxy is null ? null : proxy.Length == 0 ? [] : [proxy]));

    [Theory]
    [InlineData(null, "192.168.1.50", "192.168.1.50")] // default: a neighbour in a private network is no proxy
    [InlineData(null, "172.17.0.2", "172.17.0.2")]
    [InlineData(null, "127.0.0.1", "198.51.100.9")] // default: this machine is
    [InlineData("10.0.0.5", "192.168.1.50", "192.168.1.50")] // explicit list: other private addresses are still not believed
    [InlineData("10.0.0.5", "127.0.0.1", "127.0.0.1")] // ...and the list replaces loopback
    [InlineData("10.0.0.5", "10.0.0.5", "198.51.100.9")] // the named proxy is
    public async Task Forwarded_for_is_only_believed_from_a_sender_that_is_trusted(string? proxy, string sender, string expectedAddress)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app =>
        {
            var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
            TrustedProxies.Apply(options, proxy is null ? null : [proxy]);
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(sender);
                return next();
            });
            app.UseForwardedHeaders(options);
            app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress?.ToString() ?? ""));
        })).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "198.51.100.9");

        Assert.Equal(expectedAddress, await (await host.GetTestClient().SendAsync(request)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_forged_forwarded_address_from_a_private_network_does_not_get_round_the_lockout()
    {
        using var host = CreateHost(b =>
        {
            b.UseSetting("FORWARDEDHEADERS_ENABLED", "true");
            b.UseSetting("Api:AllowInsecureHttp", "true");
            b.UseSetting("Api:MaxLoginFailuresPerAddress", "3");
            // First in the list = first in the pipeline: the host's forwarded-headers middleware must already see the private sender.
            b.ConfigureTestServices(s => s.Insert(0, ServiceDescriptor.Singleton<IStartupFilter, PrivateAddressFilter>()));
        });
        var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var guess = Login(AdminName, "wrong password " + i);
            guess.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(guess)).StatusCode);
        }

        var next = Login(AdminName, AdminPassword);
        next.Headers.Add("X-Forwarded-For", "198.51.100.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(next)).StatusCode);
    }

    private sealed class PrivateAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
                return nextMiddleware();
            });
            next(app);
        };
    }

    [Fact]
    public void Failures_as_one_user_lock_the_name_even_for_its_known_addresses_after_the_higher_limit()
    {
        var throttle = new LoginThrottle(Options.Create(new ApiSecurityOptions { MaxLoginFailuresPerUser = 2, MaxLoginFailuresPerUserAnyAddress = 4 }), new ManualTime());
        throttle.Succeeded("10.0.0.1", "anna");

        for (var i = 0; i < 3; i++) throttle.Failed($"198.51.100.{i}", "anna");
        Assert.Null(throttle.RetryAfter("10.0.0.1", "anna")); // the owner's address still gets in
        Assert.NotNull(throttle.RetryAfter("10.0.0.2", "anna"));

        throttle.Failed("198.51.100.9", "anna");
        Assert.NotNull(throttle.RetryAfter("10.0.0.1", "anna"));
        Assert.Null(throttle.RetryAfter("10.0.0.1", "bert")); // other names are not affected
    }

    [Fact]
    public void The_any_address_lock_of_a_name_ends_with_its_window_and_can_be_switched_off()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(Options.Create(new ApiSecurityOptions
        {
            MaxLoginFailuresPerAddress = 0, MaxLoginFailuresPerUser = 0, MaxLoginFailuresPerUserAnyAddress = 2, LoginFailureWindow = TimeSpan.FromMinutes(5),
        }), time);
        throttle.Failed("a", "anna");
        throttle.Failed("b", "anna");
        Assert.Equal(TimeSpan.FromMinutes(5), throttle.RetryAfter("c", "anna"));

        time.Now += TimeSpan.FromMinutes(5);
        Assert.Null(throttle.RetryAfter("c", "anna"));

        var off = new LoginThrottle(Options.Create(new ApiSecurityOptions
        {
            MaxLoginFailuresPerAddress = 0, MaxLoginFailuresPerUser = 0, MaxLoginFailuresPerUserAnyAddress = 0,
        }), time);
        for (var i = 0; i < 500; i++) off.Failed("a" + i, "anna");
        Assert.Null(off.RetryAfter("z", "anna"));
    }

    [Fact]
    public void The_longest_of_the_locks_counts()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(Options.Create(new ApiSecurityOptions
        {
            MaxLoginFailuresPerAddress = 1, MaxLoginFailuresPerUser = 100, MaxLoginFailuresPerUserAnyAddress = 2, LoginFailureWindow = TimeSpan.FromMinutes(10),
        }), time);
        throttle.Failed("a", "anna");
        time.Now += TimeSpan.FromMinutes(4);
        throttle.Failed("b", "anna"); // starts no new window: the user's window began at the first failure

        Assert.Equal(TimeSpan.FromMinutes(6), throttle.RetryAfter("a", "anna")); // address lock 6 min left, user lock 6 min left
        Assert.Equal(TimeSpan.FromMinutes(10), throttle.RetryAfter("b", "anna")); // address lock 10 min left (own window), user lock 6: the longer one
    }

    [Fact]
    public void A_user_name_cannot_be_locked_for_the_address_its_owner_signs_in_from()
    {
        var throttle = new LoginThrottle(Options.Create(new ApiSecurityOptions { MaxLoginFailuresPerUser = 2 }), new ManualTime());

        throttle.Succeeded("10.0.0.1", "anna");
        throttle.Failed("10.0.0.9", "anna");
        throttle.Failed("10.0.0.9", "anna");

        Assert.Null(throttle.RetryAfter("10.0.0.1", "anna"));
        Assert.NotNull(throttle.RetryAfter("10.0.0.2", "anna"));
    }

    [Fact]
    public void A_verified_password_is_remembered_only_until_it_changes_or_expires()
    {
        var time = new ManualTime();
        var verified = new VerifiedLogins(time);

        verified.Remember("hash-1", "secret");
        Assert.True(verified.Contains("hash-1", "secret"));
        Assert.False(verified.Contains("hash-1", "other"));
        Assert.False(verified.Contains("hash-2", "secret")); // the password was changed

        time.Now += TimeSpan.FromMinutes(6);
        Assert.False(verified.Contains("hash-1", "secret"));
    }

    [Fact]
    public async Task Every_response_carries_the_browser_protections()
    {
        using var host = CreateHost();

        var response = await host.CreateClient().GetAsync("/api/branding");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public async Task The_policy_lets_the_page_talk_to_the_allowed_origins_only()
    {
        using var host = CreateHost(b => b.UseSetting("Api:AllowedOrigins:0", "https://club.example.org"));

        var policy = (await host.CreateClient().GetAsync("/api/branding")).Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("connect-src 'self' https://club.example.org", policy);
        Assert.DoesNotContain("evil", policy);
    }

    // --- Refreshing the Spond data -----------------------------------------------------------------------------

    [Fact]
    public async Task Refreshing_needs_a_permission_that_reads_spond_data()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var created = await admin.PostAsJsonAsync("/api/users", Input("nobody", "a good password", isAdmin: false) with { RoleIds = [], Permissions = [] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/refresh");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader("nobody", "a good password"));
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateClient().SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/refresh", null)).StatusCode);
    }

    // --- Allowed hosts -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData("kyuplan.example.com", true)]
    [InlineData("kyuplan.example.com;*.example.org", true)]
    [InlineData("localhost:5100", true)]
    [InlineData("a b", false)]
    [InlineData("exa*mple.com", false)]
    [InlineData("*.*.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("a/b", false)]
    public void Allowed_hosts_are_names_separated_by_semicolons(string hosts, bool valid) =>
        Assert.Equal(valid, SettingsRules.IsAllowedHosts(hosts));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("https://planner.example.org", true)]
    [InlineData("  https://planner.example.org/  ", true)]
    [InlineData("http://192.168.1.5:8080/kyuplan", true)]
    [InlineData("HTTPS://Planner.Example.org", true)]
    [InlineData("planner.example.org", false)]
    [InlineData("//planner.example.org", false)]
    [InlineData("ftp://planner.example.org", false)]
    [InlineData("https://planner.example.org/?a=1", false)]
    [InlineData("https://planner.example.org?", false)]
    [InlineData("https://planner.example.org/#x", false)]
    [InlineData("https://user:pw@planner.example.org", false)]
    [InlineData("https://planner example.org", false)]
    [InlineData("https://planner.exämple.org", false)]
    public void A_public_url_is_empty_or_an_absolute_http_address_without_query(string? url, bool valid) =>
        Assert.Equal(valid, SettingsRules.IsPublicUrl(url));

    [Fact]
    public void A_public_url_is_not_longer_than_the_limit()
    {
        Assert.True(SettingsRules.IsPublicUrl("https://a.example/" + new string('a', SettingsRules.MaxPublicUrlLength - 19)));
        Assert.False(SettingsRules.IsPublicUrl("https://a.example/" + new string('a', SettingsRules.MaxPublicUrlLength)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" https://p.example.org/ ", "https://p.example.org")]
    [InlineData("https://p.example.org/kyuplan//", "https://p.example.org/kyuplan")]
    public void A_public_url_loses_its_trailing_slashes(string? url, string? expected) =>
        Assert.Equal(expected, SettingsRules.NormalizePublicUrl(url));

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdef ", false)]
    [InlineData("tökentökentökentöken", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_metrics_token_is_long_enough_and_a_header_value(string? token, bool valid) =>
        Assert.Equal(valid, SettingsRules.IsMetricsToken(token));

    [Fact]
    public void A_metrics_token_has_a_maximum_length()
    {
        Assert.True(SettingsRules.IsMetricsToken(new string('a', SettingsRules.MaxMetricsTokenLength)));
        Assert.False(SettingsRules.IsMetricsToken(new string('a', SettingsRules.MaxMetricsTokenLength + 1)));
    }

    [Theory]
    [InlineData("API_TOKEN", true)]
    [InlineData("a", true)]
    [InlineData("Token2", true)]
    [InlineData("a_b_c", true)]
    [InlineData("_token", false)] // starts with a letter
    [InlineData("2token", false)]
    [InlineData("to-ken", false)]
    [InlineData("to ken", false)]
    [InlineData("to:ken", false)] // it is a configuration key: no separators
    [InlineData("tökén", false)]
    [InlineData(" API", false)] // callers trim what was typed
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_secret_name_is_a_letter_then_letters_digits_and_underscores(string? name, bool valid) =>
        Assert.Equal(valid, SettingsRules.IsSecretName(name));

    [Fact]
    public void A_secret_name_and_value_have_a_maximum_length()
    {
        Assert.True(SettingsRules.IsSecretName(new string('a', SettingsRules.MaxSecretNameLength)));
        Assert.False(SettingsRules.IsSecretName(new string('a', SettingsRules.MaxSecretNameLength + 1)));
        Assert.True(SettingsRules.IsSecretValue(new string('v', SettingsRules.MaxSecretValueLength)));
        Assert.False(SettingsRules.IsSecretValue(new string('v', SettingsRules.MaxSecretValueLength + 1)));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("with spaces and ünïcödé", true)] // a password may have them
    [InlineData("a\nb", false)]
    [InlineData("a\rb", false)]
    [InlineData("a\tb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_secret_value_is_one_line_of_text(string? value, bool valid) =>
        Assert.Equal(valid, SettingsRules.IsSecretValue(value));

    [Theory]
    [InlineData("var auth = { Authorization: 'Bearer abcdef0123456789' };", true)]
    [InlineData("headers['Authorization'] = \"Bearer eyJhbGciOi.payload.sig\";", true)]
    [InlineData("var h = { 'X-Api-Key': 'abc123456' };", true)]
    [InlineData("var apiKey = \"abc123456\";", true)]
    [InlineData("const password = 'hunter2hunter2';", true)]
    [InlineData("var token = 'abcdefgh';", true)]
    [InlineData("var auth = { Authorization: 'Bearer ' + secrets.get('API_TOKEN') };", false)]
    [InlineData("var h = { 'X-Api-Key': secrets.get('KEY') };", false)]
    [InlineData("// a token is needed here: ask the admin", false)]
    [InlineData("var token = '';", false)]
    [InlineData("var token = 'abc';", false)] // too short to be one
    [InlineData("function getGroups() { return []; }", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_script_that_holds_a_credential_as_text_is_recognised_for_the_warning(string? script, bool expected) =>
        Assert.Equal(expected, SettingsRules.MentionsCredentials(script));

    [Fact]
    public void Looking_for_credentials_in_a_huge_script_neither_hangs_nor_throws()
    {
        var script = string.Concat(Enumerable.Repeat("token = ' ", 10_000)) + new string('a', 100_000);

        Assert.False(SettingsRules.MentionsCredentials(script)); // false whether it finished or gave up
    }

    [Fact]
    public void The_example_script_does_not_carry_a_credential()
    {
        Assert.False(SettingsRules.MentionsCredentials(Kyuplan.Client.ViewModels.CustomScriptTemplate.Text));
        Assert.Contains("secrets.get(", Kyuplan.Client.ViewModels.CustomScriptTemplate.Text);
    }
}
