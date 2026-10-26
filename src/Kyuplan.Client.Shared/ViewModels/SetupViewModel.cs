using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The first start: before anything else the client asks the API whether it has to be set up (it has no user yet). If so, this is what the
/// web client shows instead of the application: the settings (<see cref="Form"/>) and the first administrator. When the API is set up the client
/// carries on as usual (<see cref="NeedsSetup"/> is <c>false</c>); after finishing the setup <see cref="IsDone"/> tells the page to load anew,
/// so that it meets the API that runs with the new settings.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly IPlannerApi _api;

    public SetupViewModel(IPlannerApi api, ApiRestartWatcher? watcher = null)
    {
        _api = api;
        Watcher = watcher ?? new ApiRestartWatcher(api);
        Form.PropertyChanged += (_, _) => FinishCommand.NotifyCanExecuteChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) FinishCommand.NotifyCanExecuteChanged();
        };
    }

    public ApiRestartWatcher Watcher { get; }

    public SettingsForm Form { get; } = new();

    /// <summary>The API has been asked whether it needs the setup.</summary>
    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _needsSetup;

    /// <summary>The API restarts to apply the settings; the screen waits for it.</summary>
    [ObservableProperty]
    private bool _isRestarting;

    /// <summary>Setup is complete and the API is ready: the page loads anew and shows the application.</summary>
    [ObservableProperty]
    private bool _isDone;

    /// <summary>Where the API keeps the settings file; it is told only when it was changed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _settingsFilePath = "";

    /// <summary>The location is fixed by the API's configuration, so it cannot be changed here.</summary>
    [ObservableProperty]
    private bool _settingsFileLocked;

    private string _loadedSettingsFilePath = "";

    public string? SettingsFilePathProblem =>
        SettingsFilePath.Trim() == _loadedSettingsFilePath || SettingsRules.IsPath(SettingsFilePath) ? null : Strings.Settings_PathInvalid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _adminUserName = "admin";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _adminPassword = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _adminPasswordRepeat = "";

    public string? AdminUserNameProblem =>
        AccountRules.CheckUserName(AdminUserName) is null ? null : string.Format(Strings.Setup_UserNameInvalid, AccountRules.MaxUserNameLength);

    public string? AdminPasswordProblem =>
        AccountRules.CheckPassword(AdminPassword) is null ? null : string.Format(Strings.Setup_PasswordInvalid, AccountRules.MinPasswordLength, AccountRules.MaxPasswordLength);

    public string? RepeatProblem => AdminPasswordRepeat.Length > 0 && AdminPasswordRepeat != AdminPassword ? Strings.Setup_PasswordsDiffer : null;

    /// <summary>Asks the API whether it has to be set up; the answer fills the form with what the API runs with.</summary>
    [RelayCommand]
    public Task CheckAsync() => LoadAsync(async ct =>
    {
        var status = await _api.GetSetupAsync(ct);
        Form.SetPlugins(status.Plugins);
        if (status.Settings is { } settings) Form.Load(settings);
        _loadedSettingsFilePath = SettingsFilePath = status.SettingsFilePath ?? "";
        SettingsFileLocked = status.SettingsFileLocked;
        NeedsSetup = status.NeedsSetup;
        IsChecked = true;
    });

    public bool CanFinish =>
        NeedsSetup && !IsBusy && !IsRestarting && !IsDone && Form.IsValid && (SettingsFileLocked || SettingsFilePathProblem is null) &&
        AdminUserNameProblem is null && AdminPasswordProblem is null && AdminPasswordRepeat == AdminPassword;

    /// <summary>Saves the settings and creates the administrator; when the API restarts to apply them, waits for it.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    public async Task FinishAsync()
    {
        SettingsSavedDto? saved = null;
        await LoadAsync(async ct => saved = await _api.SetupAsync(new SetupInputDto(Form.ToInput(), AdminUserName.Trim(), AdminPassword,
            SettingsFileLocked || SettingsFilePath.Trim() == _loadedSettingsFilePath ? null : SettingsFilePath.Trim()), ct));
        if (saved is null) return;

        if (saved.Restarting)
        {
            IsRestarting = true;
            if (!await Watcher.WaitUntilBackAsync(CancellationToken.None))
            {
                IsRestarting = false;
                ErrorMessage = Strings.Setup_NotBack;
                return;
            }

            IsRestarting = false;
        }

        IsDone = true;
    }

    partial void OnNeedsSetupChanged(bool value) => FinishCommand.NotifyCanExecuteChanged();

    partial void OnIsRestartingChanged(bool value) => FinishCommand.NotifyCanExecuteChanged();
}
