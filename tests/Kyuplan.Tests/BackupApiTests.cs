using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Backup;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Backups against the real API with real (in-memory) SQLite databases: the file that is downloaded and uploaded, the initial data of a new instance,
/// and the backup databases that are kept up to date. PostgreSQL needs a server and is not covered here.
/// </summary>
public sealed class BackupApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-backup-").FullName;
    private readonly CountingRestarter _restarter = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private WebApplicationFactory<Program> CreateHost(InMemoryDatabase? planner = null, InMemoryDatabase? accounts = null, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Backup:Enabled", "false"); // the tests run the backup databases themselves
            builder.UseSetting("Backup:TargetsFile", Path.Combine(_folder, "targets.json"));
            builder.UseSetting("Database:ConnectionString", (planner ?? _planner).ConnectionString);
            Configure(builder, accounts ?? _accounts);
            builder.ConfigureServices(services => services.AddSingleton<IAppRestarter>(_restarter));
            configure?.Invoke(builder);
        });

    private static void Configure(IWebHostBuilder builder, InMemoryDatabase accounts) => TestAccounts.Configure(builder, accounts);

    private static BeltInputDto Belt(string name) => new(name, "#112233", null, []);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<List<string>> BeltNamesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!.Select(b => b.Name).Order()];

    private static async Task<byte[]> DownloadAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/backup/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith(BackupRules.Extension, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] file) => client.PostAsync("/api/backup/restore", new ByteArrayContent(file));

    private static BackupTargetInputDto Target(string name, string folder, int minutes = 60, bool enabled = true) =>
        new(name, BackupTargetKind.Sqlite, folder, minutes, enabled);

    // --- Who may -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_backup_route_needs_its_own_permission()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);
        var anonymous = host.CreateClient();

        foreach (var client in new[] { anonymous, anna })
        {
            var denied = client == anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(denied, (await client.GetAsync("/api/backup/targets")).StatusCode);
            Assert.Equal(denied, (await client.PostAsJsonAsync("/api/backup/targets", Target("x", _folder))).StatusCode);
            Assert.Equal(denied, (await client.PostAsync("/api/backup/targets/1/run", null)).StatusCode);
            Assert.Equal(denied, (await client.GetAsync("/api/backup/download")).StatusCode);
            Assert.Equal(denied, (await UploadAsync(client, [1, 2, 3])).StatusCode);
        }

        // Viewing is not downloading: the file holds every password hash.
        var viewer = await admin.PostAsJsonAsync("/api/users", new UserInputDto("viewer", UserPassword, null, null, [], [Permission.BackupsView]));
        Assert.Equal(HttpStatusCode.Created, viewer.StatusCode);
        var viewing = host.CreateSignedInClient("viewer", UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await viewing.GetAsync("/api/backup/targets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewing.GetAsync("/api/backup/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(viewing, [1])).StatusCode);
    }

    // --- The file ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_downloaded_backup_restores_everything_it_held_and_nothing_that_came_later()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("Yellow")));
        var file = await DownloadAsync(admin);

        // After the backup: a belt is added, one removed, a user deleted.
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("Green")));
        var white = (await admin.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!.Single(b => b.Name == "White");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/belts/{white.Id}")).StatusCode);
        var users = (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{users.Single(u => u.UserName == "anna").Id}")).StatusCode);

        var restored = await ReadAsync<BackupRestoredDto>(await UploadAsync(admin, file));

        Assert.True(restored.Rows > 0);
        Assert.Equal(1, _restarter.Requests); // the API starts again from the new data
        Assert.Equal(["White", "Yellow"], await BeltNamesAsync(admin));
        Assert.Contains((await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!, u => u.UserName == "anna");
        // The restored user can sign in with the password they had.
        Assert.Equal(HttpStatusCode.OK, (await host.CreateSignedInClient("anna", UserPassword).GetAsync("/api/account")).StatusCode);

        // A new row gets an id that is free (the counters were reset).
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("Orange")));
        Assert.Equal(["Orange", "White", "Yellow"], await BeltNamesAsync(admin));
    }

    [Fact]
    public async Task A_file_that_is_no_backup_is_refused_and_changes_nothing()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));

        foreach (var junk in new byte[][] { [], [1, 2, 3], "not a zip at all"u8.ToArray() })
        {
            var response = await UploadAsync(admin, junk);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // A zip without the manifest.
        using var empty = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(empty, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("hello.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, empty.ToArray())).StatusCode);

        Assert.Equal(["White"], await BeltNamesAsync(admin));
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task A_backup_of_a_newer_program_is_refused()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var file = await DownloadAsync(admin);

        using var tampered = new MemoryStream();
        using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(file)))
        using (var zip = new System.IO.Compression.ZipArchive(tampered, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                if (entry.FullName == "manifest.json") text = text.Replace("\"PlannerSchema\":\"", "\"PlannerSchema\":\"AFutureMigration");
                await using var writer = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
                await writer.WriteAsync(text);
            }
        }

        var response = await UploadAsync(admin, tampered.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("newer version", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_instance_can_start_from_a_backup_file()
    {
        byte[] file;
        using (var source = CreateHost())
        {
            var admin = source.CreateSignedInClient();
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
            await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));
            file = await DownloadAsync(admin);
        }

        var path = Path.Combine(_folder, "start.kyubackup");
        await File.WriteAllBytesAsync(path, file);
        using var newPlanner = new InMemoryDatabase();
        using var newAccounts = new InMemoryDatabase();

        // The new instance would start without a user (and ask to be set up): from the file it has the users of the old one.
        using var fresh = CreateHost(newPlanner, newAccounts, builder =>
        {
            builder.UseSetting("Backup:InitialDataFile", path);
            builder.UseSetting("Accounts:InitialAdminPassword", "");
        });

        var anna = fresh.CreateSignedInClient("anna", UserPassword);
        Assert.Equal(["White"], await BeltNamesAsync(anna));
    }

    /// <summary>The backup with one entry replaced by <paramref name="edit"/>'s result (<c>null</c> = the entry is left out).</summary>
    private static byte[] Rewrite(byte[] file, string entryName, Func<string, string?> edit)
    {
        using var result = new MemoryStream();
        var found = false;
        using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(file)))
        using (var zip = new System.IO.Compression.ZipArchive(result, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                if (entry.FullName == entryName)
                {
                    found = true;
                    if (edit(text) is not { } edited) continue;
                    text = edited;
                }

                using var writer = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
                writer.Write(text);
            }
        }

        Assert.True(found, $"{entryName} is not in the backup");
        return result.ToArray();
    }

    private static string WithoutAdministratorRole(string rolesJson)
    {
        var roles = System.Text.Json.Nodes.JsonNode.Parse(rolesJson)!.AsArray();
        foreach (var role in roles) role!["IsSystem"] = false;
        return roles.ToJsonString();
    }

    /// <summary>The rows of a table with <paramref name="column"/> set to <paramref name="id"/> in every row.</summary>
    private static string WithColumn(string tableJson, string column, int id)
    {
        var rows = System.Text.Json.Nodes.JsonNode.Parse(tableJson)!.AsArray();
        foreach (var row in rows) row![column] = id;
        return rows.ToJsonString();
    }

    [Theory]
    [InlineData("empty Users")]
    [InlineData("missing Users")]
    [InlineData("no administrator role")]
    [InlineData("no administrator link")]
    [InlineData("link to a missing user")]
    [InlineData("link to a missing role")]
    public async Task A_backup_without_an_administrator_is_refused_and_the_setup_stays_closed(string variant)
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));
        var file = await DownloadAsync(admin);
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("Green"))); // after the backup

        var broken = variant switch
        {
            "empty Users" => Rewrite(file, "accounts/UserAccount.json", _ => "[]"),
            "missing Users" => Rewrite(file, "accounts/UserAccount.json", _ => null),
            "no administrator role" => Rewrite(file, "accounts/Role.json", WithoutAdministratorRole),
            "no administrator link" => Rewrite(file, "accounts/UserRoles.json", _ => "[]"),
            "link to a missing user" => Rewrite(file, "accounts/UserRoles.json", text => WithColumn(text, "UsersId", 99)),
            _ => Rewrite(file, "accounts/UserRoles.json", text => WithColumn(text, "RolesId", 99)),
        };
        Assert.NotEqual(file, broken);

        var response = await UploadAsync(admin, broken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Nothing was changed", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _restarter.Requests);
        Assert.Equal(["Green", "White"], await BeltNamesAsync(admin));
        Assert.Equal(HttpStatusCode.OK, (await host.CreateSignedInClient().GetAsync("/api/account")).StatusCode);
        var setup = await ReadAsync<SetupStatusDto>(await host.CreateClient().GetAsync("/api/setup"));
        Assert.False(setup.NeedsSetup);
    }

    [Fact]
    public async Task The_initial_data_of_a_new_instance_may_hold_no_users_and_the_instance_is_then_set_up()
    {
        byte[] file;
        using (var source = CreateHost())
        {
            var admin = source.CreateSignedInClient();
            await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));
            file = await DownloadAsync(admin);
            foreach (var table in new[] { "UserAccount", "UserAlias", "AccountName", "UserRoles" }) file = Rewrite(file, $"accounts/{table}.json", _ => "[]");
        }

        var path = Path.Combine(_folder, "no-users.kyubackup");
        await File.WriteAllBytesAsync(path, file);
        using var newPlanner = new InMemoryDatabase();
        using var newAccounts = new InMemoryDatabase();

        using var fresh = CreateHost(newPlanner, newAccounts, builder => builder.UseSetting("Backup:InitialDataFile", path));

        // The administrator comes from the configuration, the planner data from the file.
        Assert.Equal(["White"], await BeltNamesAsync(fresh.CreateSignedInClient()));
    }

    // --- Backup databases --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_PostgreSQL_password_is_never_returned_and_survives_an_update_that_sends_the_mask_back()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        const string location = "Host=db;Database=backup;Username=kyu;Password=s3cret!";

        var created = await ReadAsync<BackupTargetDto>(await admin.PostAsJsonAsync("/api/backup/targets",
            new BackupTargetInputDto("Pg", BackupTargetKind.PostgreSql, location, 60, false)));
        Assert.DoesNotContain("s3cret", created.Location);
        Assert.Contains(BackupTargetStore.PasswordMask, created.Location);

        var listed = Assert.Single((await admin.GetFromJsonAsync<List<BackupTargetDto>>("/api/backup/targets"))!);
        Assert.DoesNotContain("s3cret", listed.Location);

        var updated = await ReadAsync<BackupTargetDto>(await admin.PutAsJsonAsync($"/api/backup/targets/{created.Id}",
            new BackupTargetInputDto("Pg", BackupTargetKind.PostgreSql, listed.Location, 90, false)));
        Assert.Equal(90, updated.IntervalMinutes);

        // The stored password is still the old one.
        using var again = CreateHost();
        var stored = again.Services.GetRequiredService<BackupTargetStore>().Find(created.Id)!;
        Assert.Contains("s3cret!", stored.Location);
    }

    [Fact]
    public async Task A_backup_that_unpacks_to_more_than_it_declares_is_refused()
    {
        using var host = CreateHost(configure: b => b.UseSetting("Backup:MaxUnpackedMegabytes", "1"));
        var admin = host.CreateSignedInClient();
        var file = await (await admin.GetAsync("/api/backup/download")).Content.ReadAsByteArrayAsync();

        // Same entries, but the manifest is padded with 2 MB of whitespace, then the directory sizes are understated.
        using var tampered = new MemoryStream();
        using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(file)))
        using (var zip = new System.IO.Compression.ZipArchive(tampered, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                await using var writer = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
                using var reader = new StreamReader(entry.Open());
                await writer.WriteAsync(await reader.ReadToEndAsync());
                if (entry.FullName == "manifest.json") await writer.WriteAsync(new string(' ', 2 * 1024 * 1024));
            }
        }

        var bytes = tampered.ToArray();
        // Central directory records hold the uncompressed size at offset 24: set it to 1 for every record.
        for (var i = 0; i + 46 < bytes.Length; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                BitConverter.GetBytes(1u).CopyTo(bytes, i + 24);

        var response = await admin.PostAsync("/api/backup/restore", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Backup_databases_are_defined_changed_and_deleted_and_survive_a_restart()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var created = await ReadAsync<BackupTargetDto>(await admin.PostAsJsonAsync("/api/backup/targets", Target("Nightly", Path.Combine(_folder, "a"), 60)));
        Assert.Equal("Nightly", created.Name);
        Assert.Null(created.LastRunUtc);
        Assert.NotNull(created.NextRunUtc); // never ran: due now

        var changed = await ReadAsync<BackupTargetDto>(await admin.PutAsJsonAsync($"/api/backup/targets/{created.Id}", Target("Nightly", Path.Combine(_folder, "a"), 120, enabled: false)));
        Assert.Equal(120, changed.IntervalMinutes);
        Assert.Null(changed.NextRunUtc); // switched off

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/backup/targets", Target("nightly", Path.Combine(_folder, "b")))).StatusCode); // same name
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/backup/targets", Target("Fast", _folder, minutes: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/backup/targets", Target(" ", _folder))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/backup/targets/99", Target("x", _folder))).StatusCode);

        using var again = CreateHost();
        Assert.Equal("Nightly", Assert.Single((await again.CreateSignedInClient().GetFromJsonAsync<List<BackupTargetDto>>("/api/backup/targets"))!).Name);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/backup/targets/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/backup/targets/{created.Id}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<BackupTargetDto>>("/api/backup/targets"))!);
    }

    [Fact]
    public async Task Running_a_backup_database_copies_everything_and_the_next_run_replaces_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var folder = Path.Combine(_folder, "mirror");
        var target = await ReadAsync<BackupTargetDto>(await admin.PostAsJsonAsync("/api/backup/targets", Target("Mirror", folder)));
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));

        var first = await ReadAsync<BackupTargetDto>(await admin.PostAsync($"/api/backup/targets/{target.Id}/run", null));

        Assert.True(first.LastSucceeded, first.LastMessage);
        Assert.NotNull(first.LastRunUtc);
        Assert.Equal(["White"], BeltNamesIn(folder));
        Assert.Contains("admin", UserNamesIn(folder));

        var white = (await admin.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!.Single();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/belts/{white.Id}")).StatusCode);
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("Black")));
        await ReadAsync<BackupTargetDto>(await admin.PostAsync($"/api/backup/targets/{target.Id}/run", null));

        Assert.Equal(["Black"], BeltNamesIn(folder));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/backup/targets/99/run", null)).StatusCode);
    }

    private static async Task<HttpResponseMessage> WithPassphraseAsync(HttpClient client, HttpMethod method, string url, string? passphrase, byte[]? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : new ByteArrayContent(body) };
        if (passphrase is not null) request.Headers.Add(BackupRules.PassphraseHeader, BackupRules.EncodePassphrase(passphrase));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task An_encrypted_backup_is_not_a_zip_and_restores_only_with_its_passphrase()
    {
        const string passphrase = "correct horse ü battery";
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await ReadAsync<BeltDto>(await admin.PostAsJsonAsync("/api/belts", Belt("White")));

        var response = await WithPassphraseAsync(admin, HttpMethod.Get, "/api/backup/download", passphrase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadAsByteArrayAsync();
        Assert.True(BackupEncryption.IsEncrypted(file));
        Assert.NotEqual((byte)'P', file[0]); // not the start of a zip

        var white = (await admin.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!.Single();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/belts/{white.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, file)).StatusCode); // no passphrase
        Assert.Equal(HttpStatusCode.BadRequest, (await WithPassphraseAsync(admin, HttpMethod.Post, "/api/backup/restore", "wrong passphrase", file)).StatusCode);
        Assert.Empty(await BeltNamesAsync(admin));
        Assert.Equal(0, _restarter.Requests);

        await ReadAsync<BackupRestoredDto>(await WithPassphraseAsync(admin, HttpMethod.Post, "/api/backup/restore", passphrase, file));
        Assert.Equal(["White"], await BeltNamesAsync(admin));
    }

    [Fact]
    public async Task A_passphrase_that_is_too_short_is_refused_and_a_damaged_encrypted_file_is_not_read()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await WithPassphraseAsync(admin, HttpMethod.Get, "/api/backup/download", "short")).StatusCode);

        var file = await (await WithPassphraseAsync(admin, HttpMethod.Get, "/api/backup/download", "long enough phrase")).Content.ReadAsByteArrayAsync();
        file[^1] ^= 0xFF;
        Assert.Equal(HttpStatusCode.BadRequest, (await WithPassphraseAsync(admin, HttpMethod.Post, "/api/backup/restore", "long enough phrase", file)).StatusCode);
    }

    [Fact]
    public async Task A_run_that_fails_is_reported_on_the_backup_database_and_not_as_an_error()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var blocked = Path.Combine(_folder, "a-file");
        await File.WriteAllTextAsync(blocked, "this is a file, not a folder");
        var target = await ReadAsync<BackupTargetDto>(await admin.PostAsJsonAsync("/api/backup/targets", Target("Broken", blocked)));

        var result = await ReadAsync<BackupTargetDto>(await admin.PostAsync($"/api/backup/targets/{target.Id}/run", null));

        Assert.False(result.LastSucceeded);
        Assert.False(string.IsNullOrWhiteSpace(result.LastMessage));
    }

    [Fact]
    public async Task The_scheduler_updates_only_what_is_due()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var store = host.Services.GetRequiredService<BackupTargetStore>();
        var due = store.Add(Target("Due", Path.Combine(_folder, "due")));
        store.Add(Target("Off", Path.Combine(_folder, "off"), enabled: false));
        var recent = store.Add(Target("Recent", Path.Combine(_folder, "recent")));
        store.RecordRun(recent.Id, true, null);

        Assert.Equal([due.Id], store.Due().Select(t => t.Id));

        var runner = host.Services.GetRequiredService<BackupRunner>();
        await runner.RunAsync(due.Id, CancellationToken.None);

        Assert.Empty(store.Due());
        Assert.True(Directory.Exists(Path.Combine(_folder, "due")));
        Assert.False(Directory.Exists(Path.Combine(_folder, "off")));
    }

    private static List<string> BeltNamesIn(string folder) => Query(Path.Combine(folder, "planner.db"), "SELECT Name FROM Belts ORDER BY Name");

    private static List<string> UserNamesIn(string folder) => Query(Path.Combine(folder, "accounts.db"), "SELECT UserName FROM Users");

    private static List<string> Query(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    // --- The copy knows every table ------------------------------------------------------------------------------------

    [Fact]
    public void The_copy_can_handle_every_table_of_both_databases()
    {
        using var host = CreateHost();
        using var scope = host.Services.CreateScope();

        // Throws for a table with a shadow property or without a property to read: such a table would be missing from backups.
        Assert.NotEmpty(DatabaseCopier.Tables(scope.ServiceProvider.GetRequiredService<PlannerDbContext>()));
        Assert.Contains(DatabaseCopier.Tables(scope.ServiceProvider.GetRequiredService<AccountsDbContext>()), t => DatabaseCopier.NameOf(t) == "UserRoles");
    }
}
