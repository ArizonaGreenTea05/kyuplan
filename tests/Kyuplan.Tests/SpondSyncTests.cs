using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>
/// The members and the invitations of exams in the database follow Spond: what Spond has is added, what changed is changed, what
/// is gone is removed, and what Spond does not know (belts, a start day that is already there) is never touched.
/// </summary>
public sealed class SpondSyncTests : IDisposable
{
    private static readonly MemberInfo Anna = Person("m-anna", "Anna Beck");
    private static readonly MemberInfo Ben = Person("m-ben", "Ben Koch");
    private static readonly MemberInfo Cara = Person("m-cara", "Cara Wolf");
    private static readonly MemberInfo Dora = Person("m-dora", "Dora Fuchs");

    private readonly SyncTestClub _club = new();

    public void Dispose() => _club.Dispose();

    private FakeSpondDataSource Spond => _club.Spond;

    private static DateOnly?[] StartDays(IEnumerable<Member> members) => members.Select(m => m.StartedAt).ToArray();

    // ----- members -----

    [Fact]
    public async Task Members_of_Spond_are_added_without_a_belt_and_without_a_start_day()
    {
        Spond.SetGroups(Group(Dojo, Ben, Anna));

        await _club.SyncAsync();

        var members = await _club.MembersAsync();
        Assert.Equal(["Anna Beck", "Ben Koch"], members.Select(m => m.Name));
        Assert.Equal(["m-anna", "m-ben"], members.Select(m => m.SpondId));
        Assert.All(members, m =>
        {
            Assert.Equal(Dojo, m.GroupId);
            Assert.Null(m.BeltId);
            Assert.Null(m.StartedAt);
            Assert.Equal(PinnedNow, m.CreatedAt);
        });
    }

    [Fact]
    public async Task Members_of_the_unregistered_subgroup_are_marked_and_follow_their_subgroup()
    {
        SubGroupInfo[] subGroups = [new("sg-new", "Not registered")];
        _club.SpondSettings.UnregisteredSubGroup = "not REGISTERED";
        Spond.SetGroups(Group(Dojo, subGroups, Person(Anna.Id, Anna.Name, null, "sg-new"), Ben));

        await _club.SyncAsync();

        Assert.Equal([false, true], (await _club.MembersAsync()).Select(m => m.Registered));

        Spond.SetGroups(Group(Dojo, subGroups, Anna, Person(Ben.Id, Ben.Name, null, "sg-new")));
        await _club.SyncAsync();

        Assert.Equal([true, false], (await _club.MembersAsync()).Select(m => m.Registered));
    }

    [Fact]
    public async Task Everybody_is_registered_without_a_setting_or_a_matching_subgroup()
    {
        SubGroupInfo[] subGroups = [new("sg-new", "Not registered")];
        Spond.SetGroups(Group(Dojo, subGroups, Person(Anna.Id, Anna.Name, null, "sg-new")));
        await _club.SyncAsync();
        Assert.All(await _club.MembersAsync(), m => Assert.True(m.Registered));

        _club.SpondSettings.UnregisteredSubGroup = "Other";
        await _club.SyncAsync();
        Assert.All(await _club.MembersAsync(), m => Assert.True(m.Registered));
    }

    [Fact]
    public async Task A_member_is_only_added_once_and_a_new_name_is_followed()
    {
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();
        await _club.SyncAsync();
        var before = Assert.Single(await _club.MembersAsync());

        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck-Meier")));
        await _club.SyncAsync();

        var after = Assert.Single(await _club.MembersAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("Anna Beck-Meier", after.Name);
    }

    [Fact]
    public async Task Members_that_Spond_no_longer_lists_are_removed_and_the_others_keep_what_a_user_set()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        var orange = await _club.AddBeltAsync("Orange");
        await _club.EditMemberAsync(Anna.Id, m => { m.BeltId = orange; m.StartedAt = Day(-400); });

        Spond.SetGroups(Group(Dojo, Anna, Dora));
        await _club.SyncAsync();

        var members = await _club.MembersAsync();
        Assert.Equal(["m-anna", "m-dora"], members.Select(m => m.SpondId));
        Assert.Equal(orange, members[0].BeltId);
        Assert.Equal(Day(-400), members[0].StartedAt);
        Assert.Null(members[1].BeltId);
    }

