using System.Globalization;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using Microsoft.Extensions.Time.Testing;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>How the star evaluations enter the statistics (pure calculator) and how the calendar page evaluates members.</summary>
public sealed class RatingTests : IDisposable
{
    // The events of Events(At(6, 1), …) are "e0601-Training", "e0602-Training", …
    private static string E(int day) => $"e06{day:00}-Training";

    private static RatingBook Book(double defaultStars = 2.5, params (string Member, int Day, double Stars)[] given) =>
        new(given.ToDictionary(g => (g.Member, E(g.Day)), g => g.Stars), defaultStars);

    private static AttendanceStatisticsDto Calculate(string pattern, RatingBook? book, string memberId = "a")
    {
        var events = Events(At(6, 1), Patterns((memberId, pattern)));
        return AttendanceCalculator.Calculate(Group([Member(memberId)]), events, Filter(), Now, book);
    }

    // --- The calculator ------------------------------------------------------------------------------------------

    [Fact]
    public void An_attended_event_nobody_evaluated_counts_the_default_and_a_missed_one_counts_nothing()
    {
        var member = Assert.Single(Calculate("AADA", Book()).Members);

        Assert.Equal(2.5, member.AverageStars);
        Assert.Equal((3, 0), (member.StarEvents, member.EvaluatedEvents)); // the declined one is not judged
    }

    [Fact]
    public void The_default_is_the_one_given_and_without_evaluations_at_all_it_is_2_5()
    {
        Assert.Equal(4, Assert.Single(Calculate("AAA", Book(4)).Members).AverageStars);
        Assert.Equal(2.5, Assert.Single(Calculate("AAA", null).Members).AverageStars);
    }

    [Fact]
    public void An_evaluated_event_counts_what_it_was_given_and_the_rest_the_default()
    {
        // 4 and 5 at two events, the third attended and not evaluated: (4 + 5 + 2.5) / 3.
        var member = Assert.Single(Calculate("AAA", Book(2.5, ("a", 1, 4), ("a", 2, 5))).Members);

        Assert.Equal(3.83, member.AverageStars);
        Assert.Equal((3, 2), (member.StarEvents, member.EvaluatedEvents));
    }

    [Fact]
    public void Zero_stars_is_an_evaluation_that_pulls_the_average_down_and_is_not_the_same_as_none()
    {
        var zero = Assert.Single(Calculate("AA", Book(2.5, ("a", 1, 0), ("a", 2, 0))).Members);
        var none = Assert.Single(Calculate("AA", Book()).Members);

        Assert.Equal(0, zero.AverageStars);
        Assert.Equal(2, zero.EvaluatedEvents);
        Assert.Equal(2.5, none.AverageStars);
        Assert.Equal(0, none.EvaluatedEvents);
    }

    [Fact]
    public void An_event_somebody_evaluated_counts_even_when_the_member_did_not_accept()
    {
        var member = Assert.Single(Calculate("DAD", Book(2.5, ("a", 1, 5))).Members);

        Assert.Equal((2, 1), (member.StarEvents, member.EvaluatedEvents)); // the evaluated decline and the accepted event
        Assert.Equal(3.75, member.AverageStars); // (5 + 2.5) / 2
    }

    [Fact]
    public void A_member_who_attended_nothing_and_was_not_evaluated_has_no_average()
    {
        var member = Assert.Single(Calculate("DDD", Book()).Members);

        Assert.Null(member.AverageStars);
        Assert.Equal(0, member.StarEvents);
    }

    [Fact]
    public void The_events_and_the_summary_average_the_members_of_each_event_and_all_of_them()
    {
        var events = Events(At(6, 1), Patterns(("a", "AA"), ("b", "AD")));
        var book = Book(2.5, ("a", 1, 4), ("b", 1, 2));

        var result = AttendanceCalculator.Calculate(Group([Member("a"), Member("b")]), events, Filter(), Now, book);

        Assert.Equal(3, result.Events.Single(e => e.Id == E(1)).AverageStars); // (4 + 2) / 2
        Assert.Equal(2.5, result.Events.Single(e => e.Id == E(2)).AverageStars); // only a came
        Assert.Equal(2.83, result.Summary.AverageStars); // (4 + 2 + 2.5) / 3: three counted events
    }

    // --- Who is suggested ---------------------------------------------------------------------------------------

    private static readonly DateOnly ExamDay = new(2026, 10, 17);
    private static readonly Belt White = new() { Id = 1, Name = "White" };
    private static readonly Belt Yellow = new() { Id = 2, Name = "Yellow", Prerequisites = [new() { BeltId = 2, RequiredBeltId = 1 }] };

