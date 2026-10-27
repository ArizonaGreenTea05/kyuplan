using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>
/// The calendar of the selected group as a list of days with their events, and one event in detail: who was invited, what they answered, and
/// the evaluations and comments about them (reply, edit, delete), with the stars to give and a box to write a new comment (the phone is for a
/// quick note during training).
/// </summary>
public sealed class CalendarScreen : ScrollScreen
{
    private readonly CalendarViewModel _vm;
    private readonly HashSet<string> _expanded = [];

    public CalendarScreen(Ui ui, CalendarViewModel vm) : base(ui)
    {
        _vm = vm;
        Observe(vm);
        Observe(vm.Shell, nameof(ShellViewModel.SelectedGroupId), nameof(ShellViewModel.Groups));
        Observe(vm.Shell.Language);
    }

    public override bool HandleBack()
    {
        if (!_vm.HasDetail) return false;

        _expanded.Clear();
        ScrollToTop();
        _vm.CloseEventCommand.Execute(null);
        return true;
    }

    protected override void Build(LinearLayout content)
    {
        if (_vm.IsBusy) content.AddView(Ui.Busy());
        if (_vm.HasError) Ui.Add(content, Ui.Notice(_vm.ErrorMessage!, Tone.Danger), top: 8);

        if (_vm.HasDetail) BuildDetail(content);
        else BuildList(content);
    }

    // --- The days --------------------------------------------------------------------------------------------

