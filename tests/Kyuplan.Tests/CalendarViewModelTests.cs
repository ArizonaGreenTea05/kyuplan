using System.Globalization;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The calendar page (day, week, month, one event with the comments on its members) and the banner of an event in progress.</summary>
public sealed class CalendarViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Offset = new(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(2));

    private readonly FakePlannerApi _api = new();
    private readonly ShellViewModel _shell;
    private readonly CalendarViewModel _vm;
    private readonly EventBannerViewModel _banner;

    public CalendarViewModelTests()
    {
        var language = new LanguageService();
        language.Select("en");
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CalendarView, Permission.CommentsWrite);
        _shell = new ShellViewModel(_api, language);
        _vm = new CalendarViewModel(_shell, _api);
        _banner = new EventBannerViewModel(_shell, _api);
    }

    public void Dispose()
    {
        _banner.Dispose();
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static CalendarEventDto Event(string id, int day, int hour, string name = "Training", bool cancelled = false) =>
        new(id, "g1", name, Offset.AddDays(day - 13).AddHours(hour), Offset.AddDays(day - 13).AddHours(hour + 1), cancelled, 4, 2, 1, 1);

    [Theory]
    [InlineData(null, false, "")]
    [InlineData(new string[0], false, "")]
    [InlineData(new[] { "Tina Trainer" }, true, "Trainers: Tina Trainer")]
    [InlineData(new[] { "Ben", "Tina Trainer" }, true, "Trainers: Ben, Tina Trainer")]
    public void An_event_row_names_its_trainers_and_says_so_only_when_it_has_some(string[]? trainers, bool has, string text)
    {
        var row = new CalendarEventRow(Event("e1", 13, 18) with { Trainers = trainers });

        Assert.Equal(has, row.HasTrainers);
        Assert.Equal(text, row.TrainersText);
        Assert.Equal(has ? $"{row.WhenText} · {text}" : row.WhenText, row.TipText);
    }

    [Fact]
    public void Events_with_the_same_trainers_are_equal_whatever_list_holds_them()
    {
        var a = Event("e1", 13, 18) with { Trainers = ["Tina"] };

        Assert.Equal(a, Event("e1", 13, 18) with { Trainers = new List<string> { "Tina" } });
        Assert.Equal(Event("e1", 13, 18), Event("e1", 13, 18) with { Trainers = [] });
        Assert.NotEqual(a, Event("e1", 13, 18) with { Trainers = ["Ben"] });
        Assert.NotEqual(a, Event("e1", 13, 18));
        Assert.Equal(a.GetHashCode(), (Event("e1", 13, 18) with { Trainers = ["Tina"] }).GetHashCode());
    }

    [Fact]
    public void The_trainers_of_an_event_row_follow_the_language()
    {
        var row = new CalendarEventRow(Event("e1", 13, 18) with { Trainers = ["Tina"] });
        _shell.Language.Select("de");

        Assert.Equal("Trainer: Tina", row.TrainersText);
    }

    private async Task ShownAsync()
    {
        _api.CalendarEvents.AddRange([Event("e1", 13, 18), Event("e2", 14, 18, "Exam"), Event("e3", 20, 10)]);
        await _vm.ActivateAsync();
    }

    [Fact]
    public async Task The_week_is_shown_first_as_seven_days_with_their_events()
    {
        await ShownAsync();

        Assert.Equal("Calendar", _vm.Title);
        Assert.True(_vm.IsWeek);
        Assert.Equal(7, _vm.Days.Count);
        Assert.Equal(7, _vm.GridColumns);
        Assert.Equal(new DateOnly(2026, 10, 12), _vm.Days[0].Date);
        Assert.Equal(["e1", "e2"], _vm.Events.Select(e => e.Id));
        Assert.True(_vm.Days.Single(d => d.IsToday).Date == new DateOnly(2026, 10, 13));
        Assert.Equal("Training", _vm.Days[1].Events.Single().Name);
        Assert.Equal("18:00 – 19:00", _vm.Events[0].TimeText);
        Assert.Equal("2 accepted · 1 declined · 1 no answer", _vm.Events[0].CountsText);
    }

    [Fact]
    public async Task A_day_is_one_column_and_a_month_is_filled_up_to_whole_weeks()
    {
        await ShownAsync();

        await _vm.ShowViewCommand.ExecuteAsync(CalendarViews.Day);
        Assert.Single(_vm.Days);
        Assert.Equal(1, _vm.GridColumns);
        Assert.Equal(["e1"], _vm.Events.Select(e => e.Id));

        await _vm.ShowViewCommand.ExecuteAsync(CalendarViews.Month);
        Assert.Equal(0, _vm.Days.Count % 7);
        Assert.Equal(new DateOnly(2026, 9, 28), _vm.Days[0].Date); // 1 October 2026 is a Thursday: the grid starts on the Monday before
        Assert.True(_vm.Days[0].IsOutside);
        Assert.Equal(["e1", "e2", "e3"], _vm.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task Previous_next_and_today_move_by_the_size_of_the_view()
    {
        await ShownAsync();

        await _vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(new DateOnly(2026, 10, 20), _vm.Days[1].Date);
        Assert.Equal(["e3"], _vm.Events.Select(e => e.Id));
        Assert.True(_vm.TodayCommand.CanExecute(null));

        await _vm.TodayCommand.ExecuteAsync(null);
        Assert.False(_vm.TodayCommand.CanExecute(null)); // already there
        await _vm.PreviousCommand.ExecuteAsync(null);
        Assert.Empty(_vm.Events);
        Assert.True(_vm.IsEmpty);

        await _vm.ShowViewCommand.ExecuteAsync(CalendarViews.Month);
        await _vm.TodayCommand.ExecuteAsync(null);
        await _vm.NextCommand.ExecuteAsync(null);
        Assert.Equal("November 2026", _vm.PeriodText);
    }

    [Fact]
    public async Task Opening_an_event_shows_everybody_invited_and_what_was_written_for_the_event()
    {
        await ShownAsync();
        var member = _api.AddMember("Anna");
        _api.Attendees["e1"] =
        [
            new CalendarAttendeeDto(member.Id, "Anna", AttendanceMark.Attended, []),
            new CalendarAttendeeDto(null, "Ben", AttendanceMark.Declined, []),
        ];

        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);

        Assert.True(_vm.HasDetail);
        Assert.Equal("Training", _vm.Detail!.Name);
        Assert.Equal(["Anna", "Ben"], _vm.Attendees.Select(a => a.Name));
        Assert.Equal([true, false], _vm.Attendees.Select(a => a.CanComment));
        Assert.True(_vm.Events[0].IsSelected);

        _vm.CloseEventCommand.Execute(null);
        Assert.False(_vm.HasDetail);
        Assert.Empty(_vm.Attendees);
    }

    [Fact]
    public async Task A_comment_is_written_on_a_member_only_with_words_and_is_listed_afterwards()
    {
        await ShownAsync();
        var member = _api.AddMember("Anna");
        _api.Attendees["e1"] = [new CalendarAttendeeDto(member.Id, "Anna", AttendanceMark.Attended, [])];
        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);
        var anna = _vm.Attendees[0];

        Assert.False(_vm.PostCommentCommand.CanExecute(anna)); // nothing written yet
        anna.Draft = "   ";
        Assert.False(_vm.PostCommentCommand.CanExecute(anna));
        anna.Draft = "**Strong** stance";
        Assert.True(_vm.PostCommentCommand.CanExecute(anna));

        await _vm.PostCommentCommand.ExecuteAsync(anna);

        Assert.Equal([("e1", member.Id, "**Strong** stance")], _api.EventComments);
        var after = _vm.Attendees[0];
        Assert.Equal("", after.Draft);
        Assert.Equal(["**Strong** stance"], after.Comments.Select(c => c.Comment.Body));
        Assert.False(_vm.PostCommentCommand.CanExecute(after));
    }

    [Fact]
    public async Task The_attendees_can_be_filtered_by_their_answer_and_the_filter_counts_them()
    {
        await ShownAsync();
        _api.Attendees["e1"] =
        [
            new CalendarAttendeeDto(null, "Anna", AttendanceMark.Attended, []),
            new CalendarAttendeeDto(null, "Ben", AttendanceMark.Declined, []),
            new CalendarAttendeeDto(null, "Cem", AttendanceMark.Attended, []),
        ];
        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);

        Assert.Equal(3, _vm.VisibleAttendees.Count);
        Assert.Equal(["Everybody (3)", "Accepted (2)", "Declined (1)", "No answer yet (0)", "Waiting list (0)"], _vm.FilterOptions.Select(o => o.Text).Take(5));

        _vm.SetFilterCommand.Execute(_vm.FilterOptions[1]);
        Assert.Equal(["Anna", "Cem"], _vm.VisibleAttendees.Select(a => a.Name));
        Assert.True(_vm.FilterOptions[1].IsSelected);

        _vm.SetFilterCommand.Execute(_vm.FilterOptions[3]);
        Assert.Empty(_vm.VisibleAttendees);
        Assert.True(_vm.NoMatch);

        _vm.SetFilterCommand.Execute(_vm.FilterOptions[0]);
        Assert.Equal(3, _vm.VisibleAttendees.Count);
    }

    [Fact]
    public async Task A_reply_goes_to_the_comment_it_answers_and_is_shown_under_it()
    {
        await ShownAsync();
        var member = _api.AddMember("Anna");
        var tag = new CommentTagDto(1000, "t", null);
        var first = new CommentDto(50, member.Id, null, "First", 2, "ben", Offset, [tag], []);
        _api.Attendees["e1"] = [new CalendarAttendeeDto(member.Id, "Anna", AttendanceMark.Attended, [first])];
        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);
        var anna = _vm.Attendees[0];

        anna.ReplyCommand.Execute(anna.Comments[0]);
        Assert.True(anna.IsReplying);
        Assert.Equal("Reply to ben", anna.ReplyText);
        anna.Draft = "Thanks";
        await _vm.PostCommentCommand.ExecuteAsync(anna);

        var after = _vm.Attendees[0];
        Assert.False(after.IsReplying);
        Assert.Equal(["First", "Thanks"], after.Comments.Select(c => c.Comment.Body));
        Assert.Equal([0, 1], after.Comments.Select(c => c.Depth));
        Assert.Equal(50, after.Comments[1].Comment.ParentId);
    }

    [Fact]
    public async Task A_comment_is_edited_and_deleted_from_the_event_and_keeps_its_tags()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CalendarView, Permission.CommentsWrite, Permission.CommentsEdit, Permission.CommentsDelete);
        await ShownAsync();
        var member = _api.AddMember("Anna");
        var real = _api.AddTag("t");
        var tag = new CommentTagDto(real.Id, "t", null);
        var first = new CommentDto(50, member.Id, null, "First", _api.Account.Id, _api.Account.UserName, Offset, [tag], []);
        _api.Comments.Add(first);
        _api.Attendees["e1"] = [new CalendarAttendeeDto(member.Id, "Anna", AttendanceMark.Attended, [first])];
        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);
        var anna = _vm.Attendees[0];
        Assert.True(anna.Comments[0].CanEdit);
        Assert.True(anna.Comments[0].CanDelete);

        anna.EditCommand.Execute(anna.Comments[0]);
        Assert.True(anna.IsEditing);
        Assert.Equal("First", anna.Draft);
        Assert.False(_vm.PostCommentCommand.CanExecute(anna)); // nothing changed
        anna.Draft = "Second";
        Assert.True(_vm.PostCommentCommand.CanExecute(anna));
        await _vm.PostCommentCommand.ExecuteAsync(anna);

        Assert.Contains("update-comment:50", _api.CommentCalls);
        Assert.Equal([real.Id], _api.CommentUpdates[^1].TagIds);
        Assert.False(_vm.Attendees[0].IsEditing);
        Assert.Equal("", _vm.Attendees[0].Draft);

        var row = _vm.Attendees[0].Comments[0];
        _vm.AskDeleteCommand.Execute(row);
        Assert.True(row.IsConfirmingDelete);
        await _vm.ConfirmDeleteCommand.ExecuteAsync(row);
        Assert.Contains("delete-comment:50", _api.CommentCalls);
    }

    [Fact]
    public async Task Somebody_who_may_not_write_comments_cannot_and_a_member_the_planner_does_not_know_cannot_be_commented_on()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CalendarView);
        await ShownAsync();
        var member = _api.AddMember("Anna");
        _api.Attendees["e1"] = [new CalendarAttendeeDto(member.Id, "Anna", AttendanceMark.Attended, []), new CalendarAttendeeDto(null, "Ben", AttendanceMark.Other, [])];
        await _vm.OpenEventCommand.ExecuteAsync(_vm.Events[0]);

        _vm.Attendees[0].Draft = "text";
        _vm.Attendees[1].Draft = "text";

        Assert.False(_vm.CanWriteComments);
        Assert.False(_vm.PostCommentCommand.CanExecute(_vm.Attendees[0]));
        Assert.False(_vm.PostCommentCommand.CanExecute(_vm.Attendees[1]));
    }

    [Fact]
    public async Task An_event_can_be_opened_directly_before_the_calendar_was_read_and_the_calendar_is_there_when_it_is_closed()
    {
        _api.CalendarEvents.Add(Event("e1", 13, 18));
        _api.Attendees["e1"] = [];

        await _vm.OpenEventAsync("g1", "e1");
        _vm.CloseEventCommand.Execute(null);

        Assert.Equal(["e1"], _vm.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task A_user_without_the_permission_gets_nothing()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CommentsWrite);
        await ShownAsync();

        Assert.False(_vm.CanView);
        Assert.Empty(_vm.Events);
        Assert.DoesNotContain(_api.CalendarCalls, c => c.StartsWith("calendar:"));
    }

    // --- The banner ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_banner_shows_the_events_in_progress_and_nothing_when_there_are_none()
    {
        await _shell.InitializeAsync();
        await _banner.RefreshAsync();
        Assert.False(_banner.HasEvents);

        _api.CurrentEvents.Add(Event("e1", 13, 18, "Belt exam"));
        await _banner.RefreshAsync();

        Assert.True(_banner.HasEvents);
        Assert.Equal("Belt exam is in progress until 19:00", _banner.Events.Single().Text);

        _api.CurrentEvents.Clear();
        await _banner.RefreshAsync();
        Assert.False(_banner.HasEvents);
    }

    [Fact]
    public async Task Choosing_the_banner_asks_the_client_to_open_that_event()
    {
        _api.CurrentEvents.Add(Event("e1", 13, 18));
        await _shell.InitializeAsync();
        await _banner.RefreshAsync();
        CalendarEventDto? asked = null;
        _banner.OpenRequested += (_, e) => asked = e;

        _banner.OpenCommand.Execute(_banner.Events[0]);

        Assert.Equal("e1", asked?.Id);
    }

    [Fact]
    public async Task The_banner_follows_the_group_and_needs_the_permission()
    {
        _api.CurrentEvents.Add(Event("e1", 13, 18));
        await _shell.InitializeAsync();
        await _banner.RefreshAsync();
        Assert.True(_banner.HasEvents);

        _shell.SelectedGroupId = "g2"; // another group has nothing going on
        await Eventually(() => !_banner.HasEvents);

        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CommentsWrite);
        _shell.SelectedGroupId = "g1";
        await _shell.RefreshDataCommand.ExecuteAsync(null);
        await _banner.RefreshAsync();
        Assert.False(_banner.HasEvents);
    }

    [Fact]
    public async Task A_failing_request_leaves_the_banner_as_it_is()
    {
        _api.CurrentEvents.Add(Event("e1", 13, 18));
        await _shell.InitializeAsync();
        await _banner.RefreshAsync();

        _api.FailWith = new PlannerApiException("down");
        await _banner.RefreshAsync();

        Assert.True(_banner.HasEvents);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }
}
