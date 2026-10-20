using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Sources;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>The API is set up already: the first start's answers are not taken a second time.</summary>
public sealed class SetupDoneException() : Exception("The API is set up already. Administrators change the settings from the settings page.");

/// <summary>
/// Reads the settings the web client edits and saves them into the settings file (see <see cref="SettingsFile"/>). What the API runs with is
/// read once, when it starts, so a save that changes something restarts the API (<see cref="IAppRestarter"/>); a save that changes nothing does not.
/// The very first save (<see cref="SetupAsync"/>) also creates the first administrator, which is what ends the setup.
/// </summary>
public sealed class SettingsService(
    IOptions<DatabaseOptions> database,
    IOptions<AccountsOptions> accounts,
    IOptions<SpondOptions> spond,
    IOptions<DataSourceOptions> dataSource,
    IOptions<StatisticsOptions> statistics,
    IOptions<SyncOptions> sync,
    IOptions<DefaultDataOptions> defaultData,
    SettingsFile file,
    IAppRestarter restarter,
    SetupState state,
    AccountService accountService,
    IPasswordHasher<UserAccount> hasher,
    TimeProvider time,
    IHostEnvironment environment,
    ILoggerFactory loggers,
    IConfiguration configuration)
{
    public SettingsDto Get()
    {
        var first = spond.Value;
        return new SettingsDto(
            ManagesDatabaseFiles ? DataSource(database.Value.ConnectionString) : "",
            ManagesDatabaseFiles ? DataSource(accounts.Value.ConnectionString) : "",
            first.UseDemoData,
            first.Email,
            first.PhoneNumber,
            !string.IsNullOrEmpty(first.Password),
            first.IgnoreAdministrators,
            statistics.Value.TimeZone,
            statistics.Value.ExamKeyword,
            statistics.Value.DefaultMinAttendancePercent,
            statistics.Value.DefaultMinInvitedEvents,
            sync.Value.Enabled,
            Math.Max(0, (int)Math.Round(sync.Value.Interval.TotalMinutes)),
            [.. first.Accounts.Select(a => new SpondAccountDto(a.Email, a.PhoneNumber, !string.IsNullOrEmpty(a.Password)))],
            configuration[AllowedHostsKey] is { Length: > 0 } hosts ? hosts : SettingsRules.AnyHost,
            statistics.Value.DefaultEventStars,
            statistics.Value.ExamMinAverageStars,
            first.UnregisteredSubGroup ?? "",
            CurrentKind,
            dataSource.Value.Script);
    }

    /// <summary>The data source the API runs with (the demo data included).</summary>
    private string CurrentKind => dataSource.Value.Resolve(spond.Value);

    /// <summary>The data source a save asks for; an older client that does not know the field chooses between the demo data and what runs now.</summary>
    private string RequestedKind(SettingsInputDto input) =>
        input.DataSource is { } kind ? DataSourceKinds.Normalize(kind)
        : input.UseDemoData ? DataSourceKinds.Demo
        : CurrentKind == DataSourceKinds.Demo ? DataSourceKinds.Spond : CurrentKind;

    /// <summary>
    /// Whether the databases are SQLite files, whose paths this page can change. With PostgreSQL they are set in the configuration only
    /// (connection strings hold passwords): the paths are empty and not changed here.
    /// </summary>
    private bool ManagesDatabaseFiles => database.Value.Provider == DatabaseProvider.Sqlite;

    /// <summary>The configuration key ASP.NET's host filtering reads.</summary>
    public const string AllowedHostsKey = "AllowedHosts";

    public SetupStatusDto GetStatus() => new(
        state.NeedsSetup, state.NeedsSetup ? Get() : null, state.NeedsSetup ? file.Path : null, SettingsFile.IsConfigured(configuration));

    /// <summary>The problem with the settings file location typed in the setup, or <c>null</c>.</summary>
    public string? ValidateSettingsFile(string? path, bool isLocalRequest = true)
    {
        if (MovedSettingsFile(path) is null) return null;
        // The setup needs no login, so from another machine it must not choose where on the disk the API writes its files.
        if (!isLocalRequest) return "settingsFilePath can only be changed by a setup that is opened on the machine the API runs on.";
        if (SettingsFile.IsConfigured(configuration)) return "settingsFilePath is fixed by the configuration (Setup:SettingsFile) and cannot be changed here.";
        return SettingsRules.IsPath(path) ? null : "settingsFilePath must be a file path.";
    }

    /// <summary>The problems with what was typed, by field (camelCase, as the JSON names them), or <c>null</c> when it can be saved.</summary>
    public Dictionary<string, string[]>? Validate(SettingsInputDto input)
    {
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, string message) => errors[field] = [message];

        if (ManagesDatabaseFiles)
        {
            if (!SettingsRules.IsPath(input.DatabasePath)) Fail("databasePath", "databasePath must be a file path.");
            if (!SettingsRules.IsPath(input.AccountsDatabasePath)) Fail("accountsDatabasePath", "accountsDatabasePath must be a file path.");
            if (!errors.ContainsKey("databasePath") && !errors.ContainsKey("accountsDatabasePath") &&
                SettingsRules.AreSameFile(input.DatabasePath, input.AccountsDatabasePath))
                Fail("accountsDatabasePath", "The accounts database must be another file than the planner database.");
        }

        if (!SettingsRules.IsDataSource(input.DataSource)) Fail("dataSource", $"dataSource must be one of: {string.Join(", ", DataSourceKinds.All)}.");
        var script = input.CustomScript ?? dataSource.Value.Script;
        if (script.Length > SettingsRules.MaxScriptLength) Fail("customScript", $"customScript is too long (at most {SettingsRules.MaxScriptLength} characters).");
        else if (RequestedKind(input) == DataSourceKinds.Custom && CustomScriptDataSource.CheckSyntax(script) is { } problem) Fail("customScript", $"customScript: {problem}");

        if (RequestedKind(input) == DataSourceKinds.Spond &&
            SettingsRules.HasHalfCredentials(input.SpondEmail, input.SpondPhoneNumber, !string.IsNullOrEmpty(input.SpondPassword) || Get().HasSpondPassword))
            Fail("spondPassword", "A Spond login needs the email (or phone number) and the password.");

        if (input.BackupAccounts is { } backups)
        {
            var current = spond.Value.Accounts;
            if (backups.Count > SettingsRules.MaxBackupAccounts) Fail("backupAccounts", $"backupAccounts: at most {SettingsRules.MaxBackupAccounts} accounts.");
            else if (backups.Any(b => !IsEmpty(b) && SettingsRules.HasHalfCredentials(b.Email, b.PhoneNumber, !string.IsNullOrEmpty(b.Password) || StoredPassword(current, b) is not null)))
                Fail("backupAccounts", "A backup Spond login needs the email (or phone number) and the password.");
        }

        if (!SettingsRules.IsAllowedHosts(input.AllowedHosts)) Fail("allowedHosts", "allowedHosts must be host names separated by ';' (or *).");
        if (!IsTimeZone(input.TimeZone)) Fail("timeZone", "timeZone is not a time zone of this system.");
        if (!SettingsRules.IsKeywordValid(input.ExamKeyword)) Fail("examKeyword", "examKeyword is too long.");
        if (!SettingsRules.IsKeywordValid(input.UnregisteredSubGroup)) Fail("unregisteredSubGroup", "unregisteredSubGroup is too long.");
        if (!SettingsRules.IsPercent(input.DefaultMinAttendancePercent)) Fail("defaultMinAttendancePercent", "defaultMinAttendancePercent must be between 0 and 100.");
        if (!SettingsRules.IsInvitedEvents(input.DefaultMinInvitedEvents)) Fail("defaultMinInvitedEvents", "defaultMinInvitedEvents must be between 0 and 1000.");
        if (input.DefaultEventStars is { } eventStars && !SettingsRules.IsStars(eventStars)) Fail("defaultEventStars", "defaultEventStars must be between 0 and 5.");
        if (input.ExamMinAverageStars is { } minStars && !SettingsRules.IsStars(minStars)) Fail("examMinAverageStars", "examMinAverageStars must be between 0 and 5.");
        if (!SettingsRules.IsSyncInterval(input.SyncIntervalMinutes)) Fail("syncIntervalMinutes", $"syncIntervalMinutes must be between 0 and {SettingsRules.MaxSyncIntervalMinutes}.");

        return errors.Count == 0 ? null : errors;
    }

    /// <summary>Saves changed settings and restarts the API to apply them.</summary>
    /// <exception cref="InvalidInputException">A database cannot be opened at the path given.</exception>
    public Task<SettingsSavedDto> SaveAsync(SettingsInputDto input, CancellationToken cancellationToken)
    {
        var changes = Changes(input);
        CheckDatabases(changes);
        CheckInsideDataDirectory(changes, file.Path);

        if (changes.Count > 0)
        {
            file.Merge(changes);
            restarter.Request();
        }

        return Task.FromResult(new SettingsSavedDto(changes.Count > 0));
    }

    /// <summary>The first start: saves the settings and creates the first administrator, so that somebody can sign in.</summary>
    /// <exception cref="SetupDoneException">There is a user already.</exception>
    /// <exception cref="InvalidInputException">A database cannot be opened at the path given.</exception>
    public async Task<SettingsSavedDto> SetupAsync(SetupInputDto input, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken); // two visitors must not both become the administrator
        try
        {
            if (!state.NeedsSetup) throw new SetupDoneException();

            var settings = input.Settings;
            var changes = Changes(settings);
            CheckDatabases(changes);
            var moved = MovedSettingsFile(input.SettingsFilePath);
            if (moved is not null) CheckSettingsFile(moved);
            CheckInsideDataDirectory(changes, moved ?? file.Path);

            var userName = input.AdminUserName.Trim();
            if (changes.ContainsKey("Accounts:ConnectionString"))
            {
                // The accounts live elsewhere from the next start on: the administrator goes there, not into the database in use.
                var connection = (string)changes["Accounts:ConnectionString"]!;
                var options = new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite(SqliteConnectionStrings.Resolve(connection, environment.ContentRootPath)).Options;
                await using var db = new AccountsDbContext(options);
                var isNew = await db.EnsureUpToDateAsync(cancellationToken);
                if (isNew && defaultData.Value.Enabled) await DefaultRoles.SeedAsync(db, cancellationToken);

                var service = new AccountService(db, hasher, time, accounts, loggers.CreateLogger<AccountService>());
                if (!await db.Users.AnyAsync(cancellationToken)) await service.CreateAdministratorAsync(userName, input.AdminPassword, cancellationToken);
            }
            else
            {
                await accountService.CreateAdministratorAsync(userName, input.AdminPassword, cancellationToken);
            }

            state.Set(false);
            if (moved is not null)
            {
                if (File.Exists(file.Path) && !File.Exists(moved)) File.Copy(file.Path, moved);
                file.At(moved).Merge(changes);
                SettingsFile.RememberLocation(environment.ContentRootPath, moved);
                restarter.Request();
            }
            else if (changes.Count > 0)
            {
                file.Merge(changes);
                restarter.Request();
            }

            return new SettingsSavedDto(moved is not null || changes.Count > 0);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>The configuration keys that differ from what the API runs with, with their new values.</summary>
    private Dictionary<string, object?> Changes(SettingsInputDto input)
    {
        var now = Get();
        var changes = new Dictionary<string, object?>();
        void Set(string key, string current, string next)
        {
            if (!string.Equals(current, next, StringComparison.Ordinal)) changes[key] = next;
        }

        static string Flag(bool value) => value ? "true" : "false";
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string Stars(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        static string Interval(int minutes) => TimeSpan.FromMinutes(minutes).ToString("c", CultureInfo.InvariantCulture);

        if (ManagesDatabaseFiles && input.DatabasePath.Trim() != now.DatabasePath)
            changes["Database:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = input.DatabasePath.Trim() }.ToString();
        if (ManagesDatabaseFiles && input.AccountsDatabasePath.Trim() != now.AccountsDatabasePath)
            changes["Accounts:ConnectionString"] = new SqliteConnectionStringBuilder { DataSource = input.AccountsDatabasePath.Trim() }.ToString();

        var kind = RequestedKind(input);
        // The demo data is the older switch alone (Spond:UseDemoData), so that the source that is chosen under it is still there when it is switched off.
        if (kind != DataSourceKinds.Demo) Set("DataSource:Kind", DataSourceKinds.Normalize(dataSource.Value.Kind), kind);
        Set("Spond:UseDemoData", Flag(now.UseDemoData), Flag(kind == DataSourceKinds.Demo));
        // An older client that does not know the field sends none: that keeps the script.
        if (input.CustomScript is not null) Set("DataSource:Script", dataSource.Value.Script, input.CustomScript);
        Set("Spond:Email", now.SpondEmail ?? "", input.SpondEmail?.Trim() ?? "");
        Set("Spond:PhoneNumber", now.SpondPhoneNumber ?? "", input.SpondPhoneNumber?.Trim() ?? "");
        if (!string.IsNullOrEmpty(input.SpondPassword) && input.SpondPassword != spond.Value.Password) changes["Spond:Password"] = input.SpondPassword;
        Set("Spond:IgnoreAdministrators", Flag(now.IgnoreAdministrators), Flag(input.IgnoreAdministrators));
        // An older client that does not know the field sends none: that keeps what is set.
        if (input.UnregisteredSubGroup is not null) Set("Spond:UnregisteredSubGroup", now.UnregisteredSubGroup ?? "", input.UnregisteredSubGroup.Trim());
        Set("Statistics:TimeZone", now.TimeZone, input.TimeZone.Trim());
        Set("Statistics:ExamKeyword", now.ExamKeyword, input.ExamKeyword?.Trim() ?? "");
        Set("Statistics:DefaultMinAttendancePercent", Number(now.DefaultMinAttendancePercent), Number(input.DefaultMinAttendancePercent));
        Set("Statistics:DefaultMinInvitedEvents", Number(now.DefaultMinInvitedEvents), Number(input.DefaultMinInvitedEvents));
        // An older client that does not know the fields sends none: that keeps what is set.
        if (input.DefaultEventStars is { } defaultStars) Set("Statistics:DefaultEventStars", Stars(now.DefaultEventStars), Stars(defaultStars));
        if (input.ExamMinAverageStars is { } minAverage) Set("Statistics:ExamMinAverageStars", Stars(now.ExamMinAverageStars), Stars(minAverage));
        Set("Sync:Enabled", Flag(now.SyncEnabled), Flag(input.SyncEnabled));
        Set("Sync:Interval", Interval(now.SyncIntervalMinutes), Interval(input.SyncIntervalMinutes));
        // An older client that does not know the field sends none: that keeps what is set.
        if (input.AllowedHosts is not null) Set(AllowedHostsKey, now.AllowedHosts ?? SettingsRules.AnyHost, input.AllowedHosts.Trim().Length == 0 ? SettingsRules.AnyHost : input.AllowedHosts.Trim());

        var backups = BackupAccountsOf(input);
        if (backups is not null && !SameAccounts(backups, spond.Value.Accounts))
            changes["Spond:Accounts"] = new JsonArray([.. backups.Select(a => (JsonNode)new JsonObject
            {
                ["Email"] = a.Email,
                ["PhoneNumber"] = a.PhoneNumber,
                ["Password"] = a.Password,
            })]);
        return changes;
    }

    private static bool IsEmpty(SpondAccountInputDto a) =>
        string.IsNullOrWhiteSpace(a.Email) && string.IsNullOrWhiteSpace(a.PhoneNumber) && string.IsNullOrEmpty(a.Password);

    private static bool SameLogin(SpondAccount a, SpondAccountInputDto b) =>
        !string.IsNullOrWhiteSpace(b.Email)
            ? string.Equals(a.Email, b.Email.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(a.Email) && !string.IsNullOrWhiteSpace(b.PhoneNumber) && a.PhoneNumber == b.PhoneNumber.Trim();

    private static string? StoredPassword(IEnumerable<SpondAccount> current, SpondAccountInputDto b) =>
        current.FirstOrDefault(a => SameLogin(a, b))?.Password is { Length: > 0 } password ? password : null;

    /// <summary>The backup accounts as typed (blank rows dropped; an empty password keeps the stored one), or <c>null</c> when the client sent none.</summary>
    private List<SpondAccount>? BackupAccountsOf(SettingsInputDto input) =>
        input.BackupAccounts?.Where(b => !IsEmpty(b)).Select(b => new SpondAccount
        {
            Email = b.Email?.Trim() is { Length: > 0 } email ? email : null,
            PhoneNumber = b.PhoneNumber?.Trim() is { Length: > 0 } phone ? phone : null,
            Password = !string.IsNullOrEmpty(b.Password) ? b.Password : StoredPassword(spond.Value.Accounts, b),
        }).ToList();

    private static bool SameAccounts(List<SpondAccount> next, List<SpondAccount> current) =>
        next.Count == current.Count && next.Zip(current).All(p =>
            (p.First.Email ?? "") == (p.Second.Email ?? "") && (p.First.PhoneNumber ?? "") == (p.Second.PhoneNumber ?? "") &&
            (p.First.Password ?? "") == (p.Second.Password ?? ""));

    /// <summary>The full path the settings file is to move to, or <c>null</c> when none was given or it stays where it is.</summary>
    private string? MovedSettingsFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Any(char.IsControl)) return path; // not a path; ValidateSettingsFile says so

        var full = Path.GetFullPath(path.Trim(), environment.ContentRootPath);
        return string.Equals(full, file.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? null : full;
    }

    /// <summary>The settings file must be writable where it is to be, or the API would lose its settings with the restart.</summary>
    private static void CheckSettingsFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var probe = path + ".tmp";
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidInputException($"settingsFilePath: the settings file cannot be written there ({ex.Message}).");
        }
    }

    /// <summary>A database that is to be somewhere else must be creatable (or readable) there, or the API would not start the next time.</summary>
    private void CheckDatabases(Dictionary<string, object?> changes)
    {
        foreach (var (key, field) in new[] { ("Database:ConnectionString", "databasePath"), ("Accounts:ConnectionString", "accountsDatabasePath") })
        {
            if (!changes.TryGetValue(key, out var value) || value is not string connection) continue;

            try
            {
                using var probe = new SqliteConnection(SqliteConnectionStrings.Resolve(connection, environment.ContentRootPath));
                probe.Open();
                using var command = probe.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                command.ExecuteScalar();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InvalidInputException($"{field}: the database cannot be opened or created there ({ex.Message}).");
            }
        }
    }

    /// <summary>
    /// A database may only be put next to the settings file (in a container: onto the data volume). Whoever may save the settings must not be able
    /// to make the API create files anywhere its user may write; a database that stays where it is is not checked, and the configuration
    /// (environment variables, command line) can still put one anywhere.
    /// </summary>
    private void CheckInsideDataDirectory(Dictionary<string, object?> changes, string settingsFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsFilePath))!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (key, field) in new[] { ("Database:ConnectionString", "databasePath"), ("Accounts:ConnectionString", "accountsDatabasePath") })
        {
            if (!changes.TryGetValue(key, out var value) || value is not string connection) continue;

            var full = Path.GetFullPath(new SqliteConnectionStringBuilder(connection).DataSource, environment.ContentRootPath);
            var relative = Path.GetRelativePath(directory, full);
            if (relative.StartsWith("..", comparison) || Path.IsPathRooted(relative))
                throw new InvalidInputException($"{field}: the database must be in the folder of the settings file ({directory}) or below it.");
        }
    }

    private static string DataSource(string connectionString) => new SqliteConnectionStringBuilder(connectionString).DataSource;

    private static bool IsTimeZone(string? id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id?.Trim() ?? "");
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
