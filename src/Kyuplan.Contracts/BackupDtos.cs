using System.Text.Json.Serialization;

namespace Kyuplan.Contracts;

/// <summary>What kind of database a backup target is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackupTargetKind>))]
public enum BackupTargetKind
{
    /// <summary>Two SQLite files (<c>planner.db</c>, <c>accounts.db</c>) in a folder on the server; <see cref="BackupTargetDto.Location"/> is the folder.</summary>
    Sqlite,

    /// <summary>A PostgreSQL database that holds both (their tables do not clash); <see cref="BackupTargetDto.Location"/> is its connection string.</summary>
    PostgreSql,
}

/// <summary>
/// A backup database: a second database that the API keeps a complete copy of all data in (planner and accounts), once every
/// <see cref="IntervalMinutes"/> minutes. Each run replaces what the target held.
/// </summary>
/// <param name="LastRunUtc">When the last run ended; <c>null</c> if there was none yet.</param>
/// <param name="LastSucceeded"><c>null</c> if there was no run yet.</param>
/// <param name="LastMessage">What the last run reported when it failed.</param>
/// <param name="NextRunUtc">When the next run is due; <c>null</c> for a target that is switched off.</param>
public sealed record BackupTargetDto(
    int Id,
    string Name,
    BackupTargetKind Kind,
    string Location,
    int IntervalMinutes,
    bool Enabled,
    DateTimeOffset? LastRunUtc,
    bool? LastSucceeded,
    string? LastMessage,
    DateTimeOffset? NextRunUtc);

/// <summary>What is sent to create a backup target or to replace one.</summary>
public sealed record BackupTargetInputDto(string Name, BackupTargetKind Kind, string Location, int IntervalMinutes, bool Enabled);

/// <summary>What the API answers after it replaced all its data with an uploaded backup; it restarts to start from the new data.</summary>
/// <param name="CreatedUtc">When the backup was made.</param>
/// <param name="Rows">How many rows were restored.</param>
public sealed record BackupRestoredDto(DateTimeOffset CreatedUtc, int Rows);

/// <summary>What a backup target must look like. The API enforces it; the clients check the same rules. Each check returns the problem in words, or <c>null</c>.</summary>
public static class BackupRules
{
    public const int MaxNameLength = 100;
    public const int MaxLocationLength = 500;
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 60 * 24 * 365;
    public const int MaxTargets = 20;

    /// <summary>The file name of a downloaded backup, and the extension the clients offer.</summary>
    public const string Extension = ".kyubackup";

    public const string ContentType = "application/zip";

    /// <summary>Request header with the passphrase of an encrypted backup (download: encrypt with it; restore: decrypt with it), see <see cref="EncodePassphrase"/>.</summary>
    public const string PassphraseHeader = "X-Backup-Passphrase";

    public const int MinPassphraseLength = 8;
    public const int MaxPassphraseLength = 200;

    /// <summary>A header carries ASCII only: the passphrase goes over as base64 of its UTF-8 bytes.</summary>
    public static string EncodePassphrase(string passphrase) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(passphrase));

    /// <summary>The passphrase of a header value; <c>null</c> when there is none or it is no base64.</summary>
    public static string? DecodePassphrase(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The problem with a passphrase for a new backup file in words, or <c>null</c>. An empty one means "not encrypted" and is fine.</summary>
    public static string? CheckPassphrase(string? passphrase) =>
        string.IsNullOrEmpty(passphrase) ? null
        : passphrase.Length < MinPassphraseLength ? $"the passphrase must be at least {MinPassphraseLength} characters long."
        : passphrase.Length > MaxPassphraseLength ? $"the passphrase must be at most {MaxPassphraseLength} characters long."
        : null;

    public static string? Check(BackupTargetInputDto? input)
    {
        if (input is null) return "the target is required.";

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return "name is required.";
        if (name.Length > MaxNameLength) return $"name must be at most {MaxNameLength} characters long.";
        if (name.Any(char.IsControl)) return "name must not contain control characters.";

        var location = input.Location?.Trim();
        if (string.IsNullOrEmpty(location)) return input.Kind == BackupTargetKind.Sqlite ? "folder is required." : "connection string is required.";
        if (location.Length > MaxLocationLength) return $"location must be at most {MaxLocationLength} characters long.";
        if (location.Any(char.IsControl)) return "location must not contain control characters.";
        if (!Enum.IsDefined(input.Kind)) return "kind is unknown.";

        return input.IntervalMinutes is < MinIntervalMinutes or > MaxIntervalMinutes
            ? $"interval must be between {MinIntervalMinutes} and {MaxIntervalMinutes} minutes."
            : null;
    }
}
