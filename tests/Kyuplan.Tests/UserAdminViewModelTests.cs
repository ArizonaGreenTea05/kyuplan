using System.ComponentModel;
using System.Globalization;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The users page: the users, and creating, changing and deleting them, as far as the user's permissions go.</summary>
public sealed class UserAdminViewModelTests : IDisposable
{
    private const string BrowserHint = "The browser still holds the old login and will ask for the new one with the next request.";

    private readonly FakePlannerApi _api = new();
    private readonly LoginSession _session = new();
    private readonly FakeLoginStore _store = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly UserAdminViewModel _vm;

    public UserAdminViewModelTests()
    {
        _language.Select("en");

        // An administrator looks at three users: themselves, Anna and ben.
        _api.Account = Dto(1, "admin", "admin", [], true);
        _api.Users.AddRange([Dto(1, "admin", "admin", [], true), Dto(2, "Anna", "Anna", [], false), Dto(3, "ben", "ben", [], false)]);

        _shell = new ShellViewModel(_api, _language, _session);
        _vm = new UserAdminViewModel(_shell, _api, _store);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<UserAdminViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static UserRow Row(UserAdminViewModel vm, string userName) => vm.Users.Single(u => u.UserName == userName);

    private static string Quoted(string text) => $"“{text}”";

    private static RoleChoice Choice(UserAdminViewModel vm, string roleName) => vm.RoleChoices.Single(r => r.Name == roleName);

    private static PermissionRow Tick(UserAdminViewModel vm, string permission) =>
        vm.Picker.Groups.SelectMany(g => g.Permissions).Single(p => p.Key == permission);

    // --- Who gets to see it ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_for_somebody_who_may_not_see_users_does_not_even_ask_for_them()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false);

        var vm = await ShownAsync();

        Assert.False(vm.CanView);
        Assert.False(vm.CanEdit);
        Assert.Empty(vm.Users);
        Assert.Equal(0, _api.UserListRequests);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task An_administrator_sees_all_users_by_name_and_which_one_is_themselves()
    {
        var vm = await ShownAsync();

        Assert.True(vm.CanView);
        Assert.Equal("Users", vm.Title);
        Assert.Equal(["admin", "Anna", "ben"], vm.Users.Select(u => u.UserName));
        Assert.Equal(["admin"], vm.Users.Where(u => u.IsCurrent).Select(u => u.UserName));
        Assert.Equal(["Administrator", "User", "User"], vm.Users.Select(u => u.RolesText));
        Assert.Equal(1, _api.AccountRequests); // learned who is signed in on the way, as the browser has to
    }

    [Fact]
    public async Task A_failed_list_can_be_loaded_again()
    {
        _api.UserListFailure = new PlannerApiException("The list is not available.");
        var vm = await ShownAsync();
        Assert.Equal("The list is not available.", vm.ErrorMessage);
        Assert.Empty(vm.Users);

        _api.UserListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(3, vm.Users.Count);
    }

    // --- Creating ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_user_needs_a_proper_name_and_password_before_it_can_be_created()
    {
        var vm = await ShownAsync();
        Assert.False(vm.IsFormOpen);

        vm.BeginCreateCommand.Execute(null);
        Assert.True(vm.IsFormOpen);
        Assert.True(vm.IsCreating);
        Assert.Equal("New user", vm.FormTitle);
        Assert.False(vm.CanSave);
        Assert.Null(vm.UserNameProblem); // nothing typed yet: no nagging

        vm.FormUserName = "Eve";
        Assert.False(vm.CanSave); // the password is required

        vm.FormPassword = "short";
        Assert.False(vm.CanSave);
        Assert.Equal("The new password needs 12 to 128 characters.", vm.PasswordProblem);

        vm.FormPassword = "a good password";
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Null(vm.PasswordProblem);

        vm.FormUserName = "a:b";
        Assert.False(vm.CanSave);
        Assert.NotNull(vm.UserNameProblem);
    }

    [Fact]
    public async Task Creating_a_user_sends_the_form_closes_it_and_shows_the_new_list()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormUserName = "  Eve ";
        vm.FormPassword = "a good password";
        Choice(vm, "Administrator").IsSelected = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create-user:Eve"], _api.UserCalls);
        Assert.Equal(new UserInputDto("Eve", "a good password", "", [], [AdministratorRoleId], []).Show(), Assert.Single(_api.UserInputs).Show());
        Assert.False(vm.IsFormOpen);
        Assert.Equal(["admin", "Anna", "ben", "Eve"], vm.Users.Select(u => u.UserName));
        Assert.True(Row(vm, "Eve").HasAdministratorRole);
        Assert.Equal($"User {Quoted("Eve")} created.", vm.NoticeText);
        Assert.Equal("", vm.FormPassword);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_user_name_that_is_taken_keeps_the_form_open_to_be_corrected()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormUserName = "ANNA";
        vm.FormPassword = "a good password";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The user name or alias 'ANNA' is already taken.", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.HasNotice);
        Assert.Equal(3, vm.Users.Count);

        vm.FormUserName = "Eve";
        Assert.False(vm.HasError); // correcting the form takes the message back
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
        Assert.Equal(4, vm.Users.Count);
    }

