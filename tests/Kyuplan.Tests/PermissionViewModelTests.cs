using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// What a page offers follows the permissions of the signed-in user: the page itself (<c>.view</c>), and every change of it with a permission
/// of its own. A control the user may not use is not just disabled: its command does nothing either (the view hides it).
/// </summary>
public sealed class PermissionViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;

    public PermissionViewModelTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    /// <summary>The signed-in user has exactly these permissions (and the id 1 of the fake's comments' author).</summary>
    private void SignInWith(params string[] permissions) => _api.Account = WithPermissions(1, "anna", permissions);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    // --- The page itself -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_the_user_may_not_see_is_closed_and_asks_the_api_for_nothing()
    {
        SignInWith(Permission.MembersView);
        _api.AddBelt("White");
        var vm = new BeltsViewModel(_shell, _api);

        await vm.ActivateAsync();

        Assert.False(vm.CanView);
        Assert.Equal(0, _api.BeltListRequests);
        Assert.Empty(vm.Belts);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_page_loads_when_the_user_gets_the_permission_to_see_it_and_closes_what_it_may_no_longer_show()
    {
        SignInWith();
        _api.AddBelt("White");
        var vm = new BeltsViewModel(_shell, _api);
        await vm.ActivateAsync();
        Assert.Equal(0, _api.BeltListRequests);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _shell.Session.User = WithPermissions(1, "anna", Permission.BeltsView);

        Assert.True(vm.CanView);
        Assert.Contains(string.Empty, changed); // everything that depends on the permissions is announced
        await Eventually(() => vm.Belts.Count == 1);
        Assert.Equal(1, _api.BeltListRequests);
    }

    [Fact]
    public async Task A_page_that_was_not_loaded_because_the_shell_could_not_start_loads_when_the_retry_works()
    {
        SignInWith(Permission.BeltsView);
        _api.AddBelt("White");
        _api.FailWith = new PlannerApiException("down");
        var vm = new BeltsViewModel(_shell, _api);
        await vm.ActivateAsync();
        Assert.True(_shell.HasError);
        Assert.Equal(0, _api.BeltListRequests);

        _api.FailWith = null;
        await _shell.RetryCommand.ExecuteAsync(null);

        await Eventually(() => vm.Belts.Count == 1);
    }

    // --- Belts ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_may_only_see_the_belts_cannot_create_change_or_delete_them()
    {
        SignInWith(Permission.BeltsView);
        _api.AddBelt("White");
        var vm = new BeltsViewModel(_shell, _api);
        await vm.ActivateAsync();
        var row = vm.Belts.Single();

        Assert.True(vm.CanView);
        Assert.False(vm.CanEdit);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(row));
        Assert.False(vm.CanDelete(row));
        Assert.False(vm.AskDeleteCommand.CanExecute(row));

        vm.BeginCreateCommand.Execute(null);
        Assert.False(vm.IsFormOpen);
    }

    [Fact]
    public async Task An_empty_list_invites_to_create_the_first_belt_or_tag_only_somebody_who_may()
    {
        SignInWith(Permission.BeltsView, Permission.TagsView);
        var belts = new BeltsViewModel(_shell, _api);
        var tags = new TagsViewModel(_shell, _api);
        await belts.ActivateAsync();
        await tags.ActivateAsync();

        Assert.True(belts.IsEmpty);
        Assert.Equal("There are no belts yet.", belts.EmptyText);
        Assert.Equal("There are no tags yet.", tags.EmptyText);

        _shell.Session.User = WithPermissions(1, "anna", Permission.BeltsView, Permission.BeltsEdit, Permission.TagsView, Permission.TagsEdit);
        Assert.StartsWith("There are no belts yet. Create the first one", belts.EmptyText);
        Assert.StartsWith("There are no tags yet. Create one", tags.EmptyText);
    }

    [Fact]
    public async Task Somebody_who_may_edit_the_belts_can_and_a_user_who_loses_the_permission_loses_the_open_form_buttons()
    {
        SignInWith(Permission.BeltsView, Permission.BeltsEdit);
        _api.AddBelt("White");
        var vm = new BeltsViewModel(_shell, _api);
        await vm.ActivateAsync();

        Assert.True(vm.BeginCreateCommand.CanExecute(null));
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Yellow";
        Assert.True(vm.CanSave);

        _shell.Session.User = WithPermissions(1, "anna", Permission.BeltsView);

        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(vm.Belts.Single()));
    }

    // --- Members and their comments ---------------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_may_see_members_does_not_get_their_comments_without_the_permission_for_them()
    {
        SignInWith(Permission.MembersView);
        var member = _api.AddMember("Clara");
        var vm = new MembersViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)));
        await vm.ActivateAsync();
        var row = vm.Members.Single(m => m.Id == member.Id);

        Assert.False(vm.CanViewComments);
        Assert.False(vm.CanEdit);
        Assert.False(vm.OpenCommentsCommand.CanExecute(row));
        Assert.False(vm.BeginEditCommand.CanExecute(row));
        Assert.Empty(_api.CommentListRequests);

        _shell.Session.User = WithPermissions(1, "anna", Permission.MembersView, Permission.CommentsView, Permission.MembersEdit);
        Assert.True(vm.OpenCommentsCommand.CanExecute(row));
        Assert.True(vm.BeginEditCommand.CanExecute(row));
    }

    [Fact]
    public async Task Somebody_who_may_only_read_comments_sees_them_without_a_composer_and_without_asking_for_tags()
    {
        SignInWith(Permission.CommentsView);
        var member = _api.AddMember("Clara");
        _api.AddComment(member.Id, "One", authorId: 2, authorName: "ben");
        _api.AddTag("Topic");
        var vm = new MemberCommentsViewModel(_shell, _api);
        await _shell.InitializeAsync();

        await vm.ShowAsync(new MemberRow(member, new BeltOption()));

        Assert.Single(vm.Comments);
        Assert.Equal(0, _api.TagListRequests); // reading comments needs no tags (their tags come with them)
        Assert.False(vm.CanCompose);
        Assert.False(vm.CanWriteComments);
        Assert.False(vm.ReplyCommand.CanExecute(vm.Comments[0]));
        Assert.False(vm.EditCommand.CanExecute(vm.Comments[0]));
        vm.Text = "Words";
        Assert.False(vm.CanSave);
        Assert.False(vm.Comments[0].CanDelete);
    }

    [Fact]
    public async Task Somebody_who_may_write_but_not_edit_writes_and_replies_but_cannot_change_a_comment_and_may_read_the_tags()
    {
        SignInWith(Permission.CommentsView, Permission.CommentsWrite);
        var member = _api.AddMember("Clara");
        _api.AddComment(member.Id, "One");
        _api.AddTag("Topic");
        var vm = new MemberCommentsViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await vm.ShowAsync(new MemberRow(member, new BeltOption()));

        Assert.Equal(1, _api.TagListRequests);
        Assert.True(vm.CanCompose);
        Assert.True(vm.ReplyCommand.CanExecute(vm.Comments[0]));
        Assert.False(vm.EditCommand.CanExecute(vm.Comments[0]));
        Assert.False(vm.NewTagCommand.CanExecute(null)); // making tags is the tags permission

        vm.Text = "A new comment";
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["create-comment"], _api.CommentCalls.Select(c => c.Split(':')[0]).Distinct());
    }

    [Fact]
    public async Task Somebody_who_may_edit_but_not_write_sees_the_composer_only_while_changing_a_comment()
    {
        SignInWith(Permission.CommentsView, Permission.CommentsEdit);
        var member = _api.AddMember("Clara");
        _api.AddComment(member.Id, "One", authorId: 2, authorName: "ben");
        var vm = new MemberCommentsViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await vm.ShowAsync(new MemberRow(member, new BeltOption()));
        var comment = vm.Comments.Single();

        Assert.False(vm.CanCompose);
        Assert.False(vm.ReplyCommand.CanExecute(comment));
        vm.Text = "Words that nobody may post";
        Assert.False(vm.CanSave); // a new comment is writing

        vm.EditCommand.Execute(comment);
        Assert.True(vm.CanCompose);
        vm.Text = "Changed words";
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Changed words", vm.Comments.Single().Comment.Body);
        Assert.False(vm.CanCompose);
    }

    [Fact]
    public async Task A_comment_can_be_deleted_by_its_author_with_the_permission_for_their_own_and_by_others_only_with_the_permission_for_all()
    {
        SignInWith(Permission.CommentsView, Permission.CommentsDelete);
        var member = _api.AddMember("Clara");
        _api.AddComment(member.Id, "Mine", authorId: 1, authorName: "anna");
        _api.AddComment(member.Id, "Ben's", authorId: 2, authorName: "ben");
        var vm = new MemberCommentsViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await vm.ShowAsync(new MemberRow(member, new BeltOption()));

        Assert.True(vm.Comments.Single(c => c.Comment.Body == "Mine").CanDelete);
        Assert.False(vm.Comments.Single(c => c.Comment.Body == "Ben's").CanDelete);

        SignInWith(Permission.CommentsView, Permission.CommentsDeleteAny);
        _shell.Session.User = _api.Account;
        await vm.ReloadAsync();
        Assert.All(vm.Comments, c => Assert.True(c.CanDelete));

        SignInWith(Permission.CommentsView);
        _shell.Session.User = _api.Account;
        await vm.ReloadAsync();
        Assert.All(vm.Comments, c => Assert.False(c.CanDelete));
    }

    [Fact]
    public async Task Tags_can_be_made_in_the_composer_only_with_the_tag_permission()
    {
        SignInWith(Permission.CommentsView, Permission.CommentsWrite, Permission.TagsView, Permission.TagsEdit);
        var member = _api.AddMember("Clara");
        var tag = _api.AddTag("Topic");
        var vm = new MemberCommentsViewModel(_shell, _api);
        await _shell.InitializeAsync();
        await vm.ShowAsync(new MemberRow(member, new BeltOption()));

        Assert.True(vm.CanEditTags);
        Assert.True(vm.NewTagCommand.CanExecute(null));
        Assert.True(vm.EditTagCommand.CanExecute(vm.TagOptions.Single(o => o.Id == tag.Id)));

        _shell.Session.User = WithPermissions(1, "anna", Permission.CommentsView, Permission.CommentsWrite);
        Assert.False(vm.CanEditTags);
        Assert.False(vm.NewTagCommand.CanExecute(null));
        Assert.False(vm.EditTagCommand.CanExecute(vm.TagOptions.Single(o => o.Id == tag.Id)));
    }

    // --- Tags -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_may_only_see_the_tags_cannot_make_change_or_delete_them()
    {
        SignInWith(Permission.TagsView);
        _api.AddTag("Topic");
        var vm = new TagsViewModel(_shell, _api);
        await vm.ActivateAsync();
        var row = vm.Tags.Single();

        Assert.False(vm.CanEdit);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(row));
        Assert.False(vm.AskDeleteCommand.CanExecute(row));
    }

    // --- Exams ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_may_only_see_the_exams_cannot_add_or_delete_them_nor_tick_members_nor_record_belts()
    {
        SignInWith(Permission.ExamsView);
        _api.AddExam("exam-2");
        var vm = new ExamsViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)));
        await vm.ActivateAsync();
        await vm.Detail.Loaded;

        Assert.True(vm.CanView);
        Assert.NotEmpty(vm.Exams);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanOpenStatistics);
        Assert.False(vm.BeginAddCommand.CanExecute(null));
        Assert.False(vm.AskDeleteCommand.CanExecute(null));
        Assert.False(vm.Detail.CanPlan);
        Assert.False(vm.Detail.CanRecordResults);
        Assert.False(vm.Detail.SelectFitCommand.CanExecute(null));
        Assert.False(vm.Detail.ClearSelectionCommand.CanExecute(null));
        Assert.False(vm.Detail.CanSave);
    }

    [Fact]
    public async Task Somebody_who_may_edit_exams_and_plan_them_can_add_delete_and_save_members_for_one()
    {
        SignInWith(Permission.ExamsView, Permission.ExamsEdit, Permission.ExamsCandidates, Permission.ExamsResults, Permission.StatisticsView);
        _api.AddExam("exam-2");
        var vm = new ExamsViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)));
        await vm.ActivateAsync();
        await vm.Detail.Loaded;

        Assert.True(vm.CanEdit);
        Assert.True(vm.CanOpenStatistics);
        Assert.True(vm.BeginAddCommand.CanExecute(null));
        Assert.True(vm.AskDeleteCommand.CanExecute(null));
        Assert.True(vm.Detail.CanPlan);
        Assert.True(vm.Detail.CanRecordResults);
    }

    // --- The saved filters of the statistics page ---------------------------------------------------------------------

    private AttendanceStatisticsViewModel Statistics() => new(
        _shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)), new InMemoryFilterSettingsStore());

    [Fact]
    public async Task Without_the_permission_to_see_exams_the_statistics_page_has_no_saved_filters_and_asks_for_no_exams()
    {
        SignInWith(Permission.StatisticsView);
        var vm = Statistics();

        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && _api.AttendanceRequests.Count > 0);

        Assert.False(vm.SavedFilters.CanUse);
        Assert.False(vm.SavedFilters.CanSaveFilter);
        Assert.Equal(0, _api.ExamListRequests);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Saving_replacing_and_deleting_filters_and_making_exams_are_permissions_of_their_own()
    {
        var examDay = _api.AddExam("exam-2", ("Spring", new FilterSettingsDto("g1", new DateOnly(2026, 6, 20), new DateOnly(2026, 7, 2), "train-1", "train-2", [], 60, 2, null, null)));
        SignInWith(Permission.StatisticsView, Permission.ExamsView);
        var vm = Statistics();
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && _api.ExamListRequests > 0 && !vm.SavedFilters.IsBusy);
        var saved = vm.SavedFilters;
        saved.SelectedFilterId = examDay.Filters[0].Id;
        await Eventually(() => !vm.IsBusy);

        // May look at the saved filters and use them, nothing more.
        Assert.True(saved.CanUse);
        Assert.False(saved.CanSaveFilter);
        Assert.False(saved.CanChangeFilters);
        Assert.False(saved.AskDeleteFilterCommand.CanExecute(null));
        Assert.False(saved.AskUpdateCommand.CanExecute(null));
        Assert.False(saved.AskDeleteExamCommand.CanExecute(null));
        saved.BeginSaveCommand.Execute(null);
        Assert.False(saved.IsSaving);

        _shell.Session.User = WithPermissions(1, "anna", Permission.StatisticsView, Permission.ExamsView, Permission.ExamsFilters);
        await Eventually(() => !saved.IsBusy && !vm.IsBusy);
        Assert.True(saved.CanChangeFilters);
        Assert.True(saved.CanSaveFilter); // there is an exam to save for
        Assert.False(saved.CanMakeExams);
        Assert.DoesNotContain(saved.ExamChoices, e => e.IsNew); // no "new exam" without the right to make one
        Assert.True(saved.AskDeleteFilterCommand.CanExecute(null));
        Assert.False(saved.AskDeleteExamCommand.CanExecute(null));
        saved.BeginSaveCommand.Execute(null);
        Assert.True(saved.IsSaving);
    }

    // --- Branding -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_may_only_see_the_branding_settings_cannot_save_upload_or_reset()
    {
        SignInWith(Permission.BrandingView);
        var vm = new BrandingAdminViewModel(_shell, _api);

        await vm.ActivateAsync();

        Assert.True(vm.CanView);
        Assert.True(vm.IsLoaded);
        Assert.False(vm.CanEdit);
        vm.FormProductName = "Something else";
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.CanUpload);
        Assert.False(vm.CanSaveImages);
        Assert.False(vm.CanReset);
        Assert.False(vm.PickAccentColorCommand.CanExecute("#112233"));
        Assert.All(vm.Images, row => Assert.False(vm.CanRemove(row)));
    }

    [Fact]
    public async Task Somebody_who_may_edit_the_branding_can_save_what_they_changed()
    {
        SignInWith(Permission.BrandingView, Permission.BrandingEdit);
        var vm = new BrandingAdminViewModel(_shell, _api);
        await vm.ActivateAsync();

        vm.FormProductName = "Something else";

        Assert.True(vm.CanEdit);
        Assert.True(vm.CanSave);
        Assert.True(vm.CanUpload);
    }

    // --- Users ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_account_page_needs_no_permission()
    {
        SignInWith();
        var vm = new AccountViewModel(_shell, _api);

        await vm.ActivateAsync();

        Assert.True(vm.CanView);
        Assert.Equal("anna", vm.UserName);
        Assert.Equal("No role", vm.RoleText);
        Assert.Empty(vm.RoleNames);
    }
}
