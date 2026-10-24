using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The administrators' page for the settings the setup asked for (<see cref="SetupViewModel"/>): where the databases are, how Spond is
/// reached, how the statistics read the calendar. The API keeps what is saved in its settings file and restarts itself to apply it; the page
/// waits for it and reads the settings again. Only the users who may see the settings load them; only those who may change them can save.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly ApiRestartWatcher _watcher;
    private SettingsDto? _loaded;
    private Func<string>? _notice;

    public SettingsViewModel(ShellViewModel shell, IPlannerApi api, ApiRestartWatcher? watcher = null) : base(shell)
    {
        _api = api;
        _watcher = watcher ?? new ApiRestartWatcher(api);
        Form.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(IsRestarting)) SaveCommand.NotifyCanExecuteChanged();
        };
    }

    protected override string? ViewPermission => Permission.SettingsView;

    public override string Title => Strings.Nav_Settings;

    /// <summary>What was typed must survive a refresh of the Spond data, and the settings do not depend on the group.</summary>
    protected override bool ReloadsWithContext => false;

    public SettingsForm Form { get; } = new();

    public bool CanEdit => Shell.Can(Permission.SettingsEdit);

    public bool IsLoaded => _loaded is not null;

    /// <summary>The API restarts to apply what was saved; the page waits for it.</summary>
    [ObservableProperty]
    private bool _isRestarting;

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
        if (!CanView)
        {
            _loaded = null;
            Form.Load(new SettingsDto("", "", false, null, null, false, "", "", 0, 0, false, 0));
        }

        base.OnPermissionsChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(NoticeText));
    }

    protected override Task ReloadAsync() => LoadAsync(async ct =>
    {
        _loaded = await _api.GetSettingsAsync(ct);
        Form.AvailableSubGroups = [.. Shell.Groups.SelectMany(g => g.SubGroups).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
        Form.Load(_loaded);
        OnPropertyChanged(nameof(IsLoaded));
        SaveCommand.NotifyCanExecuteChanged();
    });

    /// <summary>Something was typed that is not what the API runs with, and it can be saved.</summary>
    public bool CanSave => CanEdit && IsLoaded && !IsBusy && !IsRestarting && Form.IsValid && HasChanges;

    private bool HasChanges => _loaded is { } now && !Form.Matches(now);

    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        SettingsSavedDto? saved = null;
        SetNotice(null);
        await LoadAsync(async ct => saved = await _api.SaveSettingsAsync(Form.ToInput(), ct));
        if (saved is null) return;

        if (!saved.Restarting)
        {
            SetNotice(() => Strings.Settings_NothingChanged);
            return;
        }

        IsRestarting = true;
        SetNotice(() => Strings.Settings_SavedRestarting);
        var back = await _watcher.WaitUntilBackAsync(CancellationToken.None);
        IsRestarting = false;
        if (!back)
        {
            SetNotice(null);
            ErrorMessage = Strings.Setup_NotBack;
            return;
        }

        SetNotice(() => Strings.Settings_Restarted);
        await ReloadAsync();
    }
}
