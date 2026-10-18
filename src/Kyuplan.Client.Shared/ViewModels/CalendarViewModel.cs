using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The calendar page: the events of the selected group of today, of the week or of the month (a month and a week as a grid of days, as in
/// any calendar app), and one event in detail with everybody invited, their answers and a box to write a comment on each. Comments written
/// here carry the tag of the event (the API makes it when needed). Used by both clients; a client opens an event directly with
/// <see cref="OpenEventAsync(string, string)"/> (the banner of an event in progress does).
/// </summary>
public sealed partial class CalendarViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly TimeProvider _time;
    private CalendarDto? _calendar;
    private DateOnly? _date;

    public CalendarViewModel(ShellViewModel shell, IPlannerApi api, TimeProvider? time = null) : base(shell)
    {
        _api = api;
        _time = time ?? TimeProvider.System;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IsBusy)) return;

            NotifyNavigation();
            PostCommentCommand.NotifyCanExecuteChanged();
            RateCommand.NotifyCanExecuteChanged();
        };
    }

    public override string Title => Strings.Nav_Calendar;

    protected override string? ViewPermission => Permission.CalendarView;

    public bool CanWriteComments => Shell.Can(Permission.CommentsWrite);

    /// <summary>The user may evaluate members (the permission); whether this event allows it yet is <see cref="RatingsOpen"/>.</summary>
    public bool CanRate => Shell.Can(Permission.RatingsWrite);

    public bool CanViewRatings => Shell.Can(Permission.RatingsView);

    /// <summary>The open event has started, so its members can be evaluated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotStarted))]
    private bool _ratingsOpen;

    /// <summary>The user may evaluate, but the event has not started yet: a line says so instead of the buttons.</summary>
    public bool ShowNotStarted => CanRate && HasDetail && !RatingsOpen;

    /// <summary>The user sees evaluations (or may give them): the column is shown.</summary>
    public bool ShowRatings => CanRate || CanViewRatings;

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        PostCommentCommand.NotifyCanExecuteChanged();
        RateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRate));
        OnPropertyChanged(nameof(CanViewRatings));
        OnPropertyChanged(nameof(ShowRatings));
        OnPropertyChanged(nameof(ShowNotStarted));
    }

    /// <summary>Events change on Spond: read again whenever the calendar is shown (not while an event is open: its drafts stay).</summary>
    protected override bool ReloadsOnActivation => Detail is null;

    // --- The calendar ----------------------------------------------------------------------------------------

    /// <summary>One of <see cref="CalendarViews"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsWeek), nameof(IsMonth), nameof(UsesGrid), nameof(GridColumns))]
    private string _view = CalendarViews.Week;

    public bool IsDay => View == CalendarViews.Day;

    public bool IsWeek => View == CalendarViews.Week;

    public bool IsMonth => View == CalendarViews.Month;

    /// <summary>A week and a month are shown as a grid of days, a day as the list of its events.</summary>
    public bool UsesGrid => !IsDay;

    /// <summary>How many columns the days are laid out in: the seven days of the week, or the one day.</summary>
    public int GridColumns => UsesGrid ? 7 : 1;

    /// <summary>No group is selected, so there are no events to show.</summary>
    public bool NoGroup => string.IsNullOrEmpty(Shell.SelectedGroupId);

    /// <summary>The days shown; for a month filled up to whole weeks.</summary>
    [ObservableProperty]
    private IReadOnlyList<CalendarDayRow> _days = [];

    /// <summary>The events shown, by start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<CalendarEventRow> _events = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    public bool IsEmpty => HasLoaded && Events.Count == 0;

    /// <summary>"October 2026", "12 Oct 2026 – 18 Oct 2026" or "Mon, 12 Oct 2026": what the calendar shows.</summary>
    public string PeriodText => _calendar switch
    {
        null => "",
        { View: CalendarViews.Day } c => c.Date.ToString(Strings.Format_EventDay, CultureInfo.CurrentCulture),
        { View: CalendarViews.Month } c => c.Date.ToString(Strings.Format_MonthLong, CultureInfo.CurrentCulture),
        var c => Display.Period(c.From, c.To),
    };

    /// <summary>The short names of the days of the week, Monday first, for the head of a grid.</summary>
    public IReadOnlyList<string> WeekdayNames =>
    [
        .. Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7))),
    ];

    /// <summary>The range shown includes today: the "Today" button has nothing to do.</summary>
    public bool IsTodayShown => _calendar is { } c && c.Today >= c.From && c.Today <= c.To;

    protected override async Task ReloadAsync()
    {
        if (string.IsNullOrEmpty(Shell.SelectedGroupId))
        {
            Days = [];
            Events = [];
            _calendar = null;
            HasLoaded = false;
            OnPropertyChanged(nameof(PeriodText));
            return;
        }

        var groupId = Shell.SelectedGroupId;
        var view = View;
        var date = _date;
        await LoadAsync(async ct =>
        {
            var calendar = await _api.GetCalendarAsync(groupId, view, date, ct);
            Show(calendar);
            if (Detail is { } open) await ReadDetailAsync(open.GroupId, open.Id, ct);
        });
    }

    protected override void OnContextChanged()
    {
        CloseEvent();
        OnPropertyChanged(nameof(NoGroup));
    }

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    private void Show(CalendarDto calendar)
    {
        _calendar = calendar;
        _date = calendar.Date;

        var rows = calendar.Events.Select(e => new CalendarEventRow(e) { IsSelected = e.Id == Detail?.Id }).ToList();
        var first = calendar.View == CalendarViews.Month ? CalendarViews.Range(CalendarViews.Week, calendar.From).From : calendar.From;
        var last = calendar.View == CalendarViews.Month ? CalendarViews.Range(CalendarViews.Week, calendar.To).To : calendar.To;

        List<CalendarDayRow> days = [];
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var own = rows.Where(r => DateOnly.FromDateTime(r.Event.Start.Date) == day).ToList();
            days.Add(new CalendarDayRow(day, day == calendar.Today, day < calendar.From || day > calendar.To, own));
        }

        Days = days;
        Events = rows;
        HasLoaded = true;
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(IsTodayShown));
        NotifyNavigation();
    }

    [RelayCommand]
    private async Task ShowViewAsync(string view)
    {
        if (!CalendarViews.IsValid(view) || view == View) return;

        View = view;
        await ReloadAsync();
    }

    private bool CanMove() => _calendar is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanMove))]
    private Task PreviousAsync() => MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(CanMove))]
    private Task NextAsync() => MoveAsync(1);

    private Task MoveAsync(int direction)
    {
        if (_calendar is not { } c || IsBusy) return Task.CompletedTask;

        _date = View switch
        {
            CalendarViews.Day => c.Date.AddDays(direction),
            CalendarViews.Week => c.Date.AddDays(7 * direction),
            _ => new DateOnly(c.Date.Year, c.Date.Month, 1).AddMonths(direction),
        };
        return ReloadAsync();
    }

    /// <summary>Today is what the calendar shows already, in the view it is in: the day itself, or the week or month it lies in.</summary>
    private bool CanGoToday() => _calendar is not null && !IsBusy && !(_calendar.Date == _calendar.Today
        || (View != CalendarViews.Day && IsTodayShown && _calendar.View == View));

    [RelayCommand(CanExecute = nameof(CanGoToday))]
    private Task TodayAsync()
    {
        _date = null;
        return ReloadAsync();
    }

    private void NotifyNavigation()
    {
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        TodayCommand.NotifyCanExecuteChanged();
    }

    // --- One event -------------------------------------------------------------------------------------------

    /// <summary>The event shown in detail; <c>null</c> while the calendar is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private CalendarEventRow? _detail;

    public bool HasDetail => Detail is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttendees))]
    private IReadOnlyList<EventAttendeeRow> _attendees = [];

    public bool HasAttendees => Attendees.Count > 0;

    /// <summary>The choices of the filter on the answers: everybody first, then each answer with how many gave it.</summary>
    [ObservableProperty]
    private IReadOnlyList<AnswerFilterOption> _filterOptions = [];

    private AttendanceMark? _answerFilter;

    /// <summary>The attendees with the answer chosen in the filter (everybody when none is).</summary>
    public IReadOnlyList<EventAttendeeRow> VisibleAttendees => [.. Attendees.Where(a => _answerFilter is not { } mark || a.Answer == mark)];

    /// <summary>People are invited, but none has the chosen answer.</summary>
    public bool NoMatch => HasAttendees && VisibleAttendees.Count == 0;

    [RelayCommand]
    private void SetFilter(AnswerFilterOption option)
    {
        _answerFilter = option.Mark;
        BuildFilter();
    }

    private void BuildFilter()
    {
        FilterOptions =
        [
            new AnswerFilterOption(null, Attendees.Count) { IsSelected = _answerFilter is null },
            .. new[] { AttendanceMark.Attended, AttendanceMark.Declined, AttendanceMark.Unanswered, AttendanceMark.Other }
                .Select(m => new AnswerFilterOption(m, Attendees.Count(a => a.Answer == m)) { IsSelected = _answerFilter == m }),
        ];
        OnPropertyChanged(nameof(VisibleAttendees));
        OnPropertyChanged(nameof(NoMatch));
    }

    [RelayCommand]
    private Task OpenEventAsync(CalendarEventRow row) => OpenEventAsync(row.GroupId, row.Id);

    /// <summary>Shows an event in detail: reads it and everybody invited to it.</summary>
    public async Task OpenEventAsync(string groupId, string eventId)
    {
        await Shell.InitializeAsync();
        if (!CanView) return;

        await LoadAsync(ct => ReadDetailAsync(groupId, eventId, ct));

        // Opened before the calendar itself was read (from the banner): that read was superseded by this one, so it is made up for now.
        if (!HasLoaded && !HasError && !string.IsNullOrEmpty(Shell.SelectedGroupId)) await ReloadAsync();
    }

    private async Task ReadDetailAsync(string groupId, string eventId, CancellationToken cancellationToken)
    {
        var detail = await _api.GetCalendarEventAsync(groupId, eventId, cancellationToken);

        // What is being written stays while the event is read again.
        var drafts = Attendees.Where(a => a.Draft.Length > 0 && a.MemberId is not null).ToDictionary(a => a.MemberId!.Value, a => a.Draft);
        var replies = Attendees.Where(a => a.ReplyingTo is not null && a.MemberId is not null).ToDictionary(a => a.MemberId!.Value, a => a.ReplyingTo!.Id);
        var edits = Attendees.Where(a => a.EditingRow is not null && a.MemberId is not null).ToDictionary(a => a.MemberId!.Value, a => a.EditingRow!.Id);
        var user = Shell.CurrentUser;
        bool MayDelete(CommentDto c) => user is not null && (Shell.Can(Permission.CommentsDeleteAny) || (user.Id == c.AuthorId && Shell.Can(Permission.CommentsDelete)));
        Detail = new CalendarEventRow(detail.Event);
        RatingsOpen = detail.Event.Start <= _time.GetUtcNow();
        Attendees = [.. detail.Attendees.Select(a =>
        {
            var known = a.MemberId is not null;
            var row = new EventAttendeeRow(a, CanWriteComments && known, CanEditComments && known, MayDelete, CanRate && known && RatingsOpen, user?.Id);
            if (a.MemberId is { } member)
            {
                if (replies.TryGetValue(member, out var parent)) row.ReplyCommand.Execute(row.Comments.FirstOrDefault(c => c.Id == parent));
                if (edits.TryGetValue(member, out var editing)) row.EditCommand.Execute(row.Comments.FirstOrDefault(c => c.Id == editing));
                row.Draft = drafts.GetValueOrDefault(member, row.Draft);
            }

            return row;
        })];
        BuildFilter();
        foreach (var attendee in Attendees)
        {
            attendee.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EventAttendeeRow.Draft) or nameof(EventAttendeeRow.EditingRow)) PostCommentCommand.NotifyCanExecuteChanged();
            };
        }

        foreach (var row in Events) row.IsSelected = row.Id == detail.Event.Id;
    }

    /// <summary>Back to the calendar.</summary>
    [RelayCommand]
    private void CloseEvent()
    {
        Detail = null;
        RatingsOpen = false;
        Attendees = [];
        _answerFilter = null;
        BuildFilter();
        foreach (var row in Events) row.IsSelected = false;
    }

    public bool CanEditComments => Shell.Can(Permission.CommentsEdit);

    private bool CanPostComment(EventAttendeeRow? attendee)
    {
        if (attendee is not { CanComment: true } || Detail is null || IsBusy) return false;

        var body = CommentFormat.Clean(attendee.Draft);
        if (CommentRules.CheckBody(body) is not null) return false;

        // an edit needs the permission and a change, a new comment or reply the permission to write
        return attendee.EditingRow is { } editing ? CanEditComments && body != editing.Comment.Body : CanWriteComments;
    }

    [RelayCommand(CanExecute = nameof(CanPostComment))]
    private async Task PostCommentAsync(EventAttendeeRow attendee)
    {
        if (!CanPostComment(attendee) || Detail is not { } open || attendee.MemberId is not { } memberId) return; // Execute ignores CanExecute

        var body = CommentFormat.Clean(attendee.Draft);
        var editing = attendee.EditingRow;
        var parentId = attendee.ReplyingTo?.Id;
        await LoadAsync(async ct =>
        {
            if (editing is null)
                await _api.CreateEventCommentAsync(open.GroupId, open.Id, memberId, new CalendarCommentInputDto(body, parentId), ct);
            else
                await _api.UpdateCommentAsync(editing.Id, new CommentUpdateDto(body, [.. editing.Comment.Tags.Select(t => t.Id)]), ct);

            attendee.Draft = "";
            attendee.CancelReplyCommand.Execute(null); // written: a failed read below must not leave the text to be sent a second time
            await ReadDetailAsync(open.GroupId, open.Id, ct);
        });
    }

    // --- Evaluating --------------------------------------------------------------------------------------------

    /// <summary>Only a change is worth a request: the stars the user has already given, or taking back what is not there, is not.</summary>
    private bool CanChoose(StarChoice? choice) =>
        choice is { Row.CanRate: true, Row.MemberId: not null } && Detail is not null && !IsBusy
        && (choice.Stars is { } stars ? choice.Row.MyStars != stars : choice.Row.HasMyRating);

    /// <summary>Gives the member the stars of the button, or takes the user's evaluation back (the "Take back" button).</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task RateAsync(StarChoice choice)
    {
        if (!CanChoose(choice) || Detail is not { } open || choice.Row.MemberId is not { } memberId) return; // Execute ignores CanExecute

        await LoadAsync(async ct =>
        {
            if (choice.Stars is { } stars)
                await _api.RateMemberAsync(open.GroupId, open.Id, memberId, stars, ct);
            else
                await _api.UnrateMemberAsync(open.GroupId, open.Id, memberId, ct);

            await ReadDetailAsync(open.GroupId, open.Id, ct);
        });
    }

    // --- Deleting --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void AskDelete(CommentRow comment)
    {
        if (!comment.CanDelete || IsBusy) return;

        ErrorMessage = null;
        foreach (var other in Attendees.SelectMany(a => a.Comments)) other.IsConfirmingDelete = ReferenceEquals(other, comment);
    }

    [RelayCommand]
    private void CancelDelete(CommentRow comment) => comment.IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync(CommentRow comment)
    {
        if (!comment.CanDelete || !comment.IsConfirmingDelete || Detail is not { } open) return;

        comment.IsConfirmingDelete = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteCommentAsync(comment.Id, ct);
            await ReadDetailAsync(open.GroupId, open.Id, ct);
        });
    }

    // --- Language --------------------------------------------------------------------------------------------

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var day in Days) day.Refresh();
        Detail?.Refresh();
        foreach (var attendee in Attendees) attendee.Refresh();
        foreach (var option in FilterOptions) option.Refresh();
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(WeekdayNames));
    }
}
