using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// Users who are members: the form that says which members a user is, the members page and the account page, which show what is written about
/// a member only as far as the permission for that member goes (the "own" permission for the user's own members, the general one for the others).
/// </summary>
public sealed class OwnDataViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly MemberDto _anna;
    private readonly MemberDto _ben;
    private readonly MemberDto _clara;

    public OwnDataViewModelTests()
    {
        _language.Select("en");
        _anna = _api.AddMember("Anna");
        _ben = _api.AddMember("Ben");
        _clara = _api.AddMember("Clara");
        _shell = new ShellViewModel(_api, _language);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    /// <summary>The signed-in user has exactly these permissions and is these members.</summary>
    private void SignInAs(IEnumerable<int> memberIds, params string[] permissions)
    {
        _api.Account = WithPermissions(2, "anna", permissions) with { MemberIds = [.. memberIds] };
        foreach (var id in memberIds) _api.MemberLinks[id] = 2;
    }

    // --- The rule ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Permission.CommentsView, false, true)]
    [InlineData(Permission.CommentsViewOwn, true, false)]
    [InlineData("", false, false)]
    public void A_user_reads_about_themselves_by_the_own_permission_and_about_others_by_the_general_one(string permission, bool readsOwn, bool readsOthers)
    {
        var user = WithPermissions(2, "anna", permission.Length == 0 ? [] : [permission]) with { MemberIds = [7] };

        Assert.Equal(readsOwn, user.MayRead(7, Permission.CommentsView, Permission.CommentsViewOwn));
        Assert.Equal(readsOthers, user.MayRead(8, Permission.CommentsView, Permission.CommentsViewOwn));
        Assert.True(user.IsMember(7));
        Assert.False(user.IsMember(8));
    }

    [Fact]
    public void A_user_without_members_reads_about_everybody_by_the_general_permission()
    {
        var user = WithPermissions(2, "anna", Permission.CommentsView);

        Assert.True(user.MayRead(1, Permission.CommentsView, Permission.CommentsViewOwn));
        Assert.False(user.IsMember(1));
    }

    [Fact]
    public void The_own_permissions_are_in_the_catalogue_and_do_not_come_with_the_general_ones()
    {
        var picker = new PermissionPickerViewModel();
        PermissionRow Row(string key) => picker.Groups.SelectMany(g => g.Permissions).Single(p => p.Key == key);

        Row(Permission.RatingsViewOwn).IsSelected = true;
        Row(Permission.CommentsViewOwn).IsSelected = true;

        // Ticking the own permission does not tick the general one: it works without it.
        Assert.Equal([Permission.CommentsViewOwn, Permission.RatingsViewOwn], picker.Selected);

        Row(Permission.CommentsView).IsSelected = true;
        Row(Permission.CommentsWrite).IsSelected = true;
        Row(Permission.CommentsView).IsSelected = false; // takes the rest of the area, but not the own permission
        Assert.Equal([Permission.CommentsViewOwn, Permission.RatingsViewOwn], picker.Selected);
    }

    // --- The users page --------------------------------------------------------------------------------------------------

    private async Task<UserAdminViewModel> UsersAsync()
    {
        _api.Account = Dto(1, "admin", "admin", [], true);
        _api.Users.AddRange(new[] { Dto(1, "admin", "admin", [], true), Dto(2, "Anna", "Anna", [], false), Dto(3, "ben", "ben", [], false) }
            .Select(u => u with { MemberIds = [.. _api.MemberLinks.Where(l => l.Value == u.Id).Select(l => l.Key).Order()] })); // as the API tells them
        var vm = new UserAdminViewModel(_shell, _api);
        await vm.ActivateAsync();
        return vm;
    }

    [Fact]
    public async Task The_form_offers_the_members_nobody_else_is_and_saving_links_the_ticked_ones()
    {
        _api.MemberLinks[_ben.Id] = 3; // Ben is a user's already
        var vm = await UsersAsync();

        vm.BeginEditCommand.Execute(vm.Users.Single(u => u.UserName == "Anna"));

        Assert.True(vm.HasMemberChoices);
        Assert.Equal(["Anna (Dojo)", "Clara (Dojo)"], vm.MemberChoices.Select(m => m.Label)); // with the group: a person in two groups is two members
        Assert.False(vm.CanSave); // nothing changed yet

        vm.MemberChoices.Single(m => m.Id == _clara.Id).IsSelected = true;
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains($"set-user-members:2:{_clara.Id}", _api.UserCalls);
        Assert.Equal([_clara.Id], vm.Users.Single(u => u.UserName == "Anna").User.MemberIds);
        Assert.Equal("Clara", vm.Users.Single(u => u.UserName == "Anna").MembersText);
        Assert.Equal("Ben", vm.Users.Single(u => u.UserName == "ben").MembersText);
    }

    [Fact]
    public async Task Members_are_only_sent_when_they_changed_and_a_member_can_be_taken_away_again()
    {
        _api.MemberLinks[_anna.Id] = 2;
        var vm = await UsersAsync();
        vm.BeginEditCommand.Execute(vm.Users.Single(u => u.UserName == "Anna"));
        Assert.True(vm.MemberChoices.Single(m => m.Id == _anna.Id).IsSelected);

        vm.FormDisplayName = "Anna S.";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_api.UserCalls, c => c.StartsWith("set-user-members"));

        vm.BeginEditCommand.Execute(vm.Users.Single(u => u.UserName == "Anna"));
        vm.MemberChoices.Single(m => m.Id == _anna.Id).IsSelected = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("set-user-members:2:", _api.UserCalls);
        Assert.Empty(vm.Users.Single(u => u.UserName == "Anna").User.MemberIds ?? []);
    }

    [Fact]
    public async Task A_new_user_can_be_made_a_member_in_one_go()
    {
        var vm = await UsersAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormUserName = "dora";
        vm.FormPassword = "a good password";

        vm.MemberChoices.Single(m => m.Id == _ben.Id).IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var created = vm.Users.Single(u => u.UserName == "dora");
        Assert.Equal([_ben.Id], created.User.MemberIds);
        Assert.Equal("Ben", created.MembersText);
    }

    [Fact]
    public async Task The_member_list_can_be_narrowed_by_name_and_keeps_what_is_ticked()
    {
        var vm = await UsersAsync();
        vm.BeginEditCommand.Execute(vm.Users.Single(u => u.UserName == "Anna"));
        vm.MemberChoices.Single(m => m.Id == _clara.Id).IsSelected = true;

        vm.MemberFilter = "ben";

        Assert.Equal(["Ben (Dojo)", "Clara (Dojo)"], vm.VisibleMemberChoices.Select(m => m.Label)); // Clara stays: she is ticked
        vm.MemberFilter = "";
        Assert.Equal(3, vm.VisibleMemberChoices.Count);
    }

    [Fact]
    public async Task Somebody_who_may_not_edit_users_is_not_offered_members_to_tick()
    {
        _api.Account = WithPermissions(2, "viewer", Permission.UsersView);
        _api.Users.Add(Dto(3, "ben", "ben", [], false));
        var vm = new UserAdminViewModel(_shell, _api);
        await vm.ActivateAsync();

        vm.BeginEditCommand.Execute(vm.Users.Single());

        Assert.False(vm.IsFormOpen); // nothing to edit
        Assert.False(vm.HasMemberChoices);
        Assert.Empty(vm.MemberChoices);
    }

    // --- The members page -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Permission.CommentsView, false, true)]
    [InlineData(Permission.CommentsViewOwn, true, false)]
    [InlineData(Permission.CommentsView + "," + Permission.CommentsViewOwn, true, true)]
    public async Task The_comments_of_a_member_can_be_opened_by_the_permission_that_applies_to_that_member(string permissions, bool opensOwn, bool opensOthers)
    {
        SignInAs([_anna.Id], [Permission.MembersView, .. permissions.Split(',')]);
        _api.AddComment(_anna.Id, "About Anna");
        _api.AddComment(_ben.Id, "About Ben");
        var vm = new MembersViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
        await vm.ActivateAsync();
        var own = vm.Members.Single(m => m.Id == _anna.Id);
        var other = vm.Members.Single(m => m.Id == _ben.Id);

        Assert.True(vm.CanViewComments); // the buttons are there
        Assert.Equal(opensOwn, vm.OpenCommentsCommand.CanExecute(own));
        Assert.Equal(opensOthers, vm.OpenCommentsCommand.CanExecute(other));
        Assert.Equal(opensOwn, vm.MayViewComments(own));
        Assert.Equal(opensOthers, vm.MayViewComments(other));
    }

    [Fact]
    public async Task Comments_that_are_open_close_when_the_user_may_no_longer_read_them()
    {
        SignInAs([_anna.Id], Permission.MembersView, Permission.CommentsViewOwn);
        var vm = new MembersViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
        await vm.ActivateAsync();
        await vm.OpenCommentsCommand.ExecuteAsync(vm.Members.Single(m => m.Id == _anna.Id));
        Assert.True(vm.IsShowingComments);

        _api.Account = _api.Account with { Permissions = [Permission.MembersView], EffectivePermissions = [Permission.MembersView] };
        _shell.Session.User = _api.Account;

        Assert.False(vm.IsShowingComments);
    }

    // --- The account page -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_own_permissions_the_account_page_asks_for_nothing_about_the_user()
    {
        SignInAs([_anna.Id], Permission.CommentsView, Permission.RatingsView); // the general ones show nothing about oneself
        _api.AddComment(_anna.Id, "About Anna");
        var about = new AboutMeViewModel(_shell, _api);
        await _shell.InitializeAsync();

        await about.ReloadAsync();

        Assert.False(about.IsVisible);
        Assert.Empty(about.Members);
        Assert.Empty(_api.CommentListRequests);
    }

    [Fact]
    public async Task With_the_own_permissions_the_account_page_shows_the_comments_and_evaluations_of_the_user_and_nothing_else()
    {
        SignInAs([_anna.Id], Permission.CommentsViewOwn, Permission.RatingsViewOwn);
        _api.AddComment(_anna.Id, "About Anna");
        _api.AddComment(_ben.Id, "About Ben");
        _api.MemberRatings.AddRange([
            new RatingDto(_anna.Id, "e1", 5, "Trainer", 4, new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)),
            new RatingDto(_anna.Id, "e2", 6, "Other", 2, new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero)),
            new RatingDto(_ben.Id, "e1", 5, "Trainer", 5, new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero))]);
        var about = new AboutMeViewModel(_shell, _api);
        await _shell.InitializeAsync();

        await about.ReloadAsync();

        Assert.True(about.IsVisible);
        Assert.False(about.HasSeveralMembers);
        Assert.Equal("Anna", about.Selected!.Name);
        Assert.Equal(["About Anna"], about.Comments.Comments.Select(c => c.Comment.Body));
        Assert.Equal([4, 2], about.Ratings.Select(r => r.Stars));
        Assert.Equal([_anna.Id], _api.CommentListRequests);
        Assert.False(about.HasError);
    }

    [Fact]
    public async Task A_user_who_is_two_members_chooses_which_one_to_look_at()
    {
        SignInAs([_anna.Id, _clara.Id], Permission.CommentsViewOwn);
        _api.AddComment(_anna.Id, "About Anna");
        _api.AddComment(_clara.Id, "About Clara");
        var about = new AboutMeViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await about.ReloadAsync();

        Assert.True(about.HasSeveralMembers);
        Assert.Equal(["About Anna"], about.Comments.Comments.Select(c => c.Comment.Body));

        await about.SelectCommand.ExecuteAsync(about.Members.Single(m => m.Name == "Clara"));

        Assert.Equal("Clara", about.Selected!.Name);
        Assert.Equal(["About Clara"], about.Comments.Comments.Select(c => c.Comment.Body));
        Assert.Empty(about.Ratings); // no ratings permission: none read
    }

    [Fact]
    public async Task A_user_who_is_nobodys_member_has_nothing_to_see_even_with_the_own_permissions()
    {
        SignInAs([], Permission.CommentsViewOwn, Permission.RatingsViewOwn);
        var about = new AboutMeViewModel(_shell, _api);
        await _shell.InitializeAsync();

        await about.ReloadAsync();

        Assert.False(about.IsVisible);
    }

    [Fact]
    public async Task What_the_user_may_no_longer_read_is_dropped_at_once()
    {
        SignInAs([_anna.Id], Permission.CommentsViewOwn, Permission.RatingsViewOwn);
        _api.AddComment(_anna.Id, "About Anna");
        _api.MemberRatings.Add(new RatingDto(_anna.Id, "e1", 5, "Trainer", 4, new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)));
        var about = new AboutMeViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await about.ReloadAsync();
        Assert.True(about.HasRatings);

        _api.Account = _api.Account with { Permissions = [Permission.CommentsViewOwn], EffectivePermissions = [Permission.CommentsViewOwn] };
        _shell.Session.User = _api.Account;

        Assert.False(about.HasRatings);
        Assert.True(about.Comments.IsOpen);
        Assert.False(about.CanViewRatings);
    }

    // --- The calendar --------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_attendee_whose_evaluations_are_hidden_is_not_shown_as_not_evaluated()
    {
        var hidden = new EventAttendeeRow(new CalendarAttendeeDto(1, "Anna", AttendanceMark.Attended, [], []), mayViewRatings: false);
        var shown = new EventAttendeeRow(new CalendarAttendeeDto(2, "Ben", AttendanceMark.Attended, [], []));

        Assert.False(hidden.MayViewRatings);
        Assert.False(hidden.ShowRating);
        Assert.True(shown.MayViewRatings);
    }
}
