using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The calendar (day, week, month, events in progress, one event with its invited members) and the comments written from an event, against the real API and the demo club.</summary>
public sealed class CalendarApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";

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

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_time));
        });

    /// <summary>The first day within a week of the pinned day that has an event.</summary>
    private static async Task<CalendarEventDto> AnEventAsync(PlannerApiClient api)
    {
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        return week.Events.First(e => !e.Cancelled && e.Invited > 0 && e.Start > PinnedNow); // later than the pinned moment: a test may move the clock to it
    }

    [Fact]
    public async Task A_week_runs_from_monday_to_sunday_and_holds_only_its_own_events()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());

        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));

        Assert.Equal(new DateOnly(2026, 10, 12), week.From);
        Assert.Equal(new DateOnly(2026, 10, 18), week.To);
        Assert.Equal(new DateOnly(2026, 10, 13), week.Today);
        Assert.NotEmpty(week.Events);
        Assert.All(week.Events, e => Assert.InRange(DateOnly.FromDateTime(e.Start.Date), week.From, week.To));
        Assert.Equal(week.Events.OrderBy(e => e.Start).Select(e => e.Id), week.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task A_day_and_a_month_are_those_of_the_date_and_today_is_the_default()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());

        var day = await api.GetCalendarAsync(Group, CalendarViews.Day);
        var month = await api.GetCalendarAsync(Group, CalendarViews.Month, new DateOnly(2026, 10, 20));

        Assert.Equal(new DateOnly(2026, 10, 13), day.From);
        Assert.Equal(day.From, day.To);
        Assert.All(day.Events, e => Assert.Equal(new DateOnly(2026, 10, 13), DateOnly.FromDateTime(e.Start.Date)));
        Assert.Equal(new DateOnly(2026, 10, 1), month.From);
        Assert.Equal(new DateOnly(2026, 10, 31), month.To);
        Assert.True(month.Events.Count > day.Events.Count);
    }

    [Fact]
    public async Task A_calendar_needs_a_known_view_and_a_known_group()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/calendar?groupId={Group}&view=year")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/calendar?groupId=")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/calendar?groupId=nope")).StatusCode);
    }

    [Fact]
    public async Task An_event_in_progress_is_current_and_one_that_is_over_is_not()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);

        _time.SetUtcNow(training.Start.AddMinutes(1));
        var during = await api.GetCurrentEventsAsync(Group);
        _time.SetUtcNow(training.End.AddMinutes(1));
        var after = await api.GetCurrentEventsAsync(Group);

        Assert.Contains(during, e => e.Id == training.Id);
        Assert.DoesNotContain(after, e => e.Id == training.Id);
    }

    [Fact]
    public async Task The_demo_events_are_hosted_by_trainers_who_are_not_among_the_attendees()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);

        var detail = await api.GetCalendarEventAsync(Group, training.Id);

        Assert.NotEmpty(training.Trainers!);
        var group = (await host.Services.GetRequiredService<IDataSource>().GetGroupsAsync(CancellationToken.None)).Single(g => g.Id == Group);
        Assert.All(training.Trainers!, name => Assert.Contains(group.Members, m => m.Name == name && m.SubGroupIds.Count == 0));
        Assert.DoesNotContain(detail.Attendees, a => training.Trainers!.Contains(a.Name));

        // Like Spond, the demo lists the first host among the answers too; they still are no participant.
        var info = (await host.Services.GetRequiredService<IDataSource>().GetEventsAsync(Group, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None))
            .Single(e => e.Id == training.Id);
        Assert.Contains(info.Trainers!, id => info.Responses.ContainsKey(id) && !info.Participants.Any(p => p.Key == id));
    }

    [Fact]
    public async Task An_event_lists_everybody_invited_by_name_with_their_answer()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);

        var detail = await api.GetCalendarEventAsync(Group, training.Id);

        Assert.Equal(training, detail.Event);
        Assert.Equal(training.Invited, detail.Attendees.Count);
        Assert.Equal(training.Accepted, detail.Attendees.Count(a => a.Answer == AttendanceMark.Attended));
        Assert.Equal(detail.Attendees.Select(a => a.Name).Order(StringComparer.CurrentCultureIgnoreCase), detail.Attendees.Select(a => a.Name));
        Assert.All(detail.Attendees, a => Assert.Null(a.MemberId)); // the sync has not brought them yet
        await Assert.ThrowsAsync<PlannerApiException>(() => api.GetCalendarEventAsync(Group, "nope"));
    }

    /// <summary>Puts one invited member of the event into the database as the sync would, and says who it is.</summary>
    private static async Task<(int MemberId, string Name)> SyncOneInvitedAsync(WebApplicationFactory<Program> host, CalendarEventDto training)
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
        return (member.Id, person.Name);
    }

    [Fact]
    public async Task A_comment_written_from_an_event_carries_the_tag_of_the_event_and_shows_up_on_it()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);
        var (memberId, name) = await SyncOneInvitedAsync(host, training);

        var comment = await api.CreateEventCommentAsync(Group, training.Id, memberId, new CalendarCommentInputDto("**Good** kata"));
        var again = await api.CreateEventCommentAsync(Group, training.Id, memberId, new CalendarCommentInputDto("Second"));

        var tag = Assert.Single(comment.Tags);
        Assert.Equal(tag.Id, Assert.Single(again.Tags).Id); // one tag per event, made once
        Assert.Equal(training.Id, tag.Event!.EventId);
        Assert.Equal(training.Start, tag.Event.Start);

        var detail = await api.GetCalendarEventAsync(Group, training.Id);
        var attendee = detail.Attendees.Single(a => a.MemberId == memberId);
        Assert.Equal(name, attendee.Name);
        Assert.Equal(["**Good** kata", "Second"], attendee.Comments.Select(c => c.Body));
        Assert.Equal(2, (await api.GetTagsAsync()).Single(t => t.Id == tag.Id).CommentCount);
        Assert.Equal(2, (await api.GetCommentsAsync(memberId)).Count);
    }

    [Fact]
    public async Task A_reply_written_from_an_event_answers_a_comment_of_the_member_and_carries_the_tag_too()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var training = await AnEventAsync(api);
        var (memberId, _) = await SyncOneInvitedAsync(host, training);
        var first = await api.CreateEventCommentAsync(Group, training.Id, memberId, new CalendarCommentInputDto("Question"));

        var reply = await api.CreateEventCommentAsync(Group, training.Id, memberId, new CalendarCommentInputDto("Answer", first.Id));

        Assert.Equal(first.Id, reply.ParentId);
        Assert.Equal(first.Tags[0].Id, reply.Tags[0].Id);
        var attendee = (await api.GetCalendarEventAsync(Group, training.Id)).Attendees.Single(a => a.MemberId == memberId);
        Assert.Equal([null, first.Id], attendee.Comments.Select(c => c.ParentId));
        await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateEventCommentAsync(Group, training.Id, memberId, new CalendarCommentInputDto("x", 99999)));
    }

    [Fact]
    public async Task A_second_event_of_the_same_name_and_day_gets_a_tag_of_its_own()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var week = await api.GetCalendarAsync(Group, CalendarViews.Week, new DateOnly(2026, 10, 14));
        var first = week.Events.First(e => !e.Cancelled && e.Invited > 0);
        var other = week.Events.First(e => !e.Cancelled && e.Invited > 0 && e.Id != first.Id);
        var (memberId, _) = await SyncOneInvitedAsync(host, first);

        var one = await api.CreateEventCommentAsync(Group, first.Id, memberId, new CalendarCommentInputDto("a"));
        using (var scope = host.Services.CreateScope())
        {
            // The other event happens to have the very name the first event's tag would get: the names stay unique.
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            db.Tags.Add(new Tag { Name = $"{other.Name} {other.Start:yyyy-MM-dd}", CreatedAt = PinnedNow });
            await db.SaveChangesAsync();
        }

        var (otherMember, _) = await SyncOneInvitedAsync(host, other);
        var two = await api.CreateEventCommentAsync(Group, other.Id, otherMember, new CalendarCommentInputDto("b"));

        Assert.NotEqual(one.Tags[0].Id, two.Tags[0].Id);
        Assert.EndsWith("(2)", two.Tags[0].Name);
    }

    [Fact]
    public async Task A_comment_needs_a_member_of_the_group_and_words()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var client = host.CreateSignedInClient();
        var training = await AnEventAsync(api);

        await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateEventCommentAsync(Group, training.Id, 9999, new CalendarCommentInputDto("hello")));
        var (memberId, _) = await SyncOneInvitedAsync(host, training);
        var empty = await client.PostAsJsonAsync($"/api/calendar/events/{training.Id}/members/{memberId}/comments?groupId={Group}", new CalendarCommentInputDto("   "));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Empty(await api.GetTagsAsync()); // nothing was tagged on the way
    }
}
