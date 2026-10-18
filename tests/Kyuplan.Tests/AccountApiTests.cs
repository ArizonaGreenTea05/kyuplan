using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kyuplan.Api.Accounts;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Logins and user management against the real API with real (in-memory) SQLite databases: who gets in, who may change what,
/// and that the passwords are kept apart from the planner data and only as hashes.
/// </summary>
public sealed class AccountApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string GoodPassword = "a good password";

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
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
            configure?.Invoke(builder);
        });

    private static async Task<UserDto> CreateUserAsync(HttpClient admin, string userName, string password = GoodPassword, bool isAdmin = false)
    {
        var response = await admin.PostAsJsonAsync("/api/users", Input(userName, password, isAdmin));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserDto>())!;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string userName, string password) =>
        (await client.SendAsync(WithLogin(HttpMethod.Get, "/api/account", userName, password))).StatusCode;

    private static HttpRequestMessage WithLogin(HttpMethod method, string url, string userName, string password)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader(userName, password));
        return request;
    }

    // --- Who gets in -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_api_endpoint_needs_a_login()
    {
        using var host = CreateHost();
        var anonymous = host.CreateClient();

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api"))
            .SelectMany(e => e.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods
                .Select(method => (Method: method, Pattern: e.RoutePattern.RawText!)))
            // Reading the branding is public on purpose: the clients show the name and logo before anybody has signed in
            // (BrandingApiTests). Changing it is not (BrandingAdminApiTests).
            .Where(e => !(e.Method == "GET" && e.Pattern.TrimEnd('/') is "/api/branding" or "/api/branding/{name}"))
            // The theme catalog is public for the same reason (the default theme is painted on the login screen); installing is not (ThemePackApiTests).
            .Where(e => !(e.Method == "GET" && e.Pattern.TrimEnd('/') == "/api/themes"))
            // So is asking whether the API has to be set up, and the setup itself while there is no user (SetupApiTests).
            .Where(e => e.Pattern.TrimEnd('/') != "/api/setup")
            .Select(e => (e.Method, Path: Regex.Replace(e.Pattern, @"\{[^}]+\}", "1")))
            .Distinct().ToList();

        Assert.True(endpoints.Count >= 20, $"found only {endpoints.Count} endpoints, the route enumeration is broken");
        foreach (var (method, path) in endpoints)
        {
            var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode} without a login");
        }
    }

    [Fact]
    public async Task A_request_without_a_login_is_challenged_with_basic_which_makes_a_browser_ask()
    {
        using var host = CreateHost();

        var response = await host.CreateClient().GetAsync("/api/groups");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        Assert.Contains("realm=\"Kyuplan\"", challenge.Parameter);
    }

    [Theory]
    [InlineData("Basic", "admin:wrong-password", true)]
    [InlineData("Basic", "nobody:test-admin-password", true)]
    [InlineData("Basic", "admin", true)] // no colon
    [InlineData("Basic", "not base64 at all!", false)]
    [InlineData("Bearer", "admin:test-admin-password", true)]
    public async Task A_wrong_or_malformed_login_is_challenged_too(string scheme, string credentials, bool base64)
    {
        using var host = CreateHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            scheme, base64 ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials)) : credentials);

        var response = await client.GetAsync("/api/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task The_user_name_ignores_case_but_the_password_does_not()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "ADMIN", AdminPassword));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "Admin", AdminPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "admin", AdminPassword.ToUpperInvariant()));
    }

    [Fact]
    public async Task A_password_can_contain_colons_spaces_and_umlauts_and_a_user_name_umlauts()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await CreateUserAsync(admin, "Jürgen Köhler", "pä:ss wörd:1");

        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "Jürgen Köhler", "pä:ss wörd:1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "Jürgen Köhler", "pä"));
    }

    [Fact]
    public async Task A_browser_origin_gets_its_login_answered_with_credentials_allowed_and_needs_none_for_the_preflight()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/groups");
        preflight.Headers.Add("Origin", "http://localhost:5200");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        var preflightResponse = await client.SendAsync(preflight);

        // The 401 must be readable by the page, or the browser cannot hand it over to its login dialog and the client sees a network error.
        var unauthenticated = new HttpRequestMessage(HttpMethod.Get, "/api/groups");
        unauthenticated.Headers.Add("Origin", "http://localhost:5200");
        var challenge = await client.SendAsync(unauthenticated);

        Assert.True(preflightResponse.IsSuccessStatusCode, $"preflight answered {(int)preflightResponse.StatusCode}");
        Assert.Equal("true", Assert.Single(preflightResponse.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal("http://localhost:5200", Assert.Single(challenge.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(challenge.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task The_development_docs_need_no_login_and_offer_basic_authentication()
    {
        using var host = CreateHost();

        var response = await host.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("""
            "scheme":\s*"basic"
            """, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_planner_client_signs_in_with_the_login_of_a_session()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await CreateUserAsync(admin, "Jürgen", "pä:ss wörd:1");

        var session = new LoginSession { Login = new Login("Jürgen", "pä:ss wörd:1") };
        var api = new PlannerApiClient(new HttpClient(new BasicAuthHandler(session) { InnerHandler = host.Server.CreateHandler() })
        {
            BaseAddress = host.Server.BaseAddress,
        });

        Assert.Equal("Jürgen", (await api.GetAccountAsync()).UserName);
        Assert.NotEmpty(await api.GetGroupsAsync());

        session.Login = new Login("Jürgen", "wrong");
        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetAccountAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // --- Passwords and databases -------------------------------------------------------------------------------

    [Fact]
    public async Task Passwords_are_stored_as_hashes_and_the_accounts_have_a_database_of_their_own()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");

        using (var scope = host.Services.CreateScope())
        {
            var users = await scope.ServiceProvider.GetRequiredService<AccountsDbContext>().Users.OrderBy(u => u.Id).ToListAsync();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();

            Assert.Equal(["admin", "Anna"], users.Select(u => u.UserName));
            Assert.DoesNotContain(users, u => u.PasswordHash == AdminPassword || u.PasswordHash == GoodPassword);
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(users[1], users[1].PasswordHash, GoodPassword));
            Assert.NotEqual(users[0].PasswordHash, hasher.HashPassword(users[0], AdminPassword)); // salted: the same password never hashes the same
        }

        Assert.Equal(["Roles", "UserAliases", "UserRoles", "Users"], Tables(_accounts).Where(t => !t.StartsWith("__")).Order());
        Assert.DoesNotContain("Users", Tables(_planner));
        Assert.Contains("Exams", Tables(_planner));
        Assert.DoesNotContain("Exams", Tables(_accounts));
    }

    // --- Aliases and display names -----------------------------------------------------------------------------

    private static Task<HttpResponseMessage> SetNamesAsync(HttpClient admin, UserDto user, string? displayName = null, params string[]? aliases) =>
        admin.PutAsJsonAsync($"/api/users/{user.Id}", new UserInputDto(user.UserName, null, displayName, aliases));

    [Fact]
    public async Task A_user_signs_in_with_an_alias_as_the_same_user_until_it_is_taken_away()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");

        var saved = await SetNamesAsync(admin, anna, null, "Annie", "A.K.");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("2|Anna|Anna|A.K.,Annie|User|", (await saved.Content.ReadFromJsonAsync<UserDto>()).Show());
        foreach (var name in new[] { "Anna", "annie", "A.K." }) // by the user name or an alias, whatever the case
            Assert.Equal("2|Anna|Anna|A.K.,Annie|User|", (await host.CreateSignedInClient(name, GoodPassword).GetFromJsonAsync<UserDto>("/api/account")).Show());
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Annie", "not the password"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Nobody", GoodPassword));

        // No aliases given keeps them; an empty list takes them all away, and the logins with them.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{anna.Id}", Input("Anna", null, false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Annie", GoodPassword));
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, null, "Annie")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "A.K.", GoodPassword));
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Annie", GoodPassword));
    }

    [Fact]
    public async Task A_name_signs_in_one_user_only_whether_it_is_a_user_name_or_an_alias()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");
        var ben = await CreateUserAsync(admin, "Ben");
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, null, "Annie")).StatusCode);

        // An alias that is somebody's user name or alias, and a user name that is somebody's alias: all refused, in any case.
        Assert.Equal(HttpStatusCode.Conflict, (await SetNamesAsync(admin, ben, null, "ANNA")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetNamesAsync(admin, ben, null, "annie")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/users/{ben.Id}", Input("ANNIE", null, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users", Input("Annie", GoodPassword, false))).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/users", Input("Clara", GoodPassword, false, null, ["ben"]));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Contains("'ben'", await created.Content.ReadAsStringAsync());
        Assert.Equal(3, (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Count); // the refused ones left nothing behind
        Assert.Equal("3|Ben|Ben||User|", (await admin.GetFromJsonAsync<UserDto>($"/api/users/{ben.Id}")).Show());

        // Not refused: a user keeps their own alias while they change something else, and may swap names with it.
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, "Anna K.", "Annie")).StatusCode);
        var swapped = await admin.PutAsJsonAsync($"/api/users/{anna.Id}", Input("Annie", null, false, null, ["Anna"]));
        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal("2|Annie|Anna K.|Anna|User|", (await swapped.Content.ReadFromJsonAsync<UserDto>()).Show());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
    }

    [Fact]
    public async Task Aliases_are_checked_like_user_names_and_there_are_not_too_many()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");

        foreach (var bad in new string[][] { ["ok", "OK"], ["anna"], ["a:b"], [" "], [new string('x', 65)] })
            Assert.Equal(HttpStatusCode.BadRequest, (await SetNamesAsync(admin, anna, null, bad)).StatusCode);
        var many = Enumerable.Range(1, AccountRules.MaxAliases + 1).Select(i => $"alias {i}").ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await SetNamesAsync(admin, anna, null, many)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, null, many[..AccountRules.MaxAliases])).StatusCode);
    }

    [Fact]
    public async Task The_display_name_is_the_user_name_unless_set_and_is_kept_when_not_mentioned()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var anna = await CreateUserAsync(admin, "Anna");
        Assert.Equal("Anna", anna.DisplayName);

        Assert.Equal("Anna K.", (await (await SetNamesAsync(admin, anna, " Anna K. ")).Content.ReadFromJsonAsync<UserDto>())!.DisplayName);
        Assert.Equal("Anna K.", (await admin.GetFromJsonAsync<UserDto>($"/api/users/{anna.Id}"))!.DisplayName);
        Assert.Equal("Anna K.", (await (await SetNamesAsync(admin, anna, null)).Content.ReadFromJsonAsync<UserDto>())!.DisplayName); // not mentioned
        Assert.Equal("Anna", (await (await SetNamesAsync(admin, anna, "  ")).Content.ReadFromJsonAsync<UserDto>())!.DisplayName); // empty: the user name

        var created = await admin.PostAsJsonAsync("/api/users", Input("Ben", GoodPassword, false, "The Ben"));
        Assert.Equal("The Ben", (await created.Content.ReadFromJsonAsync<UserDto>())!.DisplayName);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetNamesAsync(admin, anna, new string('x', 65))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetNamesAsync(admin, anna, "two\nlines")).StatusCode);
    }

    [Fact]
    public async Task A_user_changes_their_own_display_name_and_aliases_but_nobody_elses()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await CreateUserAsync(admin, "Anna");
        var ben = await CreateUserAsync(admin, "Ben");
        var anna = host.CreateSignedInClient("Anna", GoodPassword);

        var response = await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", null, null, "Anna K.", ["Annie"]));
        Assert.Equal("2|Anna|Anna K.|Annie|User|", (await response.Content.ReadFromJsonAsync<UserDto>()).Show());

        // A change of the password alone does not touch the names.
        await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", GoodPassword, "another good password"));
        Assert.Equal("2|Anna|Anna K.|Annie|User|", (await host.CreateSignedInClient("annie", "another good password").GetFromJsonAsync<UserDto>("/api/account")).Show());

        Assert.Equal(HttpStatusCode.Conflict, (await host.CreateSignedInClient("annie", "another good password")
            .PutAsJsonAsync("/api/account", new AccountUpdateDto("Annie", null, null, null, ["Ben"]))).StatusCode);
        Assert.Equal("3|Ben|Ben||User|", (await admin.GetFromJsonAsync<UserDto>($"/api/users/{ben.Id}")).Show());
    }

    [Fact]
    public async Task The_names_of_a_deleted_user_can_be_used_again()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, anna, null, "Annie")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{anna.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Annie", GoodPassword));
        var ben = await CreateUserAsync(admin, "Ben");
        Assert.Equal(HttpStatusCode.OK, (await SetNamesAsync(admin, ben, null, "Annie")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Annie", GoodPassword));
    }

    [Fact]
    public void The_rules_for_aliases_and_display_names_are_the_same_for_the_clients()
    {
        Assert.Null(AccountRules.CheckDisplayName(null));
        Assert.Null(AccountRules.CheckDisplayName("  "));
        Assert.Null(AccountRules.CheckDisplayName(new string('x', AccountRules.MaxDisplayNameLength)));
        Assert.NotNull(AccountRules.CheckDisplayName(new string('x', AccountRules.MaxDisplayNameLength + 1)));
        Assert.NotNull(AccountRules.CheckDisplayName("a\tb"));

        Assert.Null(AccountRules.CheckAliases(null, "Anna"));
        Assert.Null(AccountRules.CheckAliases([], "Anna"));
        Assert.Null(AccountRules.CheckAliases(["Annie", " Ann "], "Anna"));
        Assert.NotNull(AccountRules.CheckAliases(["ANNA"], "anna")); // the user's own name
        Assert.NotNull(AccountRules.CheckAliases(["Annie", "annie "], "Anna")); // twice
        Assert.NotNull(AccountRules.CheckAliases(["", "x"], "Anna"));
    }

    private static List<string> Tables(InMemoryDatabase database)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = new SqliteCommand("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", connection);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    // --- The first administrator -------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_start_creates_the_configured_administrator_and_a_restart_changes_nothing()
    {
        using (var first = CreateHost())
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(first.CreateClient(), AdminName, AdminPassword));

        // A second host on the same accounts database, as after a restart, with other settings: the account is not touched.
        using var second = CreateHost(b =>
        {
            b.UseSetting("Accounts:InitialAdminUserName", "boss");
            b.UseSetting("Accounts:InitialAdminPassword", "another password");
        });
        var client = second.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, AdminName, AdminPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "boss", "another password"));
        Assert.Single(await second.CreateSignedInClient().GetFromJsonAsync<List<UserDto>>("/api/users") ?? []);
    }

    // --- Your own account --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_account_endpoint_names_the_signed_in_user()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");

        var admin = await host.CreateSignedInClient().GetFromJsonAsync<UserDto>("/api/account");
        var anna = await host.CreateSignedInClient("anna", GoodPassword).GetFromJsonAsync<UserDto>("/api/account");

        Assert.Equal(Dto(1, "admin", "admin", [], true).Show(), admin.Show());
        Assert.Equal(Dto(2, "Anna", "Anna", [], false).Show(), anna.Show()); // the name as it was created, however it was typed to sign in
    }

    [Fact]
    public async Task A_user_changes_their_own_user_name()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");
        var anna = host.CreateSignedInClient("Anna", GoodPassword);

        var response = await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("  Anna K  ", null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Anna K", (await response.Content.ReadFromJsonAsync<UserDto>())!.UserName);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna K", GoodPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
    }

    [Fact]
    public async Task A_user_changes_their_own_password_only_with_the_current_one()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");
        var anna = host.CreateSignedInClient("Anna", GoodPassword);

        var withoutCurrent = await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", null, "a new password"));
        var wrongCurrent = await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", "not it at all", "a new password"));
        Assert.Equal(HttpStatusCode.BadRequest, withoutCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", GoodPassword)); // nothing changed

        var changed = await anna.PutAsJsonAsync("/api/account", new AccountUpdateDto("Anna", GoodPassword, "a new password"));

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", "a new password"));
    }

    [Fact]
    public async Task A_user_cannot_take_a_user_name_that_is_in_use_in_any_case()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");

        var response = await host.CreateSignedInClient("Anna", GoodPassword)
            .PutAsJsonAsync("/api/account", new AccountUpdateDto("ADMIN", null, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
    }

    [Theory]
    [InlineData("", null, "UserName")]
    [InlineData("a:b", null, "UserName")]
    [InlineData("Anna", "short", "NewPassword")]
    public async Task Own_changes_are_validated(string userName, string? newPassword, string expectedKey)
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");

        var response = await host.CreateSignedInClient("Anna", GoodPassword)
            .PutAsJsonAsync("/api/account", new AccountUpdateDto(userName, GoodPassword, newPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{expectedKey}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_normal_user_can_do_nothing_with_other_users_and_cannot_make_themselves_an_administrator()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");
        await CreateUserAsync(host.CreateSignedInClient(), "Ben");
        var anna = host.CreateSignedInClient("Anna", GoodPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/users/3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/users/2")).StatusCode); // not even their own record there
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PostAsJsonAsync("/api/users", Input("Eve", GoodPassword, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsJsonAsync("/api/users/3", Input("Ben X", null, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsJsonAsync("/api/users/2", Input("Anna", null, true))).StatusCode);

        // The own-account endpoint has no field for the rights; an extra one in the JSON is ignored.
        var sneaky = await anna.PutAsync("/api/account",
            JsonContent.Create(new { userName = "Anna", isAdmin = true }));
        Assert.Equal(HttpStatusCode.OK, sneaky.StatusCode);
        Assert.False((await anna.GetFromJsonAsync<UserDto>("/api/account"))!.HasAdministratorRole());
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/users")).StatusCode);

        var admin = host.CreateSignedInClient();
        Assert.Equal("Ben", (await admin.GetFromJsonAsync<UserDto>("/api/users/3"))!.UserName); // untouched
    }

    // --- Administrators ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_administrator_creates_users_who_can_then_sign_in()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var response = await admin.PostAsJsonAsync("/api/users", Input("  Anna  ", GoodPassword, false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(Dto(2, "Anna", "Anna", [], false).Show(), created.Show());
        Assert.EndsWith("/api/users/2", response.Headers.Location!.ToString());
        Assert.Equal(created.Show(), (await admin.GetFromJsonAsync<UserDto>(response.Headers.Location)).Show());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "anna", GoodPassword));

        var all = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        Assert.Equal(["admin", "Anna"], all!.Select(u => u.UserName)); // by name
    }

    [Fact]
    public async Task A_user_name_exists_only_once_regardless_of_case()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await CreateUserAsync(admin, "Anna");

        var duplicate = await admin.PostAsJsonAsync("/api/users", Input("ANNA", GoodPassword, false));
        var colliding = await admin.PutAsJsonAsync("/api/users/1", Input("anna", null, true));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already taken", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, colliding.StatusCode);
        Assert.Equal(2, (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Count);
    }

    [Theory]
    [InlineData("", GoodPassword, "UserName")]
    [InlineData("   ", GoodPassword, "UserName")]
    [InlineData("a:b", GoodPassword, "UserName")]
    [InlineData("line\nbreak", GoodPassword, "UserName")]
    [InlineData("Anna", "short", "Password")]
    [InlineData("Anna", null, "Password")]
    [InlineData("Anna", "", "Password")]
    public async Task A_new_user_needs_a_proper_name_and_password(string userName, string? password, string expectedKey)
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PostAsJsonAsync("/api/users", Input(userName, password, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{expectedKey}\"", await response.Content.ReadAsStringAsync());
        Assert.Single((await host.CreateSignedInClient().GetFromJsonAsync<List<UserDto>>("/api/users"))!);
    }

    [Fact]
    public async Task A_name_longer_than_the_column_is_refused_instead_of_cut_off()
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient()
            .PostAsJsonAsync("/api/users", Input(new string('x', 65), GoodPassword, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_changes_a_users_name_password_and_rights()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");

        var response = await admin.PutAsJsonAsync($"/api/users/{anna.Id}", Input("Anna K", "reset by the admin", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Renaming the user name leaves the display name alone: it is a name of its own.
        Assert.Equal(Dto(anna.Id, "Anna K", "Anna", [], true).Show(), (await response.Content.ReadFromJsonAsync<UserDto>()).Show());
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna K", GoodPassword));
        var promoted = host.CreateSignedInClient("Anna K", "reset by the admin");
        Assert.Equal(HttpStatusCode.OK, (await promoted.GetAsync("/api/users")).StatusCode); // an administrator now
    }

    [Fact]
    public async Task Leaving_the_password_out_keeps_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");

        var response = await admin.PutAsJsonAsync($"/api/users/{anna.Id}", Input("Anna Neu", null, false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna Neu", GoodPassword));
    }

    [Fact]
    public async Task An_administrator_may_step_down_only_while_another_one_remains()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var alone = await admin.PutAsJsonAsync("/api/users/1", Input("admin", null, false));
        Assert.Equal(HttpStatusCode.Conflict, alone.StatusCode);
        Assert.Contains("at least one administrator", await alone.Content.ReadAsStringAsync());
        Assert.True((await admin.GetFromJsonAsync<UserDto>("/api/account"))!.HasAdministratorRole());

        var second = await CreateUserAsync(admin, "Boss", isAdmin: true);
        var together = await admin.PutAsJsonAsync("/api/users/1", Input("admin", null, false));
        Assert.Equal(HttpStatusCode.OK, together.StatusCode);

        // Now the second one is the only one.
        var boss = host.CreateSignedInClient("Boss", GoodPassword);
        var last = await boss.PutAsJsonAsync($"/api/users/{second.Id}", Input("Boss", null, false));
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/users/99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/users/99", Input("Ghost", null, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/users/99")).StatusCode);
    }

    // --- Deleting users ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_deleted_user_is_gone_and_their_login_stops_working()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");
        var ben = await CreateUserAsync(admin, "Ben");
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));

        var response = await admin.DeleteAsync($"/api/users/{anna.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/users/{anna.Id}")).StatusCode);
        Assert.Equal(["admin", "Ben"], (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Select(u => u.UserName));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Ben", GoodPassword)); // the others are untouched
        Assert.Equal(ben.Show(), (await admin.GetFromJsonAsync<UserDto>($"/api/users/{ben.Id}")).Show());
    }

    [Fact]
    public async Task A_name_that_was_deleted_can_be_used_again()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anna = await CreateUserAsync(admin, "Anna");
        await admin.DeleteAsync($"/api/users/{anna.Id}");

        var again = await CreateUserAsync(admin, "anna", "another good one");

        Assert.NotEqual(anna.Id, again.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), "Anna", GoodPassword)); // not the old password
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", "another good one"));
    }

    [Fact]
    public async Task Administrators_cannot_delete_themselves_even_when_another_administrator_remains()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var alone = await admin.DeleteAsync("/api/users/1");
        Assert.Equal(HttpStatusCode.Conflict, alone.StatusCode);
        Assert.Contains("your own user", await alone.Content.ReadAsStringAsync());

        await CreateUserAsync(admin, "Boss", isAdmin: true);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/users/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), AdminName, AdminPassword));

        // Another administrator may delete them.
        var boss = host.CreateSignedInClient("Boss", GoodPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await boss.DeleteAsync("/api/users/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host.CreateClient(), AdminName, AdminPassword));
    }

    [Fact]
    public async Task The_only_administrator_is_never_deleted_even_if_the_request_is_not_theirs_to_make()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");

        // Only reachable when two administrators delete each other at the same moment; the service guards it all the same.
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountService>();

        await Assert.ThrowsAsync<LastAdminException>(() => service.DeleteAsync(1, actingUserId: 2, CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), AdminName, AdminPassword));
    }

    [Fact]
    public async Task A_normal_user_cannot_delete_anybody()
    {
        using var host = CreateHost();
        await CreateUserAsync(host.CreateSignedInClient(), "Anna");
        await CreateUserAsync(host.CreateSignedInClient(), "Ben");

        var anna = host.CreateSignedInClient("Anna", GoodPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync("/api/users/3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync("/api/users/2")).StatusCode); // not even themselves
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Ben", GoodPassword));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", GoodPassword));
    }

    // --- The planner client -------------------------------------------------------------------------------------

    private static PlannerApiClient ClientFor(WebApplicationFactory<Program> host, string userName, string password) =>
        new(new HttpClient(new BasicAuthHandler(new LoginSession { Login = new Login(userName, password) }) { InnerHandler = host.Server.CreateHandler() })
        {
            BaseAddress = host.Server.BaseAddress,
        });

    [Fact]
    public async Task The_planner_client_manages_users_and_the_own_account_against_the_real_api()
    {
        using var host = CreateHost();
        var admin = ClientFor(host, AdminName, AdminPassword);

        var anna = await admin.CreateUserAsync(Input("Anna", GoodPassword, false));
        var boss = await admin.CreateUserAsync(Input("Boss", GoodPassword, true));
        Assert.Equal(["admin", "Anna", "Boss"], (await admin.GetUsersAsync()).Select(u => u.UserName));

        var renamed = await admin.UpdateUserAsync(anna.Id, Input("Anna K", "reset by the admin", false));
        Assert.Equal(Dto(anna.Id, "Anna K", "Anna", [], false).Show(), renamed.Show());

        var own = ClientFor(host, "Anna K", "reset by the admin");
        Assert.Equal(Dto(anna.Id, "Anna K", "Anna", [], false).Show(), (await own.UpdateAccountAsync(new AccountUpdateDto("Anna K", null, null))).Show());
        var changed = await own.UpdateAccountAsync(new AccountUpdateDto("Anna", "reset by the admin", "a password of her own"));
        Assert.Equal("Anna", changed.UserName);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host.CreateClient(), "Anna", "a password of her own"));

        var wrong = await Assert.ThrowsAsync<PlannerApiException>(() => ClientFor(host, "Anna", "a password of her own")
            .UpdateAccountAsync(new AccountUpdateDto("Anna", "not it", "yet another one")));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var forbidden = await Assert.ThrowsAsync<PlannerApiException>(() => ClientFor(host, "Anna", "a password of her own").GetUsersAsync());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var taken = await Assert.ThrowsAsync<PlannerApiException>(() => admin.CreateUserAsync(Input("BOSS", GoodPassword, false)));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("already taken", taken.Message);

        await admin.DeleteUserAsync(boss.Id);
        Assert.DoesNotContain(await admin.GetUsersAsync(), u => u.UserName == "Boss");

        var self = await Assert.ThrowsAsync<PlannerApiException>(() => admin.DeleteUserAsync(1));
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
    }
}

/// <summary>Keeps what the API logs, to read a generated password out of it the way an operator would.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Messages) owner.Messages.Add(formatter(state, exception));
        }
    }
}

public class AccountRulesTests
{
    [Theory]
    [InlineData("Anna", true)]
    [InlineData("  Anna K  ", true)]
    [InlineData("Jürgen Köhler", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("a:b", false)]
    [InlineData("tab\there", false)]
    public void A_user_name_is_something_that_survives_basic_authentication(string? userName, bool valid) =>
        Assert.Equal(valid, AccountRules.CheckUserName(userName) is null);

    [Fact]
    public void A_user_name_has_the_length_of_its_column()
    {
        Assert.Null(AccountRules.CheckUserName(new string('x', AccountsDbContext.UserNameLength)));
        Assert.NotNull(AccountRules.CheckUserName(new string('x', AccountsDbContext.UserNameLength + 1)));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void A_password_needs_a_sensible_length_and_nothing_else(int length, bool valid) =>
        Assert.Equal(valid, AccountRules.CheckPassword(new string('x', length)) is null);

    [Fact]
    public void A_password_may_be_any_text_of_that_length() =>
        Assert.Null(AccountRules.CheckPassword("      :: ä ::      "));
}
