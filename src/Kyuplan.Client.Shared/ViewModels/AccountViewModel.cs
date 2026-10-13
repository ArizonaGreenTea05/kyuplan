using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The page where users change their own login: the names (the user name, the display name other users see, and aliases that
/// sign in as well), and the password (which needs the current one). The rights are not part of it, only an administrator
/// changes those. The two parts are saved separately, each by its own button, which is only enabled while pressing it would
/// change something the API accepts.
/// </summary>
public sealed partial class AccountViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly ILoginStore? _store;

    /// <summary>The account the name fields were last filled from; a field is only refilled while the user has not edited it.</summary>
    private UserDto? _shownAccount;

    private Func<string>? _notice;

    /// <param name="store">Where the client remembers the login, if it does (WPF). The browser keeps the login itself and has none.</param>
    public AccountViewModel(ShellViewModel shell, IPlannerApi api, ILoginStore? store = null) : base(shell)
    {
        _api = api;
        _store = store;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUser)) AdoptAccount();
        };
        AdoptAccount();
    }

    public override string Title => Strings.Nav_Account;

    /// <summary>The account page has nothing to do with the selected group or the Spond data.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>The signed-in user as the API knows them, or <c>null</c> until it has said so.</summary>
    public UserDto? Account => Shell.CurrentUser;

    /// <summary>Every signed-in user may look after their own account, so the page needs no permission.</summary>
    protected override string? ViewPermission => null;

    /// <summary>The names of the user's roles, for chips.</summary>
    public IReadOnlyList<string> RoleNames => [.. Account?.Roles.Select(r => r.Name) ?? []];

    /// <summary>The roles in one line, or that there are none.</summary>
    public string RoleText => RoleNames.Count > 0 ? string.Join(", ", RoleNames) : Strings.Role_None;

    // --- Names -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _userName = "";

    /// <summary>What other users see. Empty means the user name.</summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>More names to sign in with, one per line.</summary>
    [ObservableProperty]
    private string _aliasesText = "";

    /// <summary>The display name as it will be saved: what is typed, or the user name when that is left empty.</summary>
    private string SavedDisplayName => DisplayName.Trim().Length > 0 ? DisplayName.Trim() : UserName.Trim();

    /// <summary>The names are valid and differ from what the user has already.</summary>
    public bool CanSaveProfile =>
        !IsBusy && Account is { } account
        && UserNameProblem is null && DisplayNameProblem is null && AliasesProblem is null
        && (UserName.Trim() != account.UserName || SavedDisplayName != account.DisplayName
            || !AliasLines.Same(AliasLines.Split(AliasesText), account.Aliases));

    /// <summary>What is wrong with the user name typed so far, in words; <c>null</c> when nothing is.</summary>
    public string? UserNameProblem =>
        Account is not null && AccountRules.CheckUserName(UserName) is not null
            ? string.Format(Strings.Account_UserNameInvalid, AccountRules.MaxUserNameLength)
            : null;

    public string? DisplayNameProblem =>
        Account is not null && AccountRules.CheckDisplayName(DisplayName) is not null
            ? string.Format(Strings.Account_DisplayNameInvalid, AccountRules.MaxDisplayNameLength)
            : null;

    public string? AliasesProblem =>
        Account is not null && AccountRules.CheckAliases(AliasLines.Split(AliasesText), UserName) is not null
            ? string.Format(Strings.Account_AliasesInvalid, AccountRules.MaxAliases, AccountRules.MaxUserNameLength)
            : null;

    /// <summary>Under the aliases field: what they are for and how many there can be.</summary>
    public string AliasesHint => string.Format(Strings.Account_AliasesHint, AccountRules.MaxAliases);

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private Task SaveProfileAsync() =>
        CanSaveProfile
            ? SaveAsync(new AccountUpdateDto(UserName.Trim(), null, null, DisplayName.Trim(), AliasLines.Split(AliasesText)), () => Strings.Account_ProfileSaved)
            : Task.CompletedTask;

    // --- Password --------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _currentPassword = "";

    [ObservableProperty]
    private string _newPassword = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    /// <summary>The current password is given, the new one is a proper password other than the current one, and it was typed twice.</summary>
    public bool CanChangePassword =>
        !IsBusy && Account is not null && CurrentPassword.Length > 0 && NewPassword.Length > 0
        && PasswordProblem is null && NewPassword == ConfirmPassword;

    /// <summary>What a password has to look like, e.g. "8 to 128 characters".</summary>
    public string PasswordHint => string.Format(Strings.Account_PasswordHint, AccountRules.MinPasswordLength, AccountRules.MaxPasswordLength);

    /// <summary>What is wrong with the new password typed so far; <c>null</c> when nothing is (or nothing is typed).</summary>
    public string? PasswordProblem
    {
        get
        {
            if (NewPassword.Length == 0) return null;
            if (AccountRules.CheckPassword(NewPassword) is not null)
                return string.Format(Strings.Account_PasswordInvalid, AccountRules.MinPasswordLength, AccountRules.MaxPasswordLength);

            return NewPassword == CurrentPassword ? Strings.Account_PasswordSame : null;
        }
    }

    /// <summary>The repetition differs from the new password. Only said once as many characters were typed, so it does not nag while the user is still typing.</summary>
    public string? ConfirmProblem =>
        ConfirmPassword.Length >= NewPassword.Length && ConfirmPassword.Length > 0 && ConfirmPassword != NewPassword
            ? Strings.Account_PasswordMismatch
            : null;

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private Task ChangePasswordAsync() =>
        CanChangePassword
            ? SaveAsync(new AccountUpdateDto(Account!.UserName, CurrentPassword, NewPassword), () => Strings.Account_PasswordSaved)
            : Task.CompletedTask;

    // --- Result of the last save -----------------------------------------------------------------------------

    /// <summary>What the last save achieved, e.g. "Password changed.". Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    partial void OnUserNameChanged(string value) => InputChanged();

    partial void OnDisplayNameChanged(string value) => InputChanged();

    partial void OnAliasesTextChanged(string value) => InputChanged();

    partial void OnCurrentPasswordChanged(string value) => InputChanged();

    partial void OnNewPasswordChanged(string value) => InputChanged();

    partial void OnConfirmPasswordChanged(string value) => InputChanged();

    /// <summary>The user is typing again: what was said about the last save is old news.</summary>
    private void InputChanged()
    {
        ErrorMessage = null;
        SetNotice(null);
        NotifyStateChanged();
    }

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(UserNameProblem));
        OnPropertyChanged(nameof(DisplayNameProblem));
        OnPropertyChanged(nameof(AliasesProblem));
        OnPropertyChanged(nameof(CanChangePassword));
        OnPropertyChanged(nameof(PasswordProblem));
        OnPropertyChanged(nameof(ConfirmProblem));
        SaveProfileCommand.NotifyCanExecuteChanged();
        ChangePasswordCommand.NotifyCanExecuteChanged();
    }

    // --- Loading and saving ----------------------------------------------------------------------------------

    protected override Task ReloadAsync() =>
        // Asks again although the shell knows the user: an administrator may have renamed them since.
        LoadAsync(async ct => Shell.Session.User = await _api.GetAccountAsync(ct));

    /// <summary>The account changed (it became known, or was changed elsewhere): show it, unless the user is in the middle of editing the name.</summary>
    private void AdoptAccount()
    {
        var account = Account;
        if (_shownAccount is not null && _shownAccount.Id != account?.Id)
        {
            // Somebody else is signed in now (or nobody): nothing of the previous user's form is kept, least of all passwords.
            CurrentPassword = NewPassword = ConfirmPassword = "";
            UserName = DisplayName = AliasesText = "";
            _shownAccount = null;
        }

        if (account is not null)
        {
            if (_shownAccount is null || UserName == _shownAccount.UserName) UserName = account.UserName;
            if (_shownAccount is null || DisplayName == _shownAccount.DisplayName) DisplayName = account.DisplayName;
            if (_shownAccount is null || AliasesText == AliasLines.Join(_shownAccount.Aliases)) AliasesText = AliasLines.Join(account.Aliases);
        }

        _shownAccount = account;

        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(RoleText));
        NotifyStateChanged();
    }

    private async Task SaveAsync(AccountUpdateDto update, Func<string> notice)
    {
        SetNotice(null);
        var before = Account;

        UserDto? saved = null;
        await LoadAsync(async ct =>
        {
            try
            {
                saved = await _api.UpdateAccountAsync(update, ct);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new PlannerApiException(Strings.Account_UserNameTaken, ex.StatusCode, ex);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && update.NewPassword is not null)
            {
                // The new password was checked before, so the API can only mean the current one.
                throw new PlannerApiException(Strings.Account_CurrentPasswordWrong, ex.StatusCode, ex);
            }
        });

        if (saved is null) return; // the reason is in ErrorMessage

        // The API knows the new login from now on. A client that sends the login itself must send the new one, or its next call is refused.
        // The browser's login stops working when its name (the user name or an alias) is gone or the password changed; changing only how
        // the user is shown to others leaves it alone.
        var loginAffected = before is null || update.UserName != before.UserName || update.NewPassword is not null
                            || before.Aliases.Except(update.Aliases ?? before.Aliases).Any();
        var browserKeepsLogin = Shell.Session.Login is null && loginAffected;
        if (Shell.Session.Login is { } login)
        {
            var renewed = login.Renewed(saved, update.NewPassword);
            Shell.Session.Login = renewed;
            if (_store?.Load() is not null) _store.Save(renewed);
        }

        Shell.Session.User = saved;
        if (update.NewPassword is null)
        {
            // As the API wrote them (trimmed, display name filled in); a password change leaves names that are still being edited alone.
            UserName = saved.UserName;
            DisplayName = saved.DisplayName;
            AliasesText = AliasLines.Join(saved.Aliases);
        }

        CurrentPassword = NewPassword = ConfirmPassword = "";

        // The browser holds the old login and asks again with the next request: the one thing a page cannot do for it.
        SetNotice(() => browserKeepsLogin ? $"{notice()} {Strings.Account_BrowserAsksAgain}" : notice());
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(AliasesHint));
        OnPropertyChanged(nameof(NoticeText));
        NotifyStateChanged(); // the problems are worded in the language
    }
}
