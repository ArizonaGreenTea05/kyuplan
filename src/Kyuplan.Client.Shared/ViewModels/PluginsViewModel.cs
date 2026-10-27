using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>An installed plugin in the list of the plugins page.</summary>
public sealed class PluginRow(PluginAdminDto plugin)
{
    public PluginAdminDto Plugin => plugin;

    public string Id => plugin.Id;

    public string Name => plugin.Name;

    /// <summary>"Version 1.2.0 · from https://github.com/owner/repo".</summary>
    public string Details => string.Format(Strings.Plugins_Details, plugin.Version, plugin.Source == PluginRules.BundledSource ? Strings.Plugins_SourceBundled : plugin.Source);

    public string? Description => plugin.Description;

    /// <summary>What the plugin adds: "Data sources: spond · 2 pages".</summary>
    public string Offers
    {
        get
        {
            var parts = new List<string>();
            if (plugin.DataSources.Count > 0) parts.Add(string.Format(Strings.Plugins_DataSources, string.Join(", ", plugin.DataSources)));
            if (plugin.PageCount > 0) parts.Add(string.Format(Strings.Plugins_Pages, plugin.PageCount));
            return string.Join(" · ", parts);
        }
    }

    public bool HasOffers => Offers.Length > 0;

    /// <summary>Runs now. A plugin that was installed or changed since the API started runs after its restart.</summary>
    public bool IsRunning => plugin.IsRunning;

    public string StatusText => plugin.Problem is { } problem ? string.Format(Strings.Plugins_Problem, problem) : plugin.IsRunning ? Strings.Plugins_Running : Strings.Plugins_NotRunning;

    public bool HasProblem => plugin.Problem is not null;

    /// <summary>It came from GitHub, so it can be updated from there.</summary>
    public bool CanUpdate => ThemeSourceRules.IsValidUrl(plugin.Source);
}

/// <summary>
/// The plugins page: the installed plugins, and installing one from a public GitHub repository (its latest release). A plugin is code that runs inside the API
/// with all its rights, so the page says so before anything is installed. The API loads plugins when it starts, so a change restarts it, which the page waits for.
/// </summary>
public sealed partial class PluginsViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly ApiRestartWatcher _watcher;
    private Func<string>? _notice;

    public PluginsViewModel(ShellViewModel shell, IPlannerApi api, ApiRestartWatcher? watcher = null) : base(shell)
    {
        _api = api;
        _watcher = watcher ?? new ApiRestartWatcher(api);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(IsRestarting) or nameof(Url)) NotifyCommands();
        };
    }

    protected override string? ViewPermission => Permission.PluginsView;

    public override string Title => Strings.Nav_Plugins;

    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => true;

    public bool CanEdit => Shell.Can(Permission.PluginsEdit);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlugins))]
    private IReadOnlyList<PluginRow> _plugins = [];

    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>The address of the repository to install from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlProblem))]
    private string _url = "";

    public string? UrlProblem => Url.Trim().Length == 0 || ThemeSourceRules.IsValidUrl(Url) ? null : Strings.Plugins_UrlInvalid;

    /// <summary>The API restarts to run what was changed; the page waits for it.</summary>
    [ObservableProperty]
    private bool _isRestarting;

    /// <summary>The plugin the user is asked about removing, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRemoval), nameof(RemovalQuestion))]
    private PluginRow? _removing;

    public bool IsConfirmingRemoval => Removing is not null;

    public string RemovalQuestion => Removing is null ? "" : string.Format(Strings.Plugins_RemoveConfirm, Removing.Name);

    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    protected override void OnPermissionsChanged()
    {
        if (!CanView) Plugins = [];

        base.OnPermissionsChanged();
        NotifyCommands();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(RemovalQuestion));
        OnPropertyChanged(nameof(Plugins));
    }

    private void NotifyCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        AskRemovalCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    protected override Task ReloadAsync() => LoadAsync(async ct => Plugins = [.. (await _api.GetInstalledPluginsAsync(ct)).Select(p => new PluginRow(p))]);

    private bool CanChange => CanEdit && !IsBusy && !IsRestarting;

    private bool CanInstall() => CanChange && Url.Trim().Length > 0 && UrlProblem is null;

    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var address = Url.Trim();
        await ChangeAsync(ct => _api.InstallPluginAsync(address, ct), () => Url = "");
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task UpdateAsync(PluginRow? row) => row is null ? Task.CompletedTask : ChangeAsync(ct => _api.UpdatePluginAsync(row.Id, ct), null);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void AskRemoval(PluginRow? row)
    {
        if (row is not null && CanChange) Removing = row;
    }

    [RelayCommand]
    private void CancelRemoval() => Removing = null;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RemoveAsync()
    {
        if (Removing is not { } row) return;

        Removing = null;
        await ChangeAsync(ct => _api.RemovePluginAsync(row.Id, ct), null);
    }

    /// <summary>Does the change; when the API restarts for it, waits for it to be back, then reads the list and the shell's catalog again.</summary>
    private async Task ChangeAsync(Func<CancellationToken, Task<PluginChangedDto>> change, Action? done)
    {
        PluginChangedDto? changed = null;
        SetNotice(null);
        await LoadAsync(async ct => changed = await change(ct));
        if (changed is null) return;

        done?.Invoke();
        if (!changed.Restarting)
        {
            SetNotice(() => Strings.Plugins_NothingChanged);
            await ReloadAsync();
            return;
        }

        IsRestarting = true;
        SetNotice(() => Strings.Plugins_Restarting);
        var back = await _watcher.WaitUntilBackAsync(CancellationToken.None);
        IsRestarting = false;
        if (!back)
        {
            SetNotice(null);
            ErrorMessage = Strings.Setup_NotBack;
            return;
        }

        SetNotice(() => Strings.Plugins_Restarted);
        await ReloadAsync();
        await Shell.RefreshPluginsAsync();
    }
}
