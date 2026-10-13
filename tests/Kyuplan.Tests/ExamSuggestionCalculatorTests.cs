using Kyuplan.Api.Domain;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>
/// The rules that decide who is fit for an exam. The engine is pure, so every rule is pinned with a few belts and members and no
/// database: the belts are White, then Yellow (from White: 8 years, 3 months), then Orange (from Yellow: 9 years, 6 months).
/// </summary>
public sealed class ExamSuggestionCalculatorTests
{
    private static readonly DateOnly ExamDay = new(2026, 10, 17);

    private static readonly Belt White = new() { Id = 1, Name = "White" };

    private static readonly Belt Yellow = new() { Id = 2, Name = "Yellow", Prerequisites = [Link(2, 1, minAge: 8, minWait: 3)] };

    private static readonly Belt Orange = new() { Id = 3, Name = "Orange", Prerequisites = [Link(3, 2, minAge: 9, minWait: 6)] };

    private static readonly Belt[] Belts = [White, Yellow, Orange];

    private static BeltPrerequisite Link(int belt, int required, int minAge = 0, int minWait = 0) =>
        new() { BeltId = belt, RequiredBeltId = required, MinAgeYears = minAge, MinWaitMonths = minWait };

    private static Member Person(
        int id, string name, int? belt = 1, DateOnly? since = null, DateOnly? born = null) =>
        new() { Id = id, GroupId = "g1", SpondId = $"s{id}", Name = name, BeltId = belt, BeltSince = since, BirthDate = born };

    private static MemberAttendanceDto Attendance(Member member, QualificationStatus status = QualificationStatus.Qualified, int invited = 10, int attended = 9) =>
        MemberStats(member.SpondId, invited, attended, status: status) with { MemberId = member.SpondId };

    private sealed record Setup(
        Belt[]? Belts = null,
        Dictionary<int, DateOnly>? LastExams = null,
        Dictionary<int, AttendanceMark>? Invitations = null,
        HashSet<int>? PreSaved = null,
        DateOnly? Day = null);

    /// <summary>Judges the members (each with a Qualified attendance unless <paramref name="attendance"/> says otherwise) for the exam.</summary>
    private static List<ExamSuggestionDto> Judge(
        Member[] members, Setup? setup = null, Func<Member, MemberAttendanceDto?>? attendance = null)
    {
        setup ??= new Setup();
        attendance ??= m => Attendance(m);

        return ExamSuggestionCalculator.Calculate(
            setup.Day ?? ExamDay,
            setup.Belts ?? Belts,
            members.Select(m => (m, new MemberInfo(m.SpondId, m.Name, ["sg1"]))),
            members.Select(attendance).OfType<MemberAttendanceDto>().ToDictionary(a => a.MemberId),
            new Dictionary<string, string> { ["sg1"] = "Kinder" },
            setup.LastExams ?? [],
            setup.Invitations ?? [],
            setup.PreSaved ?? []);
    }

    private static ExamSuggestionDto JudgeOne(Member member, Setup? setup = null, Func<Member, MemberAttendanceDto?>? attendance = null) =>
        Assert.Single(Judge([member], setup, attendance));

