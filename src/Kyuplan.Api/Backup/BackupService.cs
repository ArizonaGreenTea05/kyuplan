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
    IHostEnvironment environment,
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

    /// <summary>Writes a backup of everything as a zip file.</summary>
    public async Task WriteAsync(Stream output, CancellationToken cancellationToken)
    {
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

        packed.Position = 0;
        await packed.CopyToAsync(output, cancellationToken);
    }

    /// <summary>The file name a downloaded backup gets.</summary>
    public string FileName() => $"kyuplan-backup-{time.GetUtcNow():yyyyMMdd-HHmmss}{BackupRules.Extension}";

    /// <summary>
    /// Replaces everything in both databases with the backup file's data. Throws <see cref="InvalidInputException"/> before anything is changed when the
    /// file is no backup this program can read; a failure while writing undoes everything (one transaction per database).
    /// </summary>
    public async Task<BackupRestoredDto> RestoreAsync(Stream file, CancellationToken cancellationToken)
    {
        var (manifest, content) = Read(file);

        await gate.WaitAsync(cancellationToken);
        await sync.WaitAsync(cancellationToken);
        try
        {
            // The accounts first: the planner's rows do not depend on them, so a failed second step leaves users that do not match the planner data at worst;
            // each part is undone on its own failure.
            await DatabaseCopier.ReplaceAsync(accounts, content.Accounts, cancellationToken);
            await DatabaseCopier.ReplaceAsync(planner, content.Planner, cancellationToken);
        }
        finally
        {
            sync.Release();
            gate.Release();
        }

        logger.LogWarning("All data was replaced with the backup of {Created:u} ({Rows} rows).", manifest.CreatedUtc, content.Rows);
        return new BackupRestoredDto(manifest.CreatedUtc, content.Rows);
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
        await RestoreAsync(file, cancellationToken);
        return true;
    }

    private (BackupManifest Manifest, Content Content) Read(Stream file)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.GetEntry(ManifestEntry) is not { } manifestEntry) throw new InvalidInputException("This is not a backup file (manifest.json is missing).");
            var limit = options.Value.MaxUnpackedMegabytes * 1024L * 1024L;
            if (zip.Entries.Sum(e => e.Length) > limit) throw new InvalidInputException($"The backup holds more than {options.Value.MaxUnpackedMegabytes} MB of data.");

            var manifest = ReadJson<BackupManifest>(manifestEntry) ?? throw new InvalidInputException("The backup's manifest is empty.");
            if (manifest.Format is < 1 or > FormatVersion) throw new InvalidInputException($"The backup has format {manifest.Format}; this program reads up to {FormatVersion}. Update the program first.");
            if (!Knows(planner, manifest.PlannerSchema) || !Knows(accounts, manifest.AccountsSchema))
                throw new InvalidInputException("The backup was made by a newer version of the program than this one. Update the program first.");

            var content = new Content(ReadTables(zip, PlannerFolder, planner), ReadTables(zip, AccountsFolder, accounts));
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

    private static Dictionary<string, List<JsonObject>> ReadTables(ZipArchive zip, string folder, DbContext db)
    {
        var tables = new Dictionary<string, List<JsonObject>>();
        foreach (var type in DatabaseCopier.Tables(db))
        {
            var name = DatabaseCopier.NameOf(type);
            if (zip.GetEntry($"{folder}{name}.json") is not { } entry) continue; // a table that did not exist when the backup was made

            tables[name] = ReadJson<List<JsonObject>>(entry) ?? throw new InvalidInputException($"The backup's table {name} is empty.");
        }

        return tables;
    }

    private static T? ReadJson<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream);
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
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Opens the two databases of a backup database. Refuses one that is, or would overwrite, the live data.</summary>
    private (PlannerDbContext Planner, AccountsDbContext Accounts) Open(BackupTargetDto target)
    {
        var root = environment.ContentRootPath;
        if (target.Kind == BackupTargetKind.Sqlite)
        {
            var folder = Path.GetFullPath(target.Location, root);
            var plannerFile = Path.Combine(folder, "planner.db");
            var accountsFile = Path.Combine(folder, "accounts.db");
            foreach (var live in new[] { database.Value.Provider == DatabaseProvider.Sqlite ? database.Value.ConnectionString : null, accountsOptions.Value.ConnectionString })
            {
                if (live is null) continue;

                var file = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(live).DataSource;
                if (file.Length > 0 && (SamePath(Path.GetFullPath(file, root), plannerFile) || SamePath(Path.GetFullPath(file, root), accountsFile)))
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
