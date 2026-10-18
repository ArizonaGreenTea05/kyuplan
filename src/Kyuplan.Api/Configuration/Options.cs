using Microsoft.Data.Sqlite;
using Npgsql;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Configuration;

public sealed class SpondOptions
{
    public const string Section = "Spond";

    /// <summary>Serve generated sample data instead of talking to Spond.</summary>
    public bool UseDemoData { get; set; }

    /// <summary>Spond login (email). Use either this or <see cref="PhoneNumber"/>. Together with the password it is the first account.</summary>
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// More Spond logins, tried in this order after the one above. Spond sometimes answers an account that sent too many requests with
    /// nothing at all: when an account yields no groups or events, the next one is asked, and an account that came back empty while
    /// another one delivered is asked last for <see cref="AccountCooldown"/>. All accounts must see the same groups.
    /// </summary>
    public List<SpondAccount> Accounts { get; set; } = [];

    /// <summary>How long an account that came back empty (or failed) while another one delivered is only asked after the others.</summary>
    public TimeSpan AccountCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Spond tokens are not refreshed by the client library, so the API logs in again after this long.</summary>
    public TimeSpan LoginLifetime { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long fetched events are reused before Spond is asked again.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Leave the members who are administrators of their Spond group (the ones who are not respondents, the login account
    /// usually among them) out of everything: the statistics, the member list and the exams. Members already in the
    /// database who are administrators are removed by the next sync. Has no effect with <see cref="UseDemoData"/>.
    /// </summary>
    public bool IgnoreAdministrators { get; set; }

    /// <summary>The usable accounts in the order they are asked: <see cref="Email"/> / <see cref="PhoneNumber"/> first, then <see cref="Accounts"/>.</summary>
    public IReadOnlyList<SpondAccount> GetAccounts() =>
        [.. new[] { new SpondAccount { Email = Email, PhoneNumber = PhoneNumber, Password = Password } }.Concat(Accounts).Where(a => a.HasCredentials)];
}

/// <summary>One Spond login.</summary>
public sealed class SpondAccount
{
    /// <summary>Spond login (email). Use either this or <see cref="PhoneNumber"/>.</summary>
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    public string? Password { get; set; }

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Password) &&
        (!string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(PhoneNumber));
}

public sealed class StatisticsOptions
{
    public const string Section = "Statistics";

    /// <summary>Time zone in which days, weekdays and months are evaluated. Spond timestamps are UTC.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    public int DefaultMinAttendancePercent { get; set; } = 75;

    public int DefaultMinInvitedEvents { get; set; } = 3;

    /// <summary>What an event counts for a member who attended it and whom nobody evaluated, 0–5 stars (see docs/attendance-statistics.md).</summary>
    public double DefaultEventStars { get; set; } = RatingRules.StandardDefaultEventStars;

    /// <summary>The average evaluation, 0–5 stars, a member needs to be suggested for an exam. 0 = not asked (see docs/exam-planning.md).</summary>
    public double ExamMinAverageStars { get; set; } = RatingRules.StandardExamMinAverageStars;

    /// <summary>Look-back window of the dashboard in days.</summary>
    public int DashboardDays { get; set; } = 90;

    /// <summary>How far ahead the dashboard lists upcoming events, in days.</summary>
    public int UpcomingDays { get; set; } = 14;

    /// <summary>
    /// Events whose name contains this word (case-insensitive) are exams. The statistics page preselects the
    /// time span from the last exam to the next one. Empty, or no exam pair in the search window = the page
    /// preselects the events of the last six months instead.
    /// </summary>
    public string ExamKeyword { get; set; } = "Exam";

    /// <summary>
    /// How many months back events are searched for exams and offered as the first or last day of a time span (1–24).
    /// </summary>
    public int EventSearchMonthsBack { get; set; } = 12;

    /// <summary>How many months ahead events are searched and offered, see <see cref="EventSearchMonthsBack"/> (1–24).</summary>
    public int EventSearchMonthsAhead { get; set; } = 6;

    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

/// <summary>Which database system keeps the data (<see cref="DatabaseOptions.Provider"/>).</summary>
public enum DatabaseProvider
{
    /// <summary>A file (or several) on the disk of the API; nothing to install. The default.</summary>
    Sqlite,

    /// <summary>A PostgreSQL server (version 13 or newer): next to the API or a managed one.</summary>
    PostgreSql,
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>
    /// The database system of both databases (planner and accounts): <c>Sqlite</c> (default) or <c>PostgreSql</c>. Their connection strings
    /// (<see cref="ConnectionString"/>, <see cref="AccountsOptions.ConnectionString"/>) must be of that system. Data is not moved between systems.
    /// </summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>
    /// Connection string of the database that holds exams, members, belts and comments. SQLite: a file name, e.g.
    /// <c>Data Source=data/kyuplan.db</c>; a relative one is resolved against the application's content root (and its folder is created),
    /// so the default works wherever the API runs. PostgreSQL: e.g. <c>Host=db;Database=kyuplan;Username=kyuplan;Password=…</c>.
    /// The schema is created and brought up to date when the API starts.
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=data/kyuplan.db";

