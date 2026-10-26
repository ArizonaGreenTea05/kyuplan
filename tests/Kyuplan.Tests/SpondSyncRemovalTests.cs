using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>
/// A member the data source no longer lists is hidden with everything about them and deleted for good only after the retention; the
/// limits (share, count, tolerated) decide which removals need a confirmation.
/// </summary>
public sealed class SpondSyncRemovalTests : IDisposable
{
    private static readonly MemberInfo Anna = Person("m-anna", "Anna Beck");
    private static readonly MemberInfo Ben = Person("m-ben", "Ben Koch");
    private static readonly MemberInfo Cara = Person("m-cara", "Cara Wolf");

    private readonly SyncTestClub _club = new();

    public void Dispose() => _club.Dispose();

    private FakeSpondDataSource Spond => _club.Spond;

    private static MemberInfo[] People(int count) => [.. Enumerable.Range(0, count).Select(i => Person($"p-{i}", $"Person {i:000}"))];

    /// <summary>Every member in the database, hidden ones included.</summary>
    private Task<List<Member>> AllMembersAsync() => _club.QueryAsync(sp =>
        sp.GetRequiredService<PlannerDbContext>().Members.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.Name).ToListAsync());

    /// <summary>Gives the member a payment, a comment, a rating and a result for a new exam, i.e. everything that hangs off a member.</summary>
    private async Task GiveAnnaDataAsync()
    {
        var orange = await _club.AddBeltAsync("Orange");
        await _club.QueryAsync(async sp =>
        {
            var db = sp.GetRequiredService<PlannerDbContext>();
            var member = await db.Members.SingleAsync(m => m.SpondId == "m-anna");
            var exam = new Exam { GroupId = Dojo, EventId = "e", Name = "Exam", Date = Day(10), CreatedAt = PinnedNow };
            db.Exams.Add(exam);
            await db.SaveChangesAsync();

            member.BeltId = orange;
            db.ExamPayments.Add(new ExamPayment { ExamId = exam.Id, MemberId = member.Id, AmountDue = 20, AmountPaid = 5 });
            db.ExamCandidates.Add(new ExamCandidate { ExamId = exam.Id, MemberId = member.Id });
            db.ExamResults.Add(new ExamResult { ExamId = exam.Id, MemberId = member.Id, BeltId = orange });
            db.Comments.Add(new Comment { MemberId = member.Id, Body = "Good kata", AuthorId = 1, AuthorName = "admin", CreatedAt = PinnedNow });
            db.MemberRatings.Add(new MemberRating { MemberId = member.Id, EventId = "ev", UserId = 1, UserName = "admin", Stars = 4, CreatedAt = PinnedNow, UpdatedAt = PinnedNow });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>How many rows hang off the members, with and without the filters of hidden members.</summary>
    private Task<(int Visible, int All)> RowsAsync() => _club.QueryAsync(async sp =>
    {
        var db = sp.GetRequiredService<PlannerDbContext>();
        var visible = await db.ExamPayments.CountAsync() + await db.ExamCandidates.CountAsync() + await db.ExamResults.CountAsync()
            + await db.Comments.CountAsync() + await db.MemberRatings.CountAsync();
        var all = await db.ExamPayments.IgnoreQueryFilters().CountAsync() + await db.ExamCandidates.IgnoreQueryFilters().CountAsync()
            + await db.ExamResults.IgnoreQueryFilters().CountAsync() + await db.Comments.IgnoreQueryFilters().CountAsync()
            + await db.MemberRatings.IgnoreQueryFilters().CountAsync();
        return (visible, all);
    });

    // --- soft delete ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_that_is_gone_is_hidden_and_keeps_everything_about_them()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();

        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        Assert.Equal(["m-ben"], (await _club.MembersAsync()).Select(m => m.SpondId));
        var all = await AllMembersAsync();
        Assert.Equal(PinnedNow, all.Single(m => m.SpondId == "m-anna").RemovedAt);
        Assert.Null(all.Single(m => m.SpondId == "m-ben").RemovedAt);
        Assert.Equal((0, 5), await RowsAsync());
    }

    [Fact]
    public async Task A_member_who_is_listed_again_is_back_with_everything_about_them()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck-Wolf"), Ben));
        await _club.SyncAsync();

        var anna = (await _club.MembersAsync()).Single(m => m.SpondId == "m-anna");
        Assert.Null(anna.RemovedAt);
        Assert.Equal("Anna Beck-Wolf", anna.Name);
        Assert.NotNull(anna.BeltId);
        Assert.Equal((5, 5), await RowsAsync());
        Assert.Equal(2, (await AllMembersAsync()).Count);
    }

    [Fact]
    public async Task A_hidden_member_stays_hidden_and_keeps_the_day_they_went_while_the_source_does_not_list_them()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        _club.Time.Advance(TimeSpan.FromDays(3));
        await _club.SyncAsync();

        Assert.Equal(PinnedNow, (await AllMembersAsync()).Single(m => m.SpondId == "m-anna").RemovedAt);
    }

    [Fact]
    public async Task A_hidden_member_is_deleted_for_good_with_everything_about_them_after_the_retention()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        _club.Time.Advance(TimeSpan.FromDays(29));
        await _club.SyncAsync();
        Assert.Equal(2, (await AllMembersAsync()).Count);
        Assert.Equal((0, 5), await RowsAsync());

        _club.Time.Advance(TimeSpan.FromDays(1));
        await _club.SyncAsync();

        Assert.Equal(["m-ben"], (await AllMembersAsync()).Select(m => m.SpondId));
        Assert.Equal((0, 0), await RowsAsync());
    }

    [Fact]
    public async Task Without_a_retention_a_gone_member_is_deleted_at_once()
    {
        _club.SyncSettings.RemovedMemberRetention = TimeSpan.Zero;
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();

        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        Assert.Equal(["m-ben"], (await AllMembersAsync()).Select(m => m.SpondId));
        Assert.Equal((0, 0), await RowsAsync());
    }

    [Fact]
    public async Task Switching_the_retention_off_deletes_the_members_that_are_still_hidden()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();
        Assert.Equal(2, (await AllMembersAsync()).Count);

        _club.SyncSettings.RemovedMemberRetention = TimeSpan.Zero;
        await _club.SyncAsync();

        Assert.Equal(["m-ben"], (await AllMembersAsync()).Select(m => m.SpondId));
        Assert.Equal((0, 0), await RowsAsync());
    }

    [Fact]
    public async Task The_members_of_a_group_that_is_gone_are_hidden_too()
    {
        Spond.SetGroups(Group("g-one", Anna), Group("g-two", Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group("g-two", Ben));
        await _club.SyncAsync();

        Assert.Equal(["m-ben"], (await _club.MembersAsync()).Select(m => m.SpondId));
        Assert.NotNull((await AllMembersAsync()).Single(m => m.SpondId == "m-anna").RemovedAt);
    }

    [Fact]
    public async Task A_hidden_member_keeps_their_invitation_and_gets_it_back_with_the_new_answer()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted), (Ben.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");
        Assert.Equal(2, (await _club.ParticipantsAsync(exam.Id)).Count);

        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();
        Assert.Equal(["m-ben"], (await _club.ParticipantsAsync(exam.Id)).Select(p => p.Member));
        Assert.Equal(2, await _club.QueryAsync(sp => sp.GetRequiredService<PlannerDbContext>().ExamParticipants.IgnoreQueryFilters().CountAsync()));

        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Declined), (Ben.Id, Rsvp.Accepted)));
        await _club.SyncAsync();

        var participants = await _club.ParticipantsAsync(exam.Id);
        Assert.Equal([("m-anna", Rsvp.Declined), ("m-ben", Rsvp.Accepted)], participants.Select(p => (p.Member, p.Rsvp)));
    }

    [Fact]
    public async Task Syncing_a_single_exam_brings_a_hidden_member_back_without_a_duplicate_invitation()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna, Ben));
        var stored = await _club.QueryAsync(sp => sp.GetRequiredService<PlannerDbContext>().Exams.Include(e => e.Participants).SingleAsync(e => e.Id == exam.Id));
        await _club.RunAsync(sp => sp.GetRequiredService<SpondSync>().SyncExamAsync(stored, CancellationToken.None));

        Assert.Equal(["m-anna"], (await _club.ParticipantsAsync(exam.Id)).Select(p => p.Member));
    }

    [Fact]
    public async Task A_refused_single_exam_sync_leaves_nothing_behind_in_the_context_that_is_saved_later()
    {
        _club.SyncSettings.ToleratedRemovedMembers = 0;
        _club.SyncSettings.MaxRemovedShare = 0.4;
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        Spond.SetEvents(Dojo, _club.Event("exam", Day(14), (Anna.Id, Rsvp.Accepted)));
        var exam = await _club.CreateExamAsync("exam");

        // Ben is gone (1 of 2 is more than 40 %), Anna is renamed and Cara is new: none of it may be saved by the refused sync.
        Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Renamed"), Cara));
        await _club.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<PlannerDbContext>();
            var stored = await db.Exams.SingleAsync(e => e.Id == exam.Id);
            await Assert.ThrowsAsync<SyncRefusedException>(() => sp.GetRequiredService<SpondSync>().SyncExamAsync(stored, CancellationToken.None));
            await db.SaveChangesAsync();
        });

        Assert.Equal(["Anna Beck", "Ben Koch"], (await _club.MembersAsync()).Select(m => m.Name).Order());
    }

    [Fact]
    public async Task A_belt_that_a_hidden_member_holds_cannot_be_deleted()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();
        var belt = (await AllMembersAsync()).Single(m => m.SpondId == "m-anna").BeltId!.Value;

        await Assert.ThrowsAsync<BeltInUseException>(() =>
            _club.RunAsync(sp => sp.GetRequiredService<BeltService>().DeleteAsync(belt, CancellationToken.None)));
    }

    [Fact]
    public async Task A_belt_that_a_hidden_member_received_at_an_exam_cannot_be_deleted()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        var belt = await _club.QueryAsync(async sp =>
        {
            var db = sp.GetRequiredService<PlannerDbContext>();
            var member = await db.Members.SingleAsync(m => m.SpondId == "m-anna");
            member.BeltId = null; // only the exam result refers to the belt
            await db.SaveChangesAsync();
            return (await db.ExamResults.SingleAsync()).BeltId;
        });
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        await Assert.ThrowsAsync<BeltInUseException>(() =>
            _club.RunAsync(sp => sp.GetRequiredService<BeltService>().DeleteAsync(belt, CancellationToken.None)));
    }

    [Fact]
    public async Task A_deleted_user_is_released_from_hidden_members_too()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await _club.EditMemberAsync(Anna.Id, m => m.UserId = 7);
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        await _club.RunAsync(sp => new MemberService(sp.GetRequiredService<PlannerDbContext>()).ReleaseUserAsync(7, CancellationToken.None));

        Assert.Null((await AllMembersAsync()).Single(m => m.SpondId == "m-anna").UserId);
    }

    [Fact]
    public async Task A_hidden_member_does_not_count_as_stored_for_the_limits()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        await _club.SyncAsync();
        Spond.SetGroups(Group(Dojo, Anna, Ben)); // one of three: tolerated
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, Anna)); // one of the two that are left: tolerated again, the hidden one is not counted
        await _club.SyncAsync();

        Assert.Equal(["m-anna"], (await _club.MembersAsync()).Select(m => m.SpondId));
    }

    [Fact]
    public async Task A_copy_of_the_database_keeps_the_hidden_members_and_what_hangs_off_them()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups(Group(Dojo, Ben));
        await _club.SyncAsync();

        await _club.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<PlannerDbContext>();
            var tables = new Dictionary<string, List<System.Text.Json.Nodes.JsonObject>>();
            foreach (var type in Kyuplan.Api.Backup.DatabaseCopier.Tables(db))
                tables[Kyuplan.Api.Backup.DatabaseCopier.NameOf(type)] = await Kyuplan.Api.Backup.DatabaseCopier.ReadAsync(db, type, CancellationToken.None);

            Assert.Equal(2, tables["Member"].Count); // the hidden one is in it
            await Kyuplan.Api.Backup.DatabaseCopier.ReplaceAsync(db, tables, CancellationToken.None);
        });

        Assert.Equal(["m-ben"], (await _club.MembersAsync()).Select(m => m.SpondId));
        Assert.NotNull((await AllMembersAsync()).Single(m => m.SpondId == "m-anna").RemovedAt);
        Assert.Equal((0, 5), await RowsAsync());
    }

    // --- limits ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_only_member_of_a_group_can_leave_without_a_confirmation()
    {
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo));
        await _club.SyncAsync();

        Assert.Empty(await _club.MembersAsync());
    }

    [Fact]
    public async Task The_only_member_of_a_group_needs_a_confirmation_when_nothing_is_tolerated()
    {
        _club.SyncSettings.ToleratedRemovedMembers = 0;
        Spond.SetGroups(Group(Dojo, Anna));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo));
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
        Assert.Single(await _club.MembersAsync());
    }

    [Theory]
    [InlineData(1, 2, true)] // 2 of 2 is more than the one that is tolerated
    [InlineData(2, 2, false)]
    [InlineData(3, 3, false)]
    public async Task Up_to_the_tolerated_number_of_members_leave_whatever_the_share(int tolerated, int stored, bool refused)
    {
        _club.SyncSettings.ToleratedRemovedMembers = tolerated;
        Spond.SetGroups(Group(Dojo, People(stored)));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo));
        if (refused) await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
        else await _club.SyncAsync();

        Assert.Equal(refused ? stored : 0, (await _club.MembersAsync()).Count);
    }

    [Theory]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public async Task More_than_the_largest_number_of_members_is_refused_even_when_it_is_a_small_share(int removed, bool applied)
    {
        var people = People(100);
        Spond.SetGroups(Group(Dojo, people));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, people[removed..]));
        if (applied) await _club.SyncAsync();
        else
        {
            var refused = await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
            Assert.Contains("Sync:MaxRemovedMembers", refused.Message);
        }

        Assert.Equal(applied ? 100 - removed : 100, (await _club.MembersAsync()).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task The_count_limit_is_off_at_zero_or_less(int max)
    {
        _club.SyncSettings.MaxRemovedMembers = max;
        var people = People(100);
        Spond.SetGroups(Group(Dojo, people));
        await _club.SyncAsync();

        Spond.SetGroups(Group(Dojo, people[30..]));
        await _club.SyncAsync();

        Assert.Equal(70, (await _club.MembersAsync()).Count);
    }

    [Fact]
    public async Task The_count_limit_is_checked_for_the_whole_club_not_only_per_group()
    {
        var one = People(40);
        var two = One(40);
        Spond.SetGroups(Group("g-one", one), Group("g-two", two));
        await _club.SyncAsync();

        Spond.SetGroups(Group("g-one", one[15..]), Group("g-two", two[15..])); // 15 of 40 in each, 30 in all
        var refused = await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        Assert.Contains("all groups", refused.Message);
        Assert.Equal(80, (await _club.MembersAsync()).Count);

        static MemberInfo[] One(int count) => [.. Enumerable.Range(0, count).Select(i => Person($"q-{i}", $"Other {i:000}"))];
    }

    [Fact]
    public async Task A_confirmed_run_hides_what_would_be_refused_and_it_can_be_undone()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        await _club.SyncAsync();
        await GiveAnnaDataAsync();
        Spond.SetGroups();
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        await _club.SyncConfirmedAsync();
        Assert.Empty(await _club.MembersAsync());
        Assert.Equal((0, 5), await RowsAsync());

        Spond.SetGroups(Group(Dojo, Anna, Ben, Cara));
        await _club.SyncAsync(); // adding back is no removal

        Assert.Equal(3, (await _club.MembersAsync()).Count);
        Assert.Equal((5, 5), await RowsAsync());
    }

    // --- sync status ----------------------------------------------------------------------------------------------

    private Task<SyncStatusDto> StatusAsync() =>
        _club.QueryAsync(sp => Task.FromResult(sp.GetRequiredService<SyncStatusTracker>().Snapshot(true, TimeSpan.Zero)));

    [Fact]
    public async Task A_refused_run_is_marked_as_one_that_can_be_confirmed_until_another_run_ends()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Assert.False((await StatusAsync()).RemovalRefused);

        Spond.SetGroups();
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);
        var refused = await StatusAsync();
        Assert.True(refused.RemovalRefused);
        Assert.True(refused.LastFailure!.RemovalRefused);

        await _club.SyncConfirmedAsync();
        var done = await StatusAsync();
        Assert.False(done.RemovalRefused);
        Assert.True(done.Recent[0].Succeeded);
    }

    [Fact]
    public async Task A_run_that_failed_for_another_reason_cannot_be_confirmed()
    {
        Spond.SetGroups(Group(Dojo, Anna, Ben));
        await _club.SyncAsync();
        Spond.SetGroups();
        await Assert.ThrowsAsync<SyncRefusedException>(_club.SyncAsync);

        Spond.Unreachable = true;
        await Assert.ThrowsAsync<DataSourceUnavailableException>(_club.SyncAsync);

        var status = await StatusAsync();
        Assert.False(status.RemovalRefused);
        Assert.False(status.LastFailure!.RemovalRefused);
        Assert.Equal(2, status.ConsecutiveFailures);
    }
}
