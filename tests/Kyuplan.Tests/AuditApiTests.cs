using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The audit log against the real API: what is recorded, what is not, who may read it.</summary>
public sealed class AuditApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-audit-").FullName;

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Setup:SettingsFile", Path.Combine(_folder, "settings.json"));
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("Backup:TargetsFile", Path.Combine(_folder, "targets.json"));
            Configure(builder, _accounts);
            builder.ConfigureServices(services => services.AddSingleton<IAppRestarter>(new CountingRestarter()));
        });

    private static async Task<AuditPageDto> ReadLogAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/audit" + query);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AuditPageDto>())!;
    }

    [Fact]
    public async Task Changes_to_belts_are_recorded_with_who_made_them()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var created = await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var belt = (await created.Content.ReadFromJsonAsync<BeltDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/belts/{belt.Id}", new BeltInputDto("Snow", "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/belts/{belt.Id}")).StatusCode);

        var log = await ReadLogAsync(admin, "?area=" + AuditAreas.Belts);

        // Newest first.
        Assert.Equal([AuditActions.Delete, AuditActions.Update, AuditActions.Create], log.Entries.Select(e => e.Action));
        Assert.All(log.Entries, e => Assert.Equal(AuditAreas.Belts, e.Area));
        Assert.All(log.Entries, e => Assert.NotEmpty(e.UserName));
        Assert.Equal($"id={belt.Id}", log.Entries[2].Target);
        Assert.Equal($"beltId={belt.Id}", log.Entries[0].Target);
        Assert.Contains("Snow", log.Entries[1].Details);
        Assert.Null(log.Entries[0].Details);
        Assert.Null(log.NextBefore);
    }

    [Fact]
    public async Task A_request_that_changed_nothing_leaves_nothing_behind()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        // Invalid (400), not found (404) and a duplicate name (409).
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("", "nope", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/belts/999")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("white", "#FFFFFF", null, []))).StatusCode);

        var log = await ReadLogAsync(admin);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditActions.Create, entry.Action);
    }

    [Fact]
    public async Task Users_and_roles_are_recorded_but_never_a_password()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", UserPassword, "Anna", null, [], [Permission.ExamsView]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Trainer", [Permission.ExamsView, Permission.PaymentsEdit]))).StatusCode);

        var log = await ReadLogAsync(admin);

        Assert.Equal([AuditAreas.Roles, AuditAreas.Users], log.Entries.Select(e => e.Area));
        Assert.Contains(Permission.PaymentsEdit, log.Entries[0].Details);
        Assert.Contains("anna", log.Entries[1].Details);
        Assert.Contains(Permission.ExamsView, log.Entries[1].Details);
        Assert.All(log.Entries, e => Assert.DoesNotContain(UserPassword, e.Details));
        Assert.All(log.Entries, e => Assert.DoesNotContain("assword", e.Details));
    }

    [Fact]
    public async Task The_log_is_read_in_pages_and_can_be_narrowed_down()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        foreach (var name in new[] { "A", "B", "C" })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto(name, "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Trainer", [Permission.ExamsView]))).StatusCode);

        var first = await ReadLogAsync(admin, "?limit=3");
        var second = await ReadLogAsync(admin, $"?limit=3&before={first.NextBefore}");

        Assert.Equal(3, first.Entries.Count);
        Assert.NotNull(first.NextBefore);
        Assert.Single(second.Entries);
        Assert.Null(second.NextBefore);
        Assert.Equal(4, first.Entries.Concat(second.Entries).Select(e => e.Id).Distinct().Count());

        Assert.Equal(3, (await ReadLogAsync(admin, "?area=" + AuditAreas.Belts)).Entries.Count);
        Assert.Empty((await ReadLogAsync(admin, "?userId=9999")).Entries);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/audit?area=nope")).StatusCode);
    }

    [Fact]
    public async Task Only_whoever_has_audit_view_may_read_the_log()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", UserPassword, null, null, [], [Permission.BeltsEdit]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("bob", UserPassword, null, null, [], [Permission.AuditView]))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);
        var bob = host.CreateSignedInClient("bob", UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await anna.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);

        var seen = await ReadLogAsync(bob, "?area=" + AuditAreas.Belts);

        // Whoever made the change is the one named, not whoever reads it.
        Assert.Single(seen.Entries);
        Assert.NotEqual("bob", seen.Entries[0].UserName);
    }

    // --- Redaction ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("SpondPassword")]
    [InlineData("passphrase")]
    [InlineData("metricsToken")]
    [InlineData("token")]
    [InlineData("clientSecret")]
    [InlineData("customScript")]
    [InlineData("script")]
    [InlineData("location")]
    [InlineData("connectionString")]
    [InlineData("credentials")]
    [InlineData("apiKey")]
    [InlineData("Authorization")]
    public void Every_key_that_can_hold_a_secret_is_left_out_of_the_log(string key)
    {
        var node = JsonNode.Parse($$"""{ "{{key}}": "s3cret", "name": "kept", "nested": { "{{key.ToUpperInvariant()}}": "s3cret", "list": [ { "{{key}}": "s3cret", "n": 1 } ] } }""");

        AuditFilter.Redact(node);

        Assert.DoesNotContain("s3cret", node!.ToJsonString());
        Assert.Contains("kept", node.ToJsonString());
        Assert.Contains("\"n\":1", node.ToJsonString());
    }

    [Theory]
    [InlineData("description")]
    [InlineData("name")]
    [InlineData("keyword")]
    [InlineData("examKeyword")]
    public void Keys_that_only_look_alike_stay_in_the_log(string key)
    {
        var node = JsonNode.Parse($$"""{ "{{key}}": "visible" }""");

        AuditFilter.Redact(node);

        Assert.Contains("visible", node!.ToJsonString());
    }

    // --- Settings ----------------------------------------------------------------------------------------------------

    private static async Task<SettingsInputDto> UnchangedSettingsAsync(HttpClient admin)
    {
        var s = (await admin.GetFromJsonAsync<SettingsDto>("/api/settings"))!;
        return new SettingsInputDto(
            s.DatabasePath, s.AccountsDatabasePath, s.UseDemoData, s.SpondEmail, s.SpondPhoneNumber, null,
            s.TimeZone, s.ExamKeyword, s.DefaultMinAttendancePercent, s.DefaultMinInvitedEvents, s.SyncEnabled, s.SyncIntervalMinutes);
    }

    [Fact]
    public async Task Saving_the_settings_logs_the_names_of_what_changed_and_never_a_secret()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedSettingsAsync(admin);
        const string script = "const headers = { Authorization: 'Bearer ghp_topsecret123' };";

        var response = await admin.PutAsJsonAsync("/api/settings", settings with
        {
            SpondEmail = "trainer@example.org",
            SpondPassword = "spond-pass-1",
            MetricsEnabled = true,
            MetricsToken = "scrape-token-0123456789",
            CustomScript = script,
            ExamKeyword = "Pruefung",
            BackupAccounts = [new SpondAccountInputDto("b@example.org", null, "backup-pass-2")],
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single((await ReadLogAsync(admin, "?area=" + AuditAreas.Settings)).Entries);
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.NotEmpty(entry.UserName);
        var changed = JsonNode.Parse(entry.Details!)!["changed"]!.AsObject();
        Assert.Equal("true", (string?)changed["Metrics:Enabled"]);
        Assert.Equal("Pruefung", (string?)changed["Statistics:ExamKeyword"]); // a harmless value is written down
        Assert.Equal("changed", (string?)changed["Metrics:Token"]);
        Assert.Equal("changed", (string?)changed["Spond:Password"]);
        Assert.Equal("changed", (string?)changed["Spond:Email"]);
        Assert.Equal("changed", (string?)changed["Spond:Accounts"]);
        Assert.Equal(script.Length, (int?)changed["DataSource:Script"]!["length"]);
        Assert.Equal(16, ((string?)changed["DataSource:Script"]!["sha256"])!.Length);
        foreach (var secret in new[] { "ghp_topsecret123", "Bearer", "scrape-token-0123456789", "spond-pass-1", "backup-pass-2", "trainer@example.org", "b@example.org" })
            Assert.DoesNotContain(secret, entry.Details);
    }

    [Fact]
    public async Task A_different_script_has_a_different_hash_in_the_log()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedSettingsAsync(admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", settings with { CustomScript = "a();" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", settings with { CustomScript = "b();" })).StatusCode);

        var log = (await ReadLogAsync(admin, "?area=" + AuditAreas.Settings)).Entries;
        Assert.Equal(2, log.Count);
        Assert.NotEqual(
            (string?)JsonNode.Parse(log[0].Details!)!["changed"]!["DataSource:Script"]!["sha256"],
            (string?)JsonNode.Parse(log[1].Details!)!["changed"]!["DataSource:Script"]!["sha256"]);
    }

    [Fact]
    public void A_settings_save_without_changes_is_described_as_an_empty_list_and_a_script_by_length_and_hash()
    {
        var none = JsonNode.Parse(SettingsAudit.Describe(new Dictionary<string, object?>()))!;
        Assert.Empty(none["changed"]!.AsObject());

        var some = JsonNode.Parse(SettingsAudit.Describe(
            new Dictionary<string, object?> { ["DataSource:Script"] = "", ["Sync:Enabled"] = "true", ["Database:ConnectionString"] = "Data Source=/x/secret.db", ["Spond:Accounts"] = new JsonArray() },
            new JsonObject { ["extra"] = 1 }))!;
        Assert.Equal(0, (int)some["changed"]!["DataSource:Script"]!["length"]!);
        Assert.Equal("true", (string?)some["changed"]!["Sync:Enabled"]);
        Assert.Equal("changed", (string?)some["changed"]!["Database:ConnectionString"]);
        Assert.Equal("changed", (string?)some["changed"]!["Spond:Accounts"]);
        Assert.Equal(1, (int)some["extra"]!);
    }

    [Fact]
    public async Task A_settings_save_that_changes_nothing_leaves_nothing_behind()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedSettingsAsync(admin);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", settings)).StatusCode);
        Assert.Empty((await ReadLogAsync(admin, "?area=" + AuditAreas.Settings)).Entries);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", settings with { ExamKeyword = "Pruefung" })).StatusCode);
        Assert.Single((await ReadLogAsync(admin, "?area=" + AuditAreas.Settings)).Entries);
    }

    [Fact]
    public async Task A_refused_settings_save_and_a_missing_permission_leave_nothing_behind()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var settings = await UnchangedSettingsAsync(admin);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", UserPassword, null, null, [], [Permission.SettingsView]))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings", settings with { AllowedHosts = "not a host" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateSignedInClient("anna", UserPassword).PutAsJsonAsync("/api/settings", settings)).StatusCode);

        Assert.Empty((await ReadLogAsync(admin, "?area=" + AuditAreas.Settings)).Entries);
    }

    // --- Backups -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_databases_are_logged_without_their_connection_string_and_so_are_runs_and_deletes()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var folder = Path.Combine(_folder, "mirror");
        var postgres = "Host=db.internal;Database=kyu;Username=kyu;Password=pg-secret-9";

        var created = await admin.PostAsJsonAsync("/api/backup/targets", new BackupTargetInputDto("Nightly", BackupTargetKind.Sqlite, folder, 60, true));
        var target = (await created.Content.ReadFromJsonAsync<BackupTargetDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/backup/targets/{target.Id}", new BackupTargetInputDto("Nightly", BackupTargetKind.PostgreSql, postgres, 90, false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/backup/targets/{target.Id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/backup/targets/{target.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/backup/targets/{target.Id}")).StatusCode);

        var log = (await ReadLogAsync(admin, "?area=" + AuditAreas.Backup)).Entries;

        Assert.Equal([AuditActions.Delete, AuditActions.Run, AuditActions.Update, AuditActions.Create], log.Select(e => e.Action));
        Assert.All(log, e => Assert.DoesNotContain("pg-secret-9", e.Details));
        Assert.All(log, e => Assert.DoesNotContain("mirror", e.Details ?? ""));
        Assert.Contains("Nightly", log[3].Details);
        Assert.Equal($"id={target.Id}", log[0].Target);
        Assert.Equal($"id={target.Id}", log[2].Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a long enough passphrase")]
    public async Task A_download_of_the_backup_is_logged_with_whether_it_is_encrypted_but_never_the_passphrase(string? passphrase)
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/backup/download");
        if (passphrase is not null) request.Headers.Add(BackupRules.PassphraseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(passphrase)));

        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(request)).StatusCode);

        var entry = Assert.Single((await ReadLogAsync(admin, "?area=" + AuditAreas.Backup)).Entries);
        Assert.Equal(AuditActions.Download, entry.Action);
        Assert.Equal(passphrase is not null, (bool?)JsonNode.Parse(entry.Details!)!["encrypted"]);
        Assert.DoesNotContain("passphrase", entry.Details!, StringComparison.OrdinalIgnoreCase);
        if (passphrase is not null) Assert.DoesNotContain(passphrase, entry.Details);
    }

    [Fact]
    public async Task A_refused_download_is_not_logged()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/backup/download");
        request.Headers.Add(BackupRules.PassphraseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes("short")));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(request)).StatusCode);

        Assert.Empty((await ReadLogAsync(admin, "?area=" + AuditAreas.Backup)).Entries);
    }

    [Fact]
    public async Task A_restore_keeps_the_log_of_the_time_since_the_backup_and_adds_itself()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);
        var file = await (await admin.GetAsync("/api/backup/download")).Content.ReadAsByteArrayAsync();
        // After the backup was made: more changes, which an old file does not know.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("Yellow", "#FFFF00", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("Green", "#00FF00", null, []))).StatusCode);
        var before = (await ReadLogAsync(admin)).Entries;

        var restored = await admin.PostAsync("/api/backup/restore", new ByteArrayContent(file));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        var after = (await ReadLogAsync(admin)).Entries;
        // The belts are the old ones, but every entry from before is still there (oldest first, numbered anew), and the restore comes last.
        Assert.Equal(["White"], (await admin.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!.Select(b => b.Name));
        Assert.Equal(before.Count + 1, after.Count);
        var chronological = after.AsEnumerable().Reverse().ToList();
        Assert.Equal(Enumerable.Range(1, after.Count).Select(i => (long)i), chronological.Select(e => e.Id));
        Assert.Equal(
            before.AsEnumerable().Reverse().Select(e => (e.At, e.Area, e.Action, e.Target, e.Details)),
            chronological.Take(before.Count).Select(e => (e.At, e.Area, e.Action, e.Target, e.Details)));
        var restore = chronological[^1];
        Assert.Equal((AuditAreas.Backup, AuditActions.Restore), (restore.Area, restore.Action));
        var details = JsonNode.Parse(restore.Details!)!;
        Assert.False((bool)details["encrypted"]!);
        Assert.True((int)details["rows"]! > 0);
        Assert.NotEmpty(restore.UserName);
    }

    [Fact]
    public async Task A_restore_of_a_file_that_cannot_be_read_changes_and_logs_nothing()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/backup/restore", new ByteArrayContent([1, 2, 3]))).StatusCode);

        var entry = Assert.Single((await ReadLogAsync(admin)).Entries);
        Assert.Equal(AuditAreas.Belts, entry.Area);
    }

    // --- Calendar feeds ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Calendar_feeds_are_logged_and_a_new_address_never_shows_a_token()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var group = (await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups"))![0].Id;
        var input = new CalendarFeedInputDto("Everything", [new CalendarSourceDto(CalendarSourceKinds.Events, group)]);

        var created = (await (await admin.PostAsJsonAsync("/api/calendar-feeds", input)).Content.ReadFromJsonAsync<CalendarFeedDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/calendar-feeds/{created.Id}", input with { Name = "Renamed" })).StatusCode);
        var regenerated = (await (await admin.PostAsync($"/api/calendar-feeds/{created.Id}/regenerate", null)).Content.ReadFromJsonAsync<CalendarFeedDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/calendar-feeds/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calendar-feeds", input with { Name = "" })).StatusCode);

        var log = (await ReadLogAsync(admin, "?area=" + AuditAreas.CalendarFeeds)).Entries;

        Assert.Equal([AuditActions.Delete, AuditActions.Regenerate, AuditActions.Update, AuditActions.Create], log.Select(e => e.Action));
        Assert.Equal([$"id={created.Id}", $"id={created.Id}", $"id={created.Id}", $"id={created.Id}"], log.Select(e => e.Target));
        Assert.Contains("Renamed", log[2].Details);
        Assert.NotEqual(created.Url, regenerated.Url);
        foreach (var feed in new[] { created, regenerated })
            Assert.All(log, e => Assert.DoesNotContain(CalendarFeedTests.TokenOf(feed), e.Details ?? ""));
    }

    // --- Tags, branding, themes ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Tags_branding_and_themes_are_logged()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var tag = (await (await admin.PostAsJsonAsync("/api/tags", new TagInputDto("Spring"))).Content.ReadFromJsonAsync<TagDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/tags/{tag.Id}", new TagInputDto("Autumn"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/tags/{tag.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/branding", new BrandingInputDto("PlanIt", "Karate Club", "#1a5fb4"))).StatusCode);
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/branding/logo", png)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/branding/nonsense")).StatusCode);

        var pack = new StringContent(Packs.Json(Packs.Valid()), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/themes", pack)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/themes/ocean")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/themes/" + BuiltInThemePacks.All[0].Id)).StatusCode);

        Assert.Equal([AuditActions.Delete, AuditActions.Update, AuditActions.Create], (await ReadLogAsync(admin, "?area=" + AuditAreas.Tags)).Entries.Select(e => e.Action));
        var branding = (await ReadLogAsync(admin, "?area=" + AuditAreas.Branding)).Entries;
        Assert.Equal([AuditActions.Delete, AuditActions.Delete, AuditActions.Update, AuditActions.Update], branding.Select(e => e.Action));
        Assert.Equal("logo", (string?)JsonNode.Parse(branding[1].Details!)!["slot"]);
        Assert.Equal("logo", (string?)JsonNode.Parse(branding[2].Details!)!["slot"]);
        var themes = (await ReadLogAsync(admin, "?area=" + AuditAreas.Themes)).Entries;
        Assert.Equal([AuditActions.Delete, AuditActions.Update], themes.Select(e => e.Action));
        Assert.Equal("ocean", (string?)JsonNode.Parse(themes[1].Details!)!["id"]);
        Assert.Equal("id=ocean", themes[0].Target);
    }

    [Fact]
    public async Task Comments_are_logged_with_what_they_concerned_but_never_what_they_say()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        int memberId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            var member = new Member { GroupId = "g1", SpondId = "spond-anna", Name = "Anna", CreatedAt = DateTimeOffset.UtcNow };
            db.Members.Add(member);
            await db.SaveChangesAsync();
            memberId = member.Id;
        }

        var created = await admin.PostAsJsonAsync($"/api/members/{memberId}/comments", new CommentInputDto("Private remark about Anna", []));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var comment = (await created.Content.ReadFromJsonAsync<CommentDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/comments/{comment.Id}", new CommentUpdateDto("Second private remark", []))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/comments/{comment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/members/{memberId}/comments", new CommentInputDto("", []))).StatusCode);

        var log = (await ReadLogAsync(admin, "?area=" + AuditAreas.Comments)).Entries;

        Assert.Equal([AuditActions.Delete, AuditActions.Update, AuditActions.Create], log.Select(e => e.Action));
        Assert.Equal([$"commentId={comment.Id}", $"commentId={comment.Id}", $"memberId={memberId}"], log.Select(e => e.Target));
        Assert.All(log, e => Assert.DoesNotContain("remark", e.Details ?? "", StringComparison.OrdinalIgnoreCase));
    }

    // --- Manual data -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Manual_data_changes_are_logged_with_what_they_concerned()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataSource:Kind", "manual");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });
        var admin = host.CreateSignedInClient();

        var group = (await (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto("Dojo"))).Content.ReadFromJsonAsync<ManualGroupDto>())!;
        var member = (await (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", new DateOnly(2014, 5, 1), null))).Content.ReadFromJsonAsync<ManualMemberDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/manual/members/{member.Id}", new ManualMemberInputDto("Anna B", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/manual/groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto(""))).StatusCode);

        var log = (await ReadLogAsync(admin, "?area=" + AuditAreas.Manual)).Entries;

        Assert.Equal([AuditActions.Delete, AuditActions.Update, AuditActions.Create, AuditActions.Create], log.Select(e => e.Action));
        Assert.Equal($"groupId={group.Id}", log[0].Target);
        Assert.Equal($"memberId={member.Id}", log[1].Target);
        Assert.Contains("Anna B", log[1].Details);
    }

    [Fact]
    public async Task Manual_data_is_not_logged_while_it_is_not_the_data_source()
    {
        using var host = CreateHost(); // demo data
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto("Dojo"))).StatusCode);

        Assert.Empty((await ReadLogAsync(admin)).Entries);
    }

    [Fact]
    public void Every_area_and_action_is_listed_once_and_known()
    {
        foreach (var area in AuditAreas.All) Assert.True(AuditAreas.IsKnown(area));
        Assert.Equal(AuditAreas.All.Count, AuditAreas.All.Distinct().Count());
        Assert.Equal(AuditActions.All.Count, AuditActions.All.Distinct().Count());
        Assert.False(AuditAreas.IsKnown("unknown"));
    }
}