    // --- Fit ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_member_who_meets_everything_the_next_belt_asks_for_is_fit()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2015, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.Fit, s.Verdict);
        Assert.Equal(Yellow.Id, s.NextBeltId);
        Assert.Equal(White.Id, s.BeltId);
        Assert.Equal((8, (int?)11, CheckState.Met), (s.MinAgeYears, s.AgeYears, s.AgeCheck));
        Assert.Equal((3, (int?)7, CheckState.Met), (s.MinWaitMonths, s.WaitedMonths, s.WaitCheck));
        Assert.Equal(new DateOnly(2026, 3, 1), s.WaitingSince);
        Assert.Equal(["Kinder"], s.SubGroups);
    }

    [Fact]
    public void What_the_next_belt_asks_for_depends_on_the_belt_held()
    {
        // From Yellow, Orange asks for 9 years and 6 months: 8 years and 7 months are not enough, though they would be for Yellow.
        var anna = Person(1, "Anna", belt: Yellow.Id, since: new DateOnly(2026, 3, 1), born: new DateOnly(2018, 6, 1));

        var s = JudgeOne(anna);

        Assert.Equal(Orange.Id, s.NextBeltId);
        Assert.Equal((9, (int?)8, CheckState.NotMet), (s.MinAgeYears, s.AgeYears, s.AgeCheck));
        Assert.Equal(SuggestionVerdict.NotYet, s.Verdict);
    }

    // --- Age and waiting time are counted until the day of the exam -----------------------------------------------

    [Theory]
    [InlineData("2026-10-16", 7, CheckState.NotMet)] // turns 8 on the 17th
    [InlineData("2026-10-17", 8, CheckState.Met)]
    [InlineData("2026-10-18", 8, CheckState.Met)]
    public void The_age_is_the_age_on_the_day_of_the_exam(string day, int age, CheckState check)
    {
        // Born 17 October 2018: 7 the day before the exam, 8 on it.
        var anna = Person(1, "Anna", since: new DateOnly(2026, 1, 1), born: new DateOnly(2018, 10, 17));

        var s = JudgeOne(anna, new Setup(Day: DateOnly.Parse(day)));

        Assert.Equal(((int?)age, check), (s.AgeYears, s.AgeCheck));
    }

    [Fact]
    public void Somebody_who_turns_old_enough_before_the_exam_is_fit_for_it()
    {
        // 7 years old today (13 October), 8 on the day of the exam: the exam is what counts.
        var anna = Person(1, "Anna", since: new DateOnly(2026, 1, 1), born: new DateOnly(2018, 10, 15));

        Assert.Equal(SuggestionVerdict.Fit, JudgeOne(anna).Verdict);
        Assert.Equal(SuggestionVerdict.NotYet, JudgeOne(anna, new Setup(Day: new DateOnly(2026, 10, 14))).Verdict);
    }

    [Theory]
    [InlineData("2026-07-18", 2, CheckState.NotMet)] // 3 months are full on the 17th
    [InlineData("2026-07-17", 3, CheckState.Met)]
    [InlineData("2026-01-01", 9, CheckState.Met)]
    public void The_waiting_time_counts_in_full_months_until_the_exam(string since, int months, CheckState check)
    {
        var anna = Person(1, "Anna", since: DateOnly.Parse(since), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(((int?)months, check), (s.WaitedMonths, s.WaitCheck));
    }

    [Fact]
    public void Not_enough_waiting_time_is_not_yet_even_with_everything_else_in_order()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 9, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.NotYet, s.Verdict);
        Assert.Equal(CheckState.NotMet, s.WaitCheck);
        Assert.Equal(CheckState.Met, s.AgeCheck);
    }

    // --- Attendance ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(QualificationStatus.BelowThreshold)]
    [InlineData(QualificationStatus.InsufficientData)]
    public void Too_little_attendance_is_not_yet(QualificationStatus status)
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, attendance: m => Attendance(m, status));

        Assert.Equal(SuggestionVerdict.NotYet, s.Verdict);
        Assert.Equal(status, s.AttendanceStatus);
    }

    [Fact]
    public void A_member_who_was_not_invited_to_any_event_of_the_filter_has_no_attendance_and_is_not_yet()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, attendance: _ => null);

        Assert.Equal(SuggestionVerdict.NotYet, s.Verdict);
        Assert.Equal((0, 0, 0d, QualificationStatus.InsufficientData), (s.Invited, s.Attended, s.AttendanceRate, s.AttendanceStatus));
    }

    [Fact]
    public void The_attendance_figures_of_the_filter_are_passed_on()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, attendance: m => MemberStats(m.SpondId, invited: 12, attended: 9, score: 77) with { MemberId = m.SpondId });

        Assert.Equal((12, 9, 75d, 77), (s.Invited, s.Attended, s.AttendanceRate, s.EngagementScore));
    }

    // --- What cannot be told ----------------------------------------------------------------------------------------

    [Fact]
    public void A_member_without_a_birth_date_needs_data_when_the_belt_asks_for_an_age()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: null);

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.NeedsData, s.Verdict);
        Assert.Null(s.AgeYears);
        Assert.Equal(CheckState.Unknown, s.AgeCheck);
    }

    [Fact]
    public void A_member_without_a_belt_day_or_a_last_exam_needs_data_when_the_belt_asks_for_a_wait()
    {
        var anna = Person(1, "Anna", since: null, born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.NeedsData, s.Verdict);
        Assert.Null(s.WaitedMonths);
        Assert.Null(s.WaitingSince);
        Assert.Equal(CheckState.Unknown, s.WaitCheck);
    }

    [Fact]
    public void Nothing_unknown_matters_when_the_belt_asks_for_nothing()
    {
        var easy = new Belt { Id = 4, Name = "Easy", Prerequisites = [Link(4, 1)] };
        var anna = Person(1, "Anna", since: null, born: null);

        var s = JudgeOne(anna, new Setup(Belts: [White, easy]));

        Assert.Equal(SuggestionVerdict.Fit, s.Verdict);
        Assert.Equal(easy.Id, s.NextBeltId);
    }

    [Fact]
    public void What_is_missed_wins_over_what_is_not_known()
    {
        // No birth date, but not enough waiting time: whatever the age is, the member is not fit yet.
        var anna = Person(1, "Anna", since: new DateOnly(2026, 9, 1), born: null);

        Assert.Equal(SuggestionVerdict.NotYet, JudgeOne(anna).Verdict);
    }

    [Fact]
    public void Without_the_day_of_the_belt_the_waiting_time_counts_from_the_last_exam()
    {
        var anna = Person(1, "Anna", since: null, born: new DateOnly(2010, 1, 1));

        var recent = JudgeOne(anna, new Setup(LastExams: new() { [1] = new DateOnly(2026, 9, 26) }));
        var long_ago = JudgeOne(anna, new Setup(LastExams: new() { [1] = new DateOnly(2026, 3, 28) }));

        // The belt cannot have been received later than the last exam, so counting from it never overstates the wait.
        Assert.Equal(((int?)0, CheckState.NotMet, SuggestionVerdict.NotYet), (recent.WaitedMonths, recent.WaitCheck, recent.Verdict));
        Assert.Equal(((int?)6, CheckState.Met, SuggestionVerdict.Fit), (long_ago.WaitedMonths, long_ago.WaitCheck, long_ago.Verdict));
        Assert.Null(recent.BeltSince);
        Assert.Equal(new DateOnly(2026, 9, 26), recent.WaitingSince);
        Assert.Equal(new DateOnly(2026, 9, 26), recent.LastExam);
    }

    [Fact]
    public void The_day_the_belt_was_received_wins_over_the_last_exam()
    {
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, new Setup(LastExams: new() { [1] = new DateOnly(2026, 9, 26) }));

        Assert.Equal(new DateOnly(2026, 3, 1), s.WaitingSince);
        Assert.Equal(7, s.WaitedMonths);
        Assert.Equal(new DateOnly(2026, 9, 26), s.LastExam); // known, and not what the waiting time counts from
    }

    [Fact]
    public void A_member_without_a_belt_needs_data_and_has_no_next_belt()
    {
        var anna = Person(1, "Anna", belt: null, since: new DateOnly(2026, 1, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.NeedsData, s.Verdict);
        Assert.Null(s.NextBeltId);
        // Without a belt there is no day it was received, whatever is stored.
        Assert.Null(s.BeltSince);
        Assert.Null(s.WaitingSince);
        Assert.Equal((0, 0), (s.MinAgeYears, s.MinWaitMonths));
    }

    [Fact]
    public void A_member_who_holds_the_highest_belt_has_nothing_to_take()
    {
        var anna = Person(1, "Anna", belt: Orange.Id, since: new DateOnly(2020, 1, 1), born: new DateOnly(2000, 1, 1));

        var s = JudgeOne(anna);

        Assert.Equal(SuggestionVerdict.NoNextBelt, s.Verdict);
        Assert.Null(s.NextBeltId);
    }

    // --- Several ways on ---------------------------------------------------------------------------------------------

    [Fact]
    public void When_several_belts_ask_for_the_belt_held_the_highest_one_the_member_is_fit_for_wins()
    {
        // From White one can take Yellow (at 8 years) or Shortcut (at 12), and Shortcut comes after Yellow in the order of the belts.
        var shortcut = new Belt { Id = 5, Name = "Shortcut", Prerequisites = [Link(5, 1, minAge: 12)] };
        var young = Person(1, "Young", since: new DateOnly(2026, 3, 1), born: new DateOnly(2016, 1, 1)); // 10
        var old = Person(2, "Old", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1)); // 16

        var rows = Judge([young, old], new Setup(Belts: [White, Yellow, shortcut]));

        Assert.Equal(Yellow.Id, rows.Single(r => r.MemberId == 1).NextBeltId); // too young for the higher one, fit for the lower
        Assert.Equal(shortcut.Id, rows.Single(r => r.MemberId == 2).NextBeltId); // fit for both: the higher one
    }

    [Fact]
    public void When_the_highest_way_on_is_not_open_yet_a_lower_one_the_member_is_fit_for_is_chosen()
    {
        var open = new Belt { Id = 5, Name = "Open", Prerequisites = [Link(5, 1, minAge: 8)] };
        var strict = new Belt { Id = 4, Name = "Strict", Prerequisites = [Link(4, 1, minAge: 30)] };
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, new Setup(Belts: [White, open, strict]));

        Assert.Equal(open.Id, s.NextBeltId);
        Assert.Equal(SuggestionVerdict.Fit, s.Verdict);
    }

    [Fact]
    public void When_no_way_on_is_open_the_first_one_is_shown()
    {
        var strict = new Belt { Id = 4, Name = "Strict", Prerequisites = [Link(4, 1, minAge: 30)] };
        var stricter = new Belt { Id = 5, Name = "Stricter", Prerequisites = [Link(5, 1, minAge: 40)] };
        var anna = Person(1, "Anna", since: new DateOnly(2026, 3, 1), born: new DateOnly(2010, 1, 1));

        var s = JudgeOne(anna, new Setup(Belts: [White, strict, stricter]));

        Assert.Equal(strict.Id, s.NextBeltId);
        Assert.Equal(SuggestionVerdict.NotYet, s.Verdict);
    }

    // --- The list ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_fit_come_first_by_attendance_then_those_who_need_data_those_not_yet_and_those_with_nothing_to_take()
    {
        var since = new DateOnly(2026, 3, 1);
        var born = new DateOnly(2010, 1, 1);
        var members = new[]
        {
            Person(1, "Top belt", belt: Orange.Id, since: since, born: born),
            Person(2, "Not yet", since: new DateOnly(2026, 10, 1), born: born),
            Person(3, "No belt", belt: null),
            Person(4, "Fit, less", since: since, born: born),
            Person(5, "Fit, more", since: since, born: born),
            Person(6, "Needs a birth date", since: since, born: null),
        };

        var rows = Judge(members, attendance: m => m.Id == 4
            ? MemberStats(m.SpondId, invited: 10, attended: 8) with { MemberId = m.SpondId }
            : MemberStats(m.SpondId, invited: 10, attended: 9) with { MemberId = m.SpondId });

        Assert.Equal(
            ["Fit, more", "Fit, less", "Needs a birth date", "No belt", "Not yet", "Top belt"],
            rows.Select(r => r.Name));
        Assert.Equal(
            [SuggestionVerdict.Fit, SuggestionVerdict.Fit, SuggestionVerdict.NeedsData, SuggestionVerdict.NeedsData, SuggestionVerdict.NotYet, SuggestionVerdict.NoNextBelt],
            rows.Select(r => r.Verdict));
    }

    [Fact]
    public void The_answer_to_the_invitation_and_the_saved_state_are_passed_on_per_member()
    {
        var since = new DateOnly(2026, 3, 1);
        var born = new DateOnly(2010, 1, 1);
        var anna = Person(1, "Anna", since: since, born: born);
        var ben = Person(2, "Ben", since: since, born: born);
        var clara = Person(3, "Clara", since: since, born: born);

        var rows = Judge([anna, ben, clara], new Setup(
            Invitations: new() { [1] = AttendanceMark.Attended, [2] = AttendanceMark.Unanswered },
            PreSaved: [2, 3]));

        var byName = rows.ToDictionary(r => r.Name);
        Assert.Equal(AttendanceMark.Attended, byName["Anna"].Invitation);
        Assert.False(byName["Anna"].IsPreSaved);
        Assert.Equal(AttendanceMark.Unanswered, byName["Ben"].Invitation);
        Assert.True(byName["Ben"].IsPreSaved);
        Assert.Null(byName["Clara"].Invitation);
        Assert.True(byName["Clara"].IsPreSaved);
    }

    // --- The arithmetic ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-01-31", "2026-02-28", 1)] // a month shorter than the day ends on its last day
    [InlineData("2026-01-31", "2026-02-27", 0)]
    [InlineData("2026-01-15", "2026-03-14", 1)]
    [InlineData("2026-01-15", "2026-03-15", 2)]
    [InlineData("2025-10-17", "2026-10-17", 12)]
    [InlineData("2026-10-17", "2026-10-17", 0)]
    [InlineData("2026-10-18", "2026-10-17", 0)] // never negative
    public void Months_between_two_days_are_the_full_months_that_have_passed(string from, string to, int expected)
    {
        Assert.Equal(expected, ExamSuggestionCalculator.MonthsBetween(DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    [Theory]
    [InlineData("2018-10-17", "2026-10-17", 8)]
    [InlineData("2018-10-17", "2026-10-16", 7)]
    [InlineData("2018-10-18", "2026-10-17", 7)]
    [InlineData("2000-02-29", "2026-02-28", 26)] // in a year without a 29th the birthday is the 28th, as a month ends on its last day
    [InlineData("2000-02-29", "2027-02-27", 26)]
    [InlineData("2026-10-17", "2026-10-17", 0)]
    [InlineData("2027-01-01", "2026-10-17", 0)] // not born yet
    public void The_age_is_counted_in_full_years(string birth, string day, int expected)
    {
        Assert.Equal(expected, ExamSuggestionCalculator.AgeOn(DateOnly.Parse(birth), DateOnly.Parse(day)));
    }
}
