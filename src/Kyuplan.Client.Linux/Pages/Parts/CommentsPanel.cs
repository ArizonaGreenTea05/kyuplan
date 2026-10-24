using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages.Parts;

/// <summary>
/// The comments on one member (<see cref="MemberCommentsViewModel"/>): the composer for a new comment, a reply or a change, and the threads under it.
/// Used on the members page, in an exam and on the account page.
/// </summary>
internal static class CommentsPanel
{
    public static Gtk.Widget Build(MemberCommentsViewModel comments, bool showTitle = true)
    {
        var composer = Composer(comments);
        var delete = Ui.HBox(12,
            Ui.Label(comments, nameof(MemberCommentsViewModel.ConfirmText)).Expand(),
            Ui.Button(Strings.Comments_ConfirmYes, comments.ConfirmDeleteCommand),
            Ui.Button(Strings.Comments_Cancel, comments.CancelDeleteCommand))
            .Css("banner-problem").VisibleWhen(comments, nameof(MemberCommentsViewModel.IsConfirmingDelete));

        var retry = Ui.Button(Strings.Shell_TryAgain, comments.RetryCommand).HiddenWhen(comments, nameof(MemberCommentsViewModel.HasLoaded));
        var error = Ui.HBox(12, Ui.Label(comments, nameof(ViewModelBase.ErrorMessage)).Expand(), retry)
            .Css("banner-problem").VisibleWhen(comments, nameof(ViewModelBase.HasError));

        var threads = Ui.Repeat(comments, () => comments.Comments, row => Comment(comments, row), [nameof(MemberCommentsViewModel.Comments)], spacing: 10);

        var head = Ui.HBox(12,
            Ui.Label(comments, nameof(MemberCommentsViewModel.Title), "title-3").Start(),
            Ui.Muted("").Start());
        ((Gtk.Label)head.GetLastChild()!).Hexpand = false;
        Bind.Watch(head, comments, () => ((Gtk.Label)head.GetLastChild()!).SetText(comments.CountText), nameof(MemberCommentsViewModel.Comments));

        var column = Ui.VBox(12);
        if (showTitle) column.Append(head);
        column.Append(Ui.Muted(Strings.Comments_Lede));
        column.Append(error);
        column.Append(composer);
        column.Append(delete);
        column.Append(Ui.Muted(Strings.Comments_Empty).VisibleWhen(comments, nameof(MemberCommentsViewModel.IsEmpty)));
        column.Append(threads);
        return column;
    }

    private static Gtk.Widget Composer(MemberCommentsViewModel comments)
    {
        var area = Ui.TextArea(comments, nameof(MemberCommentsViewModel.Text), out var view, 110, Strings.Comments_Placeholder);
        var buffer = view.GetBuffer();

        // Ctrl+Enter sends the comment (Enter alone is a new line).
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval is not (Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter) || !e.State.HasFlag(Gdk.ModifierType.ControlMask)) return false;

