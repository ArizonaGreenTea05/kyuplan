using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>
/// Who is fit for an exam, who is saved for it and who received which belt at it, against the real services and database and a Spond
/// the test controls. "Today" is 13 October 2026.
/// <para>
/// The club: Anna, Ben (subgroup Kids), Clara and Dora (Adults), all 14 years old. The previous exam was 30 days ago, the next is in 20; in
/// between they were invited to four trainings. Anna and Clara came to everything, Ben to little, Dora to everything as well. Everybody holds
/// the White belt since 200 days ago, except Dora, whose day is not known. Yellow is taken from White at 8 years and after 3 months.
/// </para>
/// </summary>
public sealed class ExamPlanningTests : IDisposable
{
    private const string Past = "exam-past";
    private const string Next = "exam-next";

    private static readonly SubGroupInfo[] SubGroups = [new("sg1", "Kids"), new("sg2", "Adults")];

    private readonly SyncTestClub _club = new();

    public void Dispose() => _club.Dispose();

    private FakeSpondDataSource Spond => _club.Spond;

    private sealed record World(ExamDto Past, ExamDto Next, int White, int Yellow, IReadOnlyDictionary<string, int> Members);

    private async Task<World> BuildAsync(string[]? nextSubGroups = null)
    {
        var born = new DateOnly(2012, 1, 1);
        Spond.SetGroups(Group(Dojo, SubGroups,
            Person("a", "Anna", born, "sg1"), Person("b", "Ben", born, "sg1"),
            Person("c", "Clara", born, "sg2"), Person("d", "Dora", born, "sg2")));

        const Rsvp A = Rsvp.Accepted;
        const Rsvp D = Rsvp.Declined;
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", A), ("b", A), ("d", A)),
            _club.Event("t1", Day(-20), ("a", A), ("b", A), ("c", A), ("d", A)),
            _club.Event("t2", Day(-15), ("a", A), ("b", D), ("c", A), ("d", A)),
            _club.Event("t3", Day(-10), ("a", A), ("b", D), ("c", A), ("d", A)),
            _club.Event("t4", Day(-5), ("a", A), ("b", D), ("c", A), ("d", A)),
            _club.Event(Next, "Gürtelprüfung", Day(20), ("a", Rsvp.Unanswered), ("c", A)));

        var past = await _club.CreateExamAsync(Past);
        var next = await _club.CreateExamAsync(Next, subGroups: nextSubGroups);

        var white = await _club.AddBeltAsync("White");
        var yellow = await _club.AddBeltAsync("Yellow", (white, 8, 3));

