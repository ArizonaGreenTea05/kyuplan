using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>A tag as it hangs on a comment, or in a list of tags to choose from: its name and, when it has one, its event.</summary>
public class TagChip(int id, string name, TagEventDto? tagEvent) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    public bool HasEvent => tagEvent is not null;

    /// <summary>"Gürtelprüfung · Sat, 26 Sep · 10:00", the event the tag is about; empty for a tag without one. It reads like a tooltip.</summary>
    public string EventText => tagEvent is { } e ? $"{e.Name} · {Display.EventStart(e.Start)}" : "";

    /// <summary>What a tooltip says: the event, or the name of a tag that has none.</summary>
    public string ToolTip => HasEvent ? EventText : Name;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A tag in the list of the comment being written: ticked when the comment carries it.</summary>
public sealed partial class TagOption(TagDto tag) : TagChip(tag.Id, tag.Name, tag.Event)
{
    public TagDto Tag => tag;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One edit of a comment as the history shows it: who, when, how, and what the comment said before. The texts follow the language, so the
/// row re-reads them after <see cref="Refresh"/>.
/// </summary>
public sealed class CommentEditRow(CommentEditDto edit) : ObservableObject
{
    public string EditorName => edit.EditorName;

    public string WhenText => Display.Moment(edit.EditedAt);

    /// <summary>"changed the text; tags added: Exam": what the edit did.</summary>
    public string ChangesText
    {
        get
        {
            List<string> parts = [];
            if (edit.TextChanged) parts.Add(Strings.Comments_ChangedText);
            if (edit.FormattingChanged) parts.Add(Strings.Comments_ChangedFormatting);
            if (edit.TagsAdded.Count > 0) parts.Add(string.Format(Strings.Comments_TagsAdded, string.Join(", ", edit.TagsAdded)));
            if (edit.TagsRemoved.Count > 0) parts.Add(string.Format(Strings.Comments_TagsRemoved, string.Join(", ", edit.TagsRemoved)));
            return string.Join("; ", parts);
        }
    }

    /// <summary>Only an edit of the words or the styling has a different "before" to show.</summary>
    public bool HasPrevious => edit.TextChanged || edit.FormattingChanged;

    /// <summary>What the comment said before the edit.</summary>
    public IReadOnlyList<CommentSpan> PreviousSpans { get; } = CommentFormat.Parse(edit.PreviousBody);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// A comment as one line of the thread list: the comment plus ready-to-show texts. The list is flat; <see cref="Depth"/> says how deep in the
/// replies the comment is, and <see cref="IndentWidth"/> how far to move it in. The texts follow the language, so the row re-reads them after
/// <see cref="Refresh"/>.
/// </summary>
public sealed partial class CommentRow : ObservableObject
{
    /// <summary>Deeper replies are not moved in any further: a long conversation would run out of width.</summary>
    public const int MaxIndentLevel = 4;

    /// <summary>How far one level of replies is moved in, in pixels (WPF) or CSS pixels (browser).</summary>
    public const double IndentStep = 24;

    private readonly CommentDto _comment;

    public CommentRow(CommentDto comment, int depth, int replyCount, bool canDelete, bool canEdit = false)
    {
        _comment = comment;
        Depth = depth;
        ReplyCount = replyCount;
        CanDelete = canDelete;
        CanEdit = canEdit;
        Spans = CommentFormat.Parse(comment.Body);
        Tags = [.. comment.Tags.Select(t => new TagChip(t.Id, t.Name, t.Event))];
        Edits = [.. comment.Edits.OrderByDescending(e => e.Id).Select(e => new CommentEditRow(e))];
    }

    public CommentDto Comment => _comment;

    public int Id => _comment.Id;

    /// <summary>0 for a comment that starts a thread, 1 for a reply to it, and so on.</summary>
    public int Depth { get; }

    public double IndentWidth => Math.Min(Depth, MaxIndentLevel) * IndentStep;

    /// <summary>How many replies the comment has, direct or not: they are deleted with it.</summary>
    public int ReplyCount { get; }

    public string AuthorName => _comment.AuthorName;

    public string CreatedText => Display.Moment(_comment.CreatedAt);

    public IReadOnlyList<CommentSpan> Spans { get; }

    public IReadOnlyList<TagChip> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    /// <summary>Who may delete it: its author and administrators. Everybody may reply to it and edit it.</summary>
    public bool CanDelete { get; }

    public string DeleteBlockedText => CanDelete ? "" : Strings.Comments_DeleteBlocked;

    /// <summary>The user may edit comments (a page that offers it says so; the member page has its own composer).</summary>
    public bool CanEdit { get; }

    /// <summary>The comment is being edited in a box next to it.</summary>
    [ObservableProperty]
    private bool _isBeingEdited;

    /// <summary>The comment waits for the answer to "Delete this comment?".</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>"Delete this comment and its 2 replies?"</summary>
    public string ConfirmText => ReplyCount switch
    {
        0 => Strings.Comments_ConfirmDelete,
        1 => Strings.Comments_ConfirmDeleteReply,
        _ => string.Format(Strings.Comments_ConfirmDeleteReplies, ReplyCount),
    };

    public bool IsEdited => _comment.Edits.Count > 0;

    /// <summary>The edits, the latest first.</summary>
    public IReadOnlyList<CommentEditRow> Edits { get; }

    /// <summary>"Edited by Ben, 15 Oct 2026, 09:12", with how many edits there were when there is more than one. Empty for a comment that was never edited.</summary>
    public string EditedText
    {
        get
        {
            if (_comment.Edits.Count == 0) return "";

            var last = _comment.Edits[^1];
            return _comment.Edits.Count == 1
                ? string.Format(Strings.Comments_EditedBy, last.EditorName, Display.Moment(last.EditedAt))
                : string.Format(Strings.Comments_EditedMany, last.EditorName, Display.Moment(last.EditedAt), _comment.Edits.Count);
        }
    }

    /// <summary>The history of the edits is open.</summary>
    [ObservableProperty]
    private bool _isHistoryOpen;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var tag in Tags) tag.Refresh();
        foreach (var edit in Edits) edit.Refresh();
    }
}
