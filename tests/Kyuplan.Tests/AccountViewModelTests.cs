using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The page where users change their own user name and password, as the clients show it.</summary>
public sealed class AccountViewModelTests : IDisposable
{
    private const string BrowserHint = "The browser still holds the old login and will ask for the new one with the next request.";

    private readonly FakePlannerApi _api = new();
    private readonly FakeLoginStore _store = new();
    private readonly LoginSession _session = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly AccountViewModel _vm;

    public AccountViewModelTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language, _session);
        _vm = new AccountViewModel(_shell, _api, _store);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    /// <summary>A client that asked for the login itself (WPF), and was asked to remember it.</summary>
    private void SignInAsClient(bool remembered)
    {
        _session.Login = new Login("anna", "current password");
        _session.User = _api.Account;
        if (remembered) _store.Saved = _session.Login;
    }

    private async Task<AccountViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static void FillPasswords(AccountViewModel vm, string current = "current password", string next = "a new password")
    {
        vm.CurrentPassword = current;
        vm.NewPassword = next;
        vm.ConfirmPassword = next;
    }

    // --- Showing the account -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_shows_the_signed_in_user_and_the_rights()
    {
        _api.Account = Dto(1, "anna", "anna", [], false);

        await _vm.ActivateAsync();

        Assert.Equal("Account", _vm.Title);
        Assert.Equal("anna", _vm.UserName);
        Assert.Equal("User", _vm.RoleText);
        Assert.False(_vm.HasError);

        var bossApi = new FakePlannerApi { Account = Dto(2, "boss", "boss", [], true) };
        var admin = new AccountViewModel(new ShellViewModel(bossApi, _language, new LoginSession()), bossApi, _store);
        await admin.ActivateAsync();
        Assert.Equal("Administrator", admin.RoleText);
    }

    [Fact]
    public async Task Changing_the_group_or_refreshing_neither_reloads_the_page_nor_discards_what_was_typed()
    {
        await _vm.ActivateAsync();
        var requests = _api.AccountRequests;
        _vm.UserName = "typed but not saved";

        _shell.SelectedGroupId = "g2";
        await _shell.RefreshDataCommand.ExecuteAsync(null);

        Assert.Equal(requests + 1, _api.AccountRequests); // only the shell learns again what the user may do, once; the page reads nothing
        Assert.Equal("typed but not saved", _vm.UserName);
    }

    [Fact]
    public async Task Somebody_else_signing_in_leaves_nothing_of_the_previous_form_behind()
    {
        await _vm.ActivateAsync();
        _vm.UserName = "typed";
        FillPasswords(_vm);

        _session.User = Dto(2, "ben", "ben", [], false);

        Assert.Equal("ben", _vm.UserName);
        Assert.Equal("", _vm.CurrentPassword);
        Assert.Equal("", _vm.NewPassword);
        Assert.Equal("", _vm.ConfirmPassword);
    }

    // --- User name -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_user_name_can_only_be_saved_while_it_is_a_valid_name_the_user_does_not_have_yet()
    {
        var vm = await ShownAsync();
        var changes = 0;
        vm.SaveProfileCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(vm.CanSaveProfile);
        Assert.Null(vm.UserNameProblem);

        vm.UserName = "  anna ";
        Assert.False(vm.CanSaveProfile); // the same name, only padded

        vm.UserName = "Anna K";
        Assert.True(vm.CanSaveProfile);
        Assert.True(vm.SaveProfileCommand.CanExecute(null));
        Assert.Null(vm.UserNameProblem);
        Assert.True(changes > 0);

        foreach (var bad in new[] { "", "   ", "a:b", "line\nbreak", new string('x', AccountRules.MaxUserNameLength + 1) })
        {
            vm.UserName = bad;
            Assert.False(vm.CanSaveProfile);
            Assert.False(vm.SaveProfileCommand.CanExecute(null));
            Assert.Equal($"A user name is required, at most {AccountRules.MaxUserNameLength} characters long, without a colon.", vm.UserNameProblem);
        }
    }

    [Fact]
    public async Task The_display_name_and_the_aliases_are_filled_in_and_saved_with_the_names_only_when_they_change()
    {
        _api.Account = Dto(1, "anna", "Anna K.", ["Annie"], false);
        var vm = await ShownAsync();

        Assert.Equal("Anna K.", vm.DisplayName);
        Assert.Equal("Annie", vm.AliasesText);
        Assert.False(vm.CanSaveProfile);

        vm.AliasesText = "\nAnnie\r\n"; // the same alias, only padded with line breaks
        vm.DisplayName = "  Anna K.  ";
        Assert.False(vm.CanSaveProfile);

        vm.AliasesText = "A.K.\nAnnie";
        Assert.True(vm.CanSaveProfile);
        vm.AliasesText = "Annie";
        vm.DisplayName = "The Anna";
        Assert.True(vm.CanSaveProfile);

        vm.AliasesText = "A.K.\nAnnie";
        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("anna|||The Anna|A.K.,Annie", Assert.Single(_api.AccountUpdates).Show());
        Assert.Equal("The Anna", _session.User!.DisplayName);
        Assert.Equal(["A.K.", "Annie"], _session.User.Aliases);
        Assert.Equal("The Anna", vm.DisplayName);
        Assert.Equal("A.K.\nAnnie", vm.AliasesText);
        Assert.False(vm.CanSaveProfile); // nothing left to save

        vm.DisplayName = ""; // empty: the user name is shown
        Assert.True(vm.CanSaveProfile);
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal("anna", vm.DisplayName);
    }

    [Fact]
    public async Task What_is_wrong_with_the_display_name_or_the_aliases_is_said_and_keeps_the_page_from_saving()
    {
        var vm = await ShownAsync();
        vm.UserName = "Anna K"; // a change, so that only a problem can keep the button off
        Assert.True(vm.CanSaveProfile);

        vm.AliasesText = "anna k";
        Assert.Equal(
            $"At most {AccountRules.MaxAliases} aliases, one per line, each written like a user name (at most {AccountRules.MaxUserNameLength} characters, no colon). " +
            "None may appear twice or equal the user name.",
            vm.AliasesProblem);
        foreach (var bad in new[] { "x\nX", "a:b", new string('x', AccountRules.MaxUserNameLength + 1), string.Join("\n", Enumerable.Range(1, AccountRules.MaxAliases + 1)) })
        {
            vm.AliasesText = bad;
            Assert.NotNull(vm.AliasesProblem);
            Assert.False(vm.CanSaveProfile);
        }

        vm.AliasesText = "Annie";
        Assert.Null(vm.AliasesProblem);
        Assert.True(vm.CanSaveProfile);

        vm.DisplayName = new string('x', AccountRules.MaxDisplayNameLength + 1);
        Assert.Equal($"A display name is at most {AccountRules.MaxDisplayNameLength} characters long, without line breaks.", vm.DisplayNameProblem);
        Assert.False(vm.CanSaveProfile);
    }

    [Fact]
    public async Task A_client_signed_in_by_an_alias_keeps_signing_in_by_it_until_it_is_taken_away()
    {
        _api.Account = Dto(1, "anna", "anna", ["Annie"], false);
        _session.Login = new Login("Annie", "current password");
        _session.User = _api.Account;
        _store.Saved = _session.Login;
        var vm = await ShownAsync();

        vm.DisplayName = "Anna K.";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal(new Login("Annie", "current password"), _session.Login); // the alias still signs in

        vm.AliasesText = "";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal(new Login("anna", "current password"), _session.Login);
        Assert.Equal(new Login("anna", "current password"), _store.Saved);
    }

    [Fact]
    public async Task Saving_the_user_name_changes_only_the_name_and_tells_a_client_that_keeps_the_login_to_use_the_new_one()
    {
        SignInAsClient(remembered: true);
        var vm = await ShownAsync();
        vm.UserName = "  Anna K ";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal(new AccountUpdateDto("Anna K", null, null, "anna", []).Show(), Assert.Single(_api.AccountUpdates).Show());
        Assert.Equal(new Login("Anna K", "current password"), _session.Login);
        Assert.Equal(new Login("Anna K", "current password"), _store.Saved);
        Assert.Equal("Anna K", _session.User!.UserName);
        Assert.Equal("Anna K", vm.UserName);
        Assert.Equal("Names saved.", vm.NoticeText); // WPF sends the new login itself: nothing for the user to do
        Assert.True(vm.HasNotice);
        Assert.False(vm.HasError);
        Assert.False(vm.CanSaveProfile); // nothing left to save
    }

    [Fact]
    public async Task A_login_that_was_not_remembered_is_not_written_to_the_store_by_a_save()
    {
        SignInAsClient(remembered: false);
        var vm = await ShownAsync();
        vm.UserName = "Anna K";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal(new Login("Anna K", "current password"), _session.Login);
        Assert.Equal(0, _store.Saves);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public async Task In_the_browser_the_user_is_told_that_the_browser_asks_for_the_new_login()
    {
        var vm = await ShownAsync(); // the browser holds the login: the session has none
        vm.UserName = "Anna K";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Null(_session.Login);
        Assert.Equal($"Names saved. {BrowserHint}", vm.NoticeText);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task In_the_browser_only_a_change_that_affects_the_login_comes_with_the_hint()
    {
        _api.Account = Dto(1, "anna", "anna", ["Annie"], false);
        var vm = await ShownAsync();

        vm.DisplayName = "Anna K."; // only how others see the user: the browser's login still works
        vm.AliasesText = "Annie\nA.K."; // an alias added: nothing is taken away either
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal("Names saved.", vm.NoticeText);

        vm.AliasesText = "A.K."; // the alias the browser may have signed in by is gone
        await vm.SaveProfileCommand.ExecuteAsync(null);
        Assert.Equal($"Names saved. {BrowserHint}", vm.NoticeText);
    }

    [Fact]
    public async Task A_user_name_that_is_taken_is_reported_and_nothing_changes()
    {
        _api.Users.Add(Dto(7, "Ben", "Ben", [], false));
        var vm = await ShownAsync();
        vm.UserName = "ben";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("This user name or alias is already taken.", vm.ErrorMessage);
        Assert.False(vm.HasNotice);
        Assert.Equal("anna", _session.User!.UserName);
        Assert.Equal("ben", vm.UserName); // what was typed stays, to be corrected
    }

    // --- Password ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_password_can_only_be_changed_with_the_current_one_and_a_proper_new_one_typed_twice()
    {
        var vm = await ShownAsync();
        Assert.False(vm.CanChangePassword);

        vm.CurrentPassword = "current password";
        Assert.False(vm.CanChangePassword); // no new password yet

        vm.NewPassword = "short";
        Assert.False(vm.CanChangePassword);
        Assert.Equal("The new password needs 12 to 128 characters.", vm.PasswordProblem);

        vm.NewPassword = "a new password";
        Assert.Null(vm.PasswordProblem);
        Assert.False(vm.CanChangePassword); // not repeated yet
        Assert.Null(vm.ConfirmProblem);     // and no nagging while it is empty

        vm.ConfirmPassword = "a new";
        Assert.False(vm.CanChangePassword);
        Assert.Null(vm.ConfirmProblem);     // still typing

        vm.ConfirmPassword = "a new passwurd";
        Assert.False(vm.CanChangePassword);
        Assert.Equal("The two new passwords differ.", vm.ConfirmProblem);

        vm.ConfirmPassword = "a new password";
        Assert.True(vm.CanChangePassword);
        Assert.True(vm.ChangePasswordCommand.CanExecute(null));
        Assert.Null(vm.ConfirmProblem);

        vm.CurrentPassword = "";
        Assert.False(vm.CanChangePassword);

        vm.CurrentPassword = "a new password"; // "changing" it to what it is changes nothing
        Assert.False(vm.CanChangePassword);
        Assert.Equal("The new password is the current one.", vm.PasswordProblem);
    }

    [Fact]
    public async Task Changing_the_password_sends_the_current_name_empties_the_boxes_and_renews_the_remembered_login()
    {
        SignInAsClient(remembered: true);
        var vm = await ShownAsync();
        vm.UserName = "unsaved edit"; // only the password is saved: the name waits for its own button
        FillPasswords(vm);

        await vm.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Equal(new AccountUpdateDto("anna", "current password", "a new password").Show(), Assert.Single(_api.AccountUpdates).Show());
        Assert.Equal(new Login("anna", "a new password"), _session.Login);
        Assert.Equal(new Login("anna", "a new password"), _store.Saved);
        Assert.Equal(["", "", ""], new[] { vm.CurrentPassword, vm.NewPassword, vm.ConfirmPassword });
        Assert.Equal("Password changed.", vm.NoticeText);
        Assert.False(vm.CanChangePassword);
        Assert.Equal("unsaved edit", vm.UserName);
        Assert.True(vm.CanSaveProfile); // still waiting
    }

    [Fact]
    public async Task A_wrong_current_password_is_reported_in_words_and_keeps_what_was_typed()
    {
        SignInAsClient(remembered: true);
        _api.AccountPassword = "another password";
        var vm = await ShownAsync();
        FillPasswords(vm);

        await vm.ChangePasswordCommand.ExecuteAsync(null);

        Assert.Equal("The current password is wrong.", vm.ErrorMessage);
        Assert.Equal("a new password", vm.NewPassword);
        Assert.False(vm.HasNotice);
        Assert.Equal(new Login("anna", "current password"), _session.Login); // the login did not change, so neither does the client's
        Assert.Equal(new Login("anna", "current password"), _store.Saved);
    }

    // --- Notices ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Typing_again_takes_back_the_last_error_and_the_last_notice()
    {
        _api.AccountPassword = "another password";
        var vm = await ShownAsync();
        FillPasswords(vm);
        await vm.ChangePasswordCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        vm.CurrentPassword = "another password";
        Assert.False(vm.HasError);

        await vm.ChangePasswordCommand.ExecuteAsync(null);
        Assert.True(vm.HasNotice);

        vm.UserName = "Anna K";
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var vm = await ShownAsync();
        vm.UserName = "Anna K";
        await vm.SaveProfileCommand.ExecuteAsync(null);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _language.Select("de");

        Assert.Equal("Konto", vm.Title);
        Assert.Equal("User", vm.RoleText); // the name of a role is data: it stays as it was written
        Assert.StartsWith("Namen gespeichert.", vm.NoticeText);
        Assert.Contains(nameof(AccountViewModel.Title), changed);
        Assert.Contains(nameof(AccountViewModel.NoticeText), changed);
        Assert.Contains(nameof(AccountViewModel.RoleText), changed);

        _api.Account = Nobody(1, "anna"); // the page reads the account again when what the user may do changes: the API has to say it
        _session.User = _api.Account;
        Assert.Equal("Keine Rolle", vm.RoleText);
    }

    [Fact]
    public async Task A_failed_save_that_is_not_the_users_fault_shows_the_servers_message()
    {
        var vm = await ShownAsync();
        vm.UserName = "Anna K";
        _api.FailWith = new PlannerApiException("Cannot reach the Kyuplan API.");

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("Cannot reach the Kyuplan API.", vm.ErrorMessage);
        Assert.Equal("anna", _session.User!.UserName);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanSaveProfile); // it may be tried again
    }
}

/// <summary>How the clients wire the account page up (DI is the one thing the unit tests above do by hand).</summary>
public class AccountPageRegistrationTests
{
    private static ServiceProvider Build(bool withLoginStore)
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = "http://planner.test/");
        if (withLoginStore) services.AddSingleton<Kyuplan.Client.Storage.ILoginStore, FakeLoginStore>();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)] // the browser: it keeps the login itself and has no store
    [InlineData(true)]  // WPF: a store that can remember the login
    public void The_account_page_is_available_with_and_without_a_login_store(bool withLoginStore)
    {
        using var services = Build(withLoginStore);

        var page = services.GetRequiredService<AccountViewModel>();

        Assert.Same(page, services.GetRequiredService<AccountViewModel>()); // the page keeps what was typed while the user looks at another one
        Assert.Same(services.GetRequiredService<LoginSession>(), page.Shell.Session); // the login the HTTP calls send is the one the page renews
        Assert.Same(services.GetRequiredService<ShellViewModel>(), page.Shell);
    }
}
