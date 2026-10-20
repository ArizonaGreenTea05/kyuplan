namespace Kyuplan.Contracts;

/// <summary>
/// What an installation needs to know to run: where the databases are, how to reach Spond, how the statistics read the calendar, and
/// how often the data is synced. The API keeps it in a settings file of its own (<c>data/settings.json</c>), on top of its
/// <c>appsettings.json</c>, so it survives restarts; the first start asks for it (<see cref="SetupStatusDto"/>), and administrators change it later.
/// </summary>
/// <param name="DatabasePath">The SQLite file with exams, members, belts and comments. A relative path is relative to the API's program folder.</param>
/// <param name="AccountsDatabasePath">The SQLite file with the users, apart from the other one so that logins can be backed up separately.</param>
/// <param name="UseDemoData">Show generated sample data instead of asking Spond.</param>
/// <param name="SpondEmail">Spond login (email); use this or <paramref name="SpondPhoneNumber"/>.</param>
/// <param name="HasSpondPassword">A Spond password is stored. The password itself is never sent back.</param>
/// <param name="IgnoreAdministrators">Leave the Spond group administrators out of the statistics, members and exams.</param>
/// <param name="TimeZone">Time zone id (<c>Europe/Berlin</c>) in which days and weekdays are read.</param>
/// <param name="ExamKeyword">Events whose name contains this word are exams.</param>
/// <param name="SyncIntervalMinutes">Minutes between two syncs with Spond; 0 = only when the API starts.</param>
/// <param name="BackupAccounts">More Spond logins, asked in this order when the one above yields nothing (Spond sometimes answers an account that sent too many requests with nothing at all).</param>
/// <param name="AllowedHosts">The host names this API answers to, separated by <c>;</c> (<c>kyuplan.example.com;*.example.org</c>); <c>*</c> = any. Requests with another Host header are refused.</param>
/// <param name="DefaultEventStars">What an event counts for a member who attended it and whom nobody evaluated (0–5).</param>
/// <param name="ExamMinAverageStars">The average evaluation a member needs to be suggested for an exam (0–5; 0 = not asked).</param>
/// <param name="UnregisteredSubGroup">Name (or id) of the Spond subgroup of the people not registered in the club yet; empty = none.</param>
/// <param name="DataSource">Where groups, members and events come from, one of <see cref="DataSourceKinds"/>; <see cref="UseDemoData"/> is the same as <see cref="DataSourceKinds.Demo"/>.</param>
/// <param name="CustomScript">The JavaScript that reads the data when <paramref name="DataSource"/> is <see cref="DataSourceKinds.Custom"/> (see docs/admin-guide.md).</param>
public sealed record SettingsDto(
    string DatabasePath,
    string AccountsDatabasePath,
    bool UseDemoData,
    string? SpondEmail,
    string? SpondPhoneNumber,
    bool HasSpondPassword,
    bool IgnoreAdministrators,
    string TimeZone,
    string ExamKeyword,
    int DefaultMinAttendancePercent,
    int DefaultMinInvitedEvents,
    bool SyncEnabled,
    int SyncIntervalMinutes,
    IReadOnlyList<SpondAccountDto>? BackupAccounts = null,
    string? AllowedHosts = null,
    double DefaultEventStars = RatingRules.StandardDefaultEventStars,
    double ExamMinAverageStars = RatingRules.StandardExamMinAverageStars,
    string? UnregisteredSubGroup = null,
    string DataSource = DataSourceKinds.Spond,
    string CustomScript = "");

/// <summary>A backup Spond login as the settings show it; the password itself is never sent back.</summary>
public sealed record SpondAccountDto(string? Email, string? PhoneNumber, bool HasPassword);

/// <summary>A backup Spond login to save.</summary>
/// <param name="Password"><c>null</c> or empty = keep the password stored for the account with this email / phone number.</param>
public sealed record SpondAccountInputDto(string? Email, string? PhoneNumber, string? Password);