            if (comments.SaveCommand.CanExecute(null)) comments.SaveCommand.Execute(null);
            return true;
        };
        view.AddController(keys);

        // A style button: the view model puts the markers around what is selected in the box (or takes them off), and the same words are
        // selected again, so that the next button works on them too.
        void Format(CommentStyle style)
        {
            var text = comments.Text;
            buffer.GetSelectionBounds(out var start, out var end);
            var from = RichText.ToUtf16(text, start.GetOffset());
            var length = RichText.ToUtf16(text, end.GetOffset()) - from;
            var change = comments.ApplyFormat(style, from, length);
            view.GrabFocus();
            buffer.GetIterAtOffset(out var newStart, RichText.ToCharacters(change.Text, change.SelectionStart));
            buffer.GetIterAtOffset(out var newEnd, RichText.ToCharacters(change.Text, change.SelectionStart + change.SelectionLength));
            buffer.SelectRange(newStart, newEnd);
        }

        Gtk.Button FormatButton(string glyph, string markup, string tip, CommentStyle style)
        {
            var label = Gtk.Label.New(null);
            label.UseMarkup = true;
            label.SetMarkup(markup.Replace("{0}", glyph));
            var button = Gtk.Button.New();
            button.SetChild(label);
            button.OnClicked += (_, _) => Format(style);
            button.AddCssClass("flat");
            return button.Tip(tip);
        }

        var styles = Ui.HBox(2,
            FormatButton("B", "<b>{0}</b>", Strings.Comments_Bold, CommentStyle.Bold),
            FormatButton("I", "<i>{0}</i>", Strings.Comments_Italic, CommentStyle.Italic),
            FormatButton("U", "<u>{0}</u>", Strings.Comments_Underline, CommentStyle.Underline),
            FormatButton("S", "<s>{0}</s>", Strings.Comments_Strike, CommentStyle.Strikethrough));

        // Who the reply is for.
        var replying = Ui.HBox(8, Ui.Label(comments, () => comments.ReplyingTo?.AuthorName, [nameof(MemberCommentsViewModel.ReplyingTo)], "heading"),
            Ui.Label(comments, () => comments.ReplyingTo is { } to ? string.Concat(to.Spans.Select(s => s.Text)) : "", [nameof(MemberCommentsViewModel.ReplyingTo)], "dim-label"))
            .VisibleWhen(comments, nameof(MemberCommentsViewModel.IsReplying));

        var preview = Ui.VBox(2, Ui.Muted(Strings.Comments_Preview).Css("caption"), Ui.Repeat(comments, () => new[] { comments.Preview }, spans => RichText.Label(spans), [nameof(MemberCommentsViewModel.Preview)]))
            .Css("card").VisibleWhen(comments, nameof(MemberCommentsViewModel.HasPreview));

        // Tags: tick the ones this comment is about; make a new one or change one right here.
        var tagOptions = Ui.Repeat(comments, () => comments.TagOptions, tag => TagChoice(comments, tag), [nameof(MemberCommentsViewModel.TagOptions)], 6, Gtk.Orientation.Horizontal);
        var tagArea = Ui.VBox(6,
            Ui.Label(Strings.Comments_Tags, "caption-heading").Start(),
            tagOptions,
            Ui.Muted(Strings.Comments_NoTags).VisibleWhen(comments, () => comments.TagOptions.Count == 0, nameof(MemberCommentsViewModel.TagOptions)),
            Ui.Button(Strings.Comments_NewTag, comments.NewTagCommand).Start().VisibleWhen(comments, nameof(MemberCommentsViewModel.CanEditTags)),
            TagForm.Panel(comments.TagEditor));

        var save = Ui.Primary("", comments.SaveCommand);
        Bind.Watch(save, comments, () => save.SetLabel(comments.SaveText), nameof(MemberCommentsViewModel.SaveText));
        var cancel = Ui.Button(Strings.Comments_Cancel, comments.CancelComposerCommand)
            .VisibleWhen(comments, () => comments.IsReplying || comments.IsEditing, nameof(MemberCommentsViewModel.IsReplying), nameof(MemberCommentsViewModel.IsEditing));

        var card = Ui.VBox(8,
            Ui.Label(comments, nameof(MemberCommentsViewModel.ComposerTitle), "heading").Start(),
            replying, styles, area,
            Ui.Label(comments, () => comments.TextProblem, [nameof(MemberCommentsViewModel.TextProblem), nameof(MemberCommentsViewModel.Text)], "error")
                .VisibleWhen(comments, () => comments.TextProblem is not null, nameof(MemberCommentsViewModel.TextProblem), nameof(MemberCommentsViewModel.Text)),
            Ui.Muted(Strings.Comments_FormatHint).Css("caption"),
            preview, tagArea,
            Ui.HBox(8, save, cancel));
        return card.Css("card").Padded(14).VisibleWhen(comments, nameof(MemberCommentsViewModel.CanCompose));
    }

    /// <summary>A tag that can be ticked for the comment, with a pencil to change the tag.</summary>
    private static Gtk.Widget TagChoice(MemberCommentsViewModel comments, TagOption tag)
    {
        var check = Ui.Check(tag, nameof(TagOption.IsSelected), tag.Name);
        check.Tip(tag.ToolTip);
        var edit = Ui.IconButton("document-edit-symbolic", $"{tag.Name} {Strings.Comments_Edit}", comments.EditTagCommand, tag, "flat")
            .VisibleWhen(comments, nameof(MemberCommentsViewModel.CanEditTags));
        return Ui.HBox(2, check, edit).Css("chip");
    }

    private static Gtk.Widget Comment(MemberCommentsViewModel comments, CommentRow row)
    {
        var tags = Ui.HBox(6);
        foreach (var tag in row.Tags) tags.Append(Ui.Chip(tag.Name).Tip(tag.ToolTip));
        tags.Visible = row.HasTags;

        var actions = Ui.HBox(6,
            Ui.Button(Strings.Comments_Reply, comments.ReplyCommand, row).VisibleWhen(comments, nameof(MemberCommentsViewModel.CanWriteComments)),
            Ui.Button(Strings.Comments_Edit, comments.EditCommand, row).VisibleWhen(comments, nameof(MemberCommentsViewModel.CanEditComments)),
            Ui.Button(Strings.Comments_Delete, comments.AskDeleteCommand, row).Tip(row.DeleteBlockedText));
        actions.GetLastChild()!.Visible = row.CanDelete;

        var history = Ui.Button("", comments.ToggleHistoryCommand, row, "flat");
        Bind.Watch(history, row, () => history.SetLabel(row.EditedText), string.Empty);
        history.Visible = row.IsEdited;
        var bar = Ui.HBox(6, actions.Expand(), history);

        var edits = Ui.Repeat(row, () => row.Edits, EditEntry, [nameof(CommentRow.Edits)], 6).Css("card").VisibleWhen(row, nameof(CommentRow.IsHistoryOpen));

        var card = Ui.VBox(6,
            Ui.HBox(10, Ui.Label(row.AuthorName, "heading"), Ui.Muted(row.CreatedText)),
            RichText.Label(row.Spans), tags, bar, edits);
        card.Css("card").Padded(12);
        card.MarginStart = (int)row.IndentWidth;
        return card;
    }

    private static Gtk.Widget EditEntry(CommentEditRow edit)
    {
        var box = Ui.VBox(2, Ui.HBox(8, Ui.Label(edit.EditorName, "heading"), Ui.Muted(edit.WhenText), Ui.Muted("·"), Ui.Label(edit.ChangesText)));
        if (edit.HasPrevious) box.Append(Ui.HBox(8, Ui.Muted(Strings.Comments_Before), RichText.Label(edit.PreviousSpans)));
        return box.Padded(6);
    }
}
