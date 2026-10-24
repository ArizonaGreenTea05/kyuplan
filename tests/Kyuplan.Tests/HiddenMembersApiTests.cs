using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// A hidden member is ignored by the statistics and shown nowhere; only the list of hidden members (<c>members.hidden</c>) shows them and
/// makes them visible again. Against the real API and the demo club, whose members the sync copies into the database.
/// </summary>
public sealed class HiddenMembersApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";

    /// <summary>The demo club's events are generated around "today", so the clock is pinned: 13 October 2026, 12:00 in Germany.</summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly From = new(2026, 4, 13);
    private static readonly DateOnly To = new(2026, 10, 13);

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(PinnedNow)));
        });

    /// <summary>The api of a signed-in administrator, after a sync that has brought the demo club's members into the database.</summary>
    private static async Task<PlannerApiClient> SyncedAsync(WebApplicationFactory<Program> host)
    {
        var api = new PlannerApiClient(host.CreateSignedInClient());
        await api.RunSyncAsync();
        return api;
    }

    private static async Task<int> IdOfAsync(WebApplicationFactory<Program> host, string spondId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Members.Where(m => m.SpondId == spondId).Select(m => m.Id).SingleAsync();
    }

    private static AttendanceQuery Query => new(Group, From, To, MinInvitedEvents: 1);

    // --- Hiding and making visible again -----------------------------------------------------------------------

    [Fact]
    public async Task A_hidden_member_is_in_no_list_but_the_hidden_one_and_is_back_once_made_visible()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var members = await api.GetMembersAsync(Group);
        var anna = members[0];

        await api.HideMemberAsync(anna.Id);

        Assert.DoesNotContain(await api.GetMembersAsync(Group), m => m.Id == anna.Id);
        Assert.Equal(members.Count - 1, (await api.GetMembersAsync(Group)).Count);
        Assert.Equal([anna.Id], (await api.GetHiddenMembersAsync(Group)).Select(m => m.Id));
        Assert.DoesNotContain(await api.GetHiddenMembersAsync("another-group"), m => m.Id == anna.Id);

        var back = await api.UnhideMemberAsync(anna.Id);

        Assert.Equal(anna, back);
        Assert.Equal(members, await api.GetMembersAsync(Group));
        Assert.Empty(await api.GetHiddenMembersAsync(Group));
    }

    [Fact]
    public async Task What_is_hidden_cannot_be_hidden_again_and_what_is_visible_cannot_be_made_visible_or_is_unknown()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var member = (await api.GetMembersAsync(Group))[0];
        await api.HideMemberAsync(member.Id);

        var again = await Assert.ThrowsAsync<PlannerApiException>(() => api.HideMemberAsync(member.Id));
        var unknownHide = await Assert.ThrowsAsync<PlannerApiException>(() => api.HideMemberAsync(999_999));
        var unknownUnhide = await Assert.ThrowsAsync<PlannerApiException>(() => api.UnhideMemberAsync(999_999));
        await api.UnhideMemberAsync(member.Id);
        var visible = await Assert.ThrowsAsync<PlannerApiException>(() => api.UnhideMemberAsync(member.Id));

        Assert.All([again, unknownHide, unknownUnhide, visible], e => Assert.Equal(HttpStatusCode.NotFound, e.StatusCode));
    }

    [Fact]
    public async Task A_hidden_member_cannot_be_changed_or_commented_on_until_they_are_visible_and_keep_what_was_written()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var member = (await api.GetMembersAsync(Group))[0];
        await api.CreateCommentAsync(member.Id, new CommentInputDto("Good kata", []));

        await api.HideMemberAsync(member.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<PlannerApiException>(() => api.GetCommentsAsync(member.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<PlannerApiException>(() => api.UpdateMemberAsync(member.Id, new MemberInputDto(null, null)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateCommentAsync(member.Id, new CommentInputDto("More", [])))).StatusCode);
        Assert.Equal(0, (await api.GetHiddenMembersAsync(Group)).Single().CommentCount); // the comments are no part of what shows of a hidden member

        var back = await api.UnhideMemberAsync(member.Id);

        Assert.Equal(1, back.CommentCount);
        Assert.Equal(["Good kata"], (await api.GetCommentsAsync(member.Id)).Select(c => c.Body));
    }

    [Fact]
    public async Task The_belt_of_a_hidden_member_still_blocks_deleting_it()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var white = await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var member = (await api.GetMembersAsync(Group))[0];
        await api.UpdateMemberAsync(member.Id, new MemberInputDto(white.Id, null));
        await api.HideMemberAsync(member.Id);

        var refused = await Assert.ThrowsAsync<PlannerApiException>(() => api.DeleteBeltAsync(white.Id));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    // --- Ignored by the statistics and shown nowhere -----------------------------------------------------------

    [Fact]
    public async Task A_hidden_member_is_ignored_by_the_statistics_and_counted_again_once_visible()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var before = await api.GetAttendanceAsync(Query);
        var person = before.Members[0];
        var id = await IdOfAsync(host, person.MemberId);
        var groups = await api.GetGroupsAsync();

        await api.HideMemberAsync(id);
        var during = await api.GetAttendanceAsync(Query);

        Assert.DoesNotContain(during.Members, m => m.MemberId == person.MemberId);
        Assert.Equal(before.Summary.MemberCount - 1, during.Summary.MemberCount);
        Assert.Equal(before.Events.Sum(e => e.Invited) - person.Invited, during.Events.Sum(e => e.Invited));
        Assert.Equal(groups.Single(g => g.Id == Group).MemberCount - 1, (await api.GetGroupsAsync()).Single(g => g.Id == Group).MemberCount);

        await api.UnhideMemberAsync(id);

        Assert.Equal(before.Members.Select(m => (m.MemberId, m.Invited, m.Attended)), (await api.GetAttendanceAsync(Query)).Members.Select(m => (m.MemberId, m.Invited, m.Attended)));
        Assert.Equal(groups.Single(g => g.Id == Group).MemberCount, (await api.GetGroupsAsync()).Single(g => g.Id == Group).MemberCount);
    }

    [Fact]
    public async Task A_hidden_member_is_not_on_the_dashboard()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var before = await api.GetDashboardAsync(Group);
        var person = Assert.IsType<MemberAttendanceDto>(before.TopMembers.FirstOrDefault());

        await api.HideMemberAsync(await IdOfAsync(host, person.MemberId));
        var during = await api.GetDashboardAsync(Group);

        Assert.DoesNotContain(during.TopMembers.Concat(during.NeedsAttention), m => m.MemberId == person.MemberId);
        Assert.Equal(before.Summary.MemberCount - 1, during.Summary.MemberCount);
    }

    [Fact]
    public async Task A_hidden_member_is_not_among_the_attendees_of_an_event_and_is_no_trainer_of_it()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        var training = week.Events.First(e => !e.Cancelled && e.Invited > 0 && e.Trainers is { Count: > 0 });
        var detail = await api.GetCalendarEventAsync(Group, training.Id);
        var attendee = detail.Attendees.First(a => a.MemberId is not null);
        var trainerName = training.Trainers![0];

        await api.HideMemberAsync(attendee.MemberId!.Value);
        var during = await api.GetCalendarEventAsync(Group, training.Id);

        Assert.DoesNotContain(during.Attendees, a => a.MemberId == attendee.MemberId);
        Assert.Equal(training.Invited - 1, during.Event.Invited);
        Assert.Equal(detail.Attendees.Count - 1, during.Attendees.Count);
        Assert.Equal(training.Trainers, during.Event.Trainers); // somebody else was hidden: the trainers are as they were
        Assert.Contains(trainerName, during.Event.Trainers!);
    }

    [Fact]
    public async Task A_hidden_trainer_is_not_named_on_the_event_even_by_id()
    {
        using var host = CreateHost();
        var api = await SyncedAsync(host);
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        var training = week.Events.First(e => !e.Cancelled && e.Trainers is { Count: > 0 });
        var trainerName = training.Trainers![0];
        int trainerId;
        using (var scope = host.Services.CreateScope())
            trainerId = await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Members.Where(m => m.GroupId == Group && m.Name == trainerName).Select(m => m.Id).FirstAsync();

        await api.HideMemberAsync(trainerId);
        var during = (await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14))).Events.Single(e => e.Id == training.Id);

        Assert.DoesNotContain(trainerName, during.Trainers ?? []);
        Assert.Equal(training.Trainers.Count - 1, (during.Trainers ?? []).Count);
    }

    // --- Permissions ---------------------------------------------------------------------------------------------

    [Fact]
    public void Hiding_is_a_permission_of_editing_and_the_way_back_is_a_permission_of_its_own()
    {
        Assert.Contains(Permission.MembersHidden, Permission.All);
        Assert.NotEqual(Permission.MembersEdit, Permission.MembersHidden);
        Assert.Equal(["members.view", "members.edit", "members.hidden"], Permission.Groups.Single(g => g.Name == "members").Permissions);
        Assert.DoesNotContain(Permission.MembersHidden, Kyuplan.Api.Accounts.DefaultRoles.All.SelectMany(r => r.Permissions)); // an administrator hands it out
    }
}
