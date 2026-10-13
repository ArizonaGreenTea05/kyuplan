using System.ComponentModel;
using System.Globalization;
using System.Net;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>What the shell knows about the signed-in user: what they may do decides which pages and buttons are offered.</summary>
public class ShellUserTests
{
    private readonly FakePlannerApi _api = new();
    private readonly LoginSession _session = new();
    private readonly ShellViewModel _shell;

    public ShellUserTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api, session: _session);
    }

    [Fact]
    public async Task Nobody_may_do_anything_until_the_api_has_said_who_they_are()
    {
        Assert.Null(_shell.CurrentUser);
        Assert.False(_shell.Can(Permission.UsersView));
        Assert.False(_shell.UsesSpond);

        _api.Account = Dto(1, "admin", "admin", [], true);
        await _shell.InitializeAsync();

        Assert.Equal(_api.Account, _shell.CurrentUser);
        Assert.Equal(_api.Account, _session.User);
        Assert.True(_shell.Can(Permission.UsersView));
        Assert.True(_shell.UsesSpond);
        Assert.Equal(1, _api.AccountRequests);
    }

    [Fact]
    public async Task A_normal_user_may_what_their_role_allows()
    {
        await _shell.InitializeAsync();

        Assert.Equal("anna", _shell.CurrentUser!.UserName);
        Assert.True(_shell.Can(Permission.MembersEdit));
        Assert.False(_shell.Can(Permission.UsersView));
        Assert.True(_shell.CanAny([Permission.UsersView, Permission.BeltsEdit]));
        Assert.False(_shell.CanAny([Permission.UsersView, Permission.RolesView]));
    }

    [Fact]
    public async Task A_client_that_signed_in_itself_already_knows_the_user_and_does_not_ask_again()
    {
        _session.User = Dto(5, "boss", "boss", [], true);

        await _shell.InitializeAsync();

        Assert.Equal(0, _api.AccountRequests);
        Assert.Equal("boss", _shell.CurrentUser!.UserName);
        Assert.True(_shell.Can(Permission.RolesEdit));
    }

    [Fact]
    public void A_change_of_the_signed_in_user_is_announced_and_so_is_a_change_of_what_they_may_do()
    {
        var changed = new List<string?>();
        ((INotifyPropertyChanged)_shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var permissionsChanged = 0;
        _shell.PermissionsChanged += (_, _) => permissionsChanged++;

        _session.User = Dto(1, "admin", "admin", [], true);

        Assert.Contains(nameof(ShellViewModel.CurrentUser), changed);
        Assert.True(_shell.Can(Permission.UsersView));
        Assert.Equal(1, permissionsChanged);

        // A rename changes nothing the user may do: the pages have no reason to read their data again.
        _session.User = Dto(1, "chief", "chief", [], true);
        Assert.Equal(1, permissionsChanged);

        _session.User = Dto(1, "chief", "chief", [], false);
        Assert.Equal(2, permissionsChanged);
        Assert.False(_shell.Can(Permission.UsersView));
    }

    [Fact]
    public async Task Somebody_who_may_use_no_page_with_data_from_spond_is_not_shown_its_groups_and_gets_no_error()
    {
        _api.Account = WithPermissions(1, "ben", Permission.BeltsView);

        await _shell.InitializeAsync();

        Assert.False(_shell.HasError);
        Assert.Empty(_shell.Groups);
        Assert.False(_shell.UsesSpond);
        Assert.Equal("", _shell.SelectedGroupId);
    }

    [Fact]
    public async Task Refreshing_learns_again_what_the_user_may_do()
    {
        await _shell.InitializeAsync();
        var permissionsChanged = 0;
        _shell.PermissionsChanged += (_, _) => permissionsChanged++;
        Assert.False(_shell.Can(Permission.RolesView));

        _api.Account = Dto(1, "anna", "anna", [], isAdmin: true); // an administrator gave her the administrator role
        await _shell.RefreshDataCommand.ExecuteAsync(null);

        Assert.True(_shell.Can(Permission.RolesView));
        Assert.Equal(1, permissionsChanged);
    }

    [Fact]
    public async Task When_the_user_cannot_be_asked_the_shell_tries_again_with_the_rest_of_its_start()
    {
        _api.FailWith = new PlannerApiException("not signed in", HttpStatusCode.Unauthorized);
        await _shell.InitializeAsync();
        Assert.True(_shell.HasError);
        Assert.Null(_shell.CurrentUser);
        Assert.False(_shell.Can(Permission.MembersView));

        _api.FailWith = null;
        await _shell.RetryCommand.ExecuteAsync(null);

        Assert.False(_shell.HasError);
        Assert.Equal("anna", _shell.CurrentUser!.UserName);
    }
}