    [Fact]
    public async Task The_members_of_a_group_the_account_does_not_see_any_more_are_removed_and_other_groups_stay()
    {
        Spond.SetGroups(Group("g-one", Anna), Group("g-two", Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group("g-two", Ben));
        await _club.SyncAsync();

        var member = Assert.Single(await _club.MembersAsync());
        Assert.Equal(("g-two", "m-ben"), (member.GroupId, member.SpondId));
    }

    // --- The removal guard: a successful but wrong answer must not wipe the members and what hangs off them ---------

    private static readonly MemberInfo Emil = Person("m-emil", "Emil Rau");

    private async Task<int> GiveAnnaABeltAsync()
    {
        var orange = await _club.AddBeltAsync("Orange");
        await _club.EditMemberAsync(Anna.Id, m => m.BeltId = orange);
        return orange;
    }

    [Fact]
    public async Task An_empty_list_of_groups_is_refused_and_nothing_is_removed()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        var orange = await GiveAnnaABeltAsync();

        Spond.SetGroups();
        var refused = await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        Assert.Contains("2 of the 2", refused.Message);
        Assert.Contains("confirmRemoval=true", refused.Message);
        var members = await _club.MembersAsync();
        Assert.Equal(2, members.Count);
        Assert.Equal(orange, members.Single(m => m.SpondId == Anna.Id).BeltId);
    }

