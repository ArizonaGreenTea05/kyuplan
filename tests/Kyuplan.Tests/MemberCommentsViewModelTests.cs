using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The comments on one member: the threads, writing, replying, editing with its history, tags and deleting.</summary>
public sealed class MemberCommentsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly MemberCommentsViewModel _vm;
    private readonly MemberDto _anna;
    private readonly TagDto _exam;
    private readonly TagDto _belt;

    public MemberCommentsViewModelTests()
    {
        _language.Select("en");
        _anna = _api.AddMember("Anna");
        _exam = _api.AddTag("Exam", "exam-2");
        _belt = _api.AddTag("Belt");

        _shell = new ShellViewModel(_api, _language); // the signed-in user is "anna" (id 1), not an administrator
        _vm = new MemberCommentsViewModel(_shell, _api);
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

    private static MemberRow RowOf(MemberDto member) => new(member, new BeltOption());

    private async Task<MemberCommentsViewModel> ShownAsync(MemberDto? member = null)
    {
        await _shell.InitializeAsync();
        await _vm.ShowAsync(RowOf(member ?? _anna));
        return _vm;
    }

    private CommentRow Row(string body) => _vm.Comments.Single(c => c.Comment.Body == body);

    private TagOption Option(string name) => _vm.TagOptions.Single(o => o.Name == name);

    // --- The threads -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Showing_a_member_reads_their_comments_and_the_tags()
    {
        _api.AddComment(_anna.Id, "One");
        var vm = await ShownAsync();

        Assert.True(vm.IsOpen);
        Assert.Equal("Comments on “Anna”", vm.Title);
        Assert.Equal([_anna.Id], _api.CommentListRequests);
        Assert.Equal(1, _api.TagListRequests);
        Assert.Equal(["Belt", "Exam"], vm.TagOptions.Select(o => o.Name));
        Assert.Equal("1 comment", vm.CountText);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_member_without_comments_invites_to_write_the_first()
    {
        var vm = await ShownAsync();

        Assert.True(vm.IsEmpty);
        Assert.Equal("0 comments", vm.CountText);
    }

    [Fact]
    public async Task The_threads_come_newest_first_each_followed_by_its_replies_oldest_first()
    {
        var first = _api.AddComment(_anna.Id, "First thread", authorId: 2, authorName: "ben");
        var reply1 = _api.AddComment(_anna.Id, "Reply one", parentId: first.Id);
        var second = _api.AddComment(_anna.Id, "Second thread");
        _api.AddComment(_anna.Id, "Reply to the reply", authorId: 2, authorName: "ben", parentId: reply1.Id);
        _api.AddComment(_anna.Id, "Reply two", parentId: first.Id);
        var vm = await ShownAsync();

        Assert.Equal(["Second thread", "First thread", "Reply one", "Reply to the reply", "Reply two"], vm.Comments.Select(c => c.Comment.Body));
        Assert.Equal([0, 0, 1, 2, 1], vm.Comments.Select(c => c.Depth));
        Assert.Equal([0, 3, 1, 0, 0], vm.Comments.Select(c => c.ReplyCount)); // replies of replies count: they are deleted along
        Assert.Equal([0.0, 0.0, CommentRow.IndentStep, 2 * CommentRow.IndentStep, CommentRow.IndentStep], vm.Comments.Select(c => c.IndentWidth));
        Assert.Equal(second.Id, vm.Comments[0].Id);
    }

    [Fact]
    public async Task A_conversation_is_not_moved_in_any_further_than_a_limit()
    {
        var parent = _api.AddComment(_anna.Id, "0");
        for (var i = 1; i <= CommentRow.MaxIndentLevel + 3; i++) parent = _api.AddComment(_anna.Id, i.ToString(), parentId: parent.Id);
        var vm = await ShownAsync();

        Assert.Equal(CommentRow.MaxIndentLevel + 4, vm.Comments.Count);
        Assert.Equal(CommentRow.MaxIndentLevel * CommentRow.IndentStep, vm.Comments[^1].IndentWidth);
        Assert.Equal(CommentRow.MaxIndentLevel + 3, vm.Comments[^1].Depth);
    }

    [Fact]
    public async Task A_comment_shows_its_author_its_time_its_styled_words_and_its_tags()
    {
        _api.AddComment(_anna.Id, "Needs **more** practice", authorId: 2, authorName: "ben", tags: [_exam, _belt]);
        var vm = await ShownAsync();

        var comment = Assert.Single(vm.Comments);
        Assert.Equal("ben", comment.AuthorName);
        Assert.Equal("30 Jun 2026, 12:00", comment.CreatedText);
        Assert.Equal(["Needs ", "more", " practice"], comment.Spans.Select(s => s.Text));
        Assert.Equal(CommentStyle.Bold, comment.Spans[1].Style);
        Assert.Equal(["Exam", "Belt"], comment.Tags.Select(t => t.Name));
        Assert.True(comment.HasTags);
        Assert.Contains("Gürtelprüfung", comment.Tags[0].ToolTip); // the event the tag is about
        Assert.Equal("Belt", comment.Tags[1].ToolTip);              // no event: the name
        Assert.False(comment.IsEdited);
        Assert.Equal("", comment.EditedText);
    }

    [Fact]
    public async Task Another_member_is_another_list_and_a_draft_does_not_come_along()
    {
        var ben = _api.AddMember("Ben");
        _api.AddComment(_anna.Id, "About Anna");
        _api.AddComment(ben.Id, "About Ben");
        var vm = await ShownAsync();
        vm.Text = "Half written";
        Option("Exam").IsSelected = true;

        await vm.ShowAsync(RowOf(ben));

        Assert.Equal(["About Ben"], vm.Comments.Select(c => c.Comment.Body));
        Assert.Equal("", vm.Text);
        Assert.DoesNotContain(vm.TagOptions, o => o.IsSelected);
        Assert.Equal("Comments on “Ben”", vm.Title);
    }

    [Fact]
    public async Task Closing_forgets_the_member_the_comments_and_a_draft()
    {
        var vm = await ShownAsync();
        vm.Text = "Half written";

        vm.Close();

        Assert.False(vm.IsOpen);
        Assert.Empty(vm.Comments);
        Assert.Empty(vm.TagOptions);
        Assert.Equal("", vm.Text);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task Comments_that_cannot_be_read_say_why_and_can_be_tried_again()
    {
        _api.CommentListFailure = new PlannerApiException("The API is away.", null);
        var vm = await ShownAsync();

        Assert.Equal("The API is away.", vm.ErrorMessage);
        Assert.False(vm.IsEmpty); // not "no comments": they were not read

        _api.CommentListFailure = null;
        _api.AddComment(_anna.Id, "Back");
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(["Back"], vm.Comments.Select(c => c.Comment.Body));
    }

    // --- Writing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_comment_needs_words_before_it_can_be_sent()
    {
        var vm = await ShownAsync();
        Assert.False(vm.CanSave);

        vm.Text = "  \r\n ";
        Assert.False(vm.CanSave);

        vm.Text = "Words";
        Assert.True(vm.CanSave);

        vm.Text = new string('x', CommentRules.MaxBodyLength + 1);
        Assert.False(vm.CanSave);
        Assert.Equal("The comment is too long (at most 4000 characters).", vm.TextProblem);
    }

    [Fact]
    public async Task A_comment_is_sent_cleaned_with_the_ticked_tags_and_the_composer_starts_over()
    {
        var vm = await ShownAsync();
        var counts = new List<int>();
        vm.CountChanged += (_, e) => counts.Add(e.Count);
        vm.Text = "  Needs **practice**\r\nat home\r\n";
        Option("Exam").IsSelected = true;

        await vm.SaveCommand.ExecuteAsync(null);

        var input = Assert.Single(_api.CommentInputs);
        Assert.Equal("Needs **practice**\nat home", input.Body);
        Assert.Equal([_exam.Id], input.TagIds);
        Assert.Null(input.ParentId);
        Assert.Equal([$"create-comment:{_anna.Id}"], _api.CommentCalls);
        Assert.Equal(["Needs **practice**\nat home"], vm.Comments.Select(c => c.Comment.Body));
        Assert.Equal("anna", vm.Comments[0].AuthorName);
        Assert.Equal([1], counts); // the number of comments is announced for the members list
        Assert.Equal("", vm.Text);
        Assert.DoesNotContain(vm.TagOptions, o => o.IsSelected);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task A_comment_that_could_not_be_sent_stays_in_the_composer_until_the_text_is_corrected()
    {
        var vm = await ShownAsync();
        vm.Text = "Words";
        _api.FailWith = new PlannerApiException("The API is away.", HttpStatusCode.ServiceUnavailable);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The API is away.", vm.ErrorMessage);
        Assert.Equal("Words", vm.Text);
        Assert.True(vm.CanSave);

        _api.FailWith = null;
        vm.Text = "More words"; // correcting the comment: the old complaint is old news
        Assert.False(vm.HasError);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["More words"], vm.Comments.Select(c => c.Comment.Body));
    }

    [Fact]
    public async Task The_preview_shows_the_words_as_they_will_be_styled()
    {
        var vm = await ShownAsync();
        Assert.False(vm.HasPreview);

        vm.Text = "a ~~wrong~~ and __marked__ word";

        Assert.True(vm.HasPreview);
        Assert.Equal(["a ", "wrong", " and ", "marked", " word"], vm.Preview.Select(s => s.Text));
        Assert.Equal([CommentStyle.None, CommentStyle.Strikethrough, CommentStyle.None, CommentStyle.Underline, CommentStyle.None], vm.Preview.Select(s => s.Style));
    }

    [Fact]
    public async Task The_toolbar_styles_the_selection_and_says_what_to_select_afterwards()
    {
        var vm = await ShownAsync();
        vm.Text = "a word here";

        var change = vm.ApplyFormat(CommentStyle.Bold, 2, 4);

        Assert.Equal("a **word** here", vm.Text);
        Assert.Equal(new TextChange("a **word** here", 4, 4), change);
        Assert.Equal(CommentStyle.Bold, vm.Preview[1].Style);

        vm.ApplyFormat(CommentStyle.Bold, change.SelectionStart, change.SelectionLength); // again: off
        Assert.Equal("a word here", vm.Text);
    }

    // --- Replying ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reply_is_sent_with_the_comment_it_answers_and_shows_under_it()
    {
        var question = _api.AddComment(_anna.Id, "Question", authorId: 2, authorName: "ben");
        var vm = await ShownAsync();

        vm.ReplyCommand.Execute(vm.Comments[0]);
        Assert.True(vm.IsReplying);
        Assert.Equal("Reply to ben", vm.ComposerTitle);
        Assert.Equal("Post reply", vm.SaveText);
        Assert.False(vm.ReplyCommand.CanExecute(vm.Comments[0])); // already replying to it

        vm.Text = "Answer";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(question.Id, Assert.Single(_api.CommentInputs).ParentId);
        Assert.Equal(["Question", "Answer"], vm.Comments.Select(c => c.Comment.Body));
        Assert.Equal([0, 1], vm.Comments.Select(c => c.Depth));
        Assert.False(vm.IsReplying);
        Assert.Equal("Write a comment", vm.ComposerTitle);
        Assert.Equal("Post comment", vm.SaveText);
    }

    [Fact]
    public async Task A_draft_can_become_a_reply_and_giving_up_the_reply_clears_it()
    {
        _api.AddComment(_anna.Id, "Question");
        var vm = await ShownAsync();
        vm.Text = "Half written";
        Assert.False(vm.CancelComposerCommand.CanExecute(null)); // a comment that is not written is just not sent: nothing to give up

        vm.ReplyCommand.Execute(vm.Comments[0]);

        Assert.Equal("Half written", vm.Text);
        Assert.True(vm.CancelComposerCommand.CanExecute(null));

        vm.CancelComposerCommand.Execute(null);

        Assert.False(vm.IsReplying);
        Assert.Equal("", vm.Text);
        Assert.False(vm.CancelComposerCommand.CanExecute(null));
    }

    // --- Editing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Anybody_can_edit_a_comment_and_the_composer_starts_with_what_it_says()
    {
        _api.AddComment(_anna.Id, "Needs **practice**", authorId: 2, authorName: "ben", tags: [_exam]);
        var vm = await ShownAsync();

        vm.EditCommand.Execute(vm.Comments[0]);

        Assert.True(vm.IsEditing);
        Assert.Equal("Edit comment", vm.ComposerTitle);
        Assert.Equal("Save changes", vm.SaveText);
        Assert.Equal("Needs **practice**", vm.Text);
        Assert.True(Option("Exam").IsSelected);
        Assert.False(Option("Belt").IsSelected);
        Assert.False(vm.CanSave); // nothing differs yet
        Assert.False(vm.EditCommand.CanExecute(vm.Comments[0]));
    }

    [Fact]
    public async Task An_edit_can_be_saved_once_the_words_or_the_tags_differ()
    {
        _api.AddComment(_anna.Id, "Words", tags: [_exam]);
        var vm = await ShownAsync();
        vm.EditCommand.Execute(vm.Comments[0]);

        vm.Text = "Words and more";
        Assert.True(vm.CanSave);
        vm.Text = "Words\r\n"; // the same words once cleaned
        Assert.False(vm.CanSave);

        Option("Belt").IsSelected = true;
        Assert.True(vm.CanSave);
        Option("Belt").IsSelected = false;
        Assert.False(vm.CanSave);

        Option("Exam").IsSelected = false;
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task A_saved_edit_shows_who_made_it_and_how_and_the_history_opens_on_demand()
    {
        _api.AddComment(_anna.Id, "Needs practice", authorId: 2, authorName: "ben", tags: [_belt]);
        var vm = await ShownAsync();
        vm.EditCommand.Execute(vm.Comments[0]);
        vm.Text = "Needs more practice";
        Option("Belt").IsSelected = false;
        Option("Exam").IsSelected = true;

        await vm.SaveCommand.ExecuteAsync(null);

        var update = Assert.Single(_api.CommentUpdates);
        Assert.Equal("Needs more practice", update.Body);
        Assert.Equal([_exam.Id], update.TagIds);
        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.Text);

        var comment = Assert.Single(vm.Comments);
        Assert.Equal("ben", comment.AuthorName); // still ben's comment; anna is the editor
        Assert.True(comment.IsEdited);
        Assert.StartsWith("Edited by anna, 30 Jun 2026, 12:", comment.EditedText);
        var edit = Assert.Single(comment.Edits);
        Assert.Equal("anna", edit.EditorName);
        Assert.Equal("changed the text; tags added: Exam; tags removed: Belt", edit.ChangesText);
        Assert.True(edit.HasPrevious);
        Assert.Equal("Needs practice", string.Concat(edit.PreviousSpans.Select(s => s.Text)));

        Assert.False(comment.IsHistoryOpen);
        vm.ToggleHistoryCommand.Execute(comment);
        Assert.True(comment.IsHistoryOpen);
        vm.ToggleHistoryCommand.Execute(comment);
        Assert.False(comment.IsHistoryOpen);
    }

    [Fact]
    public async Task The_history_names_every_edit_the_latest_first_and_a_change_of_tags_alone_has_no_before()
    {
        _api.AddComment(_anna.Id, "Words");
        var vm = await ShownAsync();

        vm.EditCommand.Execute(vm.Comments[0]);
        vm.Text = "**Words**";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.EditCommand.Execute(vm.Comments[0]);
        Option("Exam").IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var comment = Assert.Single(vm.Comments);
        Assert.Equal(["tags added: Exam", "changed the formatting"], comment.Edits.Select(e => e.ChangesText));
        Assert.Equal([false, true], comment.Edits.Select(e => e.HasPrevious));
        Assert.Contains("· 2 edits", comment.EditedText);
    }

    [Fact]
    public async Task Giving_up_an_edit_leaves_the_comment_alone_and_clears_the_composer()
    {
        _api.AddComment(_anna.Id, "Words", tags: [_exam]);
        var vm = await ShownAsync();
        vm.EditCommand.Execute(vm.Comments[0]);
        vm.Text = "Changed my mind";

        vm.CancelComposerCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.Text);
        Assert.DoesNotContain(vm.TagOptions, o => o.IsSelected);
        Assert.Empty(_api.CommentUpdates);
    }

    [Fact]
    public async Task A_comment_that_is_deleted_meanwhile_ends_the_edit_of_it()
    {
        var comment = _api.AddComment(_anna.Id, "Words");
        var vm = await ShownAsync();
        vm.EditCommand.Execute(vm.Comments[0]);
        vm.Text = "Changed";

        _api.Comments.RemoveAll(c => c.Id == comment.Id);
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.Text);
        Assert.True(vm.IsEmpty);
    }

    // --- Deleting ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_author_can_delete_a_comment_and_says_why_the_others_cannot()
    {
        _api.AddComment(_anna.Id, "Mine");
        _api.AddComment(_anna.Id, "Ben's", authorId: 2, authorName: "ben");
        var vm = await ShownAsync();

        var (mine, bens) = (Row("Mine"), Row("Ben's"));

        Assert.True(mine.CanDelete);
        Assert.True(vm.AskDeleteCommand.CanExecute(mine));
        Assert.Equal("", mine.DeleteBlockedText);
        Assert.False(bens.CanDelete);
        Assert.False(vm.AskDeleteCommand.CanExecute(bens));
        Assert.Equal("You are not allowed to delete this comment.", bens.DeleteBlockedText);
    }

    [Fact]
    public async Task An_administrator_can_delete_every_comment()
    {
        _api.Account = Dto(1, "anna", "anna", [], isAdmin: true);
        _api.AddComment(_anna.Id, "Ben's", authorId: 2, authorName: "ben");
        var vm = await ShownAsync();

        Assert.True(vm.Comments.Single().CanDelete);
    }

    [Fact]
    public async Task Deleting_asks_first_and_says_how_many_replies_go_along()
    {
        var thread = _api.AddComment(_anna.Id, "Thread");
        var vm = await ShownAsync();
        var row = vm.Comments[0];

        vm.AskDeleteCommand.Execute(row);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal("Delete this comment?", vm.ConfirmText);

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Empty(_api.CommentCalls);

        var reply = _api.AddComment(_anna.Id, "Reply", parentId: thread.Id);
        await vm.RetryCommand.ExecuteAsync(null);
        vm.AskDeleteCommand.Execute(vm.Comments[0]);
        Assert.Equal("Delete this comment and its reply?", vm.ConfirmText);

        _api.AddComment(_anna.Id, "Another reply", parentId: reply.Id);
        await vm.RetryCommand.ExecuteAsync(null);
        vm.AskDeleteCommand.Execute(vm.Comments[0]);
        Assert.Equal("Delete this comment and its 2 replies?", vm.ConfirmText);
    }

    [Fact]
    public async Task A_confirmed_delete_removes_the_thread_and_announces_the_new_count()
    {
        var thread = _api.AddComment(_anna.Id, "Thread");
        _api.AddComment(_anna.Id, "Reply", parentId: thread.Id);
        _api.AddComment(_anna.Id, "Other thread");
        var vm = await ShownAsync();
        var counts = new List<int>();
        vm.CountChanged += (_, e) => counts.Add(e.Count);

        vm.AskDeleteCommand.Execute(Row("Thread"));
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal([$"delete-comment:{thread.Id}"], _api.CommentCalls);
        Assert.Equal(["Other thread"], vm.Comments.Select(c => c.Comment.Body));
        Assert.Equal([1], counts);
        Assert.False(vm.IsConfirmingDelete);
    }

    // --- Tags ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_made_while_writing_is_ticked_at_once()
    {
        var vm = await ShownAsync();
        vm.Text = "Words";
        Option("Belt").IsSelected = true;

        await vm.NewTagCommand.ExecuteAsync(null);
        Assert.True(vm.TagEditor.IsOpen);
        Assert.False(vm.NewTagCommand.CanExecute(null)); // the form for a new tag is open already
        vm.TagEditor.Name = "Fresh topic";
        await vm.TagEditor.SaveCommand.ExecuteAsync(null);
        await Eventually(() => vm.TagOptions.Any(o => o.Name == "Fresh topic"));

        Assert.False(vm.TagEditor.IsOpen);
        Assert.Equal(["Belt", "Exam", "Fresh topic"], vm.TagOptions.Select(o => o.Name));
        Assert.True(Option("Fresh topic").IsSelected);
        Assert.True(Option("Belt").IsSelected); // what was ticked stays ticked
        Assert.Equal("Words", vm.Text);          // and so does the draft
    }

    [Fact]
    public async Task A_tag_changed_while_writing_shows_its_new_name_on_the_comments_that_carry_it()
    {
        _api.AddComment(_anna.Id, "Words", tags: [_belt]);
        var vm = await ShownAsync();

        await vm.EditTagCommand.ExecuteAsync(Option("Belt"));
        Assert.Equal("Belt", vm.TagEditor.Name);
        vm.TagEditor.Name = "Belts";
        await vm.TagEditor.SaveCommand.ExecuteAsync(null);
        await Eventually(() => vm.Comments[0].Tags[0].Name == "Belts");

        Assert.Contains("update-tag:" + _belt.Id, _api.CommentCalls);
        Assert.Equal(["Belts", "Exam"], vm.TagOptions.Select(o => o.Name));
    }

    [Fact]
    public async Task A_tag_cannot_be_given_a_name_another_tag_has()
    {
        var vm = await ShownAsync();

        await vm.NewTagCommand.ExecuteAsync(null);
        vm.TagEditor.Name = "belt";

        Assert.Equal("There already is a tag with this name.", vm.TagEditor.NameProblem);
        Assert.False(vm.TagEditor.CanSave);
    }

    // --- Language ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var thread = _api.AddComment(_anna.Id, "Thread", authorId: 2, authorName: "ben");
        _api.AddComment(_anna.Id, "Reply", parentId: thread.Id);
        var vm = await ShownAsync();
        vm.EditCommand.Execute(vm.Comments[1]);
        vm.Text = "Reply changed";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.ReplyCommand.Execute(vm.Comments[0]);
        vm.AskDeleteCommand.Execute(Row("Reply changed"));
        Assert.Equal("2 comments", vm.CountText);
        Assert.Equal("Reply to ben", vm.ComposerTitle);

        _language.Select("de");

        Assert.Equal("Kommentare zu „Anna“", vm.Title);
        Assert.Equal("2 Kommentare", vm.CountText);
        Assert.Equal("Antwort an ben", vm.ComposerTitle);
        Assert.Equal("Antwort senden", vm.SaveText);
        Assert.Equal("Diesen Kommentar löschen?", vm.ConfirmText);
        Assert.StartsWith("Bearbeitet von anna, 30. Jun", Row("Reply changed").EditedText); // June is "Juni" or "Jun", depending on the ICU version
        Assert.EndsWith("2026, 12:02", Row("Reply changed").EditedText);
        Assert.Equal("Text geändert", Row("Reply changed").Edits[0].ChangesText);
    }
}