    private void BuildList(LinearLayout content)
    {
        if (_vm.NoGroup)
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_NoGroup, muted: true), top: 8);
            return;
        }

        var views = new (string Label, string View)[]
        {
            (Strings.Calendar_Day, CalendarViews.Day), (Strings.Calendar_Week, CalendarViews.Week), (Strings.Calendar_Month, CalendarViews.Month),
        };
        Ui.Add(content, Ui.ChipRow([.. views.Select(v => (View)Ui.Chip(v.Label, _vm.View == v.View, () => _vm.ShowViewCommand.Execute(v.View)))]), top: 4);

        var period = Ui.Text(_vm.PeriodText, TextStyle.Heading);
        period.Gravity = GravityFlags.Center;
        var move = Ui.Row(
            Ui.IconButton(Resource.Drawable.ic_back, Strings.Calendar_Previous, () => Run(_vm.PreviousCommand)),
            period,
            Ui.IconButton(Resource.Drawable.ic_forward, Strings.Calendar_Next, () => Run(_vm.NextCommand)));
        move.GetChildAt(1)!.LayoutParameters = Ui.Params(weight: 1);
        Ui.Add(content, move);
        if (!_vm.IsTodayShown) Ui.Add(content, Ui.Chip(Strings.Calendar_Today, false, () => Run(_vm.TodayCommand)), bottom: 12);

        if (_vm.IsEmpty)
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_NoEvents, muted: true));
            return;
        }

        foreach (var day in _vm.Events.GroupBy(e => e.Event.Start.Date))
        {
            Ui.Add(content, Ui.Text(Kyuplan.Client.ViewModels.Display.EventDay(day.First().Event.Start), TextStyle.Heading), top: 8, bottom: 8);
            foreach (var row in day) Ui.Add(content, EventCard(row), bottom: 8);
        }
    }

    private View EventCard(CalendarEventRow row)
    {
        var title = Ui.Text(row.Name, TextStyle.Heading);
        if (row.IsCancelled) title.PaintFlags |= Android.Graphics.PaintFlags.StrikeThruText;

        var card = Ui.Card(title, Ui.Text(row.TimeText, TextStyle.Body));
        if (row.IsCancelled) card.AddView(Ui.Badge(Strings.Calendar_Cancelled, Tone.Danger), Ui.Params(wrap: true, top: 4));
        if (row.HasTrainers) card.AddView(Ui.Text(row.TrainersText, TextStyle.Caption));
        card.AddView(Ui.Text(row.CountsText, TextStyle.Caption), Ui.Params(top: 4));
        card.Clickable = true;
        card.Foreground = Ui.Ripple(null);
        card.Click += (_, _) =>
        {
            ScrollToTop();
            Run(_vm.OpenEventCommand, row);
        };
        return card;
    }

    // --- One event -------------------------------------------------------------------------------------------

    private void BuildDetail(LinearLayout content)
    {
        var detail = _vm.Detail!;
        var back = Ui.Row(Ui.IconButton(Resource.Drawable.ic_back, Strings.Calendar_Back, () => HandleBack()), Ui.Text(Strings.Calendar_Back, TextStyle.Caption));
        Ui.Add(content, back);

        var header = Ui.Card(Ui.Text(detail.Name, TextStyle.Title), Ui.Text(detail.WhenText, TextStyle.Body));
        if (detail.HasTrainers) header.AddView(Ui.Text(detail.TrainersText, TextStyle.Caption), Ui.Params(top: 4));
        header.AddView(Ui.Text($"{detail.CountsText} · {detail.InvitedText}", TextStyle.Caption), Ui.Params(top: 4));
        Ui.Add(content, header, bottom: 12);

        if (_vm.HasAttendees)
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_Attendees, TextStyle.Heading), bottom: 8);
            if (_vm.FilterOptions.Count > 0)
            {
                Ui.Add(content, Ui.ChipRow([.. _vm.FilterOptions.Select(o => (View)Ui.Chip(o.Text, o.IsSelected, () => _vm.SetFilterCommand.Execute(o)))]));
            }

            if (_vm.NoMatch) Ui.Add(content, Ui.Text(Strings.Calendar_NoMatch, muted: true));
            foreach (var attendee in _vm.VisibleAttendees) Ui.Add(content, AttendeeCard(attendee), bottom: 8);
        }
        else
        {
            Ui.Add(content, Ui.Text(Strings.Calendar_NoAttendees, muted: true));
        }
    }

    private static string KeyOf(EventAttendeeRow attendee) => attendee.MemberId?.ToString() ?? "name:" + attendee.Name;

    private View AttendeeCard(EventAttendeeRow attendee)
    {
        var tone = attendee.Answer switch
        {
            AttendanceMark.Attended => Tone.Success,
            AttendanceMark.Declined => Tone.Danger,
            AttendanceMark.Other => Tone.Warning,
            _ => Tone.Neutral,
        };
        var key = KeyOf(attendee);
        var open = _expanded.Contains(key);

        var title = Ui.Row();
        title.AddView(Ui.Text(attendee.Name, TextStyle.Body), Ui.Params(weight: 1));
        if (attendee.HasComments) title.AddView(Ui.Text(attendee.Comments.Count.ToString(), TextStyle.Caption), Ui.Params(wrap: true, right: 8));
        title.AddView(Ui.Badge(attendee.AnswerText, tone), Ui.Params(wrap: true));
        title.SetMinimumHeight(Ui.Dp(32));

        var card = Ui.Card(title);
        if (attendee.CanComment)
        {
            title.Clickable = true;
            title.Click += (_, _) =>
            {
                if (!_expanded.Remove(key)) _expanded.Add(key);
                Invalidate();
            };
        }

        if (open) Expanded(card, attendee);
        return card;
    }

    /// <summary>The evaluations of a member, the stars the user can give, the comments about them and the box to write one.</summary>
    private void Expanded(LinearLayout card, EventAttendeeRow attendee)
    {
        Ratings(card, attendee);

        foreach (var comment in attendee.Comments) card.AddView(CommentLine(attendee, comment), Ui.Params(top: 10));

        if (!attendee.CanCompose) return;

        if (attendee.IsComposing)
        {
            var composing = Ui.Row(
                Ui.Text(attendee.ComposingText, TextStyle.Caption),
                Ui.Chip(Strings.Comments_Cancel, false, () => Act(attendee.CancelReplyCommand)));
            composing.GetChildAt(0)!.LayoutParameters = Ui.Params(weight: 1);
            card.AddView(composing, Ui.Params(top: 12));
        }

        var input = Ui.Input(string.Format(Strings.Calendar_CommentHint, attendee.Name), attendee.Draft, text => attendee.Draft = text, multiline: true);
        card.AddView(input, Ui.Params(top: attendee.IsComposing ? 4 : 12));
        card.AddView(
            Ui.Button(attendee.PostText, () =>
            {
                Ui.Hide(input);
                Run(_vm.PostCommentCommand, attendee);
            }),
            Ui.Params(top: 8));
    }

    private View CommentLine(EventAttendeeRow attendee, CommentRow comment)
    {
        var line = Ui.Column(
            Ui.Text($"{comment.AuthorName} · {comment.CreatedText}", TextStyle.Caption),
            Ui.Text(string.Concat(comment.Spans.Select(s => s.Text)), TextStyle.Body));
        line.SetPadding(Ui.Dp(Math.Min(comment.Depth, CommentRow.MaxIndentLevel) * 12), 0, 0, 0);
        if (comment.IsEdited) line.AddView(Ui.Text(comment.EditedText, TextStyle.Caption));

        List<View> actions = [];
        if (attendee.CanReply) actions.Add(Ui.Chip(Strings.Comments_Reply, false, () => Act(attendee.ReplyCommand, comment)));
        if (comment.CanEdit && !comment.IsBeingEdited) actions.Add(Ui.Chip(Strings.Comments_Edit, false, () => Act(attendee.EditCommand, comment)));
        if (comment.CanDelete) actions.Add(Ui.Chip(Strings.Comments_Delete, false, () => Act(_vm.AskDeleteCommand, comment)));
        if (actions.Count > 0) line.AddView(Ui.ChipRow([.. actions]), Ui.Params(top: 4));

        if (comment.IsConfirmingDelete)
        {
            var answer = Ui.ChipRow(
                Ui.Chip(Strings.Comments_ConfirmYes, true, () => Act(_vm.ConfirmDeleteCommand, comment)),
                Ui.Chip(Strings.Comments_Cancel, false, () => Act(_vm.CancelDeleteCommand, comment)));
            line.AddView(Ui.Notice(comment.ConfirmText, Tone.Danger, answer), Ui.Params(top: 6));
        }

        return line;
    }

    /// <summary>What the users gave the member, and (while the event has started) the buttons to give or take back one's own.</summary>
    private void Ratings(LinearLayout card, EventAttendeeRow attendee)
    {
        if (!_vm.ShowRatings) return;

        if (_vm.ShowNotStarted) card.AddView(Ui.Text(Strings.Rating_NotStarted, TextStyle.Caption), Ui.Params(top: 10));

        if (attendee.HasRatings)
        {
            foreach (var rating in attendee.Ratings)
            {
                card.AddView(Ui.Text($"{rating.Glyphs} {rating.ValueText} · {rating.UserText}", TextStyle.Body), Ui.Params(top: 6));
            }

            card.AddView(Ui.Text(attendee.RatingSummary, TextStyle.Caption), Ui.Params(top: 2));
        }
        else if (attendee.ShowRating || attendee.MayViewRatings)
        {
            card.AddView(Ui.Text(Strings.Rating_None, TextStyle.Caption), Ui.Params(top: 10));
        }

        if (!attendee.CanRate) return;

        card.AddView(Ui.Text(Strings.Rating_YourChoice, TextStyle.Caption), Ui.Params(top: 10));
        List<View> choices = [.. attendee.Choices.Select(c =>
        {
            // The same row of stars as in the other clients: filled up to the user's evaluation, "⊘" for the poor 0.
            var chip = Ui.Chip(c.Glyph, c.IsSelected, () => Run(_vm.RateCommand, c));
            chip.ContentDescription = c.Tip;
            return chip;
        })];
        if (attendee.HasMyRating) choices.Add(Ui.Chip(attendee.ClearChoice.Text, false, () => Run(_vm.RateCommand, attendee.ClearChoice)));
        card.AddView(Ui.ChipRow([.. choices]), Ui.Params(top: 4));
    }

    /// <summary>Runs a command that changes the rows only (the rows do not tell the screen), so the screen draws them again.</summary>
    private void Act(System.Windows.Input.ICommand command, object? parameter = null)
    {
        Run(command, parameter);
        Invalidate();
    }
}
