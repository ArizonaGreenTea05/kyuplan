using System.ComponentModel;
using System.Globalization;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The roles page: bundles of permissions, and creating, changing and deleting them.</summary>
public sealed class RolesViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly RolesViewModel _vm;

    public RolesViewModelTests()
    {
        _language.Select("en");

        // An administrator looks at the two roles there always are; the administrator is the one user.
        _api.Account = Dto(1, "admin", "admin", [], true);
        _api.Users.Add(_api.Account);
        _shell = new ShellViewModel(_api, _language);
        _vm = new RolesViewModel(_shell, _api);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<RolesViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static RoleRow Row(RolesViewModel vm, string name) => vm.Roles.Single(r => r.Name == name);

    private static PermissionRow Tick(RolesViewModel vm, string permission) =>
        vm.Picker.Groups.SelectMany(g => g.Permissions).Single(p => p.Key == permission);

    private static string Quoted(string text) => $"“{text}”";

    // --- Who gets to see it ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_for_somebody_who_may_not_see_roles_does_not_even_ask_for_them()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false);

        var vm = await ShownAsync();

        Assert.False(vm.CanView);
        Assert.Empty(vm.Roles);
        Assert.Equal(0, _api.RoleListRequests);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Somebody_who_may_only_see_the_roles_cannot_create_change_or_delete_them()
    {
        _api.Account = WithPermissions(2, "mia", Permission.RolesView);
        var vm = await ShownAsync();

        Assert.Equal(2, vm.Roles.Count);
        Assert.False(vm.CanEdit);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(Row(vm, "User")));
        Assert.False(vm.CanDelete(Row(vm, "User")));
        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "User")));
    }

    // --- The list ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_roles_are_listed_administrator_first_with_what_they_allow_and_how_many_users_have_them()
    {
        var vm = await ShownAsync();

        Assert.Equal("Roles", vm.Title);
        Assert.Equal(["Administrator", "User"], vm.Roles.Select(r => r.Name));
        Assert.Equal(["All permissions", $"{UserRolePermissions.Count} of {Permission.All.Count} permissions"], vm.Roles.Select(r => r.PermissionsText));
        Assert.Equal(["1 user", "0 users"], vm.Roles.Select(r => r.UsersText));
        Assert.True(Row(vm, "Administrator").IsSystem);
    }

    [Fact]
    public async Task A_failed_list_can_be_loaded_again()
    {
        _api.RoleListFailure = new PlannerApiException("The list is not available.");
        var vm = await ShownAsync();
        Assert.Equal("The list is not available.", vm.ErrorMessage);
        Assert.Empty(vm.Roles);

        _api.RoleListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(2, vm.Roles.Count);
    }

    // --- Creating ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_role_needs_a_name_that_is_new_and_creating_it_sends_the_ticked_permissions()
    {
        var vm = await ShownAsync();

        vm.BeginCreateCommand.Execute(null);
        Assert.True(vm.IsCreating);
        Assert.Equal("New role", vm.FormTitle);
        Assert.False(vm.CanSave);
        Assert.Null(vm.NameProblem); // nothing typed yet: no nagging
        Assert.False(vm.BeginCreateCommand.CanExecute(null)); // it would only throw away what was typed

        vm.FormName = "user"; // the role "User" in another case
        Assert.Equal("There is already a role with this name.", vm.NameProblem);
        Assert.False(vm.CanSave);

        vm.FormName = "  Trainer ";
        Assert.Null(vm.NameProblem);
        Assert.True(vm.CanSave);

        Tick(vm, Permission.MembersEdit).IsSelected = true;
        Assert.True(Tick(vm, Permission.MembersView).IsSelected); // what shows the area comes with it
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create-role:Trainer"], _api.RoleCalls);
        Assert.Equal(new RoleInputDto("Trainer", [Permission.MembersView, Permission.MembersEdit]).Name, Assert.Single(_api.RoleInputs).Name);
        Assert.Equal([Permission.MembersView, Permission.MembersEdit], _api.RoleInputs[0].Permissions);
        Assert.False(vm.IsFormOpen);
        Assert.Equal(["Administrator", "Trainer", "User"], vm.Roles.Select(r => r.Name));
        Assert.Equal($"Role {Quoted("Trainer")} created.", vm.NoticeText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_name_that_was_taken_meanwhile_keeps_the_form_open_to_be_corrected()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Coach";
        _api.Roles.Add(new RoleDto(50, "coach", false, [], 0)); // somebody else made it a moment ago

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("There is already a role with this name.", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.HasNotice);

        vm.FormName = "Coach 2";
        Assert.False(vm.HasError); // correcting the form takes the message back
    }

    [Fact]
    public async Task Cancelling_the_form_changes_nothing()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Trainer";

        vm.CancelFormCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.False(vm.CanSave);
        Assert.Empty(_api.RoleCalls);
    }

    // --- Editing ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_starts_from_the_role_and_can_only_be_saved_once_something_differs()
    {
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "User"));

        Assert.Equal("Edit “User”", vm.FormTitle);
        Assert.Equal("User", vm.FormName);
        Assert.Equal(UserRolePermissions, vm.Picker.Selected);
        Assert.False(vm.CanSave);
        Assert.False(vm.BeginEditCommand.CanExecute(Row(vm, "User"))); // it is open already

        vm.FormName = "Users";
        Assert.True(vm.CanSave);
        vm.FormName = " User ";
        Assert.False(vm.CanSave); // the same name

        Tick(vm, Permission.CommentsView).IsSelected = false; // takes the rest of the comments with it
        Assert.Equal([Permission.CommentsView], UserRolePermissions.Except(vm.Picker.Selected).Intersect([Permission.CommentsView]));
        Assert.DoesNotContain(vm.Picker.Selected, p => p.StartsWith("comments."));
        Assert.True(vm.CanSave);
        Tick(vm, Permission.CommentsView).IsSelected = true;
        Assert.True(vm.CanSave); // the others of the area are still off

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["update-role:2"], _api.RoleCalls);
        Assert.DoesNotContain(Row(vm, "User").Role.Permissions, p => p == Permission.CommentsWrite);
        Assert.Equal($"Role {Quoted("User")} saved.", vm.NoticeText);
    }

    [Fact]
    public async Task The_administrator_role_cannot_be_edited_or_deleted()
    {
        var vm = await ShownAsync();
        var administrator = Row(vm, "Administrator");

        Assert.False(vm.BeginEditCommand.CanExecute(administrator));
        Assert.False(vm.CanDelete(administrator));
        Assert.False(vm.AskDeleteCommand.CanExecute(administrator));

        vm.BeginEditCommand.Execute(administrator);
        vm.AskDeleteCommand.Execute(administrator);
        Assert.False(vm.IsFormOpen);
        Assert.False(vm.IsConfirmingDelete);
    }

    [Fact]
    public async Task Changing_a_role_the_signed_in_user_has_reads_the_user_again()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false); // has the role "User" and may edit roles only after the role is changed...
        var vm = await ShownAsync();
        Assert.False(vm.CanView);

        // An administrator-level user: the roles are readable and editable.
        _api.Account = WithPermissions(3, "mia", Permission.RolesView, Permission.RolesEdit);
        _shell.Session.User = _api.Account;
        await Eventually(() => vm.Roles.Count == 2);
        var requests = _api.AccountRequests;
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Trainer";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(requests + 1, _api.AccountRequests); // what the role allows may be what the user may do
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    // --- Deleting ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_role_is_deleted_after_a_question_that_says_how_many_users_lose_it()
    {
        _api.Roles.Add(new RoleDto(10, "Trainer", false, [Permission.MembersView], 0));
        _api.Roles.Add(new RoleDto(11, "Unused", false, [], 0));
        _api.Users.Add(Dto(2, "Anna", "Anna", [], false) with { Roles = [new RoleRefDto(10, "Trainer")] });
        _api.Users.Add(Dto(3, "ben", "ben", [], false) with { Roles = [new RoleRefDto(10, "Trainer")] });
        var vm = await ShownAsync();

        vm.AskDeleteCommand.Execute(Row(vm, "Trainer"));
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal($"Delete the role {Quoted("Trainer")}? It is taken from its 2 users; their other roles and own permissions stay.", vm.ConfirmText);
        Assert.Empty(_api.RoleCalls); // nothing happens before the answer

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);

        vm.AskDeleteCommand.Execute(Row(vm, "Unused"));
        Assert.Equal($"Delete the role {Quoted("Unused")}? Nobody has it.", vm.ConfirmText);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(["delete-role:11"], _api.RoleCalls);
        Assert.Equal(["Administrator", "Trainer", "User"], vm.Roles.Select(r => r.Name));
        Assert.Equal($"Role {Quoted("Unused")} deleted.", vm.NoticeText);
    }

    [Fact]
    public async Task Deleting_the_role_whose_form_is_open_closes_the_form()
    {
        _api.Roles.Add(new RoleDto(10, "Trainer", false, [], 0));
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Trainer"));
        vm.AskDeleteCommand.Execute(Row(vm, "Trainer"));

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
        Assert.Null(vm.Editing);
    }

    [Fact]
    public async Task Confirming_without_a_question_does_nothing()
    {
        var vm = await ShownAsync();

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Empty(_api.RoleCalls);
    }

    // --- When the permissions of the user change ---------------------------------------------------------------

    [Fact]
    public async Task A_user_who_loses_the_permission_to_edit_loses_the_form_and_one_who_loses_the_page_loses_the_list()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "User"));
        Assert.True(vm.IsFormOpen);

        _shell.Session.User = WithPermissions(1, "admin", Permission.RolesView);

        Assert.False(vm.IsFormOpen);
        Assert.False(vm.CanEdit);
        Assert.Equal(2, vm.Roles.Count);

        _shell.Session.User = Nobody(1, "admin");
        Assert.False(vm.CanView);
        Assert.Empty(vm.Roles);
    }

    // --- Language ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "User"));
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _language.Select("de");

        Assert.Equal("Rollen", vm.Title);
        Assert.Equal("„User“ bearbeiten", vm.FormTitle);
        Assert.Equal("Alle Berechtigungen", Row(vm, "Administrator").PermissionsText);
        Assert.Equal($"{UserRolePermissions.Count} von {Permission.All.Count} Berechtigungen", Row(vm, "User").PermissionsText);
        Assert.Equal("Die Übersicht sehen", vm.Picker.Groups[0].Permissions[0].Label);
        Assert.Contains(nameof(RolesViewModel.FormTitle), changed);
    }
}

