using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Plugins;
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
    IOptions<MetricsOptions> metrics,
    IOptions<DefaultDataOptions> defaultData,
    SettingsFile file,
    IAppRestarter restarter,
    SetupState state,
    AccountService accountService,
    IPasswordHasher<UserAccount> hasher,
    TimeProvider time,
    IHostEnvironment environment,
    ILoggerFactory loggers,
    IConfiguration configuration,
    PluginHost plugins,
    PluginCatalogService catalog)
{
    public SettingsDto Get()
    {
        var first = spond.Value;
        return new SettingsDto(
            ManagesDatabaseFiles ? DataSource(database.Value.ConnectionString) : "",
            ManagesDatabaseFiles ? DataSource(accounts.Value.ConnectionString) : "",
            first.UseDemoData,
            statistics.Value.TimeZone,
            statistics.Value.ExamKeyword,
            statistics.Value.DefaultMinAttendancePercent,
            statistics.Value.DefaultMinInvitedEvents,
            sync.Value.Enabled,
            Math.Max(0, (int)Math.Round(sync.Value.Interval.TotalMinutes)),
            configuration[AllowedHostsKey] is { Length: > 0 } hosts ? hosts : SettingsRules.AnyHost,
            statistics.Value.DefaultEventStars,
            statistics.Value.ExamMinAverageStars,
            first.UnregisteredSubGroup ?? "",
            CurrentKind,
            dataSource.Value.Script,
            metrics.Value.Enabled,
            !string.IsNullOrEmpty(metrics.Value.Token),
            [.. dataSource.Value.Secrets.Where(s => !string.IsNullOrEmpty(s.Value)).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase)],
            PluginSettings: Forms().ToDictionary(f => f.Source.Id, f => PluginSettingsMapper.Read(f.Form, configuration.GetSection(f.Section))));
    }

    /// <summary>
    /// The settings forms of the data sources of the installed plugins: the form, and the section of the configuration its values are kept in. A data source id that two
    /// installed plugins claim (a hand-edited index; installing refuses it) has one form: the one of the plugin that runs.
    /// </summary>
    private IEnumerable<(PluginDataSourceDef Source, PluginUiLayout Form, string Section)> Forms() =>
        plugins.Plugins.OrderByDescending(p => p.IsRunning).SelectMany(p => (p.Manifest.DataSources ?? [])
                .Select(source => (Source: source, Form: p.Installed.FormOf(source), Section: p.Installed.SectionOf(source)))
                .Where(f => f.Form is not null)
                .Select(f => (f.Source, f.Form!, f.Section)))
            .DistinctBy(f => f.Source.Id);

    /// <summary>What is wrong with the settings of the plugin data sources that were sent, or <c>null</c>. A data source that was not sent is left alone.</summary>
    private string? PluginSettingsProblem(SettingsInputDto input)
    {
        if (input.PluginSettings is not { Count: > 0 } sent) return null;

        var forms = Forms().ToList();
        foreach (var (id, values) in sent)
        {
            if (values is null) return $"pluginSettings: the settings of '{id}' are empty.";
            var matches = forms.Where(f => f.Source.Id == id).ToList();
            if (matches.Count == 0) return $"pluginSettings: no installed plugin has a data source '{id}' with settings.";
            var form = matches[0];
            var section = configuration.GetSection(form.Section);
            if (PluginRules.CheckValues(form.Form, PluginSettingsMapper.WithoutStaleKeeps(form.Form, section, values), PluginSettingsMapper.HasSecret(form.Form, section)) is { } problem)
                return $"pluginSettings.{id}: {problem}";
        }

        return null;
    }

    /// <summary>The value stored for the script secret <paramref name="name"/>, or <c>null</c>.</summary>
    private string? StoredSecret(string name) =>
        dataSource.Value.Secrets.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static bool IsEmpty(ScriptSecretInputDto secret) => string.IsNullOrWhiteSpace(secret.Name) && string.IsNullOrEmpty(secret.Value);

    /// <summary>What is wrong with the script secrets as typed (never quoting a value), or <c>null</c>.</summary>
    private string? SecretsProblem(IReadOnlyList<ScriptSecretInputDto>? secrets)
    {
        if (secrets is null) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in secrets)
        {
            if (secret is null) return "scriptSecrets must not contain empty entries.";
            if (IsEmpty(secret)) continue;

            var name = secret.Name?.Trim() ?? "";
            if (!SettingsRules.IsSecretName(name))
                return $"scriptSecrets: a name is a letter followed by letters, digits and underscores, at most {SettingsRules.MaxSecretNameLength} characters.";
            if (!names.Add(name)) return $"scriptSecrets: the name {name} is used twice (names are not case sensitive).";
            if (!string.IsNullOrEmpty(secret.Value) && !SettingsRules.IsSecretValue(secret.Value))
                return $"scriptSecrets: the value of {name} must be one line of text, at most {SettingsRules.MaxSecretValueLength} characters.";
            if (string.IsNullOrEmpty(secret.Value) && StoredSecret(name) is null) return $"scriptSecrets: {name} needs a value.";
        }

        return names.Count > SettingsRules.MaxScriptSecrets ? $"scriptSecrets: at most {SettingsRules.MaxScriptSecrets} secrets." : null;
    }

    /// <summary>The script secrets as saved (an empty value keeps the stored one), or <c>null</c> when the client sent none (an older one: they stay).</summary>
    private Dictionary<string, string>? ScriptSecretsOf(SettingsInputDto input) =>
        input.ScriptSecrets?.Where(s => s is not null && !IsEmpty(s)).ToDictionary(
            s => s.Name.Trim(), s => !string.IsNullOrEmpty(s.Value) ? s.Value! : StoredSecret(s.Name.Trim()) ?? "", StringComparer.OrdinalIgnoreCase);

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
        state.NeedsSetup, state.NeedsSetup ? ForSetup(Get()) : null, state.NeedsSetup ? file.Path : null, SettingsFile.IsConfigured(configuration),
        state.NeedsSetup ? catalog.ForSetup() : null);

    /// <summary>The setup needs no login: a script that is already there (from the configuration) may hold a token and is not given to whoever asks. The form leaves it alone.</summary>
    private static SettingsDto ForSetup(SettingsDto settings) =>
        settings.CustomScript.Length > 0 ? settings with { CustomScript = "", CustomScriptHidden = true } : settings;

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

        if (!SettingsRules.IsDataSource(input.DataSource)) Fail("dataSource", $"dataSource must be one of: {string.Join(", ", DataSourceKinds.All)}, or the id of a data source of a plugin (lower case letters, digits and hyphens).");
        var script = input.CustomScript ?? dataSource.Value.Script;
        if (script.Length > SettingsRules.MaxScriptLength) Fail("customScript", $"customScript is too long (at most {SettingsRules.MaxScriptLength} characters).");
        else if (RequestedKind(input) == DataSourceKinds.Custom && CustomScriptDataSource.CheckSyntax(script) is { } problem) Fail("customScript", $"customScript: {problem}");

        if (SecretsProblem(input.ScriptSecrets) is { } secretsProblem) Fail("scriptSecrets", secretsProblem);

        if (PluginSettingsProblem(input) is { } pluginProblem) Fail("pluginSettings", pluginProblem);

        if (!string.IsNullOrEmpty(input.MetricsToken) && !SettingsRules.IsMetricsToken(input.MetricsToken))
            Fail("metricsToken", $"metricsToken must be {SettingsRules.MinMetricsTokenLength} to {SettingsRules.MaxMetricsTokenLength} characters without spaces.");
        else if ((input.MetricsEnabled ?? metrics.Value.Enabled) && string.IsNullOrEmpty(input.MetricsToken) && string.IsNullOrEmpty(metrics.Value.Token))
            Fail("metricsToken", "The metrics need a token.");

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

    /// <summary>What saving this would change, as the audit log words it: the keys, never the values of secrets (<see cref="SettingsAudit"/>); null when nothing would change.</summary>
    public string? DescribeChanges(SettingsInputDto input) => Changes(input) is { Count: > 0 } changes ? SettingsAudit.Describe(changes) : null;

    /// <summary>What the first start would change, as <see cref="DescribeChanges"/>, and who the first administrator is.</summary>
    public string DescribeSetup(SetupInputDto input) =>
        SettingsAudit.Describe(Changes(input.Settings), new JsonObject
        {
            ["adminUserName"] = input.AdminUserName.Trim(),
            ["settingsFileMoved"] = MovedSettingsFile(input.SettingsFilePath) is not null,
        });

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
        // Written as a whole: a secret that is not in the list is gone. The values are encrypted by the settings file.
        if (ScriptSecretsOf(input) is { } secrets && !SameSecrets(secrets, dataSource.Value.Secrets))
            changes["DataSource:Secrets"] = new JsonObject([.. secrets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]);
        // The settings of the data sources of plugins: only those the client sent.
        foreach (var form in Forms().Where(f => input.PluginSettings?.ContainsKey(f.Source.Id) == true))
            foreach (var (key, value) in PluginSettingsMapper.Changes(form.Form, form.Section, input.PluginSettings![form.Source.Id], configuration.GetSection(form.Section)))
                changes[key] = value;
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

        if (input.MetricsEnabled is { } metricsOn) Set("Metrics:Enabled", Flag(now.MetricsEnabled), Flag(metricsOn));
        if (!string.IsNullOrEmpty(input.MetricsToken) && input.MetricsToken != metrics.Value.Token) changes["Metrics:Token"] = input.MetricsToken;

        return changes;
    }

    private static bool SameSecrets(Dictionary<string, string> next, Dictionary<string, string?> current)
    {
        var stored = current.Where(s => !string.IsNullOrEmpty(s.Value)).ToList();
        return next.Count == stored.Count && stored.All(s => next.TryGetValue(s.Key, out var value) && value == s.Value);
    }

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