    [Fact]
    public async Task A_refused_run_is_a_failed_run_in_the_sync_status()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Spond.SetGroups();

        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        var status = await _club.QueryAsync(sp => Task.FromResult(sp.GetRequiredService<SyncStatusTracker>().Snapshot(true, TimeSpan.Zero)));
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Contains("Sync:MaxRemovedShare", status.LastFailure!.Error);
    }

    [Fact]
    public async Task More_than_half_of_the_members_missing_is_refused_but_exactly_half_is_applied()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara, Dora));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna));
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
        Assert.Equal(4, (await _club.MembersAsync()).Count);

        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Assert.Equal(2, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task A_group_that_loses_most_of_its_members_is_refused_even_when_the_club_as_a_whole_loses_little()
    {
        var big = Enumerable.Range(0, 10).Select(i => Person($"big-{i}", $"Big {i}")).ToArray();
        Spond.SetGroups(Group("g-small", Anna, Ben, Cara), Group("g-big", big));
        await _club.SyncAsync();

        Spond.SetGroups(Group("g-small", Anna), Group("g-big", big)); // 2 of 13 overall, 2 of 3 in one group
        var refused = await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        Assert.Contains("Group g-small", refused.Message);
        Assert.Equal(13, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task A_group_that_disappears_beside_a_big_one_is_applied_while_it_stays_under_the_limit()
    {
        var big = Enumerable.Range(0, 10).Select(i => Person($"big-{i}", $"Big {i}")).ToArray();
        Spond.SetGroups(Group("g-small", Anna, Ben), Group("g-big", big));
        await _club.SyncAsync();

        Spond.SetGroups(Group("g-big", big));
        await _club.SyncAsync();

        Assert.Equal(10, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task A_confirmed_run_applies_what_would_be_refused()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Spond.SetGroups();
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        await _club.SyncConfirmedAsync();

        Assert.Empty(await _club.MembersAsync());
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(2.0, true)]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    public async Task The_share_can_be_raised_to_switch_the_guard_off_or_lowered_to_refuse_any_removal(double share, bool applied)
    {
        _club.SyncSettings.MaxRemovedShare = share;
        _club.SyncSettings.ToleratedRemovedMembers = 0; // one of two is a removal the share decides about
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna)); // one of two
        if (share < 0.5) await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
        else await _club.SyncAsync();

        Assert.Equal(applied ? 1 : 2, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task Members_that_are_added_while_others_go_count_against_the_stored_ones_only()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna, Cara, Dora, Emil)); // Ben gone (1 of 2), four listed
        await _club.SyncAsync();

        Assert.Equal(4, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task A_first_sync_into_an_empty_database_and_an_empty_source_never_refuse()
    {
        await _club.SyncAsync();
        Assert.Empty(await _club.MembersAsync());

        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();
        Assert.Single(await _club.MembersAsync());
    }

    [Fact]
    public async Task A_group_listed_twice_does_not_break_the_guard()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben), Group(Dojo, Anna, Ben));

        await _club.SyncAsync();

        Assert.Equal(2, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task Creating_an_exam_does_not_remove_members_the_source_suddenly_lacks_and_the_exam_is_still_created()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted)));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna)); // two of three missing
        var exam = await _club.CreateExamAsync("exam");

        Assert.NotEqual(0, exam.Id);
        Assert.Equal(3, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task Syncing_one_exam_refuses_a_group_that_lost_most_of_its_members()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");
        Spond.SetGroups(Group(Dojo, Anna));

        var stored = await _club.QueryAsync(sp => sp.GetRequiredService<PlannerDbContext>().Exams.SingleAsync(e => e.Id == exam.Id));
        await Assert.ThrowsAsync<SyncRefusedException>(() =>
            _club.RunAsync(sp => sp.GetRequiredService<SpondSync>().SyncExamAsync(stored, CancellationToken.None)));

        Assert.Equal(3, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task The_same_person_in_two_groups_is_a_member_of_each()
    {
        // Spond ids are per group, so the two cannot be told to be one person.
        Spond.SetGroups(Group("g-one", Person("one-anna", "Anna Beck")), Group("g-two", Person("two-anna", "Anna Beck")));

        await _club.SyncAsync();

        Assert.Equal(["g-one", "g-two"], (await _club.MembersAsync()).Select(m => m.GroupId).Order());
    }

    [Fact]
    public async Task Nothing_is_removed_when_Spond_cannot_be_read()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();

        Spond.Unreachable = true;
        await Assert.ThrowsAsync<DataSourceUnavailableException>(_club.SyncAsync);

        Assert.Equal(2, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task Members_are_synced_even_when_the_events_cannot_be_read()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.EventsUnreachable = true;

        await _club.SyncAsync();

        var members = await _club.MembersAsync();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Null(m.StartedAt));
    }

    // ----- the day a member started -----

    [Fact]
    public async Task A_member_started_on_the_day_of_the_first_event_they_attended()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo,
            _club.Event("e-3", Day(-20), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)),
            _club.Event("e-1", Day(-40), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined), (Cara.Id, Rsvp.Unanswered)),
            _club.Event("e-2", Day(-30), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted), (Cara.Id, Rsvp.Other)));

        await _club.SyncAsync();

        // Anna came at once, Ben only declined the first one, Cara never came.
        Assert.Equal([Day(-40), Day(-30), null], StartDays(await _club.MembersAsync()));
    }

    [Fact]
    public async Task Events_that_were_cancelled_or_have_not_taken_place_yet_do_not_count()
    {
        Spond.SetGroups(Group(Dojo, Anna));
        Spond.SetEvents(Dojo,
            _club.Event("cancelled", Day(-10), (Anna.Id, Rsvp.Accepted)) with { Cancelled = true },
            _club.Event("this-evening", Day(0), (Anna.Id, Rsvp.Accepted)), // it is noon, the training is at 18:00
            _club.Event("tomorrow", Day(1), (Anna.Id, Rsvp.Accepted)));

        await _club.SyncAsync();

        Assert.Null(Assert.Single(await _club.MembersAsync()).StartedAt);
    }

    [Fact]
    public async Task The_start_day_is_set_as_soon_as_it_is_known()
    {
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();
        Assert.Null(Assert.Single(await _club.MembersAsync()).StartedAt);

        Spond.SetEvents(Dojo, _club.Event("e-1", Day(-3), (Anna.Id, Rsvp.Accepted)));
        await _club.SyncAsync();

        Assert.Equal(Day(-3), Assert.Single(await _club.MembersAsync()).StartedAt);
    }

    [Fact]
    public async Task A_start_day_that_is_there_is_never_changed()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("e-1", Day(-5), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)));
        await _club.SyncAsync();
        await _club.EditMemberAsync(Ben.Id, m => m.StartedAt = Day(-900)); // set by hand

        Spond.SetEvents(Dojo,
            _club.Event("e-0", Day(-50), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)), // an earlier event turns up
            _club.Event("e-1", Day(-5), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)));
        await _club.SyncAsync();

        Assert.Equal([Day(-5), Day(-900)], StartDays(await _club.MembersAsync()));
    }

    // ----- exams -----

    [Fact]
    public async Task A_new_exam_records_who_was_invited_and_what_they_answered()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara, Dora));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined), (Cara.Id, Rsvp.Unanswered)));

        var exam = await _club.CreateExamAsync("exam");

        // Dora was not invited. Nobody had synced before: creating the exam brought the members in.
        Assert.Equal(
            [(Anna.Id, Rsvp.Accepted, true), (Ben.Id, Rsvp.Declined, false), (Cara.Id, Rsvp.Unanswered, false)],
            await _club.ParticipantsAsync(exam.Id));
        Assert.Equal(4, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task The_invitations_follow_what_Spond_says_now()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined), (Cara.Id, Rsvp.Unanswered)));
        var exam = await _club.CreateExamAsync("exam");

        // Ben changed his mind, Cara was taken off the invitation, Dora joined the club and was invited. The exam was renamed.
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara, Dora));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(15), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted), (Dora.Id, Rsvp.Unanswered))
            with { Name = "Gürtelprüfung" });
        await _club.SyncAsync();

        Assert.Equal(
            [(Anna.Id, Rsvp.Accepted, true), (Ben.Id, Rsvp.Accepted, true), (Dora.Id, Rsvp.Unanswered, false)],
            await _club.ParticipantsAsync(exam.Id));
        var stored = await _club.QueryAsync(sp => sp.GetRequiredService<PlannerDbContext>().Exams.AsNoTracking().SingleAsync());
        Assert.Equal(("Gürtelprüfung", Day(15)), (stored.Name, stored.Date));
    }

    [Fact]
    public async Task A_member_who_left_Spond_is_no_longer_in_the_exam_but_the_others_stay()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");

        // The event still lists Ben (Spond may take a while to forget him there); he is not a member any more.
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();

        Assert.Equal([(Anna.Id, Rsvp.Accepted, true)], await _club.ParticipantsAsync(exam.Id));
    }

    [Fact]
    public async Task An_exam_whose_event_cannot_be_found_keeps_its_invitations()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined)));
        var exam = await _club.CreateExamAsync("exam");

        // The event is outside what can be searched, e.g. because it is long past: whatever was recorded stays.
        Spond.SetEvents(Dojo);
        await _club.SyncAsync();

        Assert.Equal(2, (await _club.ParticipantsAsync(exam.Id)).Count);
    }

    [Fact]
    public async Task The_invitations_stay_while_the_events_cannot_be_read()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined)));
        var exam = await _club.CreateExamAsync("exam");

        Spond.EventsUnreachable = true;
        await _club.SyncAsync();

        Assert.Equal(2, (await _club.ParticipantsAsync(exam.Id)).Count);
    }

    [Fact]
    public async Task An_exam_that_is_pointed_at_another_event_gets_the_invitations_of_that_event()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        Spond.SetEvents(Dojo,
            _club.Event("first", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)),
            _club.Event("second", Day(28), (Ben.Id, Rsvp.Declined), (Cara.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("first");

        await _club.RunAsync(sp => sp.GetRequiredService<ExamService>().UpdateAsync(exam.Id, new("g-dojo", "second"), CancellationToken.None));

        Assert.Equal([(Ben.Id, Rsvp.Declined, false), (Cara.Id, Rsvp.Accepted, true)], await _club.ParticipantsAsync(exam.Id));
    }

    [Fact]
    public async Task Deleting_an_exam_deletes_its_invitations_but_not_the_members()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");

        await _club.RunAsync(sp => sp.GetRequiredService<ExamService>().DeleteAsync(exam.Id, CancellationToken.None));

        Assert.Empty(await _club.ParticipantsAsync(exam.Id));
        Assert.Equal(2, (await _club.MembersAsync()).Count);
    }

    // ----- birth dates -----

    [Fact]
    public async Task A_birth_date_that_Spond_has_is_taken_over_and_a_member_without_one_has_none()
    {
        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck", new DateOnly(2014, 5, 3)), Ben));

        await _club.SyncAsync();

        var members = await _club.MembersAsync();
        Assert.Equal(new DateOnly(2014, 5, 3), members.Single(m => m.SpondId == Anna.Id).BirthDate);
        Assert.Null(members.Single(m => m.SpondId == Ben.Id).BirthDate);
    }

    [Fact]
    public async Task A_birth_date_that_is_there_is_never_changed_whoever_set_it_but_a_missing_one_is_filled_in_later()
    {
        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck", new DateOnly(2014, 5, 3)), Ben));
        await _club.SyncAsync();
        await _club.EditMemberAsync(Ben.Id, m => m.BirthDate = new DateOnly(2010, 1, 1)); // set by hand

        // Spond now says something else about Anna, and has a date for Ben.
        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck", new DateOnly(2013, 1, 1)), Person(Ben.Id, Ben.Name, new DateOnly(2011, 2, 2))));
        await _club.SyncAsync();

        var members = await _club.MembersAsync();
        Assert.Equal(new DateOnly(2014, 5, 3), members.Single(m => m.SpondId == Anna.Id).BirthDate);
        Assert.Equal(new DateOnly(2010, 1, 1), members.Single(m => m.SpondId == Ben.Id).BirthDate);

        // A member who had none is given the one Spond has later on.
        await _club.EditMemberAsync(Ben.Id, m => m.BirthDate = null);
        await _club.SyncAsync();

        Assert.Equal(new DateOnly(2011, 2, 2), (await _club.MembersAsync()).Single(m => m.SpondId == Ben.Id).BirthDate);
    }

    [Theory]
    [InlineData("1899-12-31")] // nobody is that old
    [InlineData("2026-10-14")] // not born yet: tomorrow
    public async Task A_birth_date_that_makes_no_sense_is_not_taken_over(string day)
    {
        Spond.SetGroups(Group(Dojo, Person(Anna.Id, "Anna Beck", DateOnly.Parse(day))));

        await _club.SyncAsync();

        Assert.Null(Assert.Single(await _club.MembersAsync()).BirthDate);
    }

    [Fact]
    public async Task An_exam_is_created_even_when_its_invitations_cannot_be_recorded_and_the_next_sync_does_it()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Declined)));

        // Creating an exam asks for the groups once to find its event; the second ask (for the invitations) fails.
        Spond.UnreachableFromGroupsCall = Spond.GroupsCalls + 2;
        var exam = await _club.CreateExamAsync("exam");

        Assert.NotEqual(0, exam.Id);
        Assert.Empty(await _club.ParticipantsAsync(exam.Id));

        Spond.UnreachableFromGroupsCall = 0;
        await _club.SyncAsync();

        Assert.Equal(2, (await _club.ParticipantsAsync(exam.Id)).Count);
    }
}
