using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The tags page and the form that makes and changes tags: a name and the event the tag is about (none, or one).</summary>
public sealed class TagsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly TagsViewModel _vm;

    public TagsViewModelTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language);
        _vm = new TagsViewModel(_shell, _api);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    private async Task<TagsViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private TagRow Row(string name) => _vm.Tags.Single(t => t.Name == name);

    // --- The list --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_lists_the_tags_by_name_with_their_event_and_how_many_comments_carry_them()
    {
        var member = _api.AddMember("Anna");
        var exam = _api.AddTag("Exam prep", "exam-2");
        _api.AddTag("Anything");
        _api.AddComment(member.Id, "One", tags: [exam]);
        _api.AddComment(member.Id, "Two", tags: [exam]);
        var vm = await ShownAsync();

        Assert.Equal("Tags", vm.Title);
        Assert.Equal(["Anything", "Exam prep"], vm.Tags.Select(t => t.Name));
        Assert.Equal([0, 2], vm.Tags.Select(t => t.CommentCount));
        Assert.Equal([false, true], vm.Tags.Select(t => t.HasEvent));
        Assert.Equal("No event", Row("Anything").EventText);
        Assert.StartsWith("Gürtelprüfung · ", Row("Exam prep").EventText);
        Assert.Equal("Gürtelprüfung", Row("Exam prep").EventName);
        Assert.Contains("26", Row("Exam prep").EventWhenText);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Without_tags_the_page_invites_to_create_the_first()
    {
        var vm = await ShownAsync();

        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task Tags_that_cannot_be_read_say_why_and_can_be_tried_again()
    {
        _api.TagListFailure = new PlannerApiException("The API is away.", null);
        var vm = await ShownAsync();

        Assert.Equal("The API is away.", vm.ErrorMessage);
        Assert.False(vm.IsEmpty);

        _api.TagListFailure = null;
        _api.AddTag("Back");
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(["Back"], vm.Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task The_list_is_read_again_whenever_the_page_is_shown_unless_a_form_is_open()
    {
        var vm = await ShownAsync();
        var requests = _api.TagListRequests;

        vm.Deactivate();
        _api.AddTag("Made elsewhere");
        await vm.ActivateAsync();
        Assert.Equal(requests + 1, _api.TagListRequests);
        Assert.Equal(["Made elsewhere"], vm.Tags.Select(t => t.Name));

        await vm.BeginCreateCommand.ExecuteAsync(null);
        vm.Deactivate();
        await vm.ActivateAsync();
        Assert.Equal(requests + 1, _api.TagListRequests); // not under an open form
    }

    // --- Creating ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_tag_needs_a_name_and_may_be_about_an_event_of_the_selected_group()
    {
        var vm = await ShownAsync();

        await vm.BeginCreateCommand.ExecuteAsync(null);

        var editor = vm.Editor;
        Assert.True(editor.IsOpen);
        Assert.True(editor.IsCreating);
        Assert.Equal("New tag", editor.Title);
        Assert.Equal(["g1"], _api.EventChoiceRequests);
        Assert.Equal(["", "exam-1", "train-1", "train-2", "exam-2"], editor.EventOptions.Select(o => o.Id));
        Assert.False(editor.CanSave);
        Assert.Null(editor.NameProblem); // nothing is wrong with an empty form that was just opened
        Assert.False(vm.BeginCreateCommand.CanExecute(null));

        editor.Name = "  Exam day  ";
        editor.EventId = "exam-2";
        Assert.True(editor.CanSave);

        await editor.SaveCommand.ExecuteAsync(null);
        await Eventually(() => vm.Tags.Count == 1);

        var input = Assert.Single(_api.TagInputs);
        Assert.Equal(("Exam day", "g1", "exam-2"), (input.Name, input.GroupId, input.EventId));
        Assert.False(editor.IsOpen);
        Assert.Equal("Tag “Exam day” created.", vm.NoticeText);
        Assert.Equal("exam-2", vm.Tags[0].Tag.Event!.EventId);
    }

    [Fact]
    public async Task A_tag_without_an_event_is_sent_without_one()
    {
        var vm = await ShownAsync();
        await vm.BeginCreateCommand.ExecuteAsync(null);
        vm.Editor.Name = "General";

        await vm.Editor.SaveCommand.ExecuteAsync(null);

        var input = Assert.Single(_api.TagInputs);
        Assert.Equal(("General", null, null), (input.Name, input.GroupId, input.EventId));
    }

    [Theory]
    [InlineData("spring", true)]
    [InlineData("  SPRING ", true)]
    [InlineData("Spring 2", false)]
    public async Task A_name_cannot_be_used_twice_regardless_of_case(string typed, bool taken)
    {
        _api.AddTag("Spring");
        var vm = await ShownAsync();
        await vm.BeginCreateCommand.ExecuteAsync(null);

        vm.Editor.Name = typed;

        Assert.Equal(taken ? "There already is a tag with this name." : null, vm.Editor.NameProblem);
        Assert.Equal(!taken, vm.Editor.CanSave);
    }

    [Fact]
    public async Task A_name_must_have_a_reasonable_length()
    {
        var vm = await ShownAsync();
        await vm.BeginCreateCommand.ExecuteAsync(null);

        vm.Editor.Name = new string('x', TagRules.MaxNameLength + 1);

        Assert.Equal("The name must have 1 to 50 characters.", vm.Editor.NameProblem);
        Assert.False(vm.Editor.CanSave);

        vm.Editor.Name = "   ";
        Assert.False(vm.Editor.CanSave);
        Assert.NotNull(vm.Editor.NameProblem);
    }

    [Fact]
    public async Task A_name_the_api_calls_taken_is_said_in_the_form_which_stays_open()
    {
        var vm = await ShownAsync();
        await vm.BeginCreateCommand.ExecuteAsync(null);
        _api.AddTag("Made elsewhere meanwhile"); // the page's list does not know it yet
        vm.Editor.Name = "made elsewhere MEANWHILE";

        await vm.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.Editor.IsOpen);
        Assert.Equal("There already is a tag with this name.", vm.Editor.ErrorMessage);
        Assert.Empty(vm.Tags);
    }

    [Fact]
    public async Task Without_events_the_form_still_works_for_the_name()
    {
        var vm = await ShownAsync();
        _api.FailWith = new PlannerApiException("Spond is away.", HttpStatusCode.ServiceUnavailable);

        await vm.BeginCreateCommand.ExecuteAsync(null); // the events could not be read

        Assert.True(vm.Editor.IsOpen);
        Assert.Equal("Spond is away.", vm.Editor.ErrorMessage);
        Assert.Equal([""], vm.Editor.EventOptions.Select(o => o.Id));

        _api.FailWith = null;
        vm.Editor.Name = "Offline topic"; // writing a name is correcting the form
        Assert.False(vm.Editor.HasError);
        Assert.True(vm.Editor.CanSave);

        await vm.Editor.SaveCommand.ExecuteAsync(null);
        await Eventually(() => vm.Tags.Count == 1);
    }

    [Fact]
    public async Task Giving_up_closes_the_form_without_saving()
    {
        var vm = await ShownAsync();
        await vm.BeginCreateCommand.ExecuteAsync(null);
        vm.Editor.Name = "Never mind";

        vm.Editor.CancelCommand.Execute(null);

        Assert.False(vm.Editor.IsOpen);
        Assert.Empty(_api.TagInputs);
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
    }

    // --- Changing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Changing_a_tag_starts_with_what_it_has_and_can_be_saved_once_something_differs()
    {
        _api.AddTag("Exam prep", "exam-2");
        var vm = await ShownAsync();

        await vm.BeginEditCommand.ExecuteAsync(Row("Exam prep"));

        var editor = vm.Editor;
        Assert.False(editor.IsCreating);
        Assert.Equal("Edit “Exam prep”", editor.Title);
        Assert.Equal("Exam prep", editor.Name);
        Assert.Equal("exam-2", editor.EventId);
        Assert.False(editor.CanSave);
        Assert.False(vm.BeginEditCommand.CanExecute(Row("Exam prep"))); // it is open for this tag already

        editor.Name = "Exam preparation";
        Assert.True(editor.CanSave);
        editor.Name = "  Exam prep ";
        Assert.False(editor.CanSave); // the same once trimmed
        editor.EventId = "exam-1";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public async Task A_tag_can_be_disconnected_from_its_event()
    {
        var tag = _api.AddTag("Exam prep", "exam-2");
        var vm = await ShownAsync();
        await vm.BeginEditCommand.ExecuteAsync(Row("Exam prep"));
        Assert.True(vm.Editor.ClearEventCommand.CanExecute(null));

        vm.Editor.ClearEventCommand.Execute(null);

        Assert.Equal("", vm.Editor.EventId);
        Assert.False(vm.Editor.ClearEventCommand.CanExecute(null)); // nothing left to disconnect
        await vm.Editor.SaveCommand.ExecuteAsync(null);
        await Eventually(() => !vm.Tags[0].HasEvent);

        Assert.Equal(new TagInputDto("Exam prep", null, null), _api.TagInputs.Single());
        Assert.Equal($"update-tag:{tag.Id}", _api.CommentCalls.Single());
        Assert.Equal("Tag “Exam prep” saved.", vm.NoticeText);
    }

    [Fact]
    public async Task A_tag_without_an_event_has_nothing_to_disconnect()
    {
        _api.AddTag("General");
        var vm = await ShownAsync();

        await vm.BeginEditCommand.ExecuteAsync(Row("General"));

        Assert.False(vm.Editor.ClearEventCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_event_a_tag_is_about_is_offered_even_when_the_selected_group_does_not_list_it()
    {
        _api.AddTag("Exam prep", "exam-2");
        _api.EventChoices = new EventChoicesDto([FakePlannerApi.SampleEvents[1]], null, null); // exam-2 is not among the events of the group
        var vm = await ShownAsync();

        await vm.BeginEditCommand.ExecuteAsync(Row("Exam prep"));

        Assert.Equal(["", "train-1", "exam-2"], vm.Editor.EventOptions.Select(o => o.Id)); // in order of the day
        Assert.Equal("exam-2", vm.Editor.EventId);
        Assert.False(vm.Editor.CanSave);

        vm.Editor.Name = "Exam preparation";
        await vm.Editor.SaveCommand.ExecuteAsync(null);

        // The group it was in is the group it is sent with: the event is not looked up in another one.
        Assert.Equal("g1", _api.TagInputs.Single().GroupId);
        Assert.Equal("exam-2", _api.TagInputs.Single().EventId);
    }

    [Fact]
    public async Task A_tag_can_be_given_the_name_it_already_has_in_another_case()
    {
        _api.AddTag("spring");
        var vm = await ShownAsync();
        await vm.BeginEditCommand.ExecuteAsync(Row("spring"));

        vm.Editor.Name = "Spring";

        Assert.Null(vm.Editor.NameProblem); // it is its own name
        Assert.True(vm.Editor.CanSave);
    }

    // --- Deleting ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_asks_first_and_says_how_many_comments_lose_the_tag()
    {
        var member = _api.AddMember("Anna");
        var used = _api.AddTag("Used");
        _api.AddTag("Unused");
        _api.AddComment(member.Id, "One", tags: [used]);
        _api.AddComment(member.Id, "Two", tags: [used]);
        var vm = await ShownAsync();

        vm.AskDeleteCommand.Execute(Row("Unused"));
        Assert.Equal("Delete the tag “Unused”?", vm.ConfirmText);

        vm.AskDeleteCommand.Execute(Row("Used"));
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal("Delete the tag “Used”? It is taken off the comments that carry it (2); the comments stay.", vm.ConfirmText);

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Empty(_api.CommentCalls);
    }

    [Fact]
    public async Task A_confirmed_delete_removes_the_tag_and_keeps_the_comments()
    {
        var member = _api.AddMember("Anna");
        var tag = _api.AddTag("Doomed");
        _api.AddTag("Safe");
        _api.AddComment(member.Id, "Words", tags: [tag]);
        var vm = await ShownAsync();

        vm.AskDeleteCommand.Execute(Row("Doomed"));
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal([$"delete-tag:{tag.Id}"], _api.CommentCalls);
        Assert.Equal(["Safe"], vm.Tags.Select(t => t.Name));
        Assert.Equal("Tag “Doomed” deleted.", vm.NoticeText);
        Assert.Empty(Assert.Single(_api.Comments).Tags);
    }

    [Fact]
    public async Task Deleting_the_tag_that_is_open_in_the_form_closes_the_form()
    {
        _api.AddTag("Doomed");
        var vm = await ShownAsync();
        await vm.BeginEditCommand.ExecuteAsync(Row("Doomed"));

        vm.AskDeleteCommand.Execute(Row("Doomed"));
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(vm.Editor.IsOpen);
    }

    // --- Language ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        _api.AddTag("Exam prep", "exam-2");
        var vm = await ShownAsync();
        await vm.BeginEditCommand.ExecuteAsync(Row("Exam prep"));
        vm.AskDeleteCommand.Execute(Row("Exam prep"));
        var option = vm.Editor.EventOptions[1];

        _language.Select("de");

        Assert.Equal("Schlagwörter", vm.Title);
        Assert.Equal("„Exam prep“ bearbeiten", vm.Editor.Title);
        Assert.Equal("Das Schlagwort „Exam prep“ löschen?", vm.ConfirmText);
        Assert.Same(option, vm.Editor.EventOptions[1]); // the entries stay, so a picker keeps its choice
        Assert.StartsWith("Gürtelprüfung · Sa", Row("Exam prep").EventText);

        vm.CancelDeleteCommand.Execute(null);
        vm.Editor.Name = "x".PadRight(TagRules.MaxNameLength + 1, 'x');
        Assert.Equal("Der Name muss 1 bis 50 Zeichen lang sein.", vm.Editor.NameProblem);
    }
}

/// <summary>The comments of the members page: opening them in place of the list, and the count on each member.</summary>
public sealed class MembersPageCommentsTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly MembersViewModel _vm;

    public MembersPageCommentsTests()
    {
        _language.Select("en");
        _api.AddMember("Anna");
        _api.AddMember("Ben");
        _api.AddMember("Dora", groupId: "g2");
        _shell = new ShellViewModel(_api, _language);
        _vm = new MembersViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    private async Task<MembersViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private MemberRow Row(string name) => _vm.Members.Single(m => m.Name == name);

    [Fact]
    public async Task A_member_shows_how_many_comments_there_are_on_the_button_that_opens_them()
    {
        var anna = _api.Members.Single(m => m.Name == "Anna");
        _api.AddComment(anna.Id, "One");
        _api.AddComment(anna.Id, "Two");
        var vm = await ShownAsync();

        Assert.Equal([2, 0], vm.Members.Select(m => m.CommentCount));
        Assert.Equal(["Comments (2)", "Comments"], vm.Members.Select(m => m.CommentsText));
    }

    [Fact]
    public async Task Opening_the_comments_takes_the_place_of_the_list_and_going_back_restores_it()
    {
        var vm = await ShownAsync();
        Assert.False(vm.IsShowingComments);
        Assert.False(vm.CloseCommentsCommand.CanExecute(null));

        await vm.OpenCommentsCommand.ExecuteAsync(Row("Anna"));

        Assert.True(vm.IsShowingComments);
        Assert.Equal("Comments on “Anna”", vm.Comments.Title);
        Assert.False(vm.OpenCommentsCommand.CanExecute(Row("Ben"))); // go back to the list first
        Assert.False(vm.BeginEditCommand.CanExecute(Row("Ben")));    // the list's edit form belongs to the list
        Assert.True(vm.CloseCommentsCommand.CanExecute(null));

        vm.CloseCommentsCommand.Execute(null);

        Assert.False(vm.IsShowingComments);
        Assert.True(vm.OpenCommentsCommand.CanExecute(Row("Ben")));
        Assert.True(vm.BeginEditCommand.CanExecute(Row("Ben")));
        Assert.Equal(["Anna", "Ben"], vm.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task The_count_follows_what_is_written_and_deleted()
    {
        var vm = await ShownAsync();
        await vm.OpenCommentsCommand.ExecuteAsync(Row("Anna"));

        vm.Comments.Text = "A first note";
        await vm.Comments.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Comments (1)", Row("Anna").CommentsText);
        Assert.Equal("Comments", Row("Ben").CommentsText);

        vm.Comments.AskDeleteCommand.Execute(vm.Comments.Comments[0]);
        await vm.Comments.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal("Comments", Row("Anna").CommentsText);
        Assert.Equal(0, Row("Anna").CommentCount);
    }

    [Fact]
    public async Task Another_group_closes_the_comments_of_a_member_of_the_old_one()
    {
        var vm = await ShownAsync();
        await vm.OpenCommentsCommand.ExecuteAsync(Row("Anna"));

        _shell.SelectedGroupId = "g2";
        await Eventually(() => vm.Members.Select(m => m.Name).SequenceEqual(["Dora"]));

        Assert.False(vm.IsShowingComments);
        Assert.False(vm.Comments.IsOpen);
    }

    [Fact]
    public async Task Showing_the_page_again_reads_the_open_comments_and_tags_again_and_keeps_the_draft()
    {
        var anna = _api.Members.Single(m => m.Name == "Anna");
        var doomed = _api.AddTag("Doomed");
        _api.AddTag("Safe");
        var vm = await ShownAsync();
        await vm.OpenCommentsCommand.ExecuteAsync(Row("Anna"));
        vm.Comments.Text = "A draft";
        vm.Comments.TagOptions.Single(o => o.Name == "Doomed").IsSelected = true;
        vm.Comments.TagOptions.Single(o => o.Name == "Safe").IsSelected = true;
        var lists = _api.MemberListRequests.Count;
        var reads = _api.CommentListRequests.Count;

        // Meanwhile (on the tags page, or by somebody else) a tag is deleted, one is made and a comment is written.
        vm.Deactivate();
        _api.Tags.Remove(doomed);
        _api.AddTag("New topic");
        _api.AddComment(anna.Id, "Written meanwhile");
        await vm.ActivateAsync();

        Assert.Equal(lists + 1, _api.MemberListRequests.Count);
        Assert.Equal(reads + 1, _api.CommentListRequests.Count);
        Assert.True(vm.IsShowingComments);
        Assert.Equal(["New topic", "Safe"], vm.Comments.TagOptions.Select(o => o.Name));
        Assert.Equal(["Safe"], vm.Comments.TagOptions.Where(o => o.IsSelected).Select(o => o.Name)); // the deleted tag is no longer ticked
        Assert.Equal(["Written meanwhile"], vm.Comments.Comments.Select(c => c.Comment.Body));
        Assert.Equal("A draft", vm.Comments.Text);
        Assert.Equal("Comments (1)", Row("Anna").CommentsText);
    }

    [Fact]
    public async Task Refreshing_the_data_keeps_the_comments_of_the_member_open_with_what_is_being_written()
    {
        var vm = await ShownAsync();
        await vm.OpenCommentsCommand.ExecuteAsync(Row("Anna"));
        vm.Comments.Text = "A draft";

        await _shell.RefreshDataCommand.ExecuteAsync(null);
        await Eventually(() => _api.RefreshCalls == 1 && !vm.IsBusy && vm.Members.Count == 2);

        Assert.True(vm.IsShowingComments);
        Assert.Equal("A draft", vm.Comments.Text);
        Assert.Equal(["Anna", "Ben"], vm.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task The_button_of_the_list_follows_the_language()
    {
        var anna = _api.Members.Single(m => m.Name == "Anna");
        _api.AddComment(anna.Id, "One");
        var vm = await ShownAsync();

        _language.Select("de");

        Assert.Equal("Kommentare (1)", Row("Anna").CommentsText);
        Assert.Equal("Kommentare", Row("Ben").CommentsText);
    }
}
