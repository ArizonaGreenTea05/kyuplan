using System.Text;
using System.Text.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Backup;

/// <summary>
/// The backup databases administrators defined (<see cref="BackupTargetDto"/>), in a file of their own (<see cref="BackupOptions.TargetsFile"/>):
/// they say where the data goes, so they are not part of the data and a backup does not carry them. The location is encrypted in the file (it may hold a database password).
/// </summary>
public sealed class BackupTargetStore(SettingsFile settings, IOptions<BackupOptions> options, TimeProvider time, IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();

    private sealed record Stored(
        int Id, string Name, BackupTargetKind Kind, string Location, int IntervalMinutes, bool Enabled,
        DateTimeOffset? LastRunUtc, bool? LastSucceeded, string? LastMessage);

    private string Path => options.Value.TargetsFile is { Length: > 0 } configured
        ? System.IO.Path.GetFullPath(configured)
        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settings.Path))!, "backup-targets.json");

    public IReadOnlyList<BackupTargetDto> List()
    {
        lock (_lock) return [.. Load().Select(ToDto)];
    }

    public BackupTargetDto? Find(int id)
    {
        lock (_lock) return Load().FirstOrDefault(t => t.Id == id) is { } found ? ToDto(found) : null;
    }

    /// <summary>What a PostgreSQL target's password is shown as: the API never hands it out, an update that sends it back keeps the stored one.</summary>
    public const string PasswordMask = "********";

    /// <summary>The target as the API shows it: a PostgreSQL connection string without its password.</summary>
    public static BackupTargetDto Mask(BackupTargetDto target)
    {
        if (target.Kind != BackupTargetKind.PostgreSql || !TryParse(target.Location, out var connection) || connection.Password is not { Length: > 0 }) return target;

        connection.Password = PasswordMask;
        return target with { Location = connection.ConnectionString };
    }

    private static bool TryParse(string location, out Npgsql.NpgsqlConnectionStringBuilder connection)
    {
        try
        {
            connection = new Npgsql.NpgsqlConnectionStringBuilder(location);
            return true;
        }
        catch (ArgumentException)
        {
            connection = null!;
            return false;
        }
    }

    /// <summary>The location to store: a password that is still <see cref="PasswordMask"/> is the one the target already has.</summary>
    private static string Unmask(string location, BackupTargetInputDto input, Stored existing)
    {
        if (input.Kind != BackupTargetKind.PostgreSql || existing.Kind != BackupTargetKind.PostgreSql
            || !TryParse(location, out var sent) || sent.Password != PasswordMask || !TryParse(existing.Location, out var old)) return location;

        sent.Password = old.Password;
        return sent.ConnectionString;
    }

    public BackupTargetDto Add(BackupTargetInputDto input)
    {
        Check(input);
        lock (_lock)
        {
            var all = Load();
            if (all.Count >= BackupRules.MaxTargets) throw new InvalidInputException($"At most {BackupRules.MaxTargets} backup databases can be defined.");
            CheckName(all, input, null);

            var created = new Stored(all.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1, input.Name.Trim(), input.Kind, input.Location.Trim(),
                input.IntervalMinutes, input.Enabled, null, null, null);
            Save([.. all, created]);
            return ToDto(created);
        }
    }

    /// <summary>Replaces the definition; what the last run reported is kept. <c>null</c> when there is no such target.</summary>
    public BackupTargetDto? Update(int id, BackupTargetInputDto input)
    {
        Check(input);
        lock (_lock)
        {
            var all = Load();
            if (all.FirstOrDefault(t => t.Id == id) is not { } existing) return null;
            CheckName(all, input, id);

            var changed = existing with
            {
                Name = input.Name.Trim(), Kind = input.Kind, Location = Unmask(input.Location.Trim(), input, existing), IntervalMinutes = input.IntervalMinutes, Enabled = input.Enabled,
            };
            Save([.. all.Select(t => t.Id == id ? changed : t)]);
            return ToDto(changed);
        }
    }

    public bool Delete(int id)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.All(t => t.Id != id)) return false;

            Save([.. all.Where(t => t.Id != id)]);
            return true;
        }
    }

    /// <summary>Remembers how a run went.</summary>
    public void RecordRun(int id, bool succeeded, string? message)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.FirstOrDefault(t => t.Id == id) is not { } existing) return; // deleted while it ran

            var changed = existing with { LastRunUtc = time.GetUtcNow(), LastSucceeded = succeeded, LastMessage = succeeded ? null : message };
            Save([.. all.Select(t => t.Id == id ? changed : t)]);
        }
    }

    /// <summary>The switched-on targets that never ran or whose interval is over.</summary>
    public IReadOnlyList<BackupTargetDto> Due()
    {
        var now = time.GetUtcNow();
        return [.. List().Where(t => t.Enabled && (t.LastRunUtc is null || t.NextRunUtc <= now))];
    }

    /// <summary>The input's own rules, and for a SQLite folder where it may be (<see cref="BackupFolder"/>): the error shows when saving, not at the first run.</summary>
    private void Check(BackupTargetInputDto input)
    {
        if (BackupRules.Check(input) is { } problem) throw new InvalidInputException(problem);

        if (input.Kind == BackupTargetKind.Sqlite) BackupFolder.Resolve(input.Location.Trim(), settings.Path, environment.ContentRootPath, environment.WebRootPath);
    }

    private static void CheckName(List<Stored> all, BackupTargetInputDto input, int? ownId)
    {
        if (all.Any(t => t.Id != ownId && string.Equals(t.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidInputException($"There is a backup database called '{input.Name.Trim()}' already.");
    }

    private BackupTargetDto ToDto(Stored t) => new(t.Id, t.Name, t.Kind, t.Location, t.IntervalMinutes, t.Enabled, t.LastRunUtc, t.LastSucceeded, t.LastMessage,
        t.Enabled ? (t.LastRunUtc is { } last ? last.AddMinutes(t.IntervalMinutes) : time.GetUtcNow()) : null);

    private List<Stored> Load()
    {
        var path = Path;
        if (!File.Exists(path)) return [];

        var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(path, Encoding.UTF8), Json) ?? [];
        // A location whose key is gone cannot be read; the target stays (so it can be corrected) with an empty location.
        return [.. stored.Select(t => t with { Location = settings.Unprotect(t.Location) ?? "" })];
    }

    private void Save(List<Stored> all)
    {
        var path = Path;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all.Select(t => t with { Location = settings.Protect(t.Location) }), Json), Encoding.UTF8);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
}
