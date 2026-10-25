using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kyuplan.Api.Backup;

/// <summary>Lets one backup action that changes a database run at a time (a restore, an update of a backup database), and keeps the Spond sync out of a restore.</summary>
public sealed class BackupGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

    public void Release() => _gate.Release();
}

/// <summary>What a backup file says about itself (<c>manifest.json</c>).</summary>
/// <param name="Format">Version of the file layout; this program reads <see cref="BackupService.FormatVersion"/> and older.</param>
/// <param name="PlannerSchema">Name of the newest migration of the planner database (without its timestamp): a backup of a newer program is refused.</param>
internal sealed record BackupManifest(int Format, DateTimeOffset CreatedUtc, string Provider, string PlannerSchema, string AccountsSchema, int Rows);

/// <summary>
/// Backups of both databases. A backup is a zip file (<c>.kyubackup</c>): <c>manifest.json</c> and one JSON file per table
/// (<c>planner/Members.json</c>, <c>accounts/Users.json</c>, ...), see <see cref="DatabaseCopier"/>. Because it holds model data, not SQL, a backup of a SQLite
/// instance restores into a PostgreSQL one and back. It carries everything in the two databases (members, exams, comments, the branding as saved, users with their
/// password hashes); it does not carry the settings file, uploaded theme packs or the backup databases' definitions: those are files, not data.
/// The same copy keeps a backup database (<see cref="BackupTargetDto"/>) up to date.
/// </summary>
public sealed class BackupService(
    PlannerDbContext planner,
    AccountsDbContext accounts,
    IOptions<DatabaseOptions> database,
    IOptions<AccountsOptions> accountsOptions,
    IOptions<BackupOptions> options,
    IWebHostEnvironment environment,
    SettingsFile settings,
    BackupGate gate,
    SyncGate sync,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const int FormatVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string PlannerFolder = "planner/";
    private const string AccountsFolder = "accounts/";

    private sealed record Content(Dictionary<string, List<JsonObject>> Planner, Dictionary<string, List<JsonObject>> Accounts)
    {
        public int Rows => Planner.Values.Sum(r => r.Count) + Accounts.Values.Sum(r => r.Count);
    }

    // --- Files -------------------------------------------------------------------------------------------------

    /// <summary>Writes a backup of everything as a zip file, encrypted with the passphrase when there is one (<see cref="BackupEncryption"/>).</summary>
    public async Task WriteAsync(Stream output, string? passphrase, CancellationToken cancellationToken)
    {
        if (BackupRules.CheckPassphrase(passphrase) is { } problem) throw new InvalidInputException(problem);

        var content = await ReadAllAsync(planner, accounts, cancellationToken);
        var manifest = new BackupManifest(FormatVersion, time.GetUtcNow(), database.Value.Provider.ToString(), SchemaOf(planner), SchemaOf(accounts), content.Rows);

        // Packed in memory (the content is there anyway) and then sent: a zip archive closes with synchronous writes, which a response body does not allow.
        using var packed = new MemoryStream();
        using (var zip = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, ManifestEntry, manifest, cancellationToken);
            foreach (var (name, rows) in content.Planner) await WriteEntryAsync(zip, $"{PlannerFolder}{name}.json", rows, cancellationToken);
            foreach (var (name, rows) in content.Accounts) await WriteEntryAsync(zip, $"{AccountsFolder}{name}.json", rows, cancellationToken);
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            packed.Position = 0;
            await packed.CopyToAsync(output, cancellationToken);
            return;
        }

        await output.WriteAsync(BackupEncryption.Encrypt(packed.ToArray(), passphrase), cancellationToken);
    }

    /// <summary>The file name a downloaded backup gets.</summary>
    public string FileName() => $"kyuplan-backup-{time.GetUtcNow():yyyyMMdd-HHmmss}{BackupRules.Extension}";

    /// <summary>
    /// Replaces everything in both databases with the backup file's data. Throws <see cref="InvalidInputException"/> before anything is changed when the
    /// file is no backup this program can read or none of its users is an administrator (the API would otherwise start in setup mode, open to
    /// whoever comes first, and the administrators of the running instance are gone); a failure while writing undoes everything (one transaction per database).
    /// </summary>
    /// <param name="requireAdministrator">Whether the backup must hold an administrator; only the initial data of a new instance, which is set up afterwards anyway, may do without.</param>
    public async Task<BackupRestoredDto> RestoreAsync(Stream file, string? passphrase, CancellationToken cancellationToken, bool requireAdministrator = true)
    {
        var (manifest, content) = Read(await DecryptAsync(file, passphrase, cancellationToken));
        if (requireAdministrator) CheckHasAdministrator(content.Accounts);
        var rows = content.Rows;

        await gate.WaitAsync(cancellationToken);
        await sync.WaitAsync(cancellationToken);
        try
        {
            // The audit log is the record of what was done to this instance: an older backup must not wipe the period since it was made.
            await KeepAuditLogAsync(content, cancellationToken);

            // Two databases cannot share a transaction: each part is undone on its own failure, and when the planner data fails after the accounts
            // were replaced, the accounts are put back (users and Member.UserId links must stay a pair).
            var previousAccounts = await ReadTablesAsync(accounts, cancellationToken);
            await DatabaseCopier.ReplaceAsync(accounts, content.Accounts, cancellationToken);
            try
            {
                await DatabaseCopier.ReplaceAsync(planner, content.Planner, cancellationToken);
            }
            catch
            {
                try
                {
                    await DatabaseCopier.ReplaceAsync(accounts, previousAccounts, CancellationToken.None);
                }
                catch (Exception undo)
                {
                    logger.LogCritical(undo, "The restore failed and the accounts could not be put back: users and planner data do not match.");
                    throw new InvalidOperationException(
                        "The restore failed and the user accounts could not be put back: they now come from the backup while the planner data is the old one. Restore the backup again or an older one.", undo);
                }

                throw;
            }
        }
        finally
        {
            sync.Release();
            gate.Release();
        }

        logger.LogWarning("All data was replaced with the backup of {Created:u} ({Rows} rows).", manifest.CreatedUtc, rows);
        return new BackupRestoredDto(manifest.CreatedUtc, rows);
    }

    private const string AuditTable = nameof(AuditEntry);
    private static readonly string[] AuditColumns = ["At", "UserId", "UserName", "Area", "Action", "Target", "Details"];

    /// <summary>
    /// Puts the entries of the audit log the instance has now into the backup's content, next to the ones the backup has (an entry both hold counts once), in
    /// the order of their time and numbered anew. So a restore never erases an entry, whichever backup it takes, and the restore itself is added afterwards.
    /// </summary>
    private async Task KeepAuditLogAsync(Content content, CancellationToken cancellationToken)
    {
        var type = DatabaseCopier.Tables(planner).Single(t => DatabaseCopier.NameOf(t) == AuditTable);
        var current = await DatabaseCopier.ReadAsync(planner, type, cancellationToken);
        if (current.Count == 0) return;

        // The time is compared as an instant: a PostgreSQL backup writes it with another offset than a SQLite one.
        static string Key(JsonObject row) => string.Join('\u001f',AuditColumns.Select(column =>
            column == "At" && row[column]?.Deserialize<DateTimeOffset>() is { } at ? at.UtcTicks.ToString() : row[column]?.ToJsonString() ?? ""));
        static long Ticks(JsonObject row) => row["At"]?.Deserialize<DateTimeOffset>().UtcTicks ?? 0;

        var restored = content.Planner.GetValueOrDefault(AuditTable) ?? [];
        var known = restored.Select(Key).ToHashSet();
        var merged = restored.Concat(current.Where(row => known.Add(Key(row)))).OrderBy(Ticks).ToList(); // a stable sort: entries of one instant keep their order
        for (var i = 0; i < merged.Count; i++) merged[i]["Id"] = i + 1;
        content.Planner[AuditTable] = merged;
    }

    /// <summary>
    /// Loads <see cref="BackupOptions.InitialDataFile"/> into the two databases when this start created both and the file exists.
    /// Returns whether it did; then the default belts and roles are not added (the backup has what its club made of them).
    /// </summary>
    public async Task<bool> LoadInitialDataAsync(CancellationToken cancellationToken)
    {
        if (options.Value.InitialDataFile is not { Length: > 0 } configured) return false;

        var path = Path.GetFullPath(configured, environment.ContentRootPath);
        if (!File.Exists(path))
        {
            logger.LogWarning("The initial data file {Path} does not exist: the instance starts empty.", path);
            return false;
        }

        await using var file = File.OpenRead(path);
        await RestoreAsync(file, options.Value.InitialDataPassphrase, cancellationToken, requireAdministrator: false);
        return true;
    }

    /// <summary>
    /// A backup without a user who holds the administrator role would leave the restored instance in setup mode (open to the first visitor) and lock the
    /// current administrators out. Missing tables count as empty, as <see cref="DatabaseCopier.ReplaceAsync"/> empties them.
    /// </summary>
    private static void CheckHasAdministrator(Dictionary<string, List<JsonObject>> accounts)
    {
        List<JsonObject> Rows(string table) => accounts.GetValueOrDefault(table) ?? [];

        var users = Rows("UserAccount").Select(u => IntOf(u["Id"])).OfType<int>().ToHashSet();
        if (users.Count == 0) throw new InvalidInputException("This backup holds no users: restoring it would leave nobody who could sign in. Nothing was changed.");

        var administratorRoles = Rows("Role").Where(r => r["IsSystem"] is JsonValue flag && flag.TryGetValue<bool>(out var system) && system)
            .Select(r => IntOf(r["Id"])).OfType<int>().ToHashSet();
        if (administratorRoles.Count == 0) throw new InvalidInputException("This backup has no administrator role. Nothing was changed.");

        if (!Rows("UserRoles").Any(link => IntOf(link["RolesId"]) is { } role && administratorRoles.Contains(role) && IntOf(link["UsersId"]) is { } user && users.Contains(user)))
            throw new InvalidInputException("In this backup no user is an administrator: restoring it would lock everybody out of the settings. Nothing was changed.");
    }

    private static int? IntOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>The file as a plain zip: an encrypted one is decrypted (in memory, it is at most the upload limit), a plain one is passed on.</summary>
    private static async Task<Stream> DecryptAsync(Stream file, string? passphrase, CancellationToken cancellationToken)
    {
        var head = new byte[8];
        var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        if (file.CanSeek) file.Position = 0;
        else throw new InvalidOperationException("The backup stream must be seekable.");
        if (!BackupEncryption.IsEncrypted(head.AsSpan(0, read))) return file;

        if (string.IsNullOrEmpty(passphrase)) throw new InvalidInputException("This backup file is encrypted: enter its passphrase.");

        using var all = new MemoryStream();
        await file.CopyToAsync(all, cancellationToken);
        return new MemoryStream(BackupEncryption.Decrypt(all.ToArray(), passphrase));
    }

    private (BackupManifest Manifest, Content Content) Read(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.GetEntry(ManifestEntry) is not { } manifestEntry) throw new InvalidInputException("This is not a backup file (manifest.json is missing).");
            var limit = options.Value.MaxUnpackedMegabytes * 1024L * 1024L;
            if (zip.Entries.Sum(e => e.Length) > limit) throw new InvalidInputException($"The backup holds more than {options.Value.MaxUnpackedMegabytes} MB of data.");

            // The sizes in the zip's directory can be understated: what is really unpacked is counted too.
            var budget = new UnpackBudget(limit, options.Value.MaxUnpackedMegabytes);

            var manifest = ReadJson<BackupManifest>(manifestEntry, budget) ?? throw new InvalidInputException("The backup's manifest is empty.");
            if (manifest.Format is < 1 or > FormatVersion) throw new InvalidInputException($"The backup has format {manifest.Format}; this program reads up to {FormatVersion}. Update the program first.");
            if (!Knows(planner, manifest.PlannerSchema) || !Knows(accounts, manifest.AccountsSchema))
                throw new InvalidInputException("The backup was made by a newer version of the program than this one. Update the program first.");

            var content = new Content(ReadTables(zip, PlannerFolder, planner, budget), ReadTables(zip, AccountsFolder, accounts, budget));
            return (manifest, content);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidInputException($"This is no readable backup file: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new InvalidInputException($"The backup is damaged: {ex.Message}");
        }
    }

    private static Dictionary<string, List<JsonObject>> ReadTables(ZipArchive zip, string folder, DbContext db, UnpackBudget budget)
    {
        var tables = new Dictionary<string, List<JsonObject>>();
        foreach (var type in DatabaseCopier.Tables(db))
        {
            var name = DatabaseCopier.NameOf(type);
            if (zip.GetEntry($"{folder}{name}.json") is not { } entry) continue; // a table that did not exist when the backup was made

            tables[name] = ReadJson<List<JsonObject>>(entry, budget) ?? throw new InvalidInputException($"The backup's table {name} is empty.");
        }

        return tables;
    }

    private static T? ReadJson<T>(ZipArchiveEntry entry, UnpackBudget budget)
    {
        using var stream = new CountingStream(entry.Open(), budget);
        return JsonSerializer.Deserialize<T>(stream);
    }

    private sealed class UnpackBudget(long remaining, int megabytes)
    {
        public void Take(int bytes)
        {
            if ((remaining -= bytes) < 0) throw new InvalidInputException($"The backup holds more than {megabytes} MB of data.");
        }
    }

    /// <summary>Reads through to the entry and refuses once all entries together unpacked more than the budget.</summary>
    private sealed class CountingStream(Stream inner, UnpackBudget budget) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        private int Counted(int read)
        {
            budget.Take(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task WriteEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken cancellationToken)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
    }

    /// <summary>The name of the newest migration without its timestamp: the same for the SQLite and the PostgreSQL twin.</summary>
    private static string SchemaOf(DbContext db) => NameOfMigration(db.Database.GetMigrations().LastOrDefault() ?? "");

    private static bool Knows(DbContext db, string schema) =>
        schema.Length == 0 || db.Database.GetMigrations().Select(NameOfMigration).Contains(schema);

    private static string NameOfMigration(string id) => id[(id.IndexOf('_') + 1)..];

    private static async Task<Content> ReadAllAsync(DbContext planner, DbContext accounts, CancellationToken cancellationToken) =>
        new(await ReadTablesAsync(planner, cancellationToken), await ReadTablesAsync(accounts, cancellationToken));

    private static async Task<Dictionary<string, List<JsonObject>>> ReadTablesAsync(DbContext db, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, List<JsonObject>>();
        foreach (var type in DatabaseCopier.Tables(db)) tables[DatabaseCopier.NameOf(type)] = await DatabaseCopier.ReadAsync(db, type, cancellationToken);
        return tables;
    }

    // --- Backup databases ---------------------------------------------------------------------------------------

    /// <summary>Replaces everything in the backup database with what the databases hold now. Throws when it cannot (the message says why).</summary>
    public async Task CopyToAsync(BackupTargetDto target, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var content = await ReadAllAsync(planner, accounts, cancellationToken);

            var (targetPlanner, targetAccounts) = Open(target);
            await using (targetPlanner)
            await using (targetAccounts)
            {
                await targetPlanner.EnsureUpToDateAsync(cancellationToken);
                await targetAccounts.EnsureUpToDateAsync(cancellationToken);
                await DatabaseCopier.ReplaceAsync(targetAccounts, content.Accounts, cancellationToken);
                await DatabaseCopier.ReplaceAsync(targetPlanner, content.Planner, cancellationToken);

                // The restore test: what a restore from this database would read must be what was written.
                var problems = Differences("planner", content.Planner, await ReadTablesAsync(targetPlanner, cancellationToken))
                    .Concat(Differences("accounts", content.Accounts, await ReadTablesAsync(targetAccounts, cancellationToken))).ToList();
                if (problems.Count > 0) throw new InvalidOperationException($"Restore test failed: {string.Join("; ", problems.Take(5))}.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Tables whose rows read back from the backup database are not the rows that were written (compared as sets of JSON rows).</summary>
    private static IEnumerable<string> Differences(string database, Dictionary<string, List<JsonObject>> written, Dictionary<string, List<JsonObject>> readBack)
    {
        foreach (var (table, rows) in written)
        {
            var back = readBack.GetValueOrDefault(table) ?? [];
            if (rows.Count != back.Count) yield return $"{database}/{table} has {back.Count} rows instead of {rows.Count}";
            else if (!rows.Select(r => r.ToJsonString()).Order(StringComparer.Ordinal).SequenceEqual(back.Select(r => r.ToJsonString()).Order(StringComparer.Ordinal)))
                yield return $"{database}/{table} differs from the original";
        }
    }

    /// <summary>Opens the two databases of a backup database. Refuses one that is, or would overwrite, the live data.</summary>
    private (PlannerDbContext Planner, AccountsDbContext Accounts) Open(BackupTargetDto target)
    {
        var root = environment.ContentRootPath;
        if (target.Kind == BackupTargetKind.Sqlite)
        {
            var folder = BackupFolder.Resolve(target.Location, settings.Path, root, environment.WebRootPath);
            var plannerFile = Path.Combine(folder, "planner.db");
            var accountsFile = Path.Combine(folder, "accounts.db");
            foreach (var live in new[] { database.Value.Provider == DatabaseProvider.Sqlite ? database.Value.ConnectionString : null, accountsOptions.Value.ConnectionString })
            {
                if (live is null) continue;

                var file = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(live).DataSource;
                if (file.Length == 0) continue;

                // The folder is resolved through links, so the live file has to be as well (a data folder that is a link or mount would hide the clash).
                var liveFile = BackupFolder.Real(Path.GetFullPath(file, root));
                if (SamePath(liveFile, plannerFile) || SamePath(liveFile, accountsFile))
                    throw new InvalidInputException("This folder holds the live database: a backup database must be a different one.");
            }

            Directory.CreateDirectory(folder);
            return (
                new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite($"Data Source={plannerFile}").Options),
                new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite($"Data Source={accountsFile}").Options));
        }

        var connection = DatabaseConnections.Resolve(DatabaseProvider.PostgreSql, target.Location, root);
        if (database.Value.Provider == DatabaseProvider.PostgreSql && (SamePostgres(connection, database.Value.ConnectionString) || SamePostgres(connection, accountsOptions.Value.ConnectionString)))
            throw new InvalidInputException("This is the live database: a backup database must be a different one.");

        return (
            new PlannerPostgresDbContext(new DbContextOptionsBuilder<PlannerPostgresDbContext>().UseNpgsql(connection).Options),
            new AccountsPostgresDbContext(new DbContextOptionsBuilder<AccountsPostgresDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(DatabaseRegistration.AccountsHistoryTable)).Options));
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SamePostgres(string a, string b)
    {
        var x = new NpgsqlConnectionStringBuilder(a);
        var y = new NpgsqlConnectionStringBuilder(b);
        return string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port && string.Equals(x.Database, y.Database, StringComparison.Ordinal);
    }
}
