using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

public class AttendanceCalculatorTests
{
    private static MemberAttendanceDto Single(string pattern, AttendanceFilter? filter = null)
    {
        var events = Events(At(6, 1), Patterns(("a", pattern)));
        var result = AttendanceCalculator.Calculate(Group([Member("a")]), events, filter ?? Filter(), Now);
        return Assert.Single(result.Members);
    }

    [Fact]
    public void Rate_is_based_on_the_events_a_member_was_invited_to_not_on_all_events()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAAD--"), ("b", "AAAAAA")));

        var result = AttendanceCalculator.Calculate(Group([Member("a"), Member("b")]), events, Filter(), Now);

        var a = result.Members.Single(m => m.MemberId == "a");
        Assert.Equal(4, a.Invited);
        Assert.Equal(3, a.Attended);
        Assert.Equal(75, a.AttendanceRate);
        Assert.Equal(QualificationStatus.Qualified, a.Status);
        Assert.Equal(6, result.Members.Single(m => m.MemberId == "b").Invited);
    }

    [Theory]
    [InlineData(66, QualificationStatus.Qualified)] // 2 of 3 = 66.7 %
    [InlineData(67, QualificationStatus.BelowThreshold)]
    public void Threshold_is_compared_exactly_not_on_the_rounded_rate(int threshold, QualificationStatus expected)
    {
        var member = Single("AAD", Filter(minPercent: threshold));

        Assert.Equal(66.7, member.AttendanceRate);
        Assert.Equal(expected, member.Status);
    }

    [Fact]
    public void Three_of_four_meets_a_threshold_of_exactly_75_percent()
    {
        Assert.Equal(QualificationStatus.Qualified, Single("AAAD", Filter(minPercent: 75)).Status);
    }

    [Fact]
    public void Members_with_too_few_invitations_get_no_verdict_even_with_a_perfect_rate()
    {
        var member = Single("AA", Filter(minInvited: 3));

        Assert.Equal(100, member.AttendanceRate);
        Assert.Equal(QualificationStatus.InsufficientData, member.Status);
    }

    [Fact]
    public void Members_without_any_invitation_in_the_window_are_left_out()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAA"), ("b", "---")));

        var result = AttendanceCalculator.Calculate(Group([Member("a"), Member("b")]), events, Filter(), Now);

        Assert.Equal(["a"], result.Members.Select(m => m.MemberId));
    }

    [Fact]
    public void Cancelled_and_not_yet_finished_events_do_not_count()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAAA")));
        events[1] = events[1] with { Cancelled = true };
        // Ends at 20:00 on 30 June, i.e. after "now" (12:00).
        events.Add(new EventInfo("later", "Training", At(6, 30, 18), At(6, 30, 20), false, new Dictionary<string, Rsvp> { ["a"] = Rsvp.Accepted }));

        var result = AttendanceCalculator.Calculate(Group([Member("a")]), events, Filter(), Now);

        Assert.Equal(3, result.Summary.EventCount);
        Assert.Equal(3, result.Members.Single().Invited);
    }

    [Fact]
    public void Window_includes_both_boundary_days_and_nothing_outside()
    {
        var events = Events(At(6, 9), Patterns(("a", "AAAAA"))); // 9th .. 13th

        var result = AttendanceCalculator.Calculate(
            Group([Member("a")]), events, Filter(fromMonth: 6, fromDay: 10, toMonth: 6, toDay: 12), Now);

        Assert.Equal(3, result.Summary.EventCount);
    }

    [Fact]
    public void Event_belongs_to_the_day_it_starts_in_club_time()
    {
        // 23:30 local on 12 June: must count for the 12th, whatever the UTC date is.
        var lateEvent = new EventInfo("late", "Training", At(6, 12, 23).AddMinutes(30), At(6, 13, 1), false,
            new Dictionary<string, Rsvp> { ["a"] = Rsvp.Accepted });

        var onTheDay = AttendanceCalculator.Calculate(Group([Member("a")]), [lateEvent], Filter(6, 12, 6, 12), Now);
        var nextDay = AttendanceCalculator.Calculate(Group([Member("a")]), [lateEvent], Filter(6, 13, 6, 13), Now);

        Assert.Equal(1, onTheDay.Summary.EventCount);
        Assert.Equal(0, nextDay.Summary.EventCount);
    }

    [Fact]
    public void Name_filters_match_any_term_case_insensitively()
    {
        var all = new List<EventInfo>();
        all.AddRange(Events(At(6, 1), Patterns(("a", "A")), "Training Erwachsene"));
        all.AddRange(Events(At(6, 2), Patterns(("a", "A")), "Gürtelprüfung"));
        all.AddRange(Events(At(6, 3), Patterns(("a", "A")), "Sommerfest"));

        int Count(string? contains, string? excludes) =>
            AttendanceCalculator.Calculate(Group([Member("a")]), all, Filter(contains: contains, excludes: excludes), Now).Summary.EventCount;

        Assert.Equal(3, Count(null, null));
        Assert.Equal(1, Count("TRAINING", null));
        Assert.Equal(2, Count("training; prüfung", null));
        Assert.Equal(1, Count(null, "prüfung, fest"));
        Assert.Equal(0, Count("training", "erwachsene"));
    }

    [Fact]
    public void Subgroup_filter_reports_only_its_members_and_counts_events_over_them()
    {
        var events = Events(At(6, 1), Patterns(("kid", "AAA"), ("adult", "AAA")));
        var group = Group([Member("kid", "sg-kids"), Member("adult", "sg-adults")],
            new SubGroupInfo("sg-kids", "Kinder"), new SubGroupInfo("sg-adults", "Erwachsene"));

        var result = AttendanceCalculator.Calculate(group, events, Filter(subGroupIds: ["sg-kids"]), Now);

        Assert.Equal(["kid"], result.Members.Select(m => m.MemberId));
        Assert.Equal(["Kinder"], result.Members.Single().SubGroups);
        Assert.All(result.Events, e => Assert.Equal(1, e.Invited));
    }

    [Fact]
    public void Several_subgroups_report_everyone_in_any_of_them_once()
    {
        var events = Events(At(6, 1), Patterns(("kid", "AAA"), ("teen", "AAA"), ("adult", "AAA"), ("both", "AAA"), ("none", "AAA")));
        var group = Group(
            [Member("kid", "sg-kids"), Member("teen", "sg-youth"), Member("adult", "sg-adults"), Member("both", "sg-kids", "sg-youth"), Member("none")],
            new SubGroupInfo("sg-kids", "Kinder"), new SubGroupInfo("sg-youth", "Jugend"), new SubGroupInfo("sg-adults", "Erwachsene"));

        var result = AttendanceCalculator.Calculate(group, events, Filter(subGroupIds: ["sg-kids", "sg-youth"]), Now);

        Assert.Equal(["both", "kid", "teen"], result.Members.Select(m => m.MemberId).Order());
        Assert.All(result.Events, e => Assert.Equal(3, e.Invited));
    }

    [Fact]
    public void No_subgroup_ticked_means_everyone()
    {
        var events = Events(At(6, 1), Patterns(("kid", "AAA"), ("none", "AAA")));
        var group = Group([Member("kid", "sg-kids"), Member("none")], new SubGroupInfo("sg-kids", "Kinder"));

        Assert.Equal(2, AttendanceCalculator.Calculate(group, events, Filter(subGroupIds: null), Now).Members.Count);
        Assert.Equal(2, AttendanceCalculator.Calculate(group, events, Filter(subGroupIds: []), Now).Members.Count);
    }

    [Fact]
    public void Declined_and_unanswered_are_counted_separately_and_response_rate_reflects_answers()
    {
        var member = Single("AADDUW");

        Assert.Equal(6, member.Invited);
        Assert.Equal(2, member.Attended);
        Assert.Equal(2, member.Declined);
        Assert.Equal(1, member.Unanswered);
        Assert.Equal(83.3, member.ResponseRate); // only the unanswered one is missing
    }

    [Fact]
    public void Streaks_are_counted_over_invited_events_only()
    {
        var member = Single("AADAAAUA");

        Assert.Equal(3, member.LongestStreak);
        Assert.Equal(1, member.CurrentStreak);
    }

    [Fact]
    public void Last_attended_and_days_since_are_relative_to_today()
    {
        var member = Single("AAADDD"); // last attended on 3 June

        Assert.Equal(new DateOnly(2026, 6, 3), member.LastAttended);
        Assert.Equal(27, member.DaysSinceLastAttended);
        Assert.Equal(new DateOnly(2026, 6, 1), member.FirstInvited);
    }

    [Fact]
    public void Member_who_never_attended_has_no_last_attended_date()
    {
        var member = Single("DDD");

        Assert.Null(member.LastAttended);
        Assert.Null(member.DaysSinceLastAttended);
    }

    [Fact]
    public void Sessions_per_week_divides_by_the_weeks_since_the_first_invitation()
    {
        // First invited on 1 June, window ends 28 June → exactly 4 weeks; 8 attended → 2 per week.
        var events = Events(At(6, 1), Patterns(("a", "AAAAAAAA")));

        var result = AttendanceCalculator.Calculate(
            Group([Member("a")]), events, Filter(6, 1, 6, 28), Now);

        Assert.Equal(2.0, result.Members.Single().SessionsPerWeek);
    }

    [Fact]
    public void Trend_is_rising_when_recent_events_are_much_better_than_earlier_ones()
    {
        var member = Single("DDDDDDDDDDDD" + "AAAAAAAA");

        Assert.Equal(AttendanceTrend.Rising, member.Trend);
        Assert.Equal(100, member.RecentRate);
        Assert.Equal(100, member.TrendDelta);
    }

    [Fact]
    public void Trend_is_falling_when_recent_events_are_much_worse_than_earlier_ones()
    {
        var member = Single("AAAAAAAAAAAA" + "DDDDDDDD");

        Assert.Equal(AttendanceTrend.Falling, member.Trend);
        Assert.Equal(-100, member.TrendDelta);
    }

    [Fact]
    public void Trend_is_stable_when_recent_and_earlier_attendance_are_similar()
    {
        Assert.Equal(AttendanceTrend.Stable, Single("ADAAADAADAADAADAADAA").Trend);
    }

    [Fact]
    public void Trend_needs_enough_events_to_compare()
    {
        var tenEvents = Single("AAAAAAAAAA");
        Assert.Equal(AttendanceTrend.Unknown, tenEvents.Trend);
        Assert.Null(tenEvents.TrendDelta);
        Assert.Equal(100, tenEvents.RecentRate); // enough for a recent rate, not for a comparison

        Assert.Null(Single("AAAAAAA").RecentRate);
    }

    [Fact]
    public void Recent_marks_are_the_last_ten_outcomes_oldest_first()
    {
        var member = Single("AAAAADUWAAAAD"); // 13 events; the first three drop out of the strip

        Assert.Equal(
            [
                AttendanceMark.Attended, AttendanceMark.Attended, AttendanceMark.Declined, AttendanceMark.Unanswered,
                AttendanceMark.Other, AttendanceMark.Attended, AttendanceMark.Attended, AttendanceMark.Attended,
                AttendanceMark.Attended, AttendanceMark.Declined,
            ],
            member.RecentMarks);
    }

    [Theory]
    [InlineData("AAAAAAAAAA", 100, EngagementLevel.Excellent)]
    [InlineData("UUUUUUUUUU", 0, EngagementLevel.AtRisk)]
    [InlineData("DDDDDDDDDD", 15, EngagementLevel.AtRisk)] // never came, but always answered
    public void Engagement_score_blends_attendance_recent_form_and_responsiveness(string pattern, int score, EngagementLevel level)
    {
        var member = Single(pattern);

        Assert.Equal(score, member.EngagementScore);
        Assert.Equal(level, member.Level);
    }

    [Theory]
    [InlineData(100, EngagementLevel.Excellent)]
    [InlineData(80, EngagementLevel.Excellent)]
    [InlineData(79, EngagementLevel.Good)]
    [InlineData(60, EngagementLevel.Good)]
    [InlineData(59, EngagementLevel.Irregular)]
    [InlineData(40, EngagementLevel.Irregular)]
    [InlineData(39, EngagementLevel.AtRisk)]
    [InlineData(0, EngagementLevel.AtRisk)]
    public void Engagement_level_bands(int score, EngagementLevel expected) =>
        Assert.Equal(expected, AttendanceCalculator.LevelFor(score));

    [Fact]
    public void Distribution_puts_a_perfect_rate_in_the_last_bucket_and_skips_members_without_verdict()
    {
        var events = Events(At(6, 1), Patterns(
            ("perfect", "AAAAAAAAAA"), ("half", "AAAAADDDDD"), ("never", "DDDDDDDDDD"), ("new", "AA--------")));
        var group = Group(new[] { "perfect", "half", "never", "new" }.Select(id => Member(id)));

        var result = AttendanceCalculator.Calculate(group, events, Filter(), Now);

        Assert.Equal(10, result.Distribution.Count);
        Assert.Equal(1, result.Distribution[9].Members);
        Assert.Equal(100, result.Distribution[9].UpperPercent);
        Assert.Equal(1, result.Distribution[5].Members);
        Assert.Equal(1, result.Distribution[0].Members);
        Assert.Equal(3, result.Distribution.Sum(b => b.Members)); // "new" has too few events
    }

    [Fact]
    public void Monthly_trend_sums_events_per_month_in_club_time()
    {
        var events = new List<EventInfo>();
        events.AddRange(Events(At(3, 1), Patterns(("a", "AA"), ("b", "AD"))));  // March: 2 events, 4 invited, 3 accepted
        events.AddRange(Events(At(4, 1), Patterns(("a", "A"), ("b", "A"))));    // April: 1 event, 2 invited, 2 accepted

        var result = AttendanceCalculator.Calculate(Group([Member("a"), Member("b")]), events, Filter(), Now);

        Assert.Collection(result.MonthlyTrend,
            march =>
            {
                Assert.Equal(new DateOnly(2026, 3, 1), march.Month);
                Assert.Equal(2, march.Events);
                Assert.Equal(1.5, march.AverageAttendees);
                Assert.Equal(75, march.AttendanceRate);
            },
            april =>
            {
                Assert.Equal(new DateOnly(2026, 4, 1), april.Month);
                Assert.Equal(1, april.Events);
                Assert.Equal(100, april.AttendanceRate);
            });
    }

    [Fact]
    public void Summary_aggregates_members_and_events()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAAA"), ("b", "DDDD"), ("c", "AA--")));
        var group = Group([Member("a"), Member("b"), Member("c")]);

        var s = AttendanceCalculator.Calculate(group, events, Filter(minInvited: 3), Now).Summary;

        Assert.Equal(4, s.EventCount);
        Assert.Equal(3, s.MemberCount);
        Assert.Equal(1, s.QualifiedCount);
        Assert.Equal(1, s.BelowThresholdCount);
        Assert.Equal(1, s.InsufficientDataCount);
        Assert.Equal(1, s.AtRiskCount); // b: never attended
        Assert.Equal(60, s.OverallAttendanceRate); // 6 of 10 invitations
        Assert.Equal(1.5, s.AverageAttendeesPerEvent); // 2,2,1,1
    }

    [Fact]
    public void Duplicate_events_from_overlapping_fetches_are_counted_once()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAA")));

        var result = AttendanceCalculator.Calculate(Group([Member("a")]), events.Concat(events), Filter(), Now);

        Assert.Equal(3, result.Summary.EventCount);
        Assert.Equal(3, result.Members.Single().Invited);
    }

    [Fact]
    public void Responses_of_people_who_are_not_group_members_are_ignored()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAA"), ("ghost", "AAA")));

        var result = AttendanceCalculator.Calculate(Group([Member("a")]), events, Filter(), Now);

        Assert.Equal(["a"], result.Members.Select(m => m.MemberId));
        Assert.All(result.Events, e => Assert.Equal(1, e.Invited));
    }
}
