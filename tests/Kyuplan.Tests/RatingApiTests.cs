using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The star evaluations of members at events (0–5, one per user, member and event), against the real API and the demo club.</summary>
public sealed class RatingApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";
    private const string Password = "a good password";

    /// <summary>The demo club's events are generated around "today", so the clock is pinned: 13 October 2026, 12:00 in Germany.</summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly FakeTimeProvider _time = new(PinnedNow);

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_time));
            configure?.Invoke(builder);
        });

    private static async Task<CalendarEventDto> AnEventAsync(PlannerApiClient api)
    {
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        return week.Events.First(e => !e.Cancelled && e.Invited > 0 && e.Start > PinnedNow);
    }

    /// <summary>Puts one invited member of the event into the database as the sync would.</summary>
    private static async Task<int> SyncOneInvitedAsync(WebApplicationFactory<Program> host, CalendarEventDto training)
    {
        using var scope = host.Services.CreateScope();
        var statistics = scope.ServiceProvider.GetRequiredService<StatisticsService>();
        var group = await statistics.GetGroupInfoAsync(Group, default);
        var info = await statistics.FindEventAsync(Group, training.Id, default);
        var person = group.Members.First(m => info.Responses.ContainsKey(m.Id));

        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var member = new Member { GroupId = Group, SpondId = person.Id, Name = person.Name, CreatedAt = PinnedNow };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    private static async Task<PlannerApiClient> UserAsync(WebApplicationFactory<Program> host, string name, params string[] permissions)
    {
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        await admin.CreateUserAsync(new UserInputDto(name, Password, Permissions: permissions));
        return new PlannerApiClient(host.CreateSignedInClient(name, Password));
    }

    /// <summary>The clock at the start of the event: it can be evaluated from then on.</summary>
    private void Start(CalendarEventDto training) => _time.SetUtcNow(training.Start.AddMinutes(1));

    [Fact]
    public async Task A_member_is_evaluated_replaced_and_taken_back_by_the_user_who_gave_it()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);
        var memberId = await SyncOneInvitedAsync(host, training);
        Start(training);

        var none = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);
        var given = await api.RateMemberAsync(Group, training.Id, memberId, 4);
        await api.RateMemberAsync(Group, training.Id, memberId, 2);
        var rated = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);
        await api.UnrateMemberAsync(Group, training.Id, memberId);
        var back = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);

        Assert.Empty(none.Ratings ?? []);
        Assert.Equal((memberId, training.Id, 4), (given.MemberId, given.EventId, given.Stars));
        var rating = Assert.Single(rated.Ratings!); // one per user, member and event: the second replaced the first
        Assert.Equal((2, "admin"), (rating.Stars, rating.UserName));
        Assert.Empty(back.Ratings ?? []);
    }

    [Fact]
    public async Task Zero_stars_is_an_evaluation_that_is_kept_and_shown()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);
        var memberId = await SyncOneInvitedAsync(host, training);
        Start(training);

        await api.RateMemberAsync(Group, training.Id, memberId, 0);

        var attendee = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);
        Assert.Equal(0, Assert.Single(attendee.Ratings!).Stars);
    }

    [Fact]
    public async Task Every_user_has_an_evaluation_of_their_own_and_everybody_who_may_read_sees_them_all()
    {
        using var host = CreateHost();
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(admin);
        var memberId = await SyncOneInvitedAsync(host, training);
        var ben = await UserAsync(host, "ben", Permission.CalendarView, Permission.RatingsView, Permission.RatingsWrite);
        var reader = await UserAsync(host, "reader", Permission.CalendarView, Permission.RatingsView);
        var blind = await UserAsync(host, "blind", Permission.CalendarView);
        Start(training);

        await admin.RateMemberAsync(Group, training.Id, memberId, 5);
        await ben.RateMemberAsync(Group, training.Id, memberId, 1);
        await ben.UnrateMemberAsync(Group, training.Id, memberId); // only Ben's own is gone
        await ben.RateMemberAsync(Group, training.Id, memberId, 3);

        async Task<IReadOnlyList<RatingDto>> SeenBy(PlannerApiClient who) =>
            (await who.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId).Ratings ?? [];

        Assert.Equal([("admin", 5), ("ben", 3)], (await SeenBy(reader)).Select(r => (r.UserName, r.Stars)));
        Assert.Equal([("admin", 5), ("ben", 3)], (await SeenBy(ben)).Select(r => (r.UserName, r.Stars)));
        Assert.Empty(await SeenBy(blind)); // without ratings.view nothing is told
    }

    [Fact]
    public async Task What_is_asked_must_make_sense()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var api = new PlannerApiClient(client);
        var training = await AnEventAsync(api);
        var memberId = await SyncOneInvitedAsync(host, training);
        string Url(int member, string? eventId = null) => $"/api/calendar/events/{eventId ?? training.Id}/members/{member}/rating?groupId={Group}";

        // before the event has started
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url(memberId), new RatingInputDto(3))).StatusCode);
        Start(training);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url(memberId), new RatingInputDto(6))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Url(memberId), new RatingInputDto(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Url(9999), new RatingInputDto(3))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Url(memberId, "nope"), new RatingInputDto(3))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url(memberId), new RatingInputDto(5))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(memberId))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(memberId))).StatusCode); // taking back what is not there is fine
    }

    [Fact]
    public async Task The_evaluations_of_a_member_are_listed_with_the_latest_first_for_those_who_may_read()
    {
        using var host = CreateHost();
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(admin);
        var memberId = await SyncOneInvitedAsync(host, training);
        Start(training);
        await admin.RateMemberAsync(Group, training.Id, memberId, 4);
        var client = host.CreateSignedInClient();

        var list = await client.GetFromJsonAsync<List<RatingDto>>($"/api/members/{memberId}/ratings");

        var rating = Assert.Single(list!);
        Assert.Equal((memberId, training.Id, 4), (rating.MemberId, rating.EventId, rating.Stars));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/members/9999/ratings")).StatusCode);
    }

    [Fact]
    public async Task An_event_counts_once_in_the_statistics_however_many_users_evaluated_the_member()
    {
        using var host = CreateHost();
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(admin);
        var memberId = await SyncOneInvitedAsync(host, training);
        var ben = await UserAsync(host, "ben", Permission.CalendarView, Permission.RatingsView, Permission.RatingsWrite);
        Start(training);
        _time.SetUtcNow(training.End.AddMinutes(1)); // the statistics only count events that are over

        var day = DateOnly.FromDateTime(training.Start.Date);
        async Task<MemberAttendanceDto> StatsAsync()
        {
            var result = await admin.GetAttendanceAsync(new AttendanceQuery(Group, day, day));
            var detail = (await admin.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);
            return result.Members.Single(m => m.Name == detail.Name);
        }

        var before = await StatsAsync();
        await admin.RateMemberAsync(Group, training.Id, memberId, 4);
        await ben.RateMemberAsync(Group, training.Id, memberId, 2);
        var after = await StatsAsync();

        Assert.Equal(1, before.StarEvents);
        Assert.Equal(0, before.EvaluatedEvents);
        Assert.Equal(2.5, before.AverageStars); // nobody evaluated: the default
        Assert.Equal((1, 1), (after.StarEvents, after.EvaluatedEvents)); // one event, not two evaluations
        Assert.Equal(3, after.AverageStars); // (4 + 2) / 2 for the event
    }

    [Fact]
    public async Task The_default_of_an_event_nobody_evaluated_is_the_one_of_the_settings()
    {
        using var host = CreateHost(builder => builder.UseSetting("Statistics:DefaultEventStars", "4"));
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(admin);
        var memberId = await SyncOneInvitedAsync(host, training);
        _time.SetUtcNow(training.End.AddMinutes(1));
        var day = DateOnly.FromDateTime(training.Start.Date);

        var result = await admin.GetAttendanceAsync(new AttendanceQuery(Group, day, day));

        Assert.Equal(4, (await admin.GetSettingsAsync()).DefaultEventStars);
        Assert.All(result.Members.Where(m => m.Attended > 0), m => Assert.Equal(4, m.AverageStars));
        Assert.True(memberId > 0);
    }

    [Fact]
    public async Task Members_leave_their_evaluations_behind_when_they_leave_the_club()
    {
        using var host = CreateHost();
        var admin = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(admin);
        var memberId = await SyncOneInvitedAsync(host, training);
        Start(training);
        await admin.RateMemberAsync(Group, training.Id, memberId, 4);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        await db.Members.Where(m => m.Id == memberId).ExecuteDeleteAsync();

        Assert.False(await db.MemberRatings.AnyAsync(r => r.MemberId == memberId));
    }
}
