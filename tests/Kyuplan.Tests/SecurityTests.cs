using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

        TrustedProxies.Apply(options, null); // default: loopback and the private networks
        Assert.Empty(options.KnownProxies);
        Assert.Contains(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("10.1.2.3")));
        Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Parse("203.0.113.5")));

        Assert.Throws<InvalidOperationException>(() => TrustedProxies.Apply(options, ["not an address"]));
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
}