    private static ExamSuggestionDto Suggest(double? average, double minimum)
    {
        var anna = new Member { Id = 1, GroupId = "g1", SpondId = "s1", Name = "Anna", BeltId = 1 };
        var stats = MemberStats("s1", 10, 9) with { MemberId = "s1", AverageStars = average };

        return Assert.Single(ExamSuggestionCalculator.Calculate(
            ExamDay, [White, Yellow], [(anna, new MemberInfo("s1", "Anna", []))],
            new Dictionary<string, MemberAttendanceDto> { ["s1"] = stats },
            new Dictionary<string, string>(), new Dictionary<int, DateOnly>(), new Dictionary<int, AttendanceMark>(), new HashSet<int>(), minimum));
    }

    [Theory]
    [InlineData(3.0, 3.0, SuggestionVerdict.Fit, CheckState.Met)] // at least 3: exactly 3 is enough
    [InlineData(3.4, 3.0, SuggestionVerdict.Fit, CheckState.Met)]
    [InlineData(2.99, 3.0, SuggestionVerdict.NotYet, CheckState.NotMet)]
    [InlineData(2.5, 3.0, SuggestionVerdict.NotYet, CheckState.NotMet)] // nobody evaluated: every event counts 2.5, which is not enough
    [InlineData(null, 3.0, SuggestionVerdict.NotYet, CheckState.NotMet)] // nothing to average
    [InlineData(1.0, 0.0, SuggestionVerdict.Fit, CheckState.Met)] // 0 = not asked
    [InlineData(null, 0.0, SuggestionVerdict.Fit, CheckState.Met)]
    public void A_member_needs_the_average_the_exams_ask_for(double? average, double minimum, SuggestionVerdict verdict, CheckState check)
    {
        var s = Suggest(average, minimum);

        Assert.Equal(verdict, s.Verdict);
        Assert.Equal(check, s.RatingCheck);
        Assert.Equal((average, minimum), (s.AverageStars, s.MinAverageStars));
    }

    [Fact]
    public void The_row_says_in_words_that_the_average_is_too_low()
    {
        var row = new SuggestionRow(Suggest(2.5, 3), new BeltOption(), new BeltOption(), new AttendanceFilter("g1", ExamDay, ExamDay));

        Assert.Equal(CheckState.NotMet, row.StarsState);
        Assert.Equal("2.5 (min. 3.0)", row.StarsText);
        Assert.Contains("Average evaluation 2.5 of 5, 3.0 needed.", row.ReasonText);
    }

    // --- The calendar page --------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Offset = new(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(2));

    private readonly FakePlannerApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly ShellViewModel _shell;
    private readonly CalendarViewModel _vm;

    public RatingTests()
    {
        var language = new LanguageService();
        language.Select("en");
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CalendarView, Permission.RatingsView, Permission.RatingsWrite);
        _shell = new ShellViewModel(_api, language);
        _vm = new CalendarViewModel(_shell, _api, _time);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<MemberDto> OpenEventAsync(string answerOf = "Anna", params RatingDto[] others)
        => await OpenEventAsync(true, answerOf, others);

    /// <summary>Opens the training of 18:00 to 19:00 (club time); the clock starts at 14:00 and is put to 20:00 for an event that is to have started.</summary>
    private async Task<MemberDto> OpenEventAsync(bool started, string answerOf = "Anna", params RatingDto[] others)
    {
        if (started) _time.SetUtcNow(new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero));