    [Fact]
    public async Task Cancelling_the_form_changes_nothing()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormUserName = "Eve";
        vm.FormPassword = "a good password";

        vm.CancelFormCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.False(vm.CanSave);
        Assert.Empty(_api.UserCalls);
    }

    // --- Editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_starts_from_the_user_and_can_only_be_saved_once_something_differs()
    {
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));

        Assert.True(vm.IsFormOpen);
        Assert.False(vm.IsCreating);
        Assert.Equal($"Edit {Quoted("Anna")}", vm.FormTitle);
        Assert.Equal("Anna", vm.FormUserName);
        Assert.Equal(["User"], vm.RoleChoices.Where(r => r.IsSelected).Select(r => r.Name));
        Assert.Equal("", vm.FormPassword);
        Assert.False(vm.CanSave);

        vm.FormUserName = "Anna K";
        Assert.True(vm.CanSave);
        vm.FormUserName = " Anna ";
        Assert.False(vm.CanSave); // the same name

        Choice(vm, "Administrator").IsSelected = true;
        Assert.True(vm.CanSave);
        Choice(vm, "Administrator").IsSelected = false;
        Assert.False(vm.CanSave);

        Tick(vm, Permission.CommentsDeleteAny).IsSelected = true;
        Assert.True(vm.CanSave);
        Tick(vm, Permission.CommentsDeleteAny).IsSelected = false;
        Tick(vm, Permission.CommentsView).IsSelected = false; // the permission that was ticked with it
        Assert.False(vm.CanSave);

        vm.FormPassword = "short";
        Assert.False(vm.CanSave);
        vm.FormPassword = "a good password";
        Assert.True(vm.CanSave); // a new password alone is a change
    }

    [Fact]
    public async Task The_form_shows_the_display_name_and_the_aliases_and_sends_what_was_changed()
    {
        _api.Users[1] = Dto(2, "Anna", "Anna K.", ["Annie"], false);
        var vm = await ShownAsync();
        var row = Row(vm, "Anna");
        Assert.Equal(("Anna K.", "Annie"), (row.DisplayName, row.AliasesText));

        vm.BeginEditCommand.Execute(row);
        Assert.Equal(("Anna K.", "Annie"), (vm.FormDisplayName, vm.FormAliases));
        Assert.False(vm.CanSave);

        vm.FormAliases = "Annie\nA.K.";
        Assert.True(vm.CanSave);
        vm.FormAliases = "Annie\n\n"; // the same, with blank lines
        vm.FormDisplayName = "Anna";
        Assert.True(vm.CanSave);
        vm.FormDisplayName = "Anna K.";
        Assert.False(vm.CanSave);

        vm.FormAliases = "Annie\nA.K.";
        vm.FormDisplayName = ""; // empty: the user name
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new UserInputDto("Anna", null, "", ["Annie", "A.K."], [UserRoleId], []).Show(), Assert.Single(_api.UserInputs).Show());
        Assert.Equal(("Anna", "Annie, A.K."), (Row(vm, "Anna").DisplayName, Row(vm, "Anna").AliasesText));
    }

    [Fact]
    public async Task A_new_user_can_start_with_aliases_and_what_is_wrong_with_them_is_said()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormUserName = "Eve";
        vm.FormPassword = "a good password";
        Assert.True(vm.CanSave);

        vm.FormAliases = "eve";
        Assert.NotNull(vm.AliasesProblem);
        Assert.False(vm.CanSave);

        vm.FormAliases = "Evie\nE.";
        vm.FormDisplayName = new string('x', AccountRules.MaxDisplayNameLength + 1);
        Assert.Null(vm.AliasesProblem);
        Assert.NotNull(vm.DisplayNameProblem);
        Assert.False(vm.CanSave);

        vm.FormDisplayName = "Eve E.";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new UserInputDto("Eve", "a good password", "Eve E.", ["Evie", "E."], [], []).Show(), Assert.Single(_api.UserInputs).Show());
        Assert.Equal("Evie, E.", Row(vm, "Eve").AliasesText);
    }

    [Fact]
    public async Task An_administrator_who_takes_the_alias_they_signed_in_by_from_themselves_gets_the_user_name_as_the_login()
    {
        _api.Users[0] = Dto(1, "admin", "admin", ["boss"], true);
        _api.Account = _api.Users[0];
        _session.Login = new Login("boss", "the password");
        _session.User = _api.Account;
        _store.Saved = _session.Login;
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormDisplayName = "Chief";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new Login("boss", "the password"), _session.Login); // nothing that matters to the login changed

        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormAliases = "";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new Login("admin", "the password"), _session.Login);
        Assert.Equal(new Login("admin", "the password"), _store.Saved);
    }

    [Fact]
    public async Task Leaving_the_password_empty_keeps_it_and_a_given_one_replaces_it()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        vm.FormUserName = "Anna K";
        await vm.SaveCommand.ExecuteAsync(null);

        vm.BeginEditCommand.Execute(Row(vm, "Anna K"));
        vm.FormPassword = "reset by the admin";
        Choice(vm, "Administrator").IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["update-user:2", "update-user:2"], _api.UserCalls);
        Assert.Equal(new UserInputDto("Anna K", null, "Anna", [], [UserRoleId], []).Show(), _api.UserInputs[0].Show());
        Assert.Equal(new UserInputDto("Anna K", "reset by the admin", "Anna", [], [AdministratorRoleId, UserRoleId], []).Show(), _api.UserInputs[1].Show());
        Assert.Equal($"User {Quoted("Anna K")} saved.", vm.NoticeText);
        Assert.True(Row(vm, "Anna K").HasAdministratorRole);
        Assert.False(vm.IsFormOpen);
    }

    [Fact]
    public async Task The_only_administrator_cannot_give_up_the_role_but_one_of_two_can()
    {
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        Assert.False(Choice(vm, "Administrator").IsEnabled);
        Assert.True(vm.HasLockedRole);
        Assert.True(Choice(vm, "User").IsEnabled);

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(Choice(vm, "Administrator").IsEnabled);
        Assert.False(vm.HasLockedRole);
        Choice(vm, "Administrator").IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        Assert.True(Choice(vm, "Administrator").IsEnabled); // there is another one now
    }

    [Fact]
    public async Task Changing_the_own_name_updates_who_is_signed_in_and_reminds_that_the_browser_asks_again()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormUserName = "boss";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("boss", _shell.CurrentUser!.UserName);
        Assert.True(Row(vm, "boss").IsCurrent);
        Assert.Equal($"User {Quoted("boss")} saved. {BrowserHint}", vm.NoticeText);
    }

    // --- A client that sends the login itself (WPF) -------------------------------------------------------------

    [Fact]
    public async Task A_client_that_sends_the_login_gets_the_new_name_of_the_signed_in_user_and_has_nothing_to_remind_of()
    {
        _session.Login = new Login("admin", "secret password");
        _store.Saved = _session.Login; // remembered on this computer
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormUserName = "boss";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new Login("boss", "secret password"), _session.Login);
        Assert.Equal(new Login("boss", "secret password"), _store.Saved);
        Assert.Equal($"User {Quoted("boss")} saved.", vm.NoticeText); // nobody has to enter anything again
    }

    [Fact]
    public async Task A_client_that_sends_the_login_gets_the_new_password_of_the_signed_in_user()
    {
        _session.Login = new Login("admin", "secret password");
        _store.Saved = _session.Login;
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormPassword = "another password";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new Login("admin", "another password"), _session.Login);
        Assert.Equal(new Login("admin", "another password"), _store.Saved);
    }

    [Fact]
    public async Task The_new_login_is_in_place_before_the_list_is_read_again()
    {
        // The API refuses the old login from the moment it was changed: the call that shows the new list must already carry the new one.
        _session.Login = new Login("admin", "secret password");
        var vm = await ShownAsync();
        var requestsBefore = _api.UserListRequests;
        var requestsWhenRenewed = -1;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginSession.Login)) requestsWhenRenewed = _api.UserListRequests;
        };
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormUserName = "boss";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(requestsBefore, requestsWhenRenewed);
        Assert.True(_api.UserListRequests > requestsBefore); // and the list was read afterwards
    }

    [Fact]
    public async Task A_login_that_was_not_remembered_is_not_written_to_the_store()
    {
        _session.Login = new Login("admin", "secret password"); // typed in, not kept
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormUserName = "boss";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new Login("boss", "secret password"), _session.Login);
        Assert.Equal(0, _store.Saves);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public async Task The_login_stays_when_somebody_else_is_changed()
    {
        var login = new Login("admin", "secret password");
        _session.Login = login;
        _store.Saved = login;
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        vm.FormUserName = "Anna K";
        vm.FormPassword = "a new password for Anna";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(login, _session.Login);
        Assert.Equal(login, _store.Saved);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task The_login_stays_when_the_signed_in_user_changes_nothing_of_it()
    {
        var login = new Login("admin", "secret password");
        _session.Login = login;
        _store.Saved = login;
        _api.Users[1] = Dto(2, "Anna", "Anna", [], true); // somebody else stays administrator, so the rights can be given up
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        Choice(vm, "Administrator").IsSelected = false; // only the roles: the name and the password are what the login is made of
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(login, _session.Login);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task Without_a_login_of_its_own_the_client_is_reminded_that_it_has_to_ask_again()
    {
        // The browser: the login is the browser's, and it only asks again with its next request.
        Assert.Null(_session.Login);
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        vm.FormPassword = "another password";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(_session.Login);
        Assert.Equal($"User {Quoted("admin")} saved. {BrowserHint}", vm.NoticeText);
    }

    // --- Buttons that only work when they change something -----------------------------------------------------

    [Fact]
    public async Task Opening_the_form_for_a_new_user_is_off_while_that_form_is_open()
    {
        var vm = await ShownAsync();
        Assert.True(vm.BeginCreateCommand.CanExecute(null));

        vm.BeginCreateCommand.Execute(null);
        Assert.False(vm.BeginCreateCommand.CanExecute(null)); // it would only throw away what was typed

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(vm.BeginCreateCommand.CanExecute(null));

        vm.CancelFormCommand.Execute(null);
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Deleting_is_off_for_the_signed_in_user_only()
    {
        var vm = await ShownAsync();

        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "admin")));
        Assert.True(vm.AskDeleteCommand.CanExecute(Row(vm, "ben")));
        Assert.False(vm.AskDeleteCommand.CanExecute(null)); // WPF asks before the row's button has its row
    }

    [Fact]
    public async Task The_hint_under_the_password_says_what_a_new_password_has_to_be_or_that_empty_keeps_it()
    {
        var vm = await ShownAsync();

        vm.BeginCreateCommand.Execute(null);
        Assert.Equal(
            $"{AccountRules.MinPasswordLength} to {AccountRules.MaxPasswordLength} characters; spaces, umlauts and colons are fine.", vm.PasswordHint);

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.Equal("Leave empty to keep the current password.", vm.PasswordHint);

        _language.Select("de");
        Assert.Equal("Leer lassen, um das aktuelle Passwort zu behalten.", vm.PasswordHint);
    }

    [Fact]
    public async Task An_administrator_who_steps_down_loses_the_page_and_nothing_is_asked_of_the_api_any_more()
    {
        _api.Users[1] = Dto(2, "Anna", "Anna", [], true); // somebody else stays administrator
        var vm = await ShownAsync();
        var requests = _api.UserListRequests;
        vm.BeginEditCommand.Execute(Row(vm, "admin"));
        Choice(vm, "Administrator").IsSelected = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(_shell.Can(Permission.UsersView));
        Assert.False(vm.CanView);
        Assert.Empty(vm.Users);
        Assert.False(vm.IsFormOpen);
        Assert.Equal(requests, _api.UserListRequests); // a list for a user who may not see it would only be a 403
        Assert.False(vm.HasError);
    }

    // --- Deleting ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Everybody_but_the_signed_in_user_can_be_deleted_after_a_question()
    {
        var vm = await ShownAsync();
        Assert.False(vm.CanDelete(Row(vm, "admin")));
        Assert.True(vm.CanDelete(Row(vm, "ben")));

        vm.AskDeleteCommand.Execute(Row(vm, "admin"));
        Assert.False(vm.IsConfirmingDelete); // not even asked

        vm.AskDeleteCommand.Execute(Row(vm, "ben"));
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal($"Delete the user {Quoted("ben")}? The login stops working at once.", vm.ConfirmText);
        Assert.Empty(_api.UserCalls); // nothing happens before the answer

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Empty(_api.UserCalls);

        vm.AskDeleteCommand.Execute(Row(vm, "ben"));
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(["delete-user:3"], _api.UserCalls);
        Assert.Equal(["admin", "Anna"], vm.Users.Select(u => u.UserName));
        Assert.Equal($"User {Quoted("ben")} deleted.", vm.NoticeText);
        Assert.False(vm.IsConfirmingDelete);
    }

    [Fact]
    public async Task Confirming_without_a_question_does_nothing()
    {
        var vm = await ShownAsync();

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Empty(_api.UserCalls);
    }

    [Fact]
    public async Task Deleting_the_user_whose_form_is_open_closes_the_form()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "ben"));
        vm.AskDeleteCommand.Execute(Row(vm, "ben"));
        Assert.True(vm.IsFormOpen);

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
        Assert.Null(vm.Editing);
    }

    [Fact]
    public async Task A_failed_delete_is_reported_and_the_list_stays()
    {
        var vm = await ShownAsync();
        vm.AskDeleteCommand.Execute(Row(vm, "ben"));
        _api.FailWith = new PlannerApiException("Cannot reach the Kyuplan API.");

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal("Cannot reach the Kyuplan API.", vm.ErrorMessage);
        Assert.Equal(3, vm.Users.Count);
        Assert.False(vm.HasNotice);
    }

    // --- What the user may do ------------------------------------------------------------------------------------

    private async Task<UserAdminViewModel> ShownAs(UserDto account)
    {
        _api.Account = account;
        _api.Users.Add(account);
        return await ShownAsync();
    }

    [Fact]
    public async Task Somebody_who_may_only_look_sees_the_list_and_none_of_the_buttons_work()
    {
        var vm = await ShownAs(WithPermissions(9, "mia", Permission.UsersView));

        Assert.True(vm.CanView);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanGrant);
        Assert.False(vm.CanDeleteUsers);
        Assert.Equal(4, vm.Users.Count);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(Row(vm, "Anna")));
        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "ben")));
        Assert.Equal(0, _api.RoleListRequests); // the roles are only needed to give them
    }

    [Fact]
    public async Task Somebody_who_may_edit_but_not_grant_changes_names_and_leaves_the_roles_and_permissions_alone()
    {
        var vm = await ShownAs(WithPermissions(9, "mia", Permission.UsersView, Permission.UsersEdit));
        Assert.False(vm.CanGrant);

        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.Picker.IsEnabled);
        vm.FormDisplayName = "Anna K.";
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);

        // No roles and no permissions in the request: the server keeps them (and would refuse a request that had them).
        Assert.Equal(new UserInputDto("Anna", null, "Anna K.", []).Show(), Assert.Single(_api.UserInputs).Show());
        Assert.Equal("User", Row(vm, "Anna").RolesText);
    }

    [Fact]
    public async Task Somebody_who_may_grant_gives_roles_and_own_permissions_and_ticking_a_permission_ticks_what_shows_its_area()
    {
        var vm = await ShownAs(WithPermissions(9, "mia", Permission.UsersView, Permission.UsersEdit, Permission.UsersGrant));
        vm.BeginEditCommand.Execute(Row(vm, "ben"));
        Assert.True(vm.Picker.IsEnabled);
        Assert.False(vm.CanSave);

        Tick(vm, Permission.RolesEdit).IsSelected = true;
        Assert.True(Tick(vm, Permission.RolesView).IsSelected); // roles.edit needs roles.view
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            new UserInputDto("ben", null, "ben", [], [UserRoleId], [Permission.RolesView, Permission.RolesEdit]).Show(),
            Assert.Single(_api.UserInputs).Show());
        Assert.Equal([Permission.RolesView, Permission.RolesEdit], Row(vm, "ben").User.Permissions);
        Assert.True(Row(vm, "ben").User.Can(Permission.RolesEdit));
    }

    [Fact]
    public async Task Somebody_who_may_not_delete_users_has_no_delete_button_that_works()
    {
        var vm = await ShownAs(WithPermissions(9, "mia", Permission.UsersView, Permission.UsersEdit));

        Assert.False(vm.CanDelete(Row(vm, "ben")));
        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "ben")));
    }

    [Fact]
    public async Task When_the_permissions_of_the_user_change_the_page_closes_what_it_may_no_longer_do()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(vm.IsFormOpen);

        _shell.Session.User = WithPermissions(1, "admin", Permission.UsersView); // an administrator took the rest away

        Assert.False(vm.IsFormOpen);
        Assert.False(vm.CanEdit);
        Assert.Equal(3, vm.Users.Count); // may still look
        Assert.False(vm.BeginCreateCommand.CanExecute(null));

        _shell.Session.User = Nobody(1, "admin");
        Assert.Empty(vm.Users);
    }

    // --- Language ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "ben"));
        vm.AskDeleteCommand.Execute(Row(vm, "ben"));
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _language.Select("de");

        Assert.Equal("Benutzer", vm.Title);
        Assert.Equal("„ben“ bearbeiten", vm.FormTitle);
        Assert.StartsWith("Den Benutzer „ben“ löschen?", vm.ConfirmText);
        Assert.Equal("Administrator", Row(vm, "admin").RolesText); // the names of roles are data: not translated
        Assert.Equal("User", Row(vm, "ben").RolesText);
        Assert.Contains(nameof(UserAdminViewModel.FormTitle), changed);
        Assert.Contains(nameof(UserAdminViewModel.ConfirmText), changed);
        Assert.Contains(nameof(UserAdminViewModel.Users), changed);
    }
}
