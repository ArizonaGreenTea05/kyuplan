using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>An event in the calendar with ready-to-show texts, which follow the language after <see cref="Refresh"/>.</summary>
public sealed partial class CalendarEventRow(CalendarEventDto calendarEvent) : ObservableObject
{
    public CalendarEventDto Event => calendarEvent;

    public string Id => calendarEvent.Id;

    public string GroupId => calendarEvent.GroupId;

    public string Name => calendarEvent.Name;

    public bool IsCancelled => calendarEvent.Cancelled;

    /// <summary>"18:00 – 19:30".</summary>
    public string TimeText => $"{Display.Time(calendarEvent.Start)} – {Display.Time(calendarEvent.End)}";

    /// <summary>"Sat, 26 Sep 2026 · 18:00 – 19:30".</summary>
    public string WhenText => $"{Display.EventDay(calendarEvent.Start)} · {TimeText}";

    /// <summary>"12 accepted · 3 declined · 5 no answer".</summary>
    public string CountsText => string.Format(Strings.Calendar_Counts, calendarEvent.Accepted, calendarEvent.Declined, calendarEvent.Unanswered);

    public string InvitedText => string.Format(Strings.Calendar_Invited, calendarEvent.Invited);

    [ObservableProperty]
    private bool _isSelected;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One day of the calendar and the events that start on it.</summary>
public sealed class CalendarDayRow(DateOnly date, bool isToday, bool isOutside, IReadOnlyList<CalendarEventRow> events) : ObservableObject
{
    public DateOnly Date => date;

    public int Number => date.Day;

    /// <summary>The day outside the month shown (the grid is filled up to whole weeks).</summary>
    public bool IsOutside => isOutside;

    public bool IsToday => isToday;

    public IReadOnlyList<CalendarEventRow> Events => events;

    public bool HasEvents => events.Count > 0;

    /// <summary>"Sat 26": for the week and the day.</summary>
    public string Label => date.ToString("ddd d", CultureInfo.CurrentCulture);

    /// <summary>"Sat, 26 Sep 2026".</summary>
    public string FullText => date.ToString(Strings.Format_EventDay, CultureInfo.CurrentCulture);

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var e in events) e.Refresh();
    }
}

/// <summary>A member invited to the event: their answer, what has been written about them for it (threads), and the comment being written.</summary>
public sealed partial class EventAttendeeRow : ObservableObject
{
    public EventAttendeeRow(CalendarAttendeeDto attendee, bool canReply = false, bool canEdit = false, Func<CommentDto, bool>? mayDelete = null)
    {
        Attendee = attendee;
        CanReply = canReply;
        Comments = Threads(attendee.Comments, canEdit, mayDelete ?? (_ => false));
    }

    public CalendarAttendeeDto Attendee { get; }

    public int? MemberId => Attendee.MemberId;

    public string Name => Attendee.Name;

    public AttendanceMark Answer => Attendee.Answer;

    public string AnswerText => Display.Invitation(Attendee.Answer);

    /// <summary>A member the planner does not know yet cannot be commented on: the sync with Spond brings them.</summary>
    public bool CanComment => Attendee.MemberId is not null;

    /// <summary>The user may write comments, so each comment offers "Reply".</summary>
    public bool CanReply { get; }

    /// <summary>The threads, the newest first, each comment followed by its replies, the oldest first.</summary>
    public IReadOnlyList<CommentRow> Comments { get; }

    public bool HasComments => Comments.Count > 0;

    /// <summary>What is being written, in the markup of <see cref="CommentFormat"/>.</summary>
    [ObservableProperty]
    private string _draft = "";

    /// <summary>The comment the draft replies to; <c>null</c> when it is a new comment.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplying), nameof(ReplyText), nameof(PostText), nameof(IsComposing), nameof(ComposingText))]
    private CommentRow? _replyingTo;