        _api.CalendarEvents.Add(new CalendarEventDto("e1", "g1", "Training", Offset.AddHours(18), Offset.AddHours(19), false, 2, 1, 0, 1));
        var member = _api.AddMember(answerOf);
        _api.Attendees["e1"] =
        [
            new CalendarAttendeeDto(member.Id, answerOf, AttendanceMark.Attended, [], others),
            new CalendarAttendeeDto(null, "Ben", AttendanceMark.Attended, []),
        ];
        await _vm.ActivateAsync();
        await _vm.OpenEventAsync("g1", "e1");
        return member;
    }

    [Fact]
    public async Task Nobody_evaluated_says_so_and_offers_the_buttons_from_0_to_5()
    {
        await OpenEventAsync();
        var anna = _vm.Attendees[0];

        Assert.True(_vm.RatingsOpen);
        Assert.True(anna.CanRate);
        Assert.False(anna.HasRatings);
        Assert.Equal("Not evaluated", anna.RatingSummary);
        Assert.Equal([0, 1, 2, 3, 4, 5], anna.Choices.Select(c => c.Stars));
        Assert.DoesNotContain(anna.Choices, c => c.IsSelected); // nothing chosen is not a 0
        Assert.False(anna.HasMyRating);
        Assert.False(_vm.Attendees[1].CanRate); // the planner does not know Ben yet
    }

    [Fact]
    public async Task Choosing_stars_evaluates_and_choosing_other_stars_replaces_it()
    {
        var member = await OpenEventAsync();

        await _vm.RateCommand.ExecuteAsync(_vm.Attendees[0].Choices[4]);
        Assert.Equal(4, _vm.Attendees[0].MyStars);
        Assert.True(_vm.Attendees[0].Choices[4].IsSelected);
        Assert.False(_vm.RateCommand.CanExecute(_vm.Attendees[0].Choices[4])); // nothing to change

        await _vm.RateCommand.ExecuteAsync(_vm.Attendees[0].Choices[2]);

        Assert.Equal([$"rate:e1:{member.Id}:4", $"rate:e1:{member.Id}:2"], _api.RatingCalls);
        var ratings = _vm.Attendees[0].Ratings;
        Assert.Equal(2, Assert.Single(ratings).Stars); // one evaluation per user
        Assert.True(ratings[0].IsMine);
        Assert.Equal("Average 2.0 of 5 from 1 evaluation", _vm.Attendees[0].RatingSummary);
    }

    [Fact]
    public async Task A_zero_is_an_evaluation_the_page_shows_apart_from_none()
    {
        await OpenEventAsync();

        await _vm.RateCommand.ExecuteAsync(_vm.Attendees[0].Choices[0]);

        var anna = _vm.Attendees[0];
        Assert.True(anna.HasMyRating);
        Assert.True(anna.Choices[0].IsSelectedZero);
        Assert.True(anna.Ratings[0].IsZero);
        Assert.Equal("☆☆☆☆☆", anna.Ratings[0].Glyphs);
        Assert.Equal("0/5", anna.Ratings[0].ValueText);
        Assert.NotEqual("Not evaluated", anna.RatingSummary);
    }

    [Fact]
    public async Task Taking_the_evaluation_back_is_only_offered_while_there_is_one()
    {
        await OpenEventAsync();
        var before = _vm.Attendees[0];
        Assert.False(_vm.RateCommand.CanExecute(before.ClearChoice));

        await _vm.RateCommand.ExecuteAsync(before.Choices[3]);
        Assert.True(_vm.RateCommand.CanExecute(_vm.Attendees[0].ClearChoice));
        await _vm.RateCommand.ExecuteAsync(_vm.Attendees[0].ClearChoice);

        Assert.False(_vm.Attendees[0].HasMyRating);
        Assert.False(_vm.Attendees[0].HasRatings);
        Assert.Contains(_api.RatingCalls, c => c.StartsWith("unrate:e1:"));
    }

    [Fact]
    public async Task The_evaluations_of_other_users_are_listed_and_stay_when_the_user_evaluates()
    {
        var other = new RatingDto(0, "e1", 99, "Ben", 5, Offset.AddHours(19));
        var member = await OpenEventAsync("Anna", other);

        var anna = _vm.Attendees[0];
        Assert.Equal(["Ben"], anna.Ratings.Select(r => r.UserText));
        Assert.False(anna.Ratings[0].IsMine);
        Assert.False(anna.HasMyRating);
        Assert.Equal("Average 5.0 of 5 from 1 evaluation", anna.RatingSummary);

        await _vm.RateCommand.ExecuteAsync(anna.Choices[3]);

        var ratings = _vm.Attendees[0].Ratings;
        Assert.Equal(["Ben", "anna (you)"], ratings.Select(r => r.UserText));
        Assert.Equal("Average 4.0 of 5 from 2 evaluations", _vm.Attendees[0].RatingSummary);
        Assert.Equal(member.Id, _vm.Attendees[0].MemberId);
    }

    [Fact]
    public async Task A_user_who_may_only_read_sees_the_evaluations_but_has_no_buttons()
    {
        _api.Account = TestAccounts.WithPermissions(1, "anna", Permission.CalendarView, Permission.RatingsView);
        var other = new RatingDto(1, "e1", 99, "Ben", 3, Offset.AddHours(19));

        await OpenEventAsync("Anna", other);

        Assert.True(_vm.ShowRatings);
        Assert.False(_vm.CanRate);
        Assert.True(_vm.Attendees[0].ShowRating);
        Assert.False(_vm.Attendees[0].CanRate);
    }

    [Fact]
    public async Task An_event_that_has_not_started_cannot_be_evaluated_yet()
    {
        await OpenEventAsync(started: false); // 14:00 in Germany: the training starts at 18:00

        Assert.False(_vm.RatingsOpen);
        Assert.True(_vm.ShowNotStarted);
        Assert.False(_vm.Attendees[0].CanRate);
        Assert.Empty(_api.RatingCalls);
    }
}
