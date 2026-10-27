using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;
using static Kyuplan.Tests.SyncTestClub;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>
/// The hosts (trainers) of an event: they run it instead of taking part, so the event counts for no statistic of theirs and they are no
/// participant of an exam. Spond delivers them as the owners of the event, the manual data and the custom script as a list of members.
/// </summary>
public sealed class EventTrainerTests
{
    private static readonly MemberInfo Anna = Person("m-anna", "Anna Beck");
    private static readonly MemberInfo Tina = Person("m-tina", "Tina Trainer");

    // ----- the model -----

    [Fact]
    public void Participants_are_the_responses_without_the_trainers()
    {
        var e = new EventInfo("e", "Training", At(6, 1), At(6, 1), false, new Dictionary<string, Rsvp> { ["t"] = Rsvp.Accepted, ["a"] = Rsvp.Declined }, ["t", "not-invited"]);

        Assert.Equal(["a"], e.Participants.Select(p => p.Key));
        Assert.True(e.IsTrainer("t"));
        Assert.True(e.IsTrainer("not-invited"));
        Assert.False(e.IsTrainer("a"));
    }

    [Fact]
    public void An_event_without_trainers_has_all_responses_as_participants()
    {
        var responses = new Dictionary<string, Rsvp> { ["a"] = Rsvp.Accepted };

        Assert.Same(responses, new EventInfo("e", "x", At(6, 1), At(6, 1), false, responses).Participants);
        Assert.Same(responses, new EventInfo("e", "x", At(6, 1), At(6, 1), false, responses, []).Participants);
        Assert.False(new EventInfo("e", "x", At(6, 1), At(6, 1), false, responses).IsTrainer("a"));
    }

    // ----- statistics -----

    [Fact]
    public void An_event_a_member_hosts_counts_for_none_of_their_numbers()
    {
        // t is in the responses of all four events but trains the last two.
        var events = Events(At(6, 1), Patterns(("a", "AAAA"), ("t", "AAAA")))
            .Select((e, i) => i >= 2 ? e with { Trainers = ["t"] } : e)
            .ToList();

        var result = AttendanceCalculator.Calculate(Group([Member("a"), Member("t")]), events, Filter(), Now);

        var t = result.Members.Single(m => m.MemberId == "t");
        Assert.Equal((2, 2), (t.Invited, t.Attended));
        Assert.Equal(4, result.Members.Single(m => m.MemberId == "a").Invited);
        Assert.Equal([2, 2, 1, 1], result.Events.OrderBy(e => e.Start).Select(e => e.Invited));
    }