    /// <summary>The comment the draft edits; <c>null</c> when it is a new comment or a reply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(CanCompose), nameof(PostText), nameof(IsComposing), nameof(ComposingText))]
    private CommentRow? _editingRow;

    public bool IsReplying => ReplyingTo is not null;

    public bool IsEditing => EditingRow is not null;

    /// <summary>The box can be used: to write (a user who may write comments) or to edit a comment.</summary>
    public bool CanCompose => CanReply || IsEditing;

    /// <summary>A reply or an edit is under way, which has a line saying so and a button to give it up.</summary>
    public bool IsComposing => IsReplying || IsEditing;

    /// <summary>What the button that sends says.</summary>
    public string PostText => IsEditing ? Strings.Comments_SaveEdit : IsReplying ? Strings.Comments_PostReply : Strings.Calendar_Post;

    /// <summary>"Reply to Ben".</summary>
    public string ReplyText => ReplyingTo is { } parent ? string.Format(Strings.Comments_ComposerReply, parent.AuthorName) : "";

    /// <summary>"Reply to Ben" or "Edit comment".</summary>
    public string ComposingText => IsEditing ? Strings.Comments_ComposerEdit : ReplyText;

    partial void OnEditingRowChanged(CommentRow? oldValue, CommentRow? newValue)
    {
        if (oldValue is not null) oldValue.IsBeingEdited = false;
        if (newValue is not null) newValue.IsBeingEdited = true;
    }

    [RelayCommand]
    private void Reply(CommentRow? comment)
    {
        if (!CanReply || comment is null) return;

        if (IsEditing) Draft = ""; // the text was the edited comment's
        EditingRow = null;
        ReplyingTo = Comments.FirstOrDefault(c => c.Id == comment.Id);
    }

    [RelayCommand]
    private void Edit(CommentRow? comment)
    {
        if (comment is not { CanEdit: true } || Comments.FirstOrDefault(c => c.Id == comment.Id) is not { } row) return;

        ReplyingTo = null;
        EditingRow = row;
        Draft = row.Comment.Body;
    }

    /// <summary>Gives up the reply or the edit; the text of an edit goes with it.</summary>
    [RelayCommand]
    private void CancelReply()
    {
        if (IsEditing) Draft = "";
        EditingRow = null;
        ReplyingTo = null;
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var comment in Comments) comment.Refresh();
    }

    private static List<CommentRow> Threads(IReadOnlyList<CommentDto> comments, bool canEdit, Func<CommentDto, bool> mayDelete)
    {
        var ids = comments.Select(c => c.Id).ToHashSet();
        var replies = comments.Where(c => c.ParentId is { } p && ids.Contains(p)).ToLookup(c => c.ParentId!.Value);
        List<CommentRow> rows = [];

        int Descendants(int id) => replies[id].Sum(r => 1 + Descendants(r.Id));

        void Add(CommentDto comment, int depth)
        {
            rows.Add(new CommentRow(comment, depth, Descendants(comment.Id), mayDelete(comment), canEdit));
            foreach (var reply in replies[comment.Id].OrderBy(r => r.Id)) Add(reply, depth + 1);
        }

        foreach (var root in comments.Where(c => c.ParentId is not { } p || !ids.Contains(p)).OrderByDescending(c => c.Id)) Add(root, 0);
        return rows;
    }
}

/// <summary>One choice of the filter on the answers: everybody, or those with one answer, with how many there are.</summary>
public sealed partial class AnswerFilterOption(AttendanceMark? mark, int count) : ObservableObject
{
    /// <summary><c>null</c> = everybody.</summary>
    public AttendanceMark? Mark => mark;

    public int Count => count;

    /// <summary>"Accepted (5)".</summary>
    public string Text => $"{(mark is { } m ? Display.Invitation(m) : Strings.Calendar_FilterAll)} ({count})";

    [ObservableProperty]
    private bool _isSelected;

    public void Refresh() => OnPropertyChanged(nameof(Text));
}
