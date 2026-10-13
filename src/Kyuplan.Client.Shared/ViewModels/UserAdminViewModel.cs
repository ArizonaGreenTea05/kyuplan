using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the user list.</summary>
/// <param name="IsCurrent">The user who is looking at the list: they cannot delete themselves.</param>
public sealed record UserRow(UserDto User, bool IsCurrent)
{
    public int Id => User.Id;

    public string UserName => User.UserName;

    public string DisplayName => User.DisplayName;

    /// <summary>The aliases for a table cell; empty when there are none.</summary>
    public string AliasesText => string.Join(", ", User.Aliases);

    /// <summary>The user has the role that has every permission.</summary>
    public bool HasAdministratorRole => User.HasAdministratorRole();

    /// <summary>The names of the roles for a table cell, or that there is none.</summary>
    public string RolesText => User.Roles.Count > 0 ? string.Join(", ", User.Roles.Select(r => r.Name)) : Strings.Role_None;
}

/// <summary>One role as a tick box in the user form.</summary>
public sealed partial class RoleChoice(int id, string name, bool isSystem) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>The administrator role.</summary>
    public bool IsSystem { get; } = isSystem;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The tick can be changed. Not so for the administrator role of the only user who has it: the API would refuse taking it away.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;
}

/// <summary>
/// The users page: all users, with creating, editing (names, password, roles and own permissions) and deleting. Who may do which of it
/// is up to <c>users.*</c> permissions: <c>users.view</c> for the page, <c>users.edit</c> for names and passwords, <c>users.grant</c> for roles and
/// permissions, <c>users.delete</c> for deleting. Used by both clients.
/// </summary>
public sealed partial class UserAdminViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly ILoginStore? _store;
    private IReadOnlyList<RoleDto> _roles = [];
    private Func<string>? _notice;

    /// <param name="store">Where the client remembers the login, if it does (WPF). The browser keeps the login itself and has none.</param>
    public UserAdminViewModel(ShellViewModel shell, IPlannerApi api, ILoginStore? store = null) : base(shell)
    {
        _api = api;
        _store = store;

        Picker.Changed += (_, _) => FormEdited();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Users;

    protected override string? ViewPermission => Permission.UsersView;

    /// <summary>Whether the user may create users and change their names and passwords.</summary>
    public bool CanEdit => Shell.Can(Permission.UsersEdit);

    /// <summary>Whether the user may give roles and permissions to users.</summary>
    public bool CanGrant => Shell.Can(Permission.UsersGrant);

    /// <summary>Whether the user may delete users.</summary>
    public bool CanDeleteUsers => Shell.Can(Permission.UsersDelete);

    /// <summary>
    /// The signed-in user may no longer see the page (or somebody else signed in): nothing of the list is kept. A user who may see it reads
    /// it again (the base class takes care of that); one who may no longer change anything loses the form.
    /// </summary>
    protected override void OnPermissionsChanged()
    {
        if (!CanView) Reset();
        else if (IsFormOpen && !CanEdit) CancelForm();

        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>The user list does not depend on the selected group or the Spond data.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>Roles are changed on their own page and users are counted there: read both again whenever the page is shown, unless the user is in the middle of something.</summary>
    protected override bool ReloadsOnActivation => !IsFormOpen && !IsConfirmingDelete;

    /// <summary>All users, by user name.</summary>
    [ObservableProperty]
    private IReadOnlyList<UserRow> _users = [];

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>User "Anna" created.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The form: a new user, or an existing one -----------------------------------------------------------

    /// <summary>The form is open, for a new user (<see cref="Editing"/> is <c>null</c>) or for <see cref="Editing"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle), nameof(PasswordHint))]
    private bool _isFormOpen;

    /// <summary>The user the form changes; <c>null</c> when it creates one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle), nameof(PasswordHint))]
    private UserRow? _editing;

    [ObservableProperty]
    private string _formUserName = "";

    /// <summary>What other users see. Empty means the user name.</summary>
    [ObservableProperty]
    private string _formDisplayName = "";

    /// <summary>More names the user can sign in with, one per line.</summary>
    [ObservableProperty]
    private string _formAliases = "";

    /// <summary>Required for a new user. For an existing one: the new password, or empty to keep the current one.</summary>
    [ObservableProperty]
    private string _formPassword = "";

    /// <summary>The roles to tick, for the user in the form. Only offered to somebody who may give roles (<see cref="CanGrant"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLockedRole))]
    private IReadOnlyList<RoleChoice> _roleChoices = [];

    /// <summary>The user's own permissions to tick, besides what their roles allow. Only offered to somebody who may give permissions.</summary>
    public PermissionPickerViewModel Picker { get; } = new();

    /// <summary>A role cannot be taken from the user: it is the administrator role of the only user who has it, and the API would refuse. The form says so.</summary>
    public bool HasLockedRole => RoleChoices.Any(r => !r.IsEnabled);

    public bool IsCreating => IsFormOpen && Editing is null;

    public string FormTitle => Editing is { } user ? string.Format(Strings.Users_FormEdit, user.UserName) : Strings.Users_FormNew;

    /// <summary>Under the password field: what a password has to look like for a new user, or that leaving it empty keeps the current one.</summary>
    public string PasswordHint =>
        IsCreating
            ? string.Format(Strings.Account_PasswordHint, AccountRules.MinPasswordLength, AccountRules.MaxPasswordLength)
            : Strings.Users_FormPasswordKeep;

    private List<int> SelectedRoleIds => [.. RoleChoices.Where(r => r.IsSelected).Select(r => r.Id).Order()];

    /// <summary>The form holds something the API accepts and that differs from what is there (for an existing user).</summary>
    public bool CanSave
    {
        get
        {
            if (!CanEdit || !IsFormOpen || IsBusy || AccountRules.CheckUserName(FormUserName) is not null
                || AccountRules.CheckDisplayName(FormDisplayName) is not null
                || AccountRules.CheckAliases(AliasLines.Split(FormAliases), FormUserName) is not null)
                return false;

            if (Editing is not { } user) return AccountRules.CheckPassword(FormPassword) is null;

            var passwordOk = FormPassword.Length == 0 || AccountRules.CheckPassword(FormPassword) is null;
            var shownName = FormDisplayName.Trim().Length > 0 ? FormDisplayName.Trim() : FormUserName.Trim();
            var changed = FormUserName.Trim() != user.UserName || shownName != user.DisplayName
                          || !AliasLines.Same(AliasLines.Split(FormAliases), user.User.Aliases)
                          || FormPassword.Length > 0
                          || (CanGrant && (!SelectedRoleIds.SequenceEqual(user.User.Roles.Select(r => r.Id).Order())
                                           || !Picker.Selected.SequenceEqual(user.User.Permissions)));
            return passwordOk && changed;
        }
    }

    public string? UserNameProblem =>
        IsFormOpen && (FormUserName.Length > 0 || Editing is not null) && AccountRules.CheckUserName(FormUserName) is not null
            ? string.Format(Strings.Account_UserNameInvalid, AccountRules.MaxUserNameLength)
            : null;

    public string? DisplayNameProblem =>
        IsFormOpen && AccountRules.CheckDisplayName(FormDisplayName) is not null
            ? string.Format(Strings.Account_DisplayNameInvalid, AccountRules.MaxDisplayNameLength)
            : null;

    public string? AliasesProblem =>
        IsFormOpen && AccountRules.CheckAliases(AliasLines.Split(FormAliases), FormUserName) is not null
            ? string.Format(Strings.Account_AliasesInvalid, AccountRules.MaxAliases, AccountRules.MaxUserNameLength)
            : null;

    /// <summary>Under the aliases field: what they are for and how many there can be.</summary>
    public string AliasesHint => string.Format(Strings.Account_AliasesHint, AccountRules.MaxAliases);

    public string? PasswordProblem =>
        FormPassword.Length > 0 && AccountRules.CheckPassword(FormPassword) is not null
            ? string.Format(Strings.Account_PasswordInvalid, AccountRules.MinPasswordLength, AccountRules.MaxPasswordLength)
            : null;

    /// <summary>A form for a new user is already open: opening it again would only throw away what was typed.</summary>
    private bool CanBeginCreate => CanEdit && !IsCreating;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        if (CanBeginCreate) OpenForm(null, "", "", "", [], []); // Execute does not ask CanExecute: what the user may not do is not done
    }

    private bool CanBeginEdit(UserRow? user) => CanEdit && user is not null;

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(UserRow user)
    {
        if (!CanBeginEdit(user)) return;

        OpenForm(user, user.UserName, user.DisplayName, AliasLines.Join(user.User.Aliases), [.. user.User.Roles.Select(r => r.Id)], user.User.Permissions);
    }

    private void OpenForm(UserRow? user, string userName, string displayName, string aliases, IReadOnlyList<int> roleIds, IReadOnlyList<string> permissions)
    {
        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = null;

        // The only user with the administrator role keeps it: the API would refuse taking it away, so the form does not offer it.
        var onlyAdministrator = user is { HasAdministratorRole: true } && Users.Count(u => u.HasAdministratorRole) <= 1;
        var choices = _roles.Select(r => new RoleChoice(r.Id, r.Name, r.IsSystem) { IsSelected = roleIds.Contains(r.Id), IsEnabled = !(r.IsSystem && onlyAdministrator) }).ToList();
        foreach (var choice in choices)
        {
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RoleChoice.IsSelected)) FormEdited();
            };
        }

        Editing = user;
        FormUserName = userName;
        FormDisplayName = displayName;
        FormAliases = aliases;
        FormPassword = "";
        RoleChoices = choices;
        Picker.Set(permissions);
        Picker.IsEnabled = CanGrant;
        IsFormOpen = true;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        Editing = null;
        ErrorMessage = null;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return; // a command is only guarded by its button; a form can also be submitted with Enter

        var editing = Editing;

        // Roles and permissions are only sent by somebody who may give them: for everybody else they stay as they are.
        var input = new UserInputDto(
            FormUserName.Trim(), FormPassword.Length > 0 ? FormPassword : null, FormDisplayName.Trim(), AliasLines.Split(FormAliases),
            CanGrant ? SelectedRoleIds : null, CanGrant ? Picker.Selected : null);
        var wasCreated = editing is null;

        // Their own login may change when the signed-in user renames, re-passwords or takes an alias from themselves.
        var selfChanged = !wasCreated && editing!.Id == Shell.CurrentUser?.Id
                          && (input.UserName != editing.UserName || input.Password is not null || editing.User.Aliases.Except(input.Aliases!).Any());
        var browserKeepsLogin = selfChanged && Shell.Session.Login is null;

        UserDto? saved = null;
        await LoadAsync(async ct =>
        {
            saved = wasCreated ? await _api.CreateUserAsync(input, ct) : await _api.UpdateUserAsync(editing!.Id, input, ct);

            // The API knows the new login from now on. A client that sends the login itself must send the new one, or its next call
            // (the list below) is refused.
            if (selfChanged && Shell.Session.Login is { } login)
            {
                var renewed = login.Renewed(saved, input.Password);
                Shell.Session.Login = renewed;
                if (_store?.Load() is not null) _store.Save(renewed);
            }

            await AfterChangeAsync(saved, ct);
        });

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open so the user can correct it

        // It is saved, even if showing the new list failed: the form must not be left open to save it a second time.
        IsFormOpen = false;
        Editing = null;
        FormPassword = "";

        var name = saved.UserName;
        SetNotice(() =>
        {
            var text = string.Format(wasCreated ? Strings.Users_Created : Strings.Users_Saved, name);
            // The browser still holds the old login and asks again with the next request: the one thing a page cannot do for it.
            return browserKeepsLogin ? $"{text} {Strings.Account_BrowserAsksAgain}" : text;
        });
        NotifyStateChanged();
    }

    partial void OnFormUserNameChanged(string value) => FormEdited();

    partial void OnFormDisplayNameChanged(string value) => FormEdited();

    partial void OnFormAliasesChanged(string value) => FormEdited();

    partial void OnFormPasswordChanged(string value) => FormEdited();

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- Deleting ------------------------------------------------------------------------------------------

    /// <summary>The user waiting for the confirmation that deletes them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmText))]
    private UserRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    public string ConfirmText => PendingDelete is { } user ? string.Format(Strings.Users_ConfirmDelete, user.UserName) : "";

    /// <summary>Everybody can be deleted by somebody who may delete users, but not by themselves.</summary>
    public bool CanDelete(UserRow? user) => CanDeleteUsers && user is { IsCurrent: false } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void AskDelete(UserRow user)
    {
        if (!CanDelete(user)) return;

        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = user;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } user) return;

        PendingDelete = null;
        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteUserAsync(user.Id, ct);
            deleted = true;
            Users = Rows(await _api.GetUsersAsync(ct));
        });

        // A form for the deleted user has nothing left to change.
        if (deleted && Editing?.Id == user.Id) CancelForm();
        if (deleted) SetNotice(() => string.Format(Strings.Users_Deleted, user.UserName));
    }

    // --- Loading -------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            _roles = CanGrant ? await _api.GetRolesAsync(ct) : [];
            Users = Rows(await _api.GetUsersAsync(ct));
        });
    }

    /// <summary>
    /// After a user was created or changed: the signed-in user's own record is renewed when it was the one that changed
    /// (their name or permissions may be different now), and the list is read again, as long as they may still see it.
    /// </summary>
    private async Task AfterChangeAsync(UserDto saved, CancellationToken cancellationToken)
    {
        if (saved.Id == Shell.CurrentUser?.Id) Shell.Session.User = saved;
        if (CanView) Users = Rows(await _api.GetUsersAsync(cancellationToken));
    }

    private IReadOnlyList<UserRow> Rows(IReadOnlyList<UserDto> users) =>
        [.. users.Select(u => new UserRow(u, u.Id == Shell.CurrentUser?.Id))];

    /// <summary>Nothing of a list the signed-in user may no longer see is kept.</summary>
    private void Reset()
    {
        Users = [];
        _roles = [];
        IsFormOpen = false;
        Editing = null;
        PendingDelete = null;
        SetNotice(null);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(UserNameProblem));
        OnPropertyChanged(nameof(DisplayNameProblem));
        OnPropertyChanged(nameof(AliasesProblem));
        OnPropertyChanged(nameof(PasswordProblem));
        SaveCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Picker.Refresh();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(AliasesHint));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(Users)); // the role texts
        NotifyStateChanged();
    }
}