    [Fact]
    public void A_member_who_only_hosts_has_no_row_and_an_event_of_only_trainers_is_not_listed()
    {
        var events = Events(At(6, 1), Patterns(("t", "AA"))).Select(e => e with { Trainers = ["t"] }).ToList();

        var result = AttendanceCalculator.Calculate(Group([Member("t")]), events, Filter(), Now);

        Assert.Empty(result.Members);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void A_trainer_who_is_not_among_the_responses_changes_nothing()
    {
        var events = Events(At(6, 1), Patterns(("a", "AAA")));
        var hosted = events.Select(e => e with { Trainers = ["somebody"] }).ToList();

        var plain = AttendanceCalculator.Calculate(Group([Member("a")]), events, Filter(), Now);
        var withTrainer = AttendanceCalculator.Calculate(Group([Member("a")]), hosted, Filter(), Now);

        Assert.Equal(plain.Members.Single().Invited, withTrainer.Members.Single().Invited);
        Assert.Equal(plain.Summary, withTrainer.Summary);
    }

    [Fact]
    public void The_dashboard_does_not_count_the_trainer_among_the_invited_of_an_upcoming_event()
    {
        var upcoming = new EventInfo("soon", "Training", At(7, 2), At(7, 2).AddMinutes(90), false,
            new Dictionary<string, Rsvp> { ["a"] = Rsvp.Accepted, ["t"] = Rsvp.Accepted, ["b"] = Rsvp.Unanswered }, ["t"]);

        var dashboard = DashboardBuilder.Build(Group([Member("a"), Member("b"), Member("t")]), null, [upcoming], 90, Now, new StatisticsOptions { UpcomingDays = 14 });

        var soon = Assert.Single(dashboard.Upcoming);
        Assert.Equal((2, 1, 0, 1), (soon.Invited, soon.Accepted, soon.Declined, soon.Unanswered));
    }

    // ----- the sync -----

    [Fact]
    public async Task Hosting_an_event_does_not_give_a_member_a_start_day()
    {
        using var club = new SyncTestClub();
        club.Spond.SetGroups(Group(Dojo, Anna, Tina));
        club.Spond.SetEvents(Dojo,
            club.Event("e-1", Day(-5), (Anna.Id, Rsvp.Accepted), (Tina.Id, Rsvp.Accepted)) with { Trainers = [Tina.Id] },
            club.Event("e-2", Day(-2), (Anna.Id, Rsvp.Accepted), (Tina.Id, Rsvp.Accepted)));

        await club.SyncAsync();

        var members = await club.MembersAsync();
        Assert.Equal(Day(-5), members.Single(m => m.SpondId == Anna.Id).StartedAt);
        Assert.Equal(Day(-2), members.Single(m => m.SpondId == Tina.Id).StartedAt); // the first event they took part in
    }

    [Fact]
    public async Task A_trainer_is_no_participant_of_the_exam_and_stops_being_one_when_they_become_its_trainer()
    {
        using var club = new SyncTestClub();
        club.Spond.SetGroups(Group(Dojo, Anna, Tina));
        club.Spond.SetEvents(Dojo, club.Event("exam", "Prüfung", Day(5), (Anna.Id, Rsvp.Unanswered), (Tina.Id, Rsvp.Unanswered)));
        await club.SyncAsync();
        var exam = await club.CreateExamAsync("exam");
        Assert.Equal([Anna.Id, Tina.Id], (await club.ParticipantsAsync(exam.Id)).Select(p => p.Member));

        club.Spond.SetEvents(Dojo, club.Event("exam", "Prüfung", Day(5), (Anna.Id, Rsvp.Unanswered), (Tina.Id, Rsvp.Accepted)) with { Trainers = [Tina.Id] });
        await club.SyncAsync();

        Assert.Equal([Anna.Id], (await club.ParticipantsAsync(exam.Id)).Select(p => p.Member));
    }

    // ----- the manual data -----

    [Fact]
    public async Task Trainers_of_a_manual_event_are_kept_validated_and_left_out_of_the_statistics()
    {
        using var database = new InMemoryDatabase();
        using var accounts = new InMemoryDatabase();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataSource:Kind", "manual");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", database.ConnectionString);
            TestAccounts.Configure(builder, accounts);
        });
        var admin = factory.CreateSignedInClient();

        async Task<T> Post<T>(string url, object body)
        {
            var response = await admin.PostAsJsonAsync(url, body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        var group = await Post<ManualGroupDto>("/api/manual/groups", new ManualNameInputDto("Dojo"));
        var other = await Post<ManualGroupDto>("/api/manual/groups", new ManualNameInputDto("Team"));
        var anna = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, null));
        var tina = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Tina", null, null));
        var stranger = await Post<ManualMemberDto>($"/api/manual/groups/{other.Id}/members", new ManualMemberInputDto("Stranger", null, null));
        var start = DateTimeOffset.UtcNow.AddDays(-3);
        ManualInvitationDto[] invitations = [new(anna.Id, ManualRsvp.Accepted), new(tina.Id, ManualRsvp.Accepted)];

        // Duplicates collapse.
        var created = await Post<ManualEventDto>($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", start, start.AddHours(1), false, invitations, [tina.Id, tina.Id]));
        Assert.Equal([tina.Id], created.TrainerIds);

        // Blank and null entries are left out, the ids are trimmed.
        var tidy = await admin.PutAsJsonAsync($"/api/manual/events/{created.Id}",
            new ManualEventInputDto("Training", start, start.AddHours(1), false, invitations, [$" {tina.Id} ", "", "  ", null!, tina.Id]));
        Assert.Equal(HttpStatusCode.OK, tidy.StatusCode);
        Assert.Equal([tina.Id], (await tidy.Content.ReadFromJsonAsync<ManualEventDto>())!.TrainerIds);

        // The trainer is invited and said yes, still the event counts for Anna only.
        var from = DateOnly.FromDateTime(start.UtcDateTime.AddDays(-2));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = (await admin.GetFromJsonAsync<AttendanceStatisticsDto>($"/api/statistics/attendance?groupId={group.Id}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))!;
        Assert.Equal([anna.Id], stats.Members.Select(m => m.MemberId));

        // A trainer needs no invitation; one of another group is refused.
        var hosted = await admin.PutAsJsonAsync($"/api/manual/events/{created.Id}",
            new ManualEventInputDto("Training", start, start.AddHours(1), false, [new ManualInvitationDto(anna.Id, ManualRsvp.Accepted)], [tina.Id, anna.Id]));
        Assert.Equal(new[] { anna.Id, tina.Id }.Order(), (await hosted.Content.ReadFromJsonAsync<ManualEventDto>())!.TrainerIds!.Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/manual/events/{created.Id}",
            new ManualEventInputDto("Training", start, start.AddHours(1), false, null, [stranger.Id]))).StatusCode);

        // Everything is replaced: no list means no trainer. A deleted member is no trainer any more.
        var cleared = await admin.PutAsJsonAsync($"/api/manual/events/{created.Id}", new ManualEventInputDto("Training", start, start.AddHours(1), false, invitations, null));
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<ManualEventDto>())!.TrainerIds!);
        await admin.PutAsJsonAsync($"/api/manual/events/{created.Id}", new ManualEventInputDto("Training", start, start.AddHours(1), false, invitations, [tina.Id]));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/manual/members/{tina.Id}")).StatusCode);
        Assert.Empty(Assert.Single((await admin.GetFromJsonAsync<List<ManualEventDto>>($"/api/manual/groups/{group.Id}/events"))!).TrainerIds!);
    }

    // ----- the calendar -----

    [Fact]
    public async Task The_calendar_names_the_trainers_of_an_event_by_name_and_does_not_count_them_as_invited()
    {
        // The clock is pinned to midday: with the real one, a run in the first hour after the club's midnight would put the event
        // (which started an hour ago) on the day before, and the day view would not list it.
        var now = new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
        using var database = new InMemoryDatabase();
        using var accounts = new InMemoryDatabase();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataSource:Kind", "manual");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", database.ConnectionString);
            TestAccounts.Configure(builder, accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now)));
        });
        var admin = factory.CreateSignedInClient();

        async Task<T> Post<T>(string url, object body)
        {
            var response = await admin.PostAsJsonAsync(url, body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        var group = await Post<ManualGroupDto>("/api/manual/groups", new ManualNameInputDto("Dojo"));
        var anna = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, null));
        var tina = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Tina", null, null));
        var ben = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("ben", null, null));
        var start = now.AddHours(-1);
        ManualInvitationDto[] invitations = [new(anna.Id, ManualRsvp.Accepted), new(tina.Id, ManualRsvp.Accepted)];
        var hosted = await Post<ManualEventDto>($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", start, start.AddHours(2), false, invitations, [tina.Id, ben.Id]));
        var plain = await Post<ManualEventDto>($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Open mat", start, start.AddHours(2), false, invitations, null));

        var calendar = (await admin.GetFromJsonAsync<CalendarDto>($"/api/calendar?groupId={group.Id}&view=day"))!;
        var current = (await admin.GetFromJsonAsync<List<CalendarEventDto>>($"/api/calendar/current?groupId={group.Id}"))!;
        var detail = (await admin.GetFromJsonAsync<CalendarEventDetailDto>($"/api/calendar/events/{hosted.Id}?groupId={group.Id}"))!;

        foreach (var events in new[] { calendar.Events, current })
        {
            var training = events.Single(e => e.Name == "Training");
            Assert.Equal(["ben", "Tina"], training.Trainers); // by name, sorted ignoring case
            Assert.Equal(1, training.Invited); // Tina hosts: she is no invitee
            Assert.Empty(events.Single(e => e.Name == "Open mat").Trainers ?? []);
        }
        Assert.Equal(["ben", "Tina"], detail.Event.Trainers);
        Assert.Equal(["Anna"], detail.Attendees.Select(a => a.Name));
        Assert.NotEqual(plain.Id, hosted.Id);
    }

    [Fact]
    public async Task A_calendar_feed_does_not_count_the_trainers_of_an_event_as_invited()
    {
        using var database = new InMemoryDatabase();
        using var accounts = new InMemoryDatabase();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataSource:Kind", "manual");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", database.ConnectionString);
            TestAccounts.Configure(builder, accounts);
        });
        var admin = factory.CreateSignedInClient();

        async Task<T> Post<T>(string url, object body)
        {
            var response = await admin.PostAsJsonAsync(url, body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        var group = await Post<ManualGroupDto>("/api/manual/groups", new ManualNameInputDto("Dojo"));
        var anna = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, null));
        var tina = await Post<ManualMemberDto>($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Tina", null, null));
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        await Post<ManualEventDto>($"/api/manual/groups/{group.Id}/events", new ManualEventInputDto("Training", start, start.AddHours(2), false,
            [new ManualInvitationDto(anna.Id, ManualRsvp.Accepted), new ManualInvitationDto(tina.Id, ManualRsvp.Accepted)], [tina.Id]));

        var members = (await admin.GetFromJsonAsync<List<MemberDto>>($"/api/members?groupId={group.Id}"))!;
        var annaId = members.Single(m => m.Name == "Anna").Id;
        var tinaId = members.Single(m => m.Name == "Tina").Id;

        async Task<int> EventsOfAsync(string invitation, params int[] memberIds)
        {
            var response = await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto($"{invitation} {string.Join('-', memberIds)}",
                [new CalendarSourceDto(CalendarSourceKinds.Events, group.Id, MemberIds: memberIds, Invitation: invitation)]));
            response.EnsureSuccessStatusCode();
            var feed = (await response.Content.ReadFromJsonAsync<CalendarFeedDto>())!;
            var ics = await factory.CreateClient().GetStringAsync(new Uri(feed.Url!).PathAndQuery);
            return ics.Split("BEGIN:VEVENT").Length - 1;
        }

        Assert.Equal(1, await EventsOfAsync(EventInvitations.Any, annaId));
        Assert.Equal(0, await EventsOfAsync(EventInvitations.Any, tinaId)); // Tina hosts: not invited
        Assert.Equal(1, await EventsOfAsync(EventInvitations.Only, annaId)); // the host does not break "only these"
        Assert.Equal(0, await EventsOfAsync(EventInvitations.All, annaId, tinaId));
    }
}
