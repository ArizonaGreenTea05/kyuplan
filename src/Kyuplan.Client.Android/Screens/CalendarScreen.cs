using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>
/// The calendar of the selected group as a list of days with their events, and one event in detail: who was invited, what they answered, and
/// the comments about them, with a box to write a new one (the phone is for a quick note during training).
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

    /// <summary>The comments about a member, and the box for a new one.</summary>
    private void Expanded(LinearLayout card, EventAttendeeRow attendee)
    {
        foreach (var comment in attendee.Comments)
        {
            var text = Ui.Text(string.Concat(comment.Spans.Select(s => s.Text)), TextStyle.Body);
            var line = Ui.Column(Ui.Text($"{comment.AuthorName} · {comment.CreatedText}", TextStyle.Caption), text);
            line.SetPadding(Ui.Dp(Math.Min(comment.Depth, CommentRow.MaxIndentLevel) * 12), 0, 0, 0);
            card.AddView(line, Ui.Params(top: 10));
        }

        if (!_vm.CanWriteComments) return;

        var input = Ui.Input(Strings.Calendar_CommentHint, attendee.Draft, text => attendee.Draft = text, multiline: true);
        card.AddView(input, Ui.Params(top: 12));
        card.AddView(
            Ui.Button(Strings.Calendar_Post, () =>
            {
                Ui.Hide(input);
                if (_vm.PostCommentCommand.CanExecute(attendee)) _vm.PostCommentCommand.Execute(attendee);
            }),
            Ui.Params(top: 8));
    }

    private static void Run(System.Windows.Input.ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }
}
