using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
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
    public async Task Requests_beyond_the_limit_are_refused_and_reads_are_logged()
    {
        var log = new List<string>();
        using var host = CreateHost().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(log))));
        var feed = await CreateAsync(host.CreateSignedInClient(), "Logged", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var client = host.CreateClient();
        var path = new Uri(feed.Url).PathAndQuery;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/calendar-feeds/nope.ics")).StatusCode);
        lock (log)
        {
            Assert.Contains(log, l => l.StartsWith("Kyuplan.Api.CalendarFeedAccess") && l.Contains("Logged"));
            Assert.Contains(log, l => l.StartsWith("Kyuplan.Api.CalendarFeedAccess") && l.Contains("unknown token"));
        }

        HttpStatusCode last = default;
        for (var i = 0; i < 30; i++) last = (await client.GetAsync(path)).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    private sealed class ClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip)) context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return nextMiddleware();
            });
            next(app);
        };
    }

    [Fact]
    public async Task The_limit_counts_every_client_address_on_its_own()
    {
        using var host = CreateHost().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Api:AllowInsecureHttp", "true");
            builder.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, ClientAddressFilter>());
        });
        var feed = await CreateAsync(host.CreateSignedInClient(), "Shared", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var client = host.CreateClient();
        var path = new Uri(feed.Url).PathAndQuery;

        async Task<HttpStatusCode> GetAs(string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Test-Ip", ip);
            return (await client.SendAsync(request)).StatusCode;
        }

        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, await GetAs("203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAs("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await GetAs("203.0.113.2"));
    }

    private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class ListLogger(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add($"{category} {formatter(state, exception)}");
            }
        }
    }

    [Fact]
    public async Task Tokens_are_long_unique_url_safe_and_only_the_exact_token_opens_a_feed()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var source = new CalendarSourceDto(CalendarSourceKinds.Events, Group);
        var first = await CreateAsync(admin, "One", source);
        var second = await CreateAsync(admin, "Two", source);

        Assert.NotEqual(first.Token, second.Token);
        Assert.All(new[] { first.Token, second.Token }, t =>
        {
            Assert.True(t.Length >= 43, "256 bits need 43 base64url characters");
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });

        var client = host.CreateClient();
        var swapped = new string(first.Token.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
        foreach (var wrong in new[] { swapped, first.Token[..^1], first.Token + "x", " " + first.Token })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/calendar-feeds/{Uri.EscapeDataString(wrong)}.ics")).StatusCode);
        await IcsAsync(client, first.Url);
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

    /// <summary>A feed whose sources are stored as they are, i.e. without the check of the endpoints: this is how a group that vanished later looks.</summary>
    private static async Task<CalendarFeed> StoreFeedAsync(WebApplicationFactory<Program> host, string name, params CalendarSourceDto[] sources)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var feed = new CalendarFeed
        {
            Name = name,
            Token = $"token-{name}".Replace(' ', '-'),
            SourcesJson = System.Text.Json.JsonSerializer.Serialize(sources, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedAt = PinnedNow,
        };
        db.CalendarFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed;
    }

    private static string PathOf(CalendarFeed feed) => $"http://localhost/calendar-feeds/{feed.Token}.ics";

    [Fact]
    public async Task A_source_whose_group_is_gone_is_left_out_and_the_other_sources_are_still_delivered()
    {
        var log = new List<string>();
        using var host = CreateHost().WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(log))));
        var healthy = await CreateAsync(host.CreateSignedInClient(), "Healthy", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var stale = await StoreFeedAsync(host, "Stale",
            new CalendarSourceDto(CalendarSourceKinds.Events, "gone"),
            new CalendarSourceDto(CalendarSourceKinds.Birthdays, "gone"),
            new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var client = host.CreateClient();

        var ics = await IcsAsync(client, PathOf(stale));

        var expected = Count(await IcsAsync(client, healthy.Url), "BEGIN:VEVENT");
        Assert.True(expected > 0);
        Assert.Equal(expected, Count(ics, "BEGIN:VEVENT"));
        Assert.Equal(Count(ics, "BEGIN:VEVENT"), Count(ics, "END:VEVENT"));
        lock (log)
        {
            Assert.Single(log, l => l.Contains("'gone'") && l.Contains("Stale")); // once per render, however many sources are left out
        }
    }

    [Fact]
    public async Task A_feed_whose_groups_are_all_gone_is_an_empty_calendar_not_a_404()
    {
        using var host = CreateHost();
        var stale = await StoreFeedAsync(host, "All gone", new CalendarSourceDto(CalendarSourceKinds.Events, "gone"), new CalendarSourceDto(CalendarSourceKinds.BeltAnniversaries, "also-gone"));

        var ics = await IcsAsync(host.CreateClient(), PathOf(stale));

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("X-WR-CALNAME:All gone", ics);
        Assert.Equal(0, Count(ics, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task The_feed_list_names_the_sources_whose_group_is_gone()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var fine = await CreateAsync(admin, "Fine", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        Assert.Empty(fine.BrokenSources ?? []);
        var stale = await StoreFeedAsync(host, "Stale",
            new CalendarSourceDto(CalendarSourceKinds.Events, Group),
            new CalendarSourceDto(CalendarSourceKinds.Birthdays, "gone"),
            new CalendarSourceDto(CalendarSourceKinds.Events, "gone"));

        var list = (await admin.GetFromJsonAsync<List<CalendarFeedDto>>("/api/calendar-feeds"))!;
        var one = (await admin.GetFromJsonAsync<CalendarFeedDto>($"/api/calendar-feeds/{stale.Id}"))!;
        var renewed = (await (await admin.PostAsync($"/api/calendar-feeds/{stale.Id}/regenerate", null)).Content.ReadFromJsonAsync<CalendarFeedDto>())!;

        Assert.Empty(list.Single(f => f.Name == "Fine").BrokenSources ?? []);
        Assert.Equal([1, 2], list.Single(f => f.Name == "Stale").BrokenSources);
        Assert.Equal([1, 2], one.BrokenSources);
        Assert.Equal([1, 2], renewed.BrokenSources);
    }

    [Fact]
    public async Task The_api_client_reads_the_broken_sources()
    {
        using var host = CreateHost();
        var stale = await StoreFeedAsync(host, "Stale",
            new CalendarSourceDto(CalendarSourceKinds.Events, Group),
            new CalendarSourceDto(CalendarSourceKinds.Events, "gone"));
        var api = new Kyuplan.Client.Api.PlannerApiClient(host.CreateSignedInClient());

        var feed = Assert.Single(await api.GetCalendarFeedsAsync());

        Assert.Equal(stale.Id, feed.Id);
        Assert.Equal([1], feed.BrokenSources);
    }

    [Fact]
    public async Task Fixing_the_group_of_a_broken_source_makes_it_whole_again()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var stale = await StoreFeedAsync(host, "Stale", new CalendarSourceDto(CalendarSourceKinds.Events, "gone"));

        var response = await admin.PutAsJsonAsync($"/api/calendar-feeds/{stale.Id}", new CalendarFeedInputDto("Stale", [new CalendarSourceDto(CalendarSourceKinds.Events, Group)]));

        response.EnsureSuccessStatusCode();
        Assert.Empty((await response.Content.ReadFromJsonAsync<CalendarFeedDto>())!.BrokenSources ?? []);
        Assert.Empty((await admin.GetFromJsonAsync<List<CalendarFeedDto>>("/api/calendar-feeds"))!.Single().BrokenSources ?? []);
        Assert.True(Count(await IcsAsync(host.CreateClient(), PathOf(stale)), "BEGIN:VEVENT") > 0);
    }

    private sealed class UnreachableSource : Kyuplan.Api.Spond.IDataSource
    {
        public bool IsDemo => false;

        public Task<IReadOnlyList<Kyuplan.Api.Domain.GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) =>
            throw new Kyuplan.Api.Spond.SpondUnavailableException("down");

        public Task<IReadOnlyList<Kyuplan.Api.Domain.EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            throw new Kyuplan.Api.Spond.SpondUnavailableException("down");

        public void InvalidateCache()
        {
        }
    }

    [Fact]
    public async Task The_feed_list_still_works_when_the_data_source_is_unreachable()
    {
        using var host = CreateHost().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(s => s.AddSingleton<Kyuplan.Api.Spond.IDataSource>(new UnreachableSource())));
        var stale = await StoreFeedAsync(host, "Feed", new CalendarSourceDto(CalendarSourceKinds.Events, Group));
        var admin = host.CreateSignedInClient();

        var list = (await admin.GetFromJsonAsync<List<CalendarFeedDto>>("/api/calendar-feeds"))!;

        Assert.Empty(Assert.Single(list).BrokenSources ?? []);
        // A source that is down is no stale group: the feed answers 503 (calendar apps retry), not a 404 and not an empty calendar.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.CreateClient().GetAsync(PathOf(stale))).StatusCode);
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