/// <summary>The tick boxes of the permissions: what is ticked is what is sent, and the permission that shows an area comes with every permission of it.</summary>
public sealed class PermissionPickerTests : IDisposable
{
    private readonly PermissionPickerViewModel _picker = new();

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private PermissionRow Row(string permission) => _picker.Groups.SelectMany(g => g.Permissions).Single(p => p.Key == permission);

    [Fact]
    public void Every_permission_of_the_catalogue_is_offered_by_area_in_the_order_of_the_catalogue()
    {
        Assert.Equal(Permission.Groups.Select(g => g.Name), Permission.Groups.Select(g => g.Name)); // the catalogue itself
        Assert.Equal(Permission.All, _picker.Groups.SelectMany(g => g.Permissions).Select(p => p.Key));
        Assert.Equal(Permission.Groups.Count, _picker.Groups.Count);
        Assert.Empty(_picker.Selected);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void Every_area_and_every_permission_has_a_text_in_each_language(string language)
    {
        new LanguageService().Select(language);
        _picker.Refresh();

        Assert.All(_picker.Groups, group => Assert.False(group.Label.StartsWith("PermissionGroup_") || string.IsNullOrWhiteSpace(group.Label), group.Label));
        Assert.All(_picker.Groups.SelectMany(g => g.Permissions), row => Assert.NotEqual(row.Key, row.Label));
        Assert.Equal(Permission.All.Count, _picker.Groups.SelectMany(g => g.Permissions).Select(p => p.Label).Distinct().Count()); // none reads like another
    }

    [Fact]
    public void Ticking_a_permission_ticks_the_one_that_shows_its_area_and_unticking_that_one_unticks_the_rest()
    {
        var changes = 0;
        _picker.Changed += (_, _) => changes++;

        Row(Permission.CommentsDelete).IsSelected = true;

        Assert.Equal([Permission.CommentsView, Permission.CommentsDelete], _picker.Selected);
        Assert.Equal(1, changes); // one tick of the user, however many boxes it ticked

        Row(Permission.CommentsWrite).IsSelected = true;
        Row(Permission.CommentsWrite).IsSelected = false; // only that one
        Assert.Equal([Permission.CommentsView, Permission.CommentsDelete], _picker.Selected);

        Row(Permission.CommentsView).IsSelected = false;
        Assert.Empty(_picker.Selected);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Filling_the_picker_takes_the_permissions_as_they_are_and_is_no_change_of_the_user()
    {
        var changes = 0;
        _picker.Changed += (_, _) => changes++;

        _picker.Set([Permission.TagsEdit, Permission.BeltsView]); // an edit without its .view, as an old version or the API may have saved it

        Assert.Equal([Permission.BeltsView, Permission.TagsEdit], _picker.Selected); // in the order of the catalogue
        Assert.Equal(0, changes);

        _picker.Set([]);
        Assert.Empty(_picker.Selected);
    }
}