    /// <summary>The connection string with, for SQLite, a relative database file made absolute and its folder created.</summary>
    public string Resolve(string contentRoot) => DatabaseConnections.Resolve(Provider, ConnectionString, contentRoot);
}

public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>Whether the backup databases (see <c>BackupWorker</c>) are updated by themselves. Switching it off keeps the definitions; downloads and uploads still work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A backup file (downloaded from the backup page) that a <b>new</b> instance starts with: when both databases are created by this start
    /// and the file exists, its data is loaded instead of the default belts and roles. Ignored once the databases exist. Empty = none.
    /// </summary>
    public string? InitialDataFile { get; set; }

    /// <summary>The largest backup file that can be uploaded.</summary>
    public int MaxUploadMegabytes { get; set; } = 256;

    /// <summary>The most a backup file may hold once unpacked, so that a small file cannot fill the memory.</summary>
    public int MaxUnpackedMegabytes { get; set; } = 1024;

    /// <summary>The file that holds the backup databases an administrator defined; default: <c>backup-targets.json</c> next to the settings file.</summary>
    public string? TargetsFile { get; set; }
}

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>
    /// Whether the API keeps the members and the exams' invitations in its database in line with Spond by itself (see
    /// <c>SpondSyncWorker</c>): once when it starts, then every <see cref="Interval"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait between two syncs. Spond data is cached for <see cref="SpondOptions.CacheDuration"/>, so a shorter wait
    /// mostly reads the same data again. Zero or less = only once, when the API starts.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class DefaultDataOptions
{
    public const string Section = "DefaultData";

    /// <summary>
    /// Whether a database that is created by this start is filled with the standard belts (planner database) and roles (accounts
    /// database). It happens once, when the database is created: databases that exist are never touched, and what an administrator
    /// deletes later does not come back.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class ClientDownloadOptions
{
    public const string Section = "ClientDownload";

    /// <summary>
    /// Folder with the installers of the desktop clients (<c>Kyuplan-&lt;platform&gt;-&lt;version&gt;.&lt;extension&gt;</c>, see <c>ClientDownload</c>). The container image puts
    /// them into <c>downloads</c> next to the program; empty = that folder. A platform without an installer gets no download (a build from source has none).
    /// </summary>
    public string Directory { get; set; } = "";
}

public sealed class DocumentationOptions
{
    public const string Section = "Documentation";

    /// <summary>
    /// Folder with the documentation, one built Docusaurus site per audience in <c>&lt;folder&gt;/&lt;audience&gt;</c> (see <c>Documentation</c>). The container image puts it into
    /// <c>docs</c> next to the program; empty = that folder. Without it the server has no documentation and the clients show no help link.
    /// </summary>
    public string Directory { get; set; } = "";
}

public sealed class AccountsOptions
{
    public const string Section = "Accounts";

    /// <summary>
    /// Connection string of the database that holds the user accounts, of the system <see cref="DatabaseOptions.Provider"/> names. It is a
    /// database of its own, apart from <see cref="DatabaseOptions.ConnectionString"/>, so that the logins can be backed up, protected and
    /// moved separately (with PostgreSQL it may be the same database: the tables do not clash). Resolved like
    /// <see cref="DatabaseOptions.ConnectionString"/>; the schema is created when the API starts.
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=data/accounts.db";

    /// <summary>User name of the administrator that is created when the accounts database has no user yet.</summary>
    public string InitialAdminUserName { get; set; } = "admin";

    /// <summary>
    /// Password of that administrator. Empty = a random one, written once to the log. It is only used while there is no
    /// user at all; changing it later does not change the account, and it can be removed from the configuration once the API has started.
    /// </summary>
    public string? InitialAdminPassword { get; set; }

    /// <summary>The connection string with, for SQLite, a relative database file made absolute and its folder created.</summary>
    public string Resolve(DatabaseProvider provider, string contentRoot) => DatabaseConnections.Resolve(provider, ConnectionString, contentRoot);
}

/// <summary>
/// How the instance presents itself to its users; served to the clients by <c>/api/branding</c>, so the clients carry nothing of
/// their own and one build serves any club. Settings that are left out, or that cannot be used (a file that does not exist, a
/// colour that is none), are logged when the API starts and replaced by the defaults. These settings are the basis: what administrators
/// save in the web client's branding page (see <c>BrandingService</c>) applies instead, until they reset it.
/// </summary>
public sealed class BrandingOptions
{
    public const string Section = "Branding";

    public const string DefaultProductName = "kyuplan";

    public const string DefaultAccentColor = BrandingRules.DefaultAccentColor;

    /// <summary>Name of the application: navigation, window titles, tab titles.</summary>
    public string ProductName { get; set; } = DefaultProductName;

    /// <summary>Name of the club or organisation, shown after the product name in window titles. Empty = none.</summary>
    public string OrganizationName { get; set; } = "";

    /// <summary>
    /// Accent colour of both clients (<c>#RGB</c> or <c>#RRGGBB</c>): links, focus, selection, "attended / meets the bar" and
    /// the like. The paler and darker tints they need are derived from it.
    /// </summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    /// <summary>Theme of the web client for users who chose none (see <c>ThemeIds</c>: default, dracula, catppuccin-latte, catppuccin-mocha, liquid-glass).</summary>
    public string DefaultWebTheme { get; set; } = ThemeIds.Default;

    /// <summary>The same for the Windows client, which also has windows10, aero and native.</summary>
    public string DefaultWindowsTheme { get; set; } = ThemeIds.Default;

    /// <summary>Unit of every amount of money (prices, payments), shown after the number: a symbol (€, $) or a code (CHF).</summary>
    public string Currency { get; set; } = BrandingRules.DefaultCurrency;

    /// <summary>
    /// Logo for light surfaces: path of an image file (SVG, or a bitmap: PNG, JPEG, GIF, BMP), the same one for every client. An SVG
    /// stays sharp at any size. A relative path is relative to the folder of the API's program files. Empty = no logo.
    /// </summary>
    public string? Logo { get; set; }

    /// <summary>Logo for dark surfaces, in the same way. Empty = the light logo is used there too.</summary>
    public string? LogoOnDark { get; set; }

    /// <summary>Icon of the browser tab (SVG, PNG or ICO). Empty = none.</summary>
    public string? Icon { get; set; }
}

/// <summary>
/// The text files the API writes its log to, in addition to the console. The levels are the ones of <c>Logging:LogLevel</c>, as for the
/// console. A new file is started every day; the 31 newest are kept.
/// </summary>
public sealed class FileLogOptions
{
    public const string Section = "Logging:File";

    /// <summary>
    /// Where to write, e.g. <c>logs/kyuplan-.log</c>: the date of the day is put before the extension
    /// (<c>logs/kyuplan-20261014.log</c>). A relative path is relative to the application's content root, and its folder
    /// is created. Empty = no log files.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>The full path to write to, or <c>null</c> when there are no log files.</summary>
    public string? Resolve(string contentRoot) =>
        string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetFullPath(Path, contentRoot);
}

internal static class DatabaseConnections
{
    public static string Resolve(DatabaseProvider provider, string connectionString, string contentRoot) =>
        provider == DatabaseProvider.Sqlite ? SqliteConnectionStrings.Resolve(connectionString, contentRoot) : PostgresConnectionString(connectionString);

    /// <summary>
    /// The connection string as it is, except that Kerberos (GSS) encryption is off unless the string says otherwise: the image has no Kerberos
    /// library, and trying to load it prints an error at every start although nothing is wrong.
    /// </summary>
    private static string PostgresConnectionString(string connectionString)
    {
        var given = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!given.Keys.Cast<string>().Any(k => k.Replace(" ", "").Equals("GssEncryptionMode", StringComparison.OrdinalIgnoreCase)))
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ToString();
    }
}

internal static class SqliteConnectionStrings
{
    public static string Resolve(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        var inMemory = builder.Mode == SqliteOpenMode.Memory
                       || builder.DataSource.Length == 0
                       || builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
                       || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (inMemory) return builder.ToString();

        if (!Path.IsPathRooted(builder.DataSource)) builder.DataSource = Path.GetFullPath(builder.DataSource, contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
        return builder.ToString();
    }
}

public sealed class ApiSecurityOptions
{
    public const string Section = "Api";

    /// <summary>
    /// When set, every <c>/api</c> request must carry this value in the <c>X-Api-Key</c> header, on top of the login
    /// every request needs anyway. Leave empty to disable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Browser origins (Blazor client) allowed to call the API.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Logins travel in clear text with HTTP Basic, so the API refuses every request that does not come over HTTPS (behind a reverse proxy
    /// that ends HTTPS: switch on <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>) unless it comes from this machine. Set <c>true</c> only
    /// on a network you trust completely.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Failed logins from one network address within <see cref="LoginFailureWindow"/> before that address is locked out for the rest of the window; 0 = never.</summary>
    public int MaxLoginFailuresPerAddress { get; set; } = 10;

    /// <summary>Failed logins as one user name (from any address) within <see cref="LoginFailureWindow"/> before that name is locked out for the rest of the window; 0 = never.</summary>
    public int MaxLoginFailuresPerUser { get; set; } = 30;

    public TimeSpan LoginFailureWindow { get; set; } = TimeSpan.FromMinutes(15);
}