        var members = (await _club.MembersAsync()).ToDictionary(m => m.SpondId, m => m.Id);
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            await _club.EditMemberAsync(id, m =>
            {
                m.BeltId = white;
                m.BeltSince = id == "d" ? null : Day(-200);
            });
        }

        return new World(past, next, white, yellow, members);
    }

    private Task<T> Planning<T>(Func<ExamPlanningService, Task<T>> call) =>
        _club.QueryAsync(sp => call(sp.GetRequiredService<ExamPlanningService>()));

    private Task<IReadOnlyList<ExamMemberDto>> SaveFor(int examId, int[] add, int[]? remove = null) =>
        Planning(s => s.SetCandidatesAsync(examId, add, remove ?? [], CancellationToken.None));

    private Task<T> Db<T>(Func<PlannerDbContext, Task<T>> query) =>
        _club.QueryAsync(sp => query(sp.GetRequiredService<PlannerDbContext>()));

    private Task<SavedFilterDto> SaveFilterAsync(int examId, string name, FilterSettingsDto settings) =>
        _club.QueryAsync(sp => sp.GetRequiredService<ExamService>().CreateFilterAsync(examId, new SavedFilterInputDto(name, settings), CancellationToken.None));

    private static FilterSettingsDto Settings(
        string group = Dojo, int percent = 75, int invited = 3, string[]? subGroups = null,
        string? fromEvent = null, string? toEvent = null, DateOnly? from = null, DateOnly? to = null) =>
        new(group, from ?? Day(-30), to ?? Day(20), fromEvent, toEvent, subGroups ?? [], percent, invited, null, null);

    // --- Who is fit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_filter_the_attendance_is_measured_from_the_previous_exam_to_this_one()
    {
        var w = await BuildAsync();

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None));

        Assert.Equal((w.Next.Id, "Gürtelprüfung", Day(20)), (result.ExamId, result.ExamName, result.ExamDate));
        Assert.Null(result.FilterId);
        Assert.Null(result.FilterName);
        // What the statistics page opens with: previous exam to the exam, the configured thresholds, everyone.
        Assert.Equal(new AttendanceFilter(Dojo, Day(-30), Day(20), null, 75, 3), result.Filter);
    }

    [Fact]
    public async Task The_members_are_judged_for_the_belt_after_the_one_they_hold_on_the_day_of_the_exam()
    {
        var w = await BuildAsync();

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None));

        // Fit first (Anna came to more events than Clara), then the ones who are not yet, the better attendance first.
        Assert.Equal(["Anna", "Clara", "Dora", "Ben"], result.Members.Select(m => m.Name));
        Assert.Equal(
            [SuggestionVerdict.Fit, SuggestionVerdict.Fit, SuggestionVerdict.NotYet, SuggestionVerdict.NotYet],
            result.Members.Select(m => m.Verdict));

        var anna = result.Members[0];
        Assert.Equal(w.White, anna.BeltId);
        Assert.Equal(w.Yellow, anna.NextBeltId);
        Assert.Equal((8, (int?)14, CheckState.Met), (anna.MinAgeYears, anna.AgeYears, anna.AgeCheck));
        // Her belt is from 27 March: seven full months until the exam on 2 November.
        Assert.Equal((3, (int?)7, CheckState.Met), (anna.MinWaitMonths, anna.WaitedMonths, anna.WaitCheck));
        Assert.Equal((5, 5, 100d, QualificationStatus.Qualified), (anna.Invited, anna.Attended, anna.AttendanceRate, anna.AttendanceStatus));
        Assert.Equal(["Kids"], anna.SubGroups);

        var ben = result.Members[3];
        Assert.Equal((5, 2, QualificationStatus.BelowThreshold), (ben.Invited, ben.Attended, ben.AttendanceStatus));
    }

    [Fact]
    public async Task Without_the_day_of_the_belt_the_wait_counts_from_the_last_exam_the_member_took_part_in()
    {
        var w = await BuildAsync();

        var dora = (await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None))).Members.Single(m => m.Name == "Dora");

        // Dora accepted the previous exam 30 days ago and has no belt day: a month of waiting, three are asked for.
        Assert.Null(dora.BeltSince);
        Assert.Equal(Day(-30), dora.LastExam);
        Assert.Equal(Day(-30), dora.WaitingSince);
        Assert.Equal(((int?)1, CheckState.NotMet), (dora.WaitedMonths, dora.WaitCheck));
    }

    [Fact]
    public async Task A_member_who_declined_the_last_exam_has_not_taken_part_in_it()
    {
        var w = await BuildAsync();
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", Rsvp.Accepted), ("d", Rsvp.Declined)),
            _club.Event(Next, "Gürtelprüfung", Day(20), ("a", Rsvp.Unanswered)));
        await _club.SyncAsync();

        var dora = (await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None))).Members.Single(m => m.Name == "Dora");

        Assert.Null(dora.LastExam);
        Assert.Null(dora.WaitingSince);
        Assert.Equal(CheckState.Unknown, dora.WaitCheck);
    }

    [Fact]
    public async Task An_exam_that_has_not_taken_place_is_nobodys_last_exam()
    {
        await BuildAsync();
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", Rsvp.Accepted), ("d", Rsvp.Accepted)),
            _club.Event(Next, "Gürtelprüfung", Day(20), ("a", Rsvp.Accepted), ("d", Rsvp.Accepted)),
            _club.Event("exam-later", "Gürtelprüfung", Day(80), ("d", Rsvp.Accepted)));
        await _club.SyncAsync();
        var later = await _club.CreateExamAsync("exam-later");

        // Dora accepted the exam in 20 days, but for the one in 80 it is still to come: it cannot have been taken part in yet.
        var dora = (await Planning(s => s.SuggestAsync(later.Id, null, CancellationToken.None))).Members.Single(m => m.Name == "Dora");

        Assert.Equal(Day(-30), dora.LastExam);
    }

    [Fact]
    public async Task With_no_earlier_exam_the_default_span_is_the_last_six_months()
    {
        await BuildAsync();
        await Db(async db =>
        {
            db.Exams.RemoveRange(await db.Exams.Where(e => e.EventId == Past).ToListAsync());
            await db.SaveChangesAsync();
            return 0;
        });
        var next = (await _club.QueryAsync(sp => sp.GetRequiredService<ExamService>().ListAsync(CancellationToken.None))).Single();

        var result = await Planning(s => s.SuggestAsync(next.Id, null, CancellationToken.None));

        Assert.Equal(Day(20).AddMonths(-6).AddDays(1), result.Filter.From);
        Assert.Equal(Day(20), result.Filter.To);
    }

    // --- Suggestions made with a saved filter -------------------------------------------------------------------

    [Fact]
    public async Task A_saved_filter_decides_how_much_attendance_is_enough_and_is_reported_back()
    {
        var w = await BuildAsync();
        var lenient = await SaveFilterAsync(w.Next.Id, "Lenient", Settings(percent: 25));

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, lenient.Id, CancellationToken.None));

        Assert.Equal((lenient.Id, "Lenient"), (result.FilterId, result.FilterName));
        Assert.Equal(25, result.Filter.MinAttendancePercent);
        // Ben came to 2 of 5 events: 40 % is enough now.
        Assert.Equal(SuggestionVerdict.Fit, result.Members.Single(m => m.Name == "Ben").Verdict);
    }

    [Fact]
    public async Task Any_filter_of_the_group_can_be_used_whichever_exam_it_was_saved_for()
    {
        var w = await BuildAsync();
        var strict = await SaveFilterAsync(w.Past.Id, "Strict", Settings(percent: 100));

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, strict.Id, CancellationToken.None));

        Assert.Equal("Strict", result.FilterName);
        Assert.Equal(100, result.Filter.MinAttendancePercent);
        Assert.Equal(["Anna", "Clara"], result.Members.Where(m => m.Verdict == SuggestionVerdict.Fit).Select(m => m.Name));
    }

    [Fact]
    public async Task A_filter_for_some_subgroups_leaves_the_others_out_of_the_suggestions()
    {
        var w = await BuildAsync();
        var kids = await SaveFilterAsync(w.Next.Id, "Kids only", Settings(subGroups: ["sg1"]));

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, kids.Id, CancellationToken.None));

        Assert.Equal(["Anna", "Ben"], result.Members.Select(m => m.Name));
        Assert.Equal(["sg1"], result.Filter.SubGroupIds);
    }

    [Fact]
    public async Task The_subgroups_chosen_for_an_exam_are_what_its_default_filter_looks_at()
    {
        var w = await BuildAsync(nextSubGroups: ["sg1"]);

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None));

        Assert.Equal(["sg1"], w.Next.SubGroupIds);
        Assert.Null(result.FilterId);
        Assert.Equal(["sg1"], result.Filter.SubGroupIds);
        Assert.Equal((Dojo, Day(-30), Day(20), 75, 3), (result.Filter.GroupId, result.Filter.From, result.Filter.To, result.Filter.MinAttendancePercent, result.Filter.MinInvitedEvents));
        Assert.Equal(["Anna", "Ben"], result.Members.Select(m => m.Name)); // the Kids; Clara and Dora are left out
    }

    [Fact]
    public async Task A_saved_filter_brings_its_own_subgroups_whatever_the_exam_was_made_for()
    {
        var w = await BuildAsync(nextSubGroups: ["sg1"]);
        var everyone = await SaveFilterAsync(w.Next.Id, "Everyone", Settings());

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, everyone.Id, CancellationToken.None));

        Assert.Equal(["Anna", "Clara", "Dora", "Ben"], result.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task A_filter_that_names_its_events_follows_them()
    {
        var w = await BuildAsync();
        // The days it was saved with are not the days of the events any more: the events win while they are offered.
        var byEvents = await SaveFilterAsync(w.Next.Id, "By events", Settings(fromEvent: "t1", toEvent: Next, from: Day(-300), to: Day(-290)));

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, byEvents.Id, CancellationToken.None));

        Assert.Equal((Day(-20), Day(20)), (result.Filter.From, result.Filter.To));
        Assert.Equal((4, 4), (result.Members[0].Invited, result.Members[0].Attended)); // t1 to t4
    }

    [Fact]
    public async Task A_filter_whose_events_are_gone_is_applied_with_the_days_it_stood_for()
    {
        var w = await BuildAsync();
        var gone = await SaveFilterAsync(w.Next.Id, "Gone", Settings(fromEvent: "ghost-1", toEvent: "ghost-2", from: Day(-12), to: Day(0)));

        var result = await Planning(s => s.SuggestAsync(w.Next.Id, gone.Id, CancellationToken.None));

        Assert.Equal((Day(-12), Day(0)), (result.Filter.From, result.Filter.To));
        Assert.Equal(2, result.Members.First(m => m.Name == "Anna").Invited); // t3 and t4
    }

    [Fact]
    public async Task A_filter_cannot_be_used_for_another_group_and_must_exist()
    {
        var w = await BuildAsync();
        var other = await SaveFilterAsync(w.Next.Id, "Of the team", Settings(group: "g-team"));

        var wrongGroup = await Assert.ThrowsAsync<InvalidInputException>(() => Planning(s => s.SuggestAsync(w.Next.Id, other.Id, CancellationToken.None)));
        var missing = await Assert.ThrowsAsync<InvalidInputException>(() => Planning(s => s.SuggestAsync(w.Next.Id, 9999, CancellationToken.None)));

        Assert.Contains("another group", wrongGroup.Message);
        Assert.Contains("9999", missing.Message);
    }

    [Fact]
    public async Task Suggestions_for_an_exam_that_does_not_exist_are_refused()
    {
        await BuildAsync();

        await Assert.ThrowsAsync<ExamNotFoundException>(() => Planning(s => s.SuggestAsync(9999, null, CancellationToken.None)));
    }

    [Fact]
    public async Task Suggestions_need_spond_but_saved_members_do_not()
    {
        var w = await BuildAsync();
        Spond.Unreachable = true;

        await Assert.ThrowsAsync<SpondUnavailableException>(() => Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None)));

        // What the planner holds itself is there to read whatever Spond does.
        Assert.NotEmpty(await Planning(s => s.GetMembersAsync(w.Past.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task The_invitation_and_the_saved_state_show_in_the_suggestions()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["a"], w.Members["b"]]);

        var members = (await Planning(s => s.SuggestAsync(w.Next.Id, null, CancellationToken.None))).Members.ToDictionary(m => m.Name);

        // Anna was invited and has not answered, and is saved; Clara was invited and accepted, and is not saved; Ben is saved, not invited.
        Assert.Equal(AttendanceMark.Unanswered, members["Anna"].Invitation);
        Assert.True(members["Anna"].IsPreSaved);
        Assert.Equal(AttendanceMark.Attended, members["Clara"].Invitation);
        Assert.False(members["Clara"].IsPreSaved);
        Assert.Null(members["Ben"].Invitation);
        Assert.True(members["Ben"].IsPreSaved);
        Assert.Null(members["Dora"].Invitation);
        Assert.False(members["Dora"].IsPreSaved);
    }

    // --- Members saved for an exam --------------------------------------------------------------------------------

    [Fact]
    public async Task Members_are_saved_for_an_exam_and_taken_off_and_whoever_is_not_named_stays_as_they_are()
    {
        var w = await BuildAsync();

        var first = await SaveFor(w.Next.Id, [w.Members["a"], w.Members["b"], w.Members["b"]]);
        var second = await SaveFor(w.Next.Id, [w.Members["d"]], [w.Members["b"]]);

        Assert.Equal(["Anna", "Ben"], first.Where(m => m.IsPreSaved).Select(m => m.Name));
        Assert.Equal(["Anna", "Dora"], second.Where(m => m.IsPreSaved).Select(m => m.Name)); // Anna was not named, so she stays
        Assert.Equal(2, await Db(db => db.ExamCandidates.CountAsync(c => c.ExamId == w.Next.Id)));
    }

    [Fact]
    public async Task Saving_somebody_twice_and_taking_off_somebody_who_is_not_saved_do_no_harm_and_both_lists_may_name_a_member()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["a"]]);

        await SaveFor(w.Next.Id, [w.Members["a"]], [w.Members["c"]]); // already saved / not saved
        var both = await SaveFor(w.Next.Id, [w.Members["b"]], [w.Members["b"]]); // named in both: saved

        Assert.Equal(["Anna", "Ben"], both.Where(m => m.IsPreSaved).Select(m => m.Name));
        Assert.Equal(2, await Db(db => db.ExamCandidates.CountAsync()));
    }

    [Fact]
    public async Task A_member_saved_for_one_exam_is_not_saved_for_another_and_an_empty_change_changes_nothing()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["a"]]);
        await SaveFor(w.Past.Id, [w.Members["b"]]);

        var unchanged = await SaveFor(w.Next.Id, []);
        var cleared = await SaveFor(w.Next.Id, [], [w.Members["a"]]);

        Assert.Equal(["Anna"], unchanged.Where(m => m.IsPreSaved).Select(m => m.Name));
        Assert.DoesNotContain(cleared, m => m.IsPreSaved);
        Assert.Equal([w.Members["b"]], await Db(db => db.ExamCandidates.Select(c => c.MemberId).ToListAsync()));
    }

    [Fact]
    public async Task Saved_members_are_listed_with_what_spond_says_about_them()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["b"], w.Members["c"]]);

        var members = await Planning(s => s.GetMembersAsync(w.Next.Id, CancellationToken.None));

        // Invited: Anna (no answer yet) and Clara (accepted); saved: Ben and Clara. Dora is neither.
        Assert.Equal(["Anna", "Ben", "Clara"], members.Select(m => m.Name));
        Assert.Equal([AttendanceMark.Unanswered, null, AttendanceMark.Attended], members.Select(m => m.Invitation));
        Assert.Equal([false, true, true], members.Select(m => m.IsPreSaved));
    }

    [Fact]
    public async Task The_sync_with_spond_never_touches_the_saved_members_and_shows_them_once_they_are_invited()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["b"]]);

        await _club.SyncAsync();
        var before = (await Planning(s => s.GetMembersAsync(w.Next.Id, CancellationToken.None))).Single(m => m.Name == "Ben");

        // Somebody invites Ben on Spond, and he accepts.
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", Rsvp.Accepted)),
            _club.Event(Next, "Gürtelprüfung", Day(20), ("a", Rsvp.Unanswered), ("b", Rsvp.Accepted)));
        await _club.SyncAsync();
        var after = (await Planning(s => s.GetMembersAsync(w.Next.Id, CancellationToken.None))).Single(m => m.Name == "Ben");

        Assert.Null(before.Invitation);
        Assert.True(before.IsPreSaved);
        Assert.Equal(AttendanceMark.Attended, after.Invitation);
        Assert.True(after.IsPreSaved);
    }

    [Fact]
    public async Task Members_to_save_must_exist_and_belong_to_the_group_of_the_exam()
    {
        var w = await BuildAsync();
        Spond.SetGroups(Group(Dojo, SubGroups, Person("a", "Anna", null, "sg1")), Group("g-team", Person("t", "Tim")));
        await _club.SyncAsync();
        var tim = (await _club.MembersAsync()).Single(m => m.SpondId == "t").Id;

        var unknown = await Assert.ThrowsAsync<InvalidInputException>(() => SaveFor(w.Next.Id, [w.Members["a"], 9999]));
        var stranger = await Assert.ThrowsAsync<InvalidInputException>(() => SaveFor(w.Next.Id, [tim]));
        await Assert.ThrowsAsync<ExamNotFoundException>(() => SaveFor(9999, []));

        Assert.Contains("9999", unknown.Message);
        Assert.Contains("group", stranger.Message);
        Assert.Equal(0, await Db(db => db.ExamCandidates.CountAsync())); // a refused list saves nothing
    }

    [Fact]
    public async Task Deleting_an_exam_deletes_whoever_was_saved_for_it_and_what_was_recorded_at_it()
    {
        var w = await BuildAsync();
        await SaveFor(w.Past.Id, [w.Members["a"]]);
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        await _club.RunAsync(sp => sp.GetRequiredService<ExamService>().DeleteAsync(w.Past.Id, CancellationToken.None));

        Assert.Equal(0, await Db(db => db.ExamCandidates.CountAsync()));
        Assert.Equal(0, await Db(db => db.ExamResults.CountAsync()));
        // The member keeps the belt: it was handed out, deleting the exam does not take it back.
        Assert.Equal(w.Yellow, (await _club.MembersAsync()).Single(m => m.SpondId == "a").BeltId);
    }

    [Fact]
    public async Task A_member_who_leaves_spond_leaves_the_exams_lists_too()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["b"]]);
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["b"], w.Yellow, CancellationToken.None));

        Spond.SetGroups(Group(Dojo, SubGroups, Person("a", "Anna", null, "sg1")));
        await _club.SyncAsync();

        Assert.Equal(0, await Db(db => db.ExamCandidates.CountAsync()));
        Assert.Equal(0, await Db(db => db.ExamResults.CountAsync()));
    }

    // --- Belts received at an exam ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_belt_received_at_an_exam_is_recorded_and_becomes_the_belt_the_member_holds_from_that_day()
    {
        var w = await BuildAsync();

        var row = await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        Assert.Equal((w.Members["a"], w.Yellow, w.Yellow), (row.MemberId, row.ReceivedBeltId ?? 0, row.BeltId ?? 0));
        Assert.Equal(AttendanceMark.Attended, row.Invitation); // Anna accepted the exam
        var anna = (await _club.MembersAsync()).Single(m => m.SpondId == "a");
        Assert.Equal(w.Yellow, anna.BeltId);
        Assert.Equal(Day(-30), anna.BeltSince); // the day of the exam
        Assert.Equal([w.Members["a"]], (await Planning(s => s.GetMembersAsync(w.Past.Id, CancellationToken.None))).Where(m => m.ReceivedBeltId == w.Yellow).Select(m => m.MemberId));
    }

    [Fact]
    public async Task A_belt_can_be_changed_at_the_same_exam_and_the_member_follows()
    {
        var w = await BuildAsync();
        var orange = await _club.AddBeltAsync("Orange", (w.Yellow, 9, 6));
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], orange, CancellationToken.None));

        Assert.Equal(1, await Db(db => db.ExamResults.CountAsync(r => r.MemberId == w.Members["a"])));
        Assert.Equal(orange, (await _club.MembersAsync()).Single(m => m.SpondId == "a").BeltId);
    }

    [Fact]
    public async Task Taking_a_result_back_leaves_the_belt_the_member_holds_as_it_is()
    {
        var w = await BuildAsync();
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        var row = await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], null, CancellationToken.None));

        Assert.Null(row.ReceivedBeltId);
        Assert.Equal(0, await Db(db => db.ExamResults.CountAsync()));
        Assert.Equal(w.Yellow, (await _club.MembersAsync()).Single(m => m.SpondId == "a").BeltId); // corrected on the member, not here
    }

    [Fact]
    public async Task A_result_of_an_older_exam_does_not_take_a_later_belt_away_from_the_member()
    {
        var w = await BuildAsync();
        var orange = await _club.AddBeltAsync("Orange", (w.Yellow, 9, 6));
        // A second exam, yesterday: the one 30 days ago is the earlier of the two.
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", Rsvp.Accepted)),
            _club.Event("exam-yesterday", "Gürtelprüfung", Day(-1), ("a", Rsvp.Accepted)),
            _club.Event(Next, "Gürtelprüfung", Day(20), ("a", Rsvp.Unanswered)));
        var yesterday = await _club.CreateExamAsync("exam-yesterday");

        await Planning(s => s.SetResultAsync(yesterday.Id, w.Members["a"], orange, CancellationToken.None));
        // The result of the older exam is entered afterwards, as when the history is typed in.
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        var anna = (await _club.MembersAsync()).Single(m => m.SpondId == "a");
        Assert.Equal(orange, anna.BeltId);
        Assert.Equal(Day(-1), anna.BeltSince);
        Assert.Equal(w.Yellow, (await Planning(s => s.GetMembersAsync(w.Past.Id, CancellationToken.None))).Single(m => m.MemberId == w.Members["a"]).ReceivedBeltId); // still on record
    }

    [Fact]
    public async Task A_member_who_got_their_belt_after_the_exam_keeps_it()
    {
        var w = await BuildAsync();
        await _club.EditMemberAsync("a", m => m.BeltSince = Day(-5));

        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        var anna = (await _club.MembersAsync()).Single(m => m.SpondId == "a");
        Assert.Equal(w.White, anna.BeltId);
        Assert.Equal(Day(-5), anna.BeltSince);
    }

    [Fact]
    public async Task Belts_are_only_recorded_once_the_exam_has_taken_place()
    {
        var w = await BuildAsync();

        var early = await Assert.ThrowsAsync<InvalidInputException>(() => Planning(s => s.SetResultAsync(w.Next.Id, w.Members["c"], w.Yellow, CancellationToken.None)));

        Assert.Contains("taken place", early.Message);
        Assert.Equal(0, await Db(db => db.ExamResults.CountAsync()));
        Assert.Equal(w.White, (await _club.MembersAsync()).Single(m => m.SpondId == "c").BeltId);
    }

    [Fact]
    public async Task Belts_can_be_recorded_on_the_day_of_the_exam()
    {
        var w = await BuildAsync();
        Spond.SetEvents(Dojo,
            _club.Event(Past, "Gürtelprüfung", Day(-30), ("a", Rsvp.Accepted)),
            _club.Event("exam-today", "Gürtelprüfung", Day(0), ("a", Rsvp.Accepted)));
        var today = await _club.CreateExamAsync("exam-today");

        await Planning(s => s.SetResultAsync(today.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        Assert.Equal(Day(0), (await _club.MembersAsync()).Single(m => m.SpondId == "a").BeltSince);
    }

    [Fact]
    public async Task A_result_needs_an_exam_a_member_of_its_group_and_a_belt_that_exists()
    {
        var w = await BuildAsync();
        Spond.SetGroups(Group(Dojo, SubGroups, Person("a", "Anna", null, "sg1")), Group("g-team", Person("t", "Tim")));
        await _club.SyncAsync();
        var tim = (await _club.MembersAsync()).Single(m => m.SpondId == "t").Id;

        await Assert.ThrowsAsync<ExamNotFoundException>(() => Planning(s => s.SetResultAsync(9999, w.Members["a"], w.Yellow, CancellationToken.None)));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => Planning(s => s.SetResultAsync(w.Past.Id, 9999, w.Yellow, CancellationToken.None)));
        var stranger = await Assert.ThrowsAsync<InvalidInputException>(() => Planning(s => s.SetResultAsync(w.Past.Id, tim, w.Yellow, CancellationToken.None)));
        var noBelt = await Assert.ThrowsAsync<InvalidInputException>(() => Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], 9999, CancellationToken.None)));

        Assert.Contains("group", stranger.Message);
        Assert.Contains("9999", noBelt.Message);
        Assert.Equal(0, await Db(db => db.ExamResults.CountAsync()));
    }

    [Fact]
    public async Task A_belt_that_was_handed_out_at_an_exam_cannot_be_deleted()
    {
        var w = await BuildAsync();
        var orange = await _club.AddBeltAsync("Orange");
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], orange, CancellationToken.None));
        // Give the member another belt, so that only the result is left to hold Orange.
        await _club.EditMemberAsync("a", m => m.BeltId = w.White);

        var refused = await Assert.ThrowsAsync<BeltInUseException>(() =>
            _club.RunAsync(sp => sp.GetRequiredService<BeltService>().DeleteAsync(orange, CancellationToken.None)));

        Assert.Contains("handed out", refused.Message);
    }

    [Fact]
    public async Task Somebody_who_received_a_belt_but_is_no_longer_invited_is_still_in_the_list()
    {
        var w = await BuildAsync();
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));

        // The event on Spond is changed so that Anna is not invited any more: the invitation goes, what was handed out stays.
        Spond.SetEvents(Dojo, _club.Event(Past, "Gürtelprüfung", Day(-30), ("b", Rsvp.Accepted)));
        await _club.SyncAsync();

        var anna = (await Planning(s => s.GetMembersAsync(w.Past.Id, CancellationToken.None))).Single(m => m.Name == "Anna");
        Assert.Null(anna.Invitation);
        Assert.Equal(w.Yellow, anna.ReceivedBeltId);
    }

    // --- What the exam list shows --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exam_counts_who_was_invited_who_accepted_who_is_saved_and_who_received_a_belt()
    {
        var w = await BuildAsync();
        await SaveFor(w.Next.Id, [w.Members["b"], w.Members["d"]]);
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["a"], w.Yellow, CancellationToken.None));
        await Planning(s => s.SetResultAsync(w.Past.Id, w.Members["b"], w.Yellow, CancellationToken.None));

        var exams = await _club.QueryAsync(sp => sp.GetRequiredService<ExamService>().ListAsync(CancellationToken.None));

        var next = exams.Single(e => e.Id == w.Next.Id);
        var past = exams.Single(e => e.Id == w.Past.Id);
        Assert.Equal((2, 1, 2, 0), (next.InvitedCount, next.AttendedCount, next.PreSavedCount, next.BeltsAwardedCount)); // Anna (no answer) and Clara (accepted)
        Assert.Equal((3, 3, 0, 2), (past.InvitedCount, past.AttendedCount, past.PreSavedCount, past.BeltsAwardedCount)); // Anna, Ben and Dora accepted
    }
}
