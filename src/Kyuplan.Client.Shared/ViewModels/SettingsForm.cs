using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The settings as they are typed, on the setup screen and on the settings page: the same fields, checked with the same rules
/// (<see cref="SettingsRules"/>) as the API checks them. The time zone is only checked by the API, which knows the zones of its system.
/// The settings of a data source that comes from a plugin are a form the plugin describes in JSON (<see cref="PluginForm"/>); the form is drawn from that, not coded here.
/// </summary>
public sealed partial class SettingsForm : ObservableObject
{
    private IReadOnlyList<PluginDataSourceDto> _pluginSources = [];
    private readonly Dictionary<string, PluginFormViewModel> _pluginForms = [];

    public SettingsForm()
    {
        ScriptSecrets.CollectionChanged += (_, _) => SecretsChanged();
    }

    // The error label of the Linux client is bound to the problem by name, so it has to be announced as well.
    private void SecretsChanged()
    {
        OnPropertyChanged(nameof(ScriptSecrets));
        OnPropertyChanged(nameof(ScriptSecretsProblem));
    }

    /// <summary>The secrets the custom script reads with <c>secrets.get('NAME')</c>; a stored value is never shown, only typed to set or change it.</summary>
    public ObservableCollection<ScriptSecretForm> ScriptSecrets { get; } = [];

    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _accountsDatabasePath = "";

    /// <summary>Where groups, members and events come from: one of <see cref="DataSourceKinds"/> or the id of the data source of a plugin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseDemoData), nameof(IsPluginSource), nameof(IsMissingPlugin), nameof(PluginForm), nameof(PluginFormProblem), nameof(IsCustomSource), nameof(IsManualSource), nameof(CustomScriptProblem), nameof(CustomScriptWarning), nameof(ScriptSecretsProblem))]
    private string _dataSource = DataSourceKinds.Spond;

    /// <summary>The JavaScript of the custom data source.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomScriptProblem), nameof(CustomScriptWarning))]
    private string _customScript = "";

    /// <summary>The API left the script out because this user may look at the settings but not change them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomScriptProblem), nameof(CustomScriptWarning))]
    [NotifyCanExecuteChangedFor(nameof(InsertScriptTemplateCommand))]
    private bool _customScriptHidden;

    /// <summary>The demo data is one of the data sources; this is the older way to ask for it.</summary>
    public bool UseDemoData
    {
        get => DataSource == DataSourceKinds.Demo;
        set => DataSource = value ? DataSourceKinds.Demo : DataSource == DataSourceKinds.Demo ? DataSourceKinds.Spond : DataSource;
    }

    /// <summary>The data source comes from a plugin (it is none of the program's own).</summary>
    public bool IsPluginSource => !DataSourceKinds.All.Contains(DataSource);

    public bool IsCustomSource => DataSource == DataSourceKinds.Custom;

    public bool IsManualSource => DataSource == DataSourceKinds.Manual;

    /// <summary>What the plugins add is known (the API answered). Until then the form cannot say that a plugin is missing: it only does not know.</summary>
    private bool _pluginsKnown;

    /// <summary>The chosen data source is a plugin's, but no installed plugin offers it (the plugin was removed, or never installed): saving it would leave the API without data.</summary>
    public bool IsMissingPlugin => _pluginsKnown && IsPluginSource && _pluginSources.All(s => s.Id != DataSource);

    /// <summary>
    /// Takes the data sources the plugins offer (<see cref="PluginCatalogDto"/>) and builds the forms of their settings. Call it before <see cref="Load"/>, which fills the forms.
    /// </summary>
    public void SetPlugins(PluginCatalogDto? catalog)
    {
        _pluginsKnown = catalog is not null;
        _pluginSources = [.. (catalog?.Plugins ?? []).SelectMany(p => p.DataSources)];
        foreach (var form in _pluginForms.Values) form.ValueChanged -= OnPluginFormChanged;
        _pluginForms.Clear();
        foreach (var source in _pluginSources.Where(s => s.Form is not null))
        {
            var form = new PluginFormViewModel(source.Form!);
            form.ValueChanged += OnPluginFormChanged;
            _pluginForms[source.Id] = form;
        }

        OnPropertyChanged(nameof(DataSources));
        OnPropertyChanged(nameof(IsMissingPlugin));
        OnPropertyChanged(nameof(PluginForm));
        OnPropertyChanged(nameof(PluginFormProblem));
    }

