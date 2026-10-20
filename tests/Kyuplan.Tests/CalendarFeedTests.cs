using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The calendar feeds: defined by an administrator, read as ICS without a login, against the real API and the demo club.</summary>
public sealed class CalendarFeedTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";

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

    private static async Task<CalendarFeedDto> CreateAsync(HttpClient admin, string name, params CalendarSourceDto[] sources)
    {
        var response = await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto(name, sources));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CalendarFeedDto>())!;
    }

    private static async Task<string> IcsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static int Count(string ics, string text) => ics.Split(text).Length - 1;

    [Fact]
    public async Task A_feed_is_read_as_ics_without_a_login_and_an_unknown_token_is_not_found()
    {
        using var host = CreateHost();
        var feed = await CreateAsync(host.CreateSignedInClient(), "Everything", new CalendarSourceDto(CalendarSourceKinds.Events, Group));

        var ics = await IcsAsync(host.CreateClient(), feed.Url);

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("X-WR-CALNAME:Everything", ics);
        Assert.True(Count(ics, "BEGIN:VEVENT") > 0);
        Assert.Equal(Count(ics, "BEGIN:VEVENT"), Count(ics, "END:VEVENT"));
        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync("/calendar-feeds/nope.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/calendar-feeds")).StatusCode);
    }

    [Fact]
    public async Task Events_are_narrowed_by_who_is_invited_and_by_name()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var anyone = await CreateAsync(admin, "All", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var kids = await CreateAsync(admin, "Kids", new CalendarSourceDto(CalendarSourceKinds.Events, Group, ["sg-kids"]));
        var onlyKids = await CreateAsync(admin, "Only kids", new CalendarSourceDto(CalendarSourceKinds.Events, Group, ["sg-kids"], Invitation: EventInvitations.Only));
        var none = await CreateAsync(admin, "None", new CalendarSourceDto(CalendarSourceKinds.Events, Group, NameContains: "no such event"));
        var labelled = await CreateAsync(admin, "Labelled", new CalendarSourceDto(CalendarSourceKinds.Events, Group, Label: "Dojo"));

        var client = host.CreateClient();
        var all = Count(await IcsAsync(client, anyone.Url), "BEGIN:VEVENT");

        Assert.InRange(Count(await IcsAsync(client, kids.Url), "BEGIN:VEVENT"), 1, all);
        Assert.True(Count(await IcsAsync(client, onlyKids.Url), "BEGIN:VEVENT") <= Count(await IcsAsync(client, kids.Url), "BEGIN:VEVENT"));
        Assert.Equal(0, Count(await IcsAsync(client, none.Url), "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Dojo: ", await IcsAsync(client, labelled.Url));
    }

    [Fact]
    public async Task Birthdays_are_whole_days_with_the_age_and_29_february_falls_back_to_the_28th()
    {
        using var host = CreateHost();
        using (var scope = host.Services.CreateScope())
        {
            var info = await scope.ServiceProvider.GetRequiredService<StatisticsService>().GetGroupInfoAsync(Group, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            db.Members.Add(new Member { GroupId = Group, SpondId = info.Members[0].Id, Name = "Leap, Anna", BirthDate = new DateOnly(2000, 2, 29) });
            db.Members.Add(new Member { GroupId = Group, SpondId = info.Members[1].Id, Name = "No Birthday" });
            await db.SaveChangesAsync();
        }

        var feed = await CreateAsync(host.CreateSignedInClient(), "Birthdays", new CalendarSourceDto(CalendarSourceKinds.Birthdays, Group));
        var ics = await IcsAsync(host.CreateClient(), feed.Url);

        Assert.Equal(3, Count(ics, "BEGIN:VEVENT")); // the year before, this year and the next
        Assert.Contains("DTSTART;VALUE=DATE:20250228", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20270228", ics);
        Assert.Contains("(26)", ics);
        Assert.Contains("Leap\\, Anna", ics); // the comma is escaped
        Assert.DoesNotContain("No Birthday", ics);
    }

    [Fact]
    public async Task A_new_token_ends_the_old_address_and_a_deleted_feed_is_gone()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var feed = await CreateAsync(admin, "Feed", new CalendarSourceDto(CalendarSourceKinds.Events, Group));

        var renewed = await (await admin.PostAsync($"/api/calendar-feeds/{feed.Id}/regenerate", null)).Content.ReadFromJsonAsync<CalendarFeedDto>();

        Assert.NotEqual(feed.Token, renewed!.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync(new Uri(feed.Url).PathAndQuery)).StatusCode);
        await IcsAsync(host.CreateClient(), renewed.Url);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/calendar-feeds/{feed.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync(new Uri(renewed.Url).PathAndQuery)).StatusCode);
    }

    [Fact]
    public async Task A_feed_needs_a_name_sources_a_known_group_and_subgroup_and_a_name_of_its_own()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var events = new CalendarSourceDto(CalendarSourceKinds.Events, Group);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("", [events]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("x", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("x", [events with { Kind = "nope" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("x", [events with { GroupId = "nope" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("x", [events with { SubGroupIds = ["nope"] }]))).StatusCode);

        await CreateAsync(admin, "Same", events);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/calendar-feeds", new CalendarFeedInputDto("same", [events]))).StatusCode);
    }

    [Fact]
    public async Task Subgroup_words_are_put_in_front_of_the_titles_of_their_entries()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var feed = await CreateAsync(admin, "Words", new CalendarSourceDto(
            CalendarSourceKinds.Events, Group, ["sg-kids"], Label: "Dojo", SubGroupLabels: new Dictionary<string, string> { ["sg-kids"] = "Kids" }));

        var ics = await IcsAsync(host.CreateClient(), feed.Url);

        Assert.Contains("SUMMARY:Dojo / Kids: ", ics);
        Assert.Equal("Kids", Assert.Single(feed.Sources).SubGroupLabels!["sg-kids"]);
    }

    [Fact]
    public void Ics_lines_are_folded_at_75_octets_without_cutting_a_character()
    {
        var ics = IcsWriter.Write(
            "Cal", [new IcsEntry("u@kyuplan", string.Concat(Enumerable.Repeat("\U0001F382ä;", 40)), PinnedNow, PinnedNow.AddHours(1), null, false)], PinnedNow);

        var lines = ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Contains(lines, l => l.StartsWith(' '));
        Assert.Contains("DTSTART:20261013T100000Z", ics);
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F382ä\\;", 40)), string.Concat(
            lines.SkipWhile(l => !l.StartsWith("SUMMARY:")).TakeWhile(l => !l.StartsWith("STATUS:")).Select((l, i) => i == 0 ? l["SUMMARY:".Length..] : l[1..])));
    }
}