/// <summary>What is saved: the settings as <see cref="SettingsDto"/>, with the Spond password to set.</summary>
/// <param name="SpondPassword"><c>null</c> or empty = keep the stored password.</param>
/// <param name="DataSource"><c>null</c> = an older client: <see cref="UseDemoData"/> chooses between demo and what is set now.</param>
/// <param name="CustomScript"><c>null</c> = keep the stored script.</param>
public sealed record SettingsInputDto(
    string DatabasePath,
    string AccountsDatabasePath,
    bool UseDemoData,
    string? SpondEmail,
    string? SpondPhoneNumber,
    string? SpondPassword,
    bool IgnoreAdministrators,
    string TimeZone,
    string ExamKeyword,
    int DefaultMinAttendancePercent,
    int DefaultMinInvitedEvents,
    bool SyncEnabled,
    int SyncIntervalMinutes,
    IReadOnlyList<SpondAccountInputDto>? BackupAccounts = null,
    string? AllowedHosts = null,
    double? DefaultEventStars = null,
    double? ExamMinAverageStars = null,
    string? UnregisteredSubGroup = null,
    string? DataSource = null,
    string? CustomScript = null);

/// <summary>Whether the API still has to be set up (it has no user yet), and what it would start with. Open to everybody.</summary>
/// <param name="Settings">What the API runs with now, as the form to fill in starts; <c>null</c> once it is set up (the settings are then for administrators only).</param>
/// <param name="SettingsFilePath">Where the settings file is now; <c>null</c> once the API is set up.</param>
/// <param name="SettingsFileLocked">The location is fixed by the API's configuration (environment variable, command line), so the setup cannot move it.</param>
public sealed record SetupStatusDto(bool NeedsSetup, SettingsDto? Settings, string? SettingsFilePath = null, bool SettingsFileLocked = false);

/// <summary>The first start's answers: the settings and the first administrator.</summary>
/// <param name="SettingsFilePath">Where to keep the settings file; <c>null</c> or unchanged = where it is.</param>
public sealed record SetupInputDto(SettingsInputDto Settings, string AdminUserName, string AdminPassword, string? SettingsFilePath = null);

/// <summary>What saving did.</summary>
/// <param name="Restarting">Something changed that only a restart applies, and the API is restarting itself now: it is back within seconds.</param>
public sealed record SettingsSavedDto(bool Restarting);

/// <summary>What the settings have to look like. The API enforces it; the clients check the same rules to offer only what will be accepted.</summary>
public static class SettingsRules
{
    public const int MaxPathLength = 260;
    public const int MaxTextLength = 100;
    public const int MaxSyncIntervalMinutes = 7 * 24 * 60;

    public static bool IsPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().Length <= MaxPathLength && !path.Any(char.IsControl) && !path.Contains(';');

    public const int MaxBackupAccounts = 10;

    public const int MaxScriptLength = 100_000;

    public static bool IsDataSource(string? kind) => kind is null || DataSourceKinds.IsKnown(kind.Trim().ToLowerInvariant());

    public const string AnyHost = "*";

    /// <summary>
    /// Host names as ASP.NET's host filtering reads them: separated by <c>;</c>, each a name (<c>kyuplan.example.com</c>), a wildcard
    /// subdomain (<c>*.example.com</c>) or <c>*</c> for any; a port may follow a name. Empty is allowed and means any.
    /// </summary>
    public static bool IsAllowedHosts(string? hosts)
    {
        if (string.IsNullOrWhiteSpace(hosts)) return true;
        if (hosts.Length > 500) return false;

        return hosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(IsHost);

        static bool IsHost(string host) =>
            host == AnyHost || (host.Length > 0 && !host.StartsWith("*.*") && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '*' or '[' or ']')
                                && (!host.Contains('*') || host.StartsWith("*.")) && !host.Skip(1).Contains('*'));
    }

    public static bool IsPercent(int value) => value is >= 0 and <= 100;

    public static bool IsStars(double value) => RatingRules.IsAverage(value);

    public static bool IsInvitedEvents(int value) => value is >= 0 and <= 1000;

    public static bool IsSyncInterval(int minutes) => minutes is >= 0 and <= MaxSyncIntervalMinutes;

    public static bool IsKeywordValid(string? keyword) => keyword is null || (keyword.Length <= MaxTextLength && !keyword.Any(char.IsControl));

    /// <summary>The two databases must be two files.</summary>
    public static bool AreSameFile(string? first, string? second) =>
        string.Equals(first?.Trim().Replace('\\', '/'), second?.Trim().Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Credentials are optional (they can be added later), but half of them is a mistake: a login needs both parts.</summary>
    public static bool HasHalfCredentials(string? email, string? phone, bool hasPassword) =>
        (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(phone)) != hasPassword;
}