    private void OnPluginFormChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PluginFormProblem));
        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>The data sources to choose from, in the order they are offered: those of the plugins first. The chosen one is always in the list, so that a data source whose plugin is gone is still shown.</summary>
    public IReadOnlyList<string> DataSources =>
        [.. _pluginSources.Select(s => s.Id).Concat(DataSourceKinds.All).Append(DataSource).Distinct()];

    public string DataSourceText(string kind) => _pluginSources.FirstOrDefault(s => s.Id == kind) is { } source
        ? PluginText.Pick(source.Name, source.NameDe)
        : kind switch
        {
            DataSourceKinds.Custom => Strings.Settings_DataSource_custom,
            DataSourceKinds.Manual => Strings.Settings_DataSource_manual,
            DataSourceKinds.Demo => Strings.Settings_DataSource_demo,
            _ when !_pluginsKnown => kind,
            _ => string.Format(Strings.Settings_DataSource_missing, kind),
        };

    /// <summary>What the chosen data source means, in a sentence.</summary>
    public string DataSourceHint => DataSource switch
    {
        DataSourceKinds.Custom => Strings.Settings_DataSourceHint_custom,
        DataSourceKinds.Manual => Strings.Settings_DataSourceHint_manual,
        DataSourceKinds.Demo => Strings.Settings_UseDemoDataHint,
        _ when _pluginSources.FirstOrDefault(s => s.Id == DataSource) is { } source => PluginText.Pick(source.Description, source.DescriptionDe),
        _ when !_pluginsKnown => "",
        _ => Strings.Settings_DataSourceHint_missing,
    };

    partial void OnDataSourceChanged(string value) => OnPropertyChanged(nameof(DataSourceHint));

    /// <summary>The settings form of the chosen data source, when it is a plugin's and has one.</summary>
    public PluginFormViewModel? PluginForm => _pluginForms.GetValueOrDefault(DataSource);

    /// <summary>What is wrong with the settings of the chosen plugin data source, in words.</summary>
    public string? PluginFormProblem => PluginForm?.Problem;

    [ObservableProperty] private string _timeZone = "";
    [ObservableProperty] private string _examKeyword = "";
    [ObservableProperty] private int _defaultMinAttendancePercent;
    [ObservableProperty] private int _defaultMinInvitedEvents;
    [ObservableProperty] private double _defaultEventStars = RatingRules.StandardDefaultEventStars;
    [ObservableProperty] private double _examMinAverageStars = RatingRules.StandardExamMinAverageStars;
    [ObservableProperty] private string _unregisteredSubGroup = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubGroupChoices))]
    private IReadOnlyList<string> _availableSubGroups = [];

    [ObservableProperty] private bool _syncEnabled;
    [ObservableProperty] private int _syncIntervalMinutes;
    [ObservableProperty] private string _allowedHosts = SettingsRules.AnyHost;

    [ObservableProperty] private bool _metricsEnabled;

    /// <summary>Only filled in to set or change the token; the stored one is never sent back.</summary>
    [ObservableProperty] private string _metricsToken = "";

    [ObservableProperty] private bool _hasMetricsToken;

    /// <summary>The names of the subgroups the groups have (the combobox's choices), plus the one that is set when the data source does not list it (any more).</summary>
    public IReadOnlyList<string> SubGroupChoices =>
        [.. AvailableSubGroups.Append(UnregisteredSubGroup.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

    partial void OnUnregisteredSubGroupChanged(string value) => OnPropertyChanged(nameof(SubGroupChoices));

    public void Load(SettingsDto settings)
    {
        DatabasePath = settings.DatabasePath;
        AccountsDatabasePath = settings.AccountsDatabasePath;
        DataSource = DataSourceKinds.Normalize(settings.UseDemoData ? DataSourceKinds.Demo : settings.DataSource);
        CustomScript = settings.CustomScript;
        CustomScriptHidden = settings.CustomScriptHidden;
        foreach (var (id, form) in _pluginForms) form.Load(settings.PluginSettings?.GetValueOrDefault(id));
        TimeZone = settings.TimeZone;
        ExamKeyword = settings.ExamKeyword;
        DefaultMinAttendancePercent = settings.DefaultMinAttendancePercent;
        DefaultMinInvitedEvents = settings.DefaultMinInvitedEvents;
        DefaultEventStars = settings.DefaultEventStars;
        ExamMinAverageStars = settings.ExamMinAverageStars;
        UnregisteredSubGroup = settings.UnregisteredSubGroup ?? "";
        SyncEnabled = settings.SyncEnabled;
        SyncIntervalMinutes = settings.SyncIntervalMinutes;
        AllowedHosts = string.IsNullOrWhiteSpace(settings.AllowedHosts) ? SettingsRules.AnyHost : settings.AllowedHosts;
        MetricsEnabled = settings.MetricsEnabled;
        MetricsToken = "";
        HasMetricsToken = settings.HasMetricsToken;
        ScriptSecrets.Clear();
        foreach (var name in settings.ScriptSecrets ?? []) AddSecret(new ScriptSecretForm(name));
        OnPropertyChanged(string.Empty);
    }

    public SettingsInputDto ToInput() => new(
        DatabasePath.Trim(), AccountsDatabasePath.Trim(), DataSource == DataSourceKinds.Demo, TimeZone.Trim(), ExamKeyword.Trim(),
        DefaultMinAttendancePercent, DefaultMinInvitedEvents, SyncEnabled, SyncIntervalMinutes, AllowedHosts.Trim(),
        DefaultEventStars, ExamMinAverageStars, UnregisteredSubGroup.Trim(), DataSource, CustomScriptHidden ? null : CustomScript,
        MetricsEnabled, MetricsToken.Length == 0 ? null : MetricsToken,
        IsCustomSource ? [.. ScriptSecrets.Where(s => !s.IsEmpty).Select(s => s.ToInput())] : null, // another source leaves the stored secrets alone
        PluginForm is { } form ? new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> { [DataSource] = form.Values() } : null); // only the settings of the chosen data source

    // A script the API kept back is never sent, so a template put there would be dropped silently.
    [RelayCommand(CanExecute = nameof(CanInsertScriptTemplate))]
    private void InsertScriptTemplate() => CustomScript = CustomScriptTemplate.Text;

    private bool CanInsertScriptTemplate() => !CustomScriptHidden;

    [RelayCommand]
    private void AddScriptSecret() => AddSecret(new ScriptSecretForm());

    [RelayCommand]
    private void RemoveScriptSecret(ScriptSecretForm secret) => ScriptSecrets.Remove(secret);

    private void AddSecret(ScriptSecretForm secret)
    {
        secret.PropertyChanged += (_, _) => SecretsChanged();
        ScriptSecrets.Add(secret);
    }

    /// <summary>Everything typed is what the API runs with (and no new password was typed).</summary>
    public bool Matches(SettingsDto s) =>
        DatabasePath.Trim() == s.DatabasePath && AccountsDatabasePath.Trim() == s.AccountsDatabasePath && DataSource == DataSourceKinds.Normalize(s.UseDemoData ? DataSourceKinds.Demo : s.DataSource) && (CustomScriptHidden || CustomScript == s.CustomScript) && SecretsMatch(s.ScriptSecrets ?? []) &&
        (PluginForm is not { IsChanged: true }) &&
        TimeZone.Trim() == s.TimeZone && ExamKeyword.Trim() == s.ExamKeyword &&
        DefaultMinAttendancePercent == s.DefaultMinAttendancePercent && DefaultMinInvitedEvents == s.DefaultMinInvitedEvents &&
        DefaultEventStars == s.DefaultEventStars && ExamMinAverageStars == s.ExamMinAverageStars &&
        UnregisteredSubGroup.Trim() == (s.UnregisteredSubGroup ?? "") &&
        SyncEnabled == s.SyncEnabled && SyncIntervalMinutes == s.SyncIntervalMinutes && AllowedHosts.Trim() == (s.AllowedHosts ?? SettingsRules.AnyHost) &&
        MetricsEnabled == s.MetricsEnabled && MetricsToken.Length == 0;

    private bool SecretsMatch(IReadOnlyList<string> saved)
    {
        var typed = ScriptSecrets.Where(s => !s.IsEmpty).ToList();
        return typed.Count == saved.Count && typed.All(s => s.Value.Length == 0 && saved.Contains(s.Name.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    public string? DatabasePathProblem => SettingsRules.IsPath(DatabasePath) ? null : Strings.Settings_PathInvalid;

    public string? AccountsDatabasePathProblem =>
        !SettingsRules.IsPath(AccountsDatabasePath) ? Strings.Settings_PathInvalid
        : SettingsRules.AreSameFile(DatabasePath, AccountsDatabasePath) ? Strings.Settings_DatabasesSame
        : null;

    public string? CustomScriptProblem =>
        !IsCustomSource || CustomScriptHidden ? null
        : string.IsNullOrWhiteSpace(CustomScript) ? Strings.Settings_ScriptEmpty
        : CustomScript.Length > SettingsRules.MaxScriptLength ? string.Format(Strings.Settings_ScriptTooLong, SettingsRules.MaxScriptLength)
        : null;

    /// <summary>Something that looks like a token or password is written into the script as text (a warning, it can still be saved).</summary>
    public string? CustomScriptWarning =>
        IsCustomSource && !CustomScriptHidden && SettingsRules.MentionsCredentials(CustomScript) ? Strings.Settings_ScriptCredentialsWarning : null;

    public string? ScriptSecretsProblem =>
        !IsCustomSource ? null
        : ScriptSecrets.Any(s => s.IsInvalid) || HasDuplicateSecretName || ScriptSecrets.Count(s => !s.IsEmpty) > SettingsRules.MaxScriptSecrets ? Strings.Settings_ScriptSecretsInvalid
        : null;

    private bool HasDuplicateSecretName =>
        ScriptSecrets.Where(s => !s.IsEmpty).GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);

    public string? TimeZoneProblem => string.IsNullOrWhiteSpace(TimeZone) ? Strings.Settings_TimeZoneInvalid : null;

    public string? PercentProblem => SettingsRules.IsPercent(DefaultMinAttendancePercent) ? null : Strings.Settings_PercentInvalid;

    public string? InvitedEventsProblem => SettingsRules.IsInvitedEvents(DefaultMinInvitedEvents) ? null : Strings.Settings_InvitedInvalid;

    public string? DefaultEventStarsProblem => SettingsRules.IsStars(DefaultEventStars) ? null : Strings.Settings_StarsInvalid;

    public string? ExamMinAverageStarsProblem => SettingsRules.IsStars(ExamMinAverageStars) ? null : Strings.Settings_StarsInvalid;

    public string? SyncIntervalProblem =>
        SettingsRules.IsSyncInterval(SyncIntervalMinutes) ? null : string.Format(Strings.Settings_SyncIntervalInvalid, SettingsRules.MaxSyncIntervalMinutes);

    public string? AllowedHostsProblem => SettingsRules.IsAllowedHosts(AllowedHosts) ? null : Strings.Settings_AllowedHostsInvalid;

    /// <summary>The metrics need a token when they are on: a typed one, or the stored one.</summary>
    public string? MetricsTokenProblem =>
        MetricsToken.Length > 0 ? (SettingsRules.IsMetricsToken(MetricsToken) ? null : string.Format(Strings.Settings_MetricsTokenInvalid, SettingsRules.MinMetricsTokenLength))
        : MetricsEnabled && !HasMetricsToken ? Strings.Settings_MetricsTokenMissing
        : null;

    public bool IsValid =>
        DatabasePathProblem is null && AccountsDatabasePathProblem is null && PluginFormProblem is null && CustomScriptProblem is null && ScriptSecretsProblem is null && TimeZoneProblem is null &&
        PercentProblem is null && InvitedEventsProblem is null && DefaultEventStarsProblem is null && ExamMinAverageStarsProblem is null && SyncIntervalProblem is null && AllowedHostsProblem is null && MetricsTokenProblem is null;

    /// <summary>The language changed: the texts of the plugins' forms are read again.</summary>
    public void LanguageChanged()
    {
        foreach (var form in _pluginForms.Values) form.LanguageChanged();
        OnPropertyChanged(nameof(DataSources));
        OnPropertyChanged(nameof(DataSourceHint));
    }
}

/// <summary>One secret of the custom script as it is typed: the value is only filled in to set or change it.</summary>
public sealed partial class ScriptSecretForm : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValue), nameof(IsInvalid))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
    private string _value = "";

    private readonly string? _storedName;

    public ScriptSecretForm()
    {
    }

    /// <summary>A secret that has a value stored under <paramref name="storedName"/>.</summary>
    public ScriptSecretForm(string storedName)
    {
        _storedName = storedName;
        _name = storedName;
    }

    /// <summary>A value is stored for this name, so an empty value keeps it (a renamed secret has none: it needs a value).</summary>
    public bool HasValue => _storedName is not null && string.Equals(Name.Trim(), _storedName, StringComparison.OrdinalIgnoreCase);

    public bool IsEmpty => Name.Trim().Length == 0 && Value.Length == 0;

    /// <summary>Something typed, but not a whole secret: a name that cannot be one, a value that cannot be sent, or no value at all.</summary>
    public bool IsInvalid =>
        !IsEmpty && (!SettingsRules.IsSecretName(Name.Trim()) || (Value.Length > 0 ? !SettingsRules.IsSecretValue(Value) : !HasValue));

    public ScriptSecretInputDto ToInput() => new(Name.Trim(), Value.Length == 0 ? null : Value);
}
