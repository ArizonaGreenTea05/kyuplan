using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>Counts the restarts the API asks for instead of stopping the test host.</summary>
internal sealed class CountingRestarter : IAppRestarter
{
    public int Requests { get; private set; }

    public void Request() => Requests++;
}

/// <summary>
/// The first start and the settings against the real API: an API without a user asks to be set up, the setup creates the first
/// administrator, what is saved lands in the settings file and is read again by the next start.
/// </summary>
public sealed class SetupApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string GoodPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-setup-" + Guid.NewGuid().ToString("N"));
    private readonly CountingRestarter _restarter = new();

    /// <summary>A password as the settings file keeps it (encrypted, key in the folder next to the file) in plain text.</summary>
    private string Plain(string? stored)
    {
        Assert.StartsWith("enc:v1:", stored);
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return Api.Configuration.SettingsFile.CreateProtector(configuration, SettingsFile).Unprotect(stored["enc:v1:".Length..]);
    }

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        SqliteConnectionsCleanup();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static void SqliteConnectionsCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>An API with nobody in it unless <paramref name="withAdmin"/>; the settings file is this test's own.</summary>
    private WebApplicationFactory<Program> CreateHost(bool withAdmin = false, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Accounts:ConnectionString", _accounts.ConnectionString);
            builder.UseSetting("Accounts:InitialAdminUserName", AdminName);
            builder.UseSetting("Accounts:InitialAdminPassword", withAdmin ? AdminPassword : "");
            builder.UseSetting("Setup:SettingsFile", SettingsFile);
            builder.ConfigureServices(services => services.AddSingleton<IAppRestarter>(_restarter));
            configure?.Invoke(builder);
        });

    private static async Task<SettingsDto> SettingsOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsDto>("/api/settings"))!;

    /// <summary>The input that changes nothing: what the API runs with.</summary>
    private static SettingsInputDto Unchanged(SettingsDto s) => new(
        s.DatabasePath, s.AccountsDatabasePath, s.UseDemoData, s.SpondEmail, s.SpondPhoneNumber, null, s.IgnoreAdministrators,
        s.TimeZone, s.ExamKeyword, s.DefaultMinAttendancePercent, s.DefaultMinInvitedEvents, s.SyncEnabled, s.SyncIntervalMinutes);

    private static async Task<SetupStatusDto> StatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SetupStatusDto>("/api/setup"))!;

    private static async Task<SetupInputDto> SetupInputAsync(HttpClient client, Func<SettingsInputDto, SettingsInputDto>? change = null)
    {
        var status = await StatusAsync(client);
        var settings = Unchanged(status.Settings!);
        return new SetupInputDto(change?.Invoke(settings) ?? settings, "boss", GoodPassword);
    }

    // --- Is there something to set up --------------------------------------------------------------------------

    [Fact]
    public async Task An_api_without_a_user_starts_and_asks_for_the_setup()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        var status = await StatusAsync(client);

        Assert.True(status.NeedsSetup);
        Assert.NotNull(status.Settings); // what the form starts with
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/groups")).StatusCode); // nobody can sign in yet
    }

    [Fact]
    public async Task An_api_with_a_user_needs_no_setup_and_tells_nobody_its_settings()
    {
        using var host = CreateHost(withAdmin: true);

        var status = await StatusAsync(host.CreateClient());

        Assert.False(status.NeedsSetup);
        Assert.Null(status.Settings);
    }

    // --- The setup -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_setup_creates_the_first_administrator_who_can_sign_in_and_ends_the_setup()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", await SetupInputAsync(client));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting); // nothing changed, so nothing restarts
        Assert.Equal(0, _restarter.Requests);

        var admin = host.CreateSignedInClient("boss", GoodPassword);
        var me = (await admin.GetFromJsonAsync<UserDto>("/api/account"))!;
        Assert.Equal(Permission.All, me.EffectivePermissions); // an administrator: every permission, also the new ones
        Assert.False((await StatusAsync(client)).NeedsSetup);
    }

    [Fact]
    public async Task The_setup_can_be_done_once_only_so_nobody_can_take_over_a_running_api()
    {
        using var host = CreateHost();
        var client = host.CreateClient();
        var input = await SetupInputAsync(client);
        await client.PostAsJsonAsync("/api/setup", input);

        var second = await client.PostAsJsonAsync("/api/setup", input with { AdminUserName = "intruder" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateSignedInClient("intruder", GoodPassword).GetAsync("/api/account")).StatusCode);
    }

    [Fact]
    public async Task The_setup_is_closed_for_an_api_that_was_set_up_from_its_configuration()
    {
        using var host = CreateHost(withAdmin: true);
        var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new SetupInputDto(Unchanged(FakePlannerApi.DefaultSettings), "intruder", GoodPassword));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Changed_settings_are_written_to_the_settings_file_the_api_restarts_and_the_next_start_reads_them()
    {
        using var first = CreateHost();
        var client = first.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            await SetupInputAsync(client, s => s with { TimeZone = "UTC", ExamKeyword = "Pruefung", SyncIntervalMinutes = 90, SpondPassword = "secret", SpondEmail = "me@example.org" }));

        Assert.True((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting);
        Assert.Equal(1, _restarter.Requests);
        var written = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("UTC", (string?)written["Statistics"]!["TimeZone"]);
        Assert.Equal("Pruefung", (string?)written["Statistics"]!["ExamKeyword"]);
        Assert.Equal("01:30:00", (string?)written["Sync"]!["Interval"]);
        Assert.Equal("secret", Plain((string?)written["Spond"]!["Password"]));
        Assert.Null(written["Database"]); // what stayed as it was is not written

        // The next start (a restart, or a new process) reads the file.
        using var restarted = CreateHost(withAdmin: true);
        var settings = await SettingsOfAsync(restarted.CreateSignedInClient("boss", GoodPassword));
        Assert.Equal("UTC", settings.TimeZone);
        Assert.Equal("Pruefung", settings.ExamKeyword);
        Assert.Equal(90, settings.SyncIntervalMinutes);
        Assert.Equal("me@example.org", settings.SpondEmail);
        Assert.True(settings.HasSpondPassword);
    }

    [Fact]
    public async Task The_setup_refuses_what_cannot_work_and_creates_nobody()
    {
        using var host = CreateHost();
        var client = host.CreateClient();
        var good = await SetupInputAsync(client);

        var shortPassword = await client.PostAsJsonAsync("/api/setup", good with { AdminPassword = "short" });
        var colon = await client.PostAsJsonAsync("/api/setup", good with { AdminUserName = "a:b" });
        var zone = await client.PostAsJsonAsync("/api/setup", good with { Settings = good.Settings with { TimeZone = "Mars/Olympus" } });
        var sameFile = await client.PostAsJsonAsync("/api/setup", good with { Settings = good.Settings with { AccountsDatabasePath = good.Settings.DatabasePath } });
        var halfLogin = await client.PostAsJsonAsync("/api/setup", good with { Settings = good.Settings with { UseDemoData = false, SpondEmail = "me@example.org" } });

        Assert.All([shortPassword, colon, zone, sameFile, halfLogin], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.True((await StatusAsync(client)).NeedsSetup);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_where_it_is_to_be_is_refused_before_anything_is_saved()
    {
        Directory.CreateDirectory(_folder);
        var notADatabase = Path.Combine(_folder, "not-a-database.db");
        File.WriteAllText(notADatabase, "this is not an SQLite file, and it is long enough not to be taken for an empty one " + new string('x', 200));
        using var host = CreateHost();
        var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", await SetupInputAsync(client, s => s with { DatabasePath = notADatabase }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(SettingsFile));
        Assert.True((await StatusAsync(client)).NeedsSetup);
    }

    [Fact]
    public async Task The_first_administrator_goes_to_the_accounts_database_at_its_new_place()
    {
        Directory.CreateDirectory(_folder);
        var elsewhere = Path.Combine(_folder, "elsewhere", "accounts.db");
        using var host = CreateHost();
        var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", await SetupInputAsync(client, s => s with { AccountsDatabasePath = elsewhere }));

        Assert.True((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting);
        var options = new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite($"Data Source={elsewhere}").Options;
        await using var db = new AccountsDbContext(options);
        var user = await db.Users.Include(u => u.Roles).SingleAsync();
        Assert.Equal("boss", user.UserName);
        Assert.Contains(user.Roles, r => r.IsSystem);
        Assert.Contains("elsewhere", File.ReadAllText(SettingsFile)); // the next start looks there
    }

    [Fact]
    public async Task A_settings_file_that_is_not_valid_json_does_not_keep_the_api_from_starting()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, "{ this is not json");
        using var host = CreateHost();

        Assert.True((await StatusAsync(host.CreateClient())).NeedsSetup);
    }

    // --- The settings page -------------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_are_for_users_who_may_see_and_change_them()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = await SettingsOfAsync(admin);
        await admin.PostAsJsonAsync("/api/users", Input("trainer", GoodPassword, isAdmin: false));
        var trainer = host.CreateSignedInClient("trainer", GoodPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainer.PutAsJsonAsync("/api/settings", Unchanged(settings))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", Unchanged(settings))).StatusCode);
    }

    [Fact]
    public async Task Saving_what_did_not_change_does_not_restart_the_api_and_writes_nothing()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();

        var response = await admin.PutAsJsonAsync("/api/settings", Unchanged(await SettingsOfAsync(admin)));

        Assert.False((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting);
        Assert.Equal(0, _restarter.Requests);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_change_is_saved_into_the_file_beside_what_was_saved_before_and_restarts_the_api()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));

        await admin.PutAsJsonAsync("/api/settings", settings with { TimeZone = "UTC" });
        var response = await admin.PutAsJsonAsync("/api/settings", settings with { ExamKeyword = "Test" });

        Assert.True((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting);
        Assert.Equal(2, _restarter.Requests);
        var written = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("Test", (string?)written["Statistics"]!["ExamKeyword"]);
        Assert.Equal("UTC", (string?)written["Statistics"]!["TimeZone"]); // the earlier save is still in the file
    }

    [Fact]
    public async Task The_spond_password_is_never_given_back_and_an_empty_one_keeps_the_stored_one()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));
        await admin.PutAsJsonAsync("/api/settings", settings with { UseDemoData = false, SpondEmail = "me@example.org", SpondPassword = "secret" });

        var raw = await (await admin.GetAsync("/api/settings")).Content.ReadAsStringAsync();
        await admin.PutAsJsonAsync("/api/settings", settings with { SpondEmail = "other@example.org", SpondPassword = "" });

        Assert.DoesNotContain("secret", raw);
        var written = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("secret", Plain((string?)written["Spond"]!["Password"]));
        Assert.DoesNotContain("secret", File.ReadAllText(SettingsFile)); // the password is encrypted in the file
        Assert.Equal("other@example.org", (string?)written["Spond"]!["Email"]);
    }

    [Fact]
    public async Task The_allowed_hosts_are_saved_with_the_settings_and_must_be_host_names()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));
        Assert.Equal("*", (await SettingsOfAsync(admin)).AllowedHosts);

        var bad = await admin.PutAsJsonAsync("/api/settings", settings with { AllowedHosts = "not a host" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await admin.PutAsJsonAsync("/api/settings", settings with { AllowedHosts = "kyuplan.example.org; *.example.com" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("kyuplan.example.org; *.example.com".Replace("; ", ";").Replace(";", "; "), (string?)JsonNode.Parse(File.ReadAllText(SettingsFile))!["AllowedHosts"]);
    }

    [Fact]
    public async Task The_first_start_can_set_the_allowed_hosts()
    {
        using var first = CreateHost();
        var client = first.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", await SetupInputAsync(client, s => s with { AllowedHosts = "kyuplan.example.org" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("kyuplan.example.org", (string?)JsonNode.Parse(File.ReadAllText(SettingsFile))!["AllowedHosts"]);
    }

    [Fact]
    public async Task A_database_cannot_be_moved_out_of_the_folder_of_the_settings_file()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));

        var outside = await admin.PutAsJsonAsync("/api/settings", settings with { DatabasePath = Path.Combine(Path.GetTempPath(), "elsewhere", "kyuplan.db") });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        var traversal = await admin.PutAsJsonAsync("/api/settings", settings with { AccountsDatabasePath = Path.Combine(_folder, "..", "accounts.db") });
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

        var inside = await admin.PutAsJsonAsync("/api/settings", settings with { DatabasePath = Path.Combine(_folder, "sub", "planner.db") });
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
    }

    [Fact]
    public async Task A_password_that_an_earlier_version_left_in_plain_text_is_encrypted_at_the_next_start_and_still_works()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, "{ \"Spond\": { \"Email\": \"me@example.org\", \"Password\": \"old-secret\" } }");

        using var host = CreateHost(withAdmin: true);
        var settings = await SettingsOfAsync(host.CreateSignedInClient());

        Assert.True(settings.HasSpondPassword);
        Assert.DoesNotContain("old-secret", File.ReadAllText(SettingsFile));
        Assert.Equal("old-secret", Plain((string?)JsonNode.Parse(File.ReadAllText(SettingsFile))!["Spond"]!["Password"]));
        Assert.Equal("old-secret", host.Services.GetRequiredService<IOptions<Api.Configuration.SpondOptions>>().Value.Password);
    }

    [Fact]
    public async Task Backup_spond_accounts_are_saved_as_a_list_keep_their_passwords_and_can_be_removed()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));

        await admin.PutAsJsonAsync("/api/settings", settings with { BackupAccounts = [new("one@example.org", null, "pw1"), new(null, "+491234", "pw2")] });
        var raw = await (await admin.GetAsync("/api/settings")).Content.ReadAsStringAsync();
        var written = JsonNode.Parse(File.ReadAllText(SettingsFile))!;

        Assert.DoesNotContain("pw1", raw);
        Assert.Equal(2, written["Spond"]!["Accounts"]!.AsArray().Count);
        Assert.Equal("pw2", Plain((string?)written["Spond"]!["Accounts"]![1]!["Password"]));
        Assert.DoesNotContain("pw1", File.ReadAllText(SettingsFile));

        // An empty password keeps the stored one (an account is known by its email / phone number); a blank row is ignored.
        using var restarted = CreateHost(withAdmin: true);
        var again = restarted.CreateSignedInClient();
        var loaded = await SettingsOfAsync(again);
        Assert.Equal(2, loaded.BackupAccounts!.Count);
        Assert.All(loaded.BackupAccounts, a => Assert.True(a.HasPassword));
        var kept = await again.PutAsJsonAsync("/api/settings", Unchanged(loaded) with { BackupAccounts = [new("one@example.org", null, ""), new(null, null, null)] });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        written = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Single(written["Spond"]!["Accounts"]!.AsArray());
        Assert.Equal("pw1", Plain((string?)written["Spond"]!["Accounts"]![0]!["Password"]));
    }

    [Fact]
    public async Task A_backup_account_without_a_password_is_refused()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = Unchanged(await SettingsOfAsync(admin));

        var response = await admin.PutAsJsonAsync("/api/settings", settings with { BackupAccounts = [new("one@example.org", null, null)] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task The_setup_shows_where_the_settings_file_is_and_refuses_to_move_it_when_the_configuration_fixes_it()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        var status = await StatusAsync(client);
        var response = await client.PostAsJsonAsync("/api/setup", (await SetupInputAsync(client)) with { SettingsFilePath = Path.Combine(_folder, "other", "settings.json") });

        Assert.Equal(SettingsFile, status.SettingsFilePath);
        Assert.True(status.SettingsFileLocked); // CreateHost sets Setup:SettingsFile
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await StatusAsync(client)).NeedsSetup);
    }

    [Fact]
    public void A_remembered_settings_file_location_is_used_when_the_configuration_does_not_set_one()
    {
        Directory.CreateDirectory(_folder);
        var elsewhere = Path.Combine(_folder, "elsewhere", "settings.json");
        var empty = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Assert.Equal(Path.Combine(_folder, "data", "settings.json"), Api.Configuration.SettingsFile.Resolve(empty, _folder));
        Api.Configuration.SettingsFile.RememberLocation(_folder, elsewhere);

        Assert.Equal(elsewhere, Api.Configuration.SettingsFile.Resolve(empty, _folder));
    }

    [Fact]
    public async Task The_stars_of_the_evaluations_are_settings_with_standard_values_and_are_saved_like_the_others()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var settings = await SettingsOfAsync(admin);

        Assert.Equal((2.5, 3.0), (settings.DefaultEventStars, settings.ExamMinAverageStars));

        var response = await admin.PutAsJsonAsync("/api/settings", Unchanged(settings) with { DefaultEventStars = 3.5, ExamMinAverageStars = 4 });

        Assert.True((await response.Content.ReadFromJsonAsync<SettingsSavedDto>())!.Restarting);
        var written = JsonNode.Parse(File.ReadAllText(SettingsFile))!["Statistics"]!;
        Assert.Equal("3.5", (string?)written["DefaultEventStars"]);
        Assert.Equal("4", (string?)written["ExamMinAverageStars"]);
    }

    [Fact]
    public async Task The_settings_refuse_values_that_would_keep_the_api_from_starting()
    {
        using var host = CreateHost(withAdmin: true);
        var admin = host.CreateSignedInClient();
        var good = Unchanged(await SettingsOfAsync(admin));

        var badInput = new[]
        {
            good with { DatabasePath = " " }, good with { TimeZone = "Nowhere/Land" }, good with { DefaultMinAttendancePercent = 101 },
            good with { SyncIntervalMinutes = -1 }, good with { AccountsDatabasePath = good.DatabasePath },
            good with { DefaultEventStars = 5.5 }, good with { ExamMinAverageStars = -1 },
        };

        foreach (var input in badInput)
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings", input)).StatusCode);
        Assert.Equal(0, _restarter.Requests);
    }
}
