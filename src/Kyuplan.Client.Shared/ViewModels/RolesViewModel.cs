using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the role list.</summary>
public sealed partial class RoleRow(RoleDto role) : ObservableObject
{
    public RoleDto Role { get; } = role;

    public int Id => Role.Id;

    public string Name => Role.Name;

    /// <summary>The administrator role has every permission and cannot be changed or deleted.</summary>
    public bool IsSystem => Role.IsSystem;

    /// <summary>"All permissions", or "12 of 28 permissions".</summary>
    public string PermissionsText =>
        IsSystem ? Strings.Roles_AllPermissions : string.Format(Strings.Roles_PermissionCount, Role.Permissions.Count, Permission.All.Count);

    /// <summary>"1 user", "3 users".</summary>
    public string UsersText => Role.UserCount == 1 ? Strings.Roles_UsersOne : string.Format(Strings.Roles_UsersMany, Role.UserCount);

    /// <summary>The language changed: the texts are read again.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(PermissionsText));
        OnPropertyChanged(nameof(UsersText));
    }
}

/// <summary>
/// The roles page: bundles of permissions that are given to many users at once, with creating, changing and deleting them. The administrator
/// role is always there; it has every permission and cannot be changed or deleted. Used by both clients.
/// </summary>
public sealed partial class RolesViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private Func<string>? _notice;

    public RolesViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;

        Picker.Changed += (_, _) => FormEdited();
        // The permissions the plugins add are ticked like the program's own.
        Picker.SetPlugins(shell.Plugins);
        shell.PluginsChanged += (_, _) => Picker.SetPlugins(shell.Plugins);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Roles;

    protected override string? ViewPermission => Permission.RolesView;

    /// <summary>Whether the user may create, change and delete roles; without it the page only shows them.</summary>
    public bool CanEdit => Shell.Can(Permission.RolesEdit);

    protected override void OnPermissionsChanged()
    {
        if (!CanView || !CanEdit) Reset();

        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>The roles do not depend on the selected group or on Spond.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>The user counts change on the users page, so the list is read again whenever the page is shown (unless the user is in the middle of something).</summary>
    protected override bool ReloadsOnActivation => !IsFormOpen && !IsConfirmingDelete;

    /// <summary>All roles by name, the administrator role first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoles))]
    private IReadOnlyList<RoleRow> _roles = [];

    public bool HasRoles => Roles.Count > 0;

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>Role "Trainer" created.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The form: a new role, or an existing one ------------------------------------------------------------

    /// <summary>The permissions to tick, for the role in the form.</summary>
    public PermissionPickerViewModel Picker { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle))]
    private bool _isFormOpen;

    /// <summary>The role the form changes; <c>null</c> when it creates one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle))]
    private RoleRow? _editing;

    [ObservableProperty]
    private string _formName = "";

    public bool IsCreating => IsFormOpen && Editing is null;

    public string FormTitle => Editing is { } role ? string.Format(Strings.Roles_FormEdit, role.Name) : Strings.Roles_FormNew;

    public string? NameProblem
    {
        get
        {
            if (!IsFormOpen) return null;

            var name = FormName.Trim();
            if (RoleRules.CheckName(name) is not null)
                return FormName.Length > 0 || Editing is not null ? string.Format(Strings.Roles_NameInvalid, RoleRules.MaxNameLength) : null;

            return Roles.Any(r => r.Id != Editing?.Id && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) ? Strings.Roles_NameTaken : null;
        }
    }

    /// <summary>The form holds a role the API accepts and, for an existing role, one that differs from what is there.</summary>
    public bool CanSave =>
        CanEdit && IsFormOpen && !IsBusy && NameProblem is null && RoleRules.CheckName(FormName) is null
        && (Editing is null || FormName.Trim() != Editing.Name || !Picker.Selected.SequenceEqual(Editing.Role.Permissions));

    private bool CanBeginCreate() => CanEdit && !IsCreating;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        if (CanBeginCreate()) OpenForm(null); // Execute does not ask CanExecute: what the user may not do is not done
    }

    /// <summary>The administrator role cannot be changed.</summary>
    private bool CanBeginEdit(RoleRow? role) => CanEdit && role is { IsSystem: false } && !ReferenceEquals(role, Editing);

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(RoleRow role)
    {
        if (CanBeginEdit(role)) OpenForm(role);
    }

    private void OpenForm(RoleRow? role)
    {
        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = null;

        Editing = role;
        FormName = role?.Name ?? "";
        Picker.Set(role?.Role.Permissions ?? []);
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
        var input = new RoleInputDto(FormName.Trim(), Picker.Selected);
        RoleDto? saved = null;
        await LoadAsync(async ct =>
        {
            try
            {
                saved = editing is null ? await _api.CreateRoleAsync(input, ct) : await _api.UpdateRoleAsync(editing.Id, input, ct);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new PlannerApiException(Strings.Roles_NameTaken, ex.StatusCode, ex);
            }

            await ReadAfterChangeAsync(ct);
        });

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open so the user can correct it

        // It is saved, even if showing the new list failed: the form must not be left open to save it a second time.
        IsFormOpen = false;
        Editing = null;

        var name = saved.Name;
        var wasCreated = editing is null;
        SetNotice(() => string.Format(wasCreated ? Strings.Roles_Created : Strings.Roles_Saved, name));
        NotifyStateChanged();
    }

    partial void OnFormNameChanged(string value) => FormEdited();

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- Deleting --------------------------------------------------------------------------------------------

    /// <summary>The role waiting for the confirmation that deletes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmText))]
    private RoleRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    /// <summary>"Delete the role Trainer? It is taken from its 3 users; …"</summary>
    public string ConfirmText => PendingDelete is { } role
        ? role.Role.UserCount == 0
            ? string.Format(Strings.Roles_ConfirmDeleteUnused, role.Name)
            : string.Format(Strings.Roles_ConfirmDelete, role.Name, role.Role.UserCount)
        : "";

    /// <summary>The administrator role cannot be deleted.</summary>
    public bool CanDelete(RoleRow? role) => CanEdit && role is { IsSystem: false } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void AskDelete(RoleRow role)
    {
        if (!CanDelete(role)) return;

        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = role;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } role) return;

        PendingDelete = null;
        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteRoleAsync(role.Id, ct);
            deleted = true;
            await ReadAfterChangeAsync(ct);
        });

        // A form for the deleted role has nothing left to change.
        if (deleted && Editing?.Id == role.Id) CancelForm();
        if (deleted) SetNotice(() => string.Format(Strings.Roles_Deleted, role.Name));
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync() => await LoadAsync(ReadAsync);

    private async Task ReadAsync(CancellationToken cancellationToken) =>
        Roles = [.. (await _api.GetRolesAsync(cancellationToken)).Select(r => new RoleRow(r))];

    /// <summary>
    /// Reads the roles, and the signed-in user again: what a role allows changed what they may do when they have it. The change shows
    /// in the user's own pages (and in this one) at once.
    /// </summary>
    private async Task ReadAfterChangeAsync(CancellationToken cancellationToken)
    {
        await ReadAsync(cancellationToken);
        Shell.Session.User = await _api.GetAccountAsync(cancellationToken);
    }

    /// <summary>Nothing of what the signed-in user may no longer see or change is kept.</summary>
    private void Reset()
    {
        if (!CanView) Roles = [];
        IsFormOpen = false;
        Editing = null;
        PendingDelete = null;
        SetNotice(null);
    }

    partial void OnRolesChanged(IReadOnlyList<RoleRow> value) => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NameProblem));
        SaveCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var role in Roles) role.Refresh();
        Picker.Refresh();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NoticeText));
        NotifyStateChanged(); // the problems are worded in the language
    }
}
