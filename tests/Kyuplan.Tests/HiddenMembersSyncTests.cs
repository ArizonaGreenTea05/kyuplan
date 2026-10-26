using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>
/// A member a user hid: what the statistics service, the sync and the member service do with them. The sync keeps them up to date and
/// never changes whether they are hidden; everything else does not see them.
/// </summary>
public sealed class HiddenMembersSyncTests : IDisposable
{
    private static readonly MemberInfo Anna = Person("m-anna", "Anna Beck");
    private static readonly MemberInfo Ben = Person("m-ben", "Ben Koch");
    private static readonly MemberInfo Cara = Person("m-cara", "Cara Wolf");
    private static readonly MemberInfo Dora = Person("m-dora", "Dora Roth");

    private readonly SyncTestClub _club = new();

    public void Dispose() => _club.Dispose();

    private FakeSpondDataSource Spond => _club.Spond;

    /// <summary>Syncs the club (Anna, Ben and Cara, all invited to the exam and the training) and hides Anna.</summary>
    private async Task<int> ClubWithHiddenAnnaAsync()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo,
            _club.Event("training", Day(-3), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted), (Cara.Id, Rsvp.Declined)) with { Trainers = [Cara.Id] },
            _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Unanswered), (Cara.Id, Rsvp.Declined)));
        var exam = await _club.CreateExamAsync("exam");
        await _club.SyncAsync();
        await HideAsync(Anna.Id);
        return exam.Id;
    }

    private Task HideAsync(string spondId) => _club.RunAsync(async sp =>
    {
        var db = sp.GetRequiredService<PlannerDbContext>();
        await sp.GetRequiredService<MemberService>().HideAsync(await db.Members.Where(m => m.SpondId == spondId).Select(m => m.Id).SingleAsync(), CancellationToken.None);
    });

    private Task UnhideAsync(string spondId) => _club.RunAsync(async sp =>
    {
        var db = sp.GetRequiredService<PlannerDbContext>();
        await sp.GetRequiredService<MemberService>().UnhideAsync(await db.Members.IgnoreQueryFilters().Where(m => m.SpondId == spondId).Select(m => m.Id).SingleAsync(), CancellationToken.None);
    });

    private Task<List<Member>> AllMembersAsync() => _club.QueryAsync(sp =>
        sp.GetRequiredService<PlannerDbContext>().Members.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.Name).ToListAsync());

    /// <summary>The invitations of the exam by Spond id, hidden members' included.</summary>
    private Task<Dictionary<string, Rsvp>> AllInvitationsAsync(int examId) => _club.QueryAsync(async sp =>
        (await sp.GetRequiredService<PlannerDbContext>().ExamParticipants.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ExamId == examId).Select(p => new { p.Member.SpondId, p.Rsvp }).ToListAsync()).ToDictionary(p => p.SpondId, p => p.Rsvp));

    // --- What the statistics service tells ------------------------------------------------------------------

    [Fact]
    public async Task The_statistics_service_knows_nothing_of_a_hidden_member_but_the_sync_can_still_see_them()
    {
        await ClubWithHiddenAnnaAsync();

        var (groups, group, events, syncEvents) = await _club.QueryAsync(async sp =>
        {
            var statistics = sp.GetRequiredService<StatisticsService>();
            return (await statistics.GetGroupsAsync(CancellationToken.None), await statistics.GetGroupInfoAsync(Dojo, CancellationToken.None),
                await statistics.GetWindowEventsAsync(Dojo, CancellationToken.None), await statistics.GetWindowEventsAsync(Dojo, CancellationToken.None, includeHidden: true));
        });

        Assert.Equal(2, Assert.Single(groups).MemberCount);
        Assert.Equal([Ben.Id, Cara.Id], group.Members.Select(m => m.Id));
        Assert.All(events, e => Assert.DoesNotContain(Anna.Id, e.Responses.Keys));
        Assert.Equal(2, events.Single(e => e.Id == "training").Responses.Count);
        Assert.All(syncEvents, e => Assert.Contains(Anna.Id, e.Responses.Keys));
    }

    [Fact]
    public async Task A_hidden_trainer_is_no_trainer_of_the_event_any_more()
    {
        await ClubWithHiddenAnnaAsync();
        await HideAsync(Cara.Id);

        var training = (await _club.QueryAsync(sp => sp.GetRequiredService<StatisticsService>().GetWindowEventsAsync(Dojo, CancellationToken.None))).Single(e => e.Id == "training");

        Assert.Empty(training.Trainers ?? []);
        Assert.Equal([Ben.Id], training.Responses.Keys);
    }

    [Fact]
    public async Task The_attendance_statistics_of_a_group_leave_out_a_hidden_member_and_what_they_answered()
    {
        await ClubWithHiddenAnnaAsync();

        var attendance = await _club.QueryAsync(sp => sp.GetRequiredService<StatisticsService>().GetAttendanceAsync(
            new AttendanceFilter(Dojo, Day(-10), Day(0), null, 75, 1), Kyuplan.Api.Statistics.RatingBook.None(), CancellationToken.None));

        Assert.DoesNotContain(attendance.Members, m => m.MemberId == Anna.Id);
        Assert.Equal(1, attendance.Events.Single().Invited); // Ben; Cara hosts the training
    }

    // --- The sync -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sync_keeps_a_hidden_member_hidden_renames_them_and_keeps_their_invitations_up_to_date()
    {
        var examId = await ClubWithHiddenAnnaAsync();

        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck-Wolf"), Ben, Cara));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Declined), (Ben.Id, Rsvp.Unanswered), (Cara.Id, Rsvp.Declined)));
        await _club.SyncAsync();
        await _club.SyncAsync(); // nothing is added twice

        var anna = (await AllMembersAsync()).Single(m => m.SpondId == Anna.Id);
        Assert.True(anna.Hidden);
        Assert.Null(anna.RemovedAt);
        Assert.Equal("Anna Beck-Wolf", anna.Name);
        Assert.DoesNotContain(await _club.MembersAsync(), m => m.SpondId == Anna.Id);
        Assert.Equal(Rsvp.Declined, (await AllInvitationsAsync(examId))[Anna.Id]);
        Assert.Equal(3, (await AllInvitationsAsync(examId)).Count);
    }

    [Fact]
    public async Task Syncing_the_exam_again_does_not_trip_over_the_invitation_of_a_hidden_member()
    {
        var examId = await ClubWithHiddenAnnaAsync();
        var exam = await _club.QueryAsync(sp => sp.GetRequiredService<PlannerDbContext>().Exams.AsNoTracking().SingleAsync(e => e.Id == examId));

        await _club.RunAsync(sp => sp.GetRequiredService<SpondSync>().SyncExamAsync(exam, CancellationToken.None));

        Assert.Equal(3, (await AllInvitationsAsync(examId)).Count);
    }

    [Fact]
    public async Task A_hidden_member_is_not_among_the_members_of_an_exam_nor_suggested_for_it_until_made_visible()
    {
        var examId = await ClubWithHiddenAnnaAsync();

        var members = await _club.QueryAsync(sp => sp.GetRequiredService<ExamPlanningService>().GetMembersAsync(examId, CancellationToken.None));
        var suggested = await _club.QueryAsync(sp => sp.GetRequiredService<ExamPlanningService>().SuggestAsync(examId, null, CancellationToken.None));

        Assert.Equal(["Ben Koch", "Cara Wolf"], members.Select(m => m.Name));
        Assert.DoesNotContain(suggested.Members, m => m.Name == "Anna Beck");

        await UnhideAsync(Anna.Id);

        Assert.Equal(["Anna Beck", "Ben Koch", "Cara Wolf"], (await _club.QueryAsync(sp => sp.GetRequiredService<ExamPlanningService>().GetMembersAsync(examId, CancellationToken.None))).Select(m => m.Name));
        Assert.Contains((await _club.QueryAsync(sp => sp.GetRequiredService<ExamPlanningService>().SuggestAsync(examId, null, CancellationToken.None))).Members, m => m.Name == "Anna Beck");
    }

    [Fact]
    public async Task A_hidden_member_the_source_no_longer_lists_is_removed_like_any_other_and_stays_hidden_if_they_return()
    {
        await ClubWithHiddenAnnaAsync();
        _club.SyncSettings.MaxRemovedShare = 1; // two of three go

        Spond.SetGroups(Group(Dojo, Cara));
        await _club.SyncConfirmedAsync();

        var anna = (await AllMembersAsync()).Single(m => m.SpondId == Anna.Id);
        Assert.Equal(PinnedNow, anna.RemovedAt);
        Assert.True(anna.Hidden);
        Assert.Empty(await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync(Dojo, CancellationToken.None))); // gone is not hidden

        Spond.SetGroups(Group(Dojo, Anna, Cara));
        await _club.SyncAsync();

        Assert.Null((await AllMembersAsync()).Single(m => m.SpondId == Anna.Id).RemovedAt);
        Assert.Equal(["Anna Beck"], (await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync(Dojo, CancellationToken.None))).Select(m => m.Name));
    }

    [Fact]
    public async Task The_hidden_members_of_a_group_the_account_does_not_see_any_more_are_removed_with_the_others()
    {
        await ClubWithHiddenAnnaAsync();

        Spond.SetGroups(Group("g-other", Dora));
        await _club.SyncConfirmedAsync();

        Assert.All((await AllMembersAsync()).Where(m => m.GroupId == Dojo), m => Assert.NotNull(m.RemovedAt));
        Assert.Equal(PinnedNow, (await AllMembersAsync()).Single(m => m.SpondId == Anna.Id).RemovedAt);
    }

    [Fact]
    public async Task A_sync_that_would_lose_most_members_is_refused_also_when_the_ones_it_loses_are_hidden()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara, Dora));
        await _club.SyncAsync();
        await HideAsync(Anna.Id);
        await HideAsync(Ben.Id);
        await HideAsync(Cara.Id);
        _club.SyncSettings.ToleratedRemovedMembers = 0;

        Spond.SetGroups(Group(Dojo, Dora));

        await Assert.ThrowsAsync<Kyuplan.Api.Persistence.SyncRefusedException>(() => _club.SyncAsync());
        Assert.All(await AllMembersAsync(), m => Assert.Null(m.RemovedAt));
    }

    // --- The member service -------------------------------------------------------------------------------------

    [Fact]
    public async Task Hiding_and_showing_a_member_changes_nothing_but_whether_they_show()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        var white = await _club.AddBeltAsync("White");
        await _club.EditMemberAsync(Anna.Id, m =>
        {
            m.BeltId = white;
            m.StartedAt = new DateOnly(2024, 1, 2);
        });
        var before = (await _club.MembersAsync()).Single(m => m.SpondId == Anna.Id);

        await HideAsync(Anna.Id);
        var hidden = (await AllMembersAsync()).Single(m => m.SpondId == Anna.Id);
        await UnhideAsync(Anna.Id);
        var after = (await _club.MembersAsync()).Single(m => m.SpondId == Anna.Id);

        Assert.True(hidden.Hidden);
        Assert.False(after.Hidden);
        Assert.Equal((before.Name, before.BeltId, before.StartedAt), (after.Name, after.BeltId, after.StartedAt));
    }

    [Fact]
    public async Task A_hidden_member_cannot_be_hidden_again_and_a_visible_one_cannot_be_made_visible()
    {
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();
        var id = (await _club.MembersAsync()).Single().Id;
        Task Service(Func<MemberService, Task> action) => _club.RunAsync(sp => action(sp.GetRequiredService<MemberService>()));

        await Service(s => s.HideAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => Service(s => s.HideAsync(id, CancellationToken.None)));
        await Service(s => s.UnhideAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => Service(s => s.UnhideAsync(id, CancellationToken.None)));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => Service(s => s.UnhideAsync(999, CancellationToken.None)));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => Service(s => s.HideAsync(999, CancellationToken.None)));
    }

    [Fact]
    public async Task A_member_that_is_gone_from_the_source_cannot_be_made_visible()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await HideAsync(Anna.Id);
        var id = (await AllMembersAsync()).Single(m => m.SpondId == Anna.Id).Id;
        _club.SyncSettings.MaxRemovedShare = 1;
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncConfirmedAsync();

        await Assert.ThrowsAsync<MemberNotFoundException>(() => _club.RunAsync(sp => sp.GetRequiredService<MemberService>().UnhideAsync(id, CancellationToken.None)));
    }

    [Fact]
    public async Task The_hidden_list_is_by_name_and_can_be_narrowed_to_one_group()
    {
        Spond.SetGroups(Group(Dojo, Cara, Anna, Ben), Group("g-other", Dora));
        await _club.SyncAsync();
        foreach (var id in new[] { Cara.Id, Anna.Id, Dora.Id }) await HideAsync(id);

        var all = await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync(null, CancellationToken.None));
        var dojo = await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync(Dojo, CancellationToken.None));
        var blank = await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync("  ", CancellationToken.None));
        var none = await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListHiddenAsync("nowhere", CancellationToken.None));

        Assert.Equal(["Anna Beck", "Cara Wolf", "Dora Roth"], all.Select(m => m.Name));
        Assert.Equal(["Anna Beck", "Cara Wolf"], dojo.Select(m => m.Name));
        Assert.Equal(all, blank);
        Assert.Empty(none);
        Assert.Equal(["Ben Koch"], (await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ListAsync(Dojo, CancellationToken.None))).Select(m => m.Name));
    }

    [Fact]
    public async Task A_hidden_member_is_not_offered_for_a_user_and_is_released_with_the_other_members_of_the_user()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        var ids = (await _club.MembersAsync()).ToDictionary(m => m.SpondId, m => m.Id);
        await _club.RunAsync(sp => sp.GetRequiredService<MemberService>().SetUserMembersAsync(7, [ids[Anna.Id], ids[Ben.Id]], CancellationToken.None));
        await HideAsync(Anna.Id);

        Assert.Equal([ids[Ben.Id]], (await _club.QueryAsync(sp => sp.GetRequiredService<MemberService>().ChoicesAsync(CancellationToken.None))).Select(m => m.Id));
        await Assert.ThrowsAsync<MemberNotFoundException>(() => _club.RunAsync(sp => sp.GetRequiredService<MemberService>().SetUserMembersAsync(8, [ids[Anna.Id]], CancellationToken.None)));

        await _club.RunAsync(sp => sp.GetRequiredService<MemberService>().SetUserMembersAsync(7, [ids[Ben.Id]], CancellationToken.None));

        Assert.Null((await AllMembersAsync()).Single(m => m.SpondId == Anna.Id).UserId); // not still linked to somebody who cannot see them
        Assert.Equal(7, (await AllMembersAsync()).Single(m => m.SpondId == Ben.Id).UserId);
    }
}
