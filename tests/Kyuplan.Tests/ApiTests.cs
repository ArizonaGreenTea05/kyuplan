using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// Runs the real API in memory against the demo data source. The <see cref="PlannerApiClient"/> tests double as a
/// wire-format check: what the server serializes must be readable by the clients' source-generated JSON.
/// </summary>
public sealed class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private const int DefaultMinAttendancePercent = 70;

    private static readonly DateOnly To = DateOnly.FromDateTime(DateTime.Today);
    private static readonly DateOnly From = To.AddMonths(-6);

    private HttpClient CreateClient(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            builder.UseSetting("Statistics:DefaultMinAttendancePercent", DefaultMinAttendancePercent.ToString()); // the shipped appsettings.json is the club's own value
            builder.UseSetting("Statistics:ExamKeyword", "Prüfung"); // the demo club's exams; the default keyword is English
            TestAccounts.Configure(builder, _accounts);
            configure?.Invoke(builder);
        }).CreateSignedInClient();

    private PlannerApiClient CreateApi(Action<IWebHostBuilder>? configure = null) => new(CreateClient(configure));

    /// <summary>
    /// The demo club's exams are in March and September and its data is generated around "today", so tests of the
    /// exam search pin the clock: on 13 October 2026 the last exam was on 26 September, the next is on 27 March 2027.
    /// </summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly LastExam = new(2026, 9, 26);
    private static readonly DateOnly NextExam = new(2027, 3, 27);

    private PlannerApiClient CreateApiAt(DateTimeOffset now, Action<IWebHostBuilder>? configure = null) =>
        CreateApi(b =>
        {
            b.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now)));
            configure?.Invoke(b);
        });

    private static DateOnly DayOf(EventChoiceDto e) => DateOnly.FromDateTime(e.Start.Date);

    [Fact]
    public async Task Info_reports_demo_mode_and_the_configured_defaults()
    {
        var info = await CreateApi().GetInfoAsync();

        Assert.True(info.IsDemoData);
        Assert.Equal(DefaultMinAttendancePercent, info.DefaultMinAttendancePercent);
        Assert.Equal("Europe/Berlin", info.TimeZone);
    }

    [Fact]
    public async Task Groups_lists_the_demo_groups_with_subgroups()
    {
        var groups = await CreateApi().GetGroupsAsync();

        Assert.Equal(2, groups.Count);
        var dojo = groups.Single(g => g.Id == "demo-dojo");
        Assert.Equal(33, dojo.MemberCount); // 30 members and their 3 trainers
        Assert.Equal(["Erwachsene", "Jugend", "Kinder"], dojo.SubGroups.Select(s => s.Name).Order());
    }

    [Fact]
    public async Task Attendance_statistics_are_internally_consistent()
    {
        var api = CreateApi();
        var query = new AttendanceQuery("demo-dojo", From, To, MinAttendancePercent: 70, MinInvitedEvents: 5);

        var result = await api.GetAttendanceAsync(query);

        Assert.Equal(new AttendanceFilter("demo-dojo", From, To, MinAttendancePercent: 70, MinInvitedEvents: 5), result.Filter);
        Assert.NotEmpty(result.Members);
        Assert.All(result.Members, m =>
        {
            Assert.InRange(m.AttendanceRate, 0, 100);
            Assert.True(m.Attended <= m.Invited);
            Assert.Equal(m.Invited, m.Attended + m.Declined + m.Unanswered); // demo data has no waiting list
            var expected = m.Invited < 5 ? QualificationStatus.InsufficientData
                : m.Attended * 100 >= 70 * m.Invited ? QualificationStatus.Qualified
                : QualificationStatus.BelowThreshold;
            Assert.Equal(expected, m.Status);
        });
        Assert.Equal(result.Members.Count(m => m.Status == QualificationStatus.Qualified), result.Summary.QualifiedCount);
        Assert.Equal(result.Events.Count, result.Summary.EventCount);
        Assert.All(result.Events, e => Assert.True(e.Start < DateTimeOffset.Now, "future events must not count as held"));
        Assert.Equal(10, result.Distribution.Count);
    }

    [Fact]
    public async Task Subgroup_and_name_filters_narrow_the_result()
    {
        var api = CreateApi();
        var all = await api.GetAttendanceAsync(new AttendanceQuery("demo-dojo", From, To));

        var kids = await api.GetAttendanceAsync(new AttendanceQuery("demo-dojo", From, To, SubGroupIds: ["sg-kids"]));
        var trainings = await api.GetAttendanceAsync(new AttendanceQuery(
            "demo-dojo", From, To, EventNameContains: "Training", EventNameExcludes: "Offenes"));

        Assert.Equal(["sg-kids"], kids.Filter.SubGroupIds);
        Assert.Equal(9, kids.Members.Count);
        Assert.All(kids.Members, m => Assert.Contains("Kinder", m.SubGroups));
        Assert.True(trainings.Summary.EventCount < all.Summary.EventCount);
        Assert.DoesNotContain(trainings.Events, e => e.Name.Contains("Offenes"));
        Assert.DoesNotContain(trainings.Events, e => e.Name.Contains("Gürtelprüfung"));
    }

    [Fact]
    public async Task Several_subgroups_report_the_members_of_any_of_them()
    {
        var api = CreateApi();

        var kidsAndYouth = await api.GetAttendanceAsync(new AttendanceQuery("demo-dojo", From, To, SubGroupIds: ["sg-kids", "sg-youth"]));
        var everyone = await api.GetAttendanceAsync(new AttendanceQuery("demo-dojo", From, To, SubGroupIds: []));

        Assert.Equal(["sg-kids", "sg-youth"], kidsAndYouth.Filter.SubGroupIds);
        Assert.Equal(18, kidsAndYouth.Members.Count);
        Assert.All(kidsAndYouth.Members, m => Assert.True(m.SubGroups.Contains("Kinder") || m.SubGroups.Contains("Jugend")));
        Assert.Contains(kidsAndYouth.Members, m => m.SubGroups.Contains("Kinder"));
        Assert.Contains(kidsAndYouth.Members, m => m.SubGroups.Contains("Jugend"));
        Assert.Null(everyone.Filter.SubGroupIds); // nothing ticked is no filter, not a filter that matches nobody
        Assert.Equal(30, everyone.Members.Count);
    }

    [Fact]
    public async Task Dashboard_returns_upcoming_events_and_member_highlights()
    {
        var dashboard = await CreateApi().GetDashboardAsync("demo-dojo");

        Assert.Equal("demo-dojo", dashboard.GroupId);
        Assert.NotEmpty(dashboard.Upcoming);
        Assert.All(dashboard.Upcoming, u => Assert.Equal(u.Invited, u.Accepted + u.Declined + u.Unanswered));
        Assert.Equal(5, dashboard.TopMembers.Count);
        Assert.Equal(dashboard.To.AddDays(-89), dashboard.From);
    }

    [Fact]
    public async Task Dashboard_clamps_an_absurd_look_back_window()
    {
        var dashboard = await CreateApi().GetDashboardAsync("demo-dojo", days: 100_000);

        Assert.Equal(dashboard.To.AddDays(-364), dashboard.From);
    }

    [Fact]
    public async Task Invalid_filters_are_rejected_with_a_readable_message()
    {
        var api = CreateApi();

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.GetAttendanceAsync(new AttendanceQuery("demo-dojo", To, From, MinAttendancePercent: 150)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("from must not be after to", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
    }

    [Fact]
    public async Task Unknown_group_is_a_404()
    {
        var ex = await Assert.ThrowsAsync<PlannerApiException>(() =>
            CreateApi().GetAttendanceAsync(new AttendanceQuery("nope", From, To)));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Events_are_the_search_window_and_the_suggested_span_runs_from_the_last_exam_to_the_next()
    {
        var choices = await CreateApiAt(PinnedNow).GetEventChoicesAsync("demo-dojo");

        var from = choices.Events.Single(e => e.Id == choices.SuggestedFromEventId);
        var to = choices.Events.Single(e => e.Id == choices.SuggestedToEventId);
        Assert.Equal("Gürtelprüfung", from.Name);
        Assert.Equal("Gürtelprüfung", to.Name);
        Assert.Equal(LastExam, DayOf(from));
        Assert.Equal(NextExam, DayOf(to));

        // A year back and six months ahead of 13 October 2026, chronological, with the ordinary trainings too.
        Assert.Equal(choices.Events.OrderBy(e => e.Start), choices.Events);
        Assert.All(choices.Events, e => Assert.InRange(DayOf(e), new DateOnly(2025, 10, 13), new DateOnly(2027, 4, 13)));
        Assert.Contains(choices.Events, e => e.Name == "Kindertraining");
        Assert.Contains(choices.Events, e => DayOf(e) <= new DateOnly(2025, 10, 20)); // reaches back to the start of the window
        Assert.Contains(choices.Events, e => DayOf(e) >= new DateOnly(2027, 4, 6));  // and ahead to its end
    }

    [Fact]
    public async Task The_exam_keyword_comes_from_the_configuration()
    {
        var api = CreateApiAt(PinnedNow, b => b.UseSetting("Statistics:ExamKeyword", "Turnier"));

        var choices = await api.GetEventChoicesAsync("demo-dojo");

        // "Turnier" takes place in May and November.
        Assert.Equal(new DateOnly(2026, 5, 10), DayOf(choices.Events.Single(e => e.Id == choices.SuggestedFromEventId)));
        Assert.Equal(new DateOnly(2026, 11, 8), DayOf(choices.Events.Single(e => e.Id == choices.SuggestedToEventId)));
    }

    [Fact]
    public async Task A_group_without_exams_suggests_its_events_of_the_last_six_months()
    {
        var choices = await CreateApiAt(PinnedNow).GetEventChoicesAsync("demo-team");

        var from = choices.Events.Single(e => e.Id == choices.SuggestedFromEventId);
        var to = choices.Events.Single(e => e.Id == choices.SuggestedToEventId);
        Assert.Equal(choices.Events.First(e => DayOf(e) >= new DateOnly(2026, 4, 14)), from);
        Assert.Equal(choices.Events.Last(e => DayOf(e) <= new DateOnly(2026, 10, 13)), to);
    }

    [Fact]
    public async Task The_search_window_is_configurable_and_a_missing_exam_in_it_falls_back_to_the_recent_events()
    {
        // One month ahead of 13 October 2026 holds no exam: the next one is on 27 March 2027.
        var api = CreateApiAt(PinnedNow, b =>
        {
            b.UseSetting("Statistics:EventSearchMonthsBack", "1");
            b.UseSetting("Statistics:EventSearchMonthsAhead", "1");
        });

        var choices = await api.GetEventChoicesAsync("demo-dojo");

        Assert.All(choices.Events, e => Assert.InRange(DayOf(e), new DateOnly(2026, 9, 13), new DateOnly(2026, 11, 13)));

        // Not the exams, but everything the window holds up to today.
        Assert.Equal(choices.Events[0].Id, choices.SuggestedFromEventId);
        Assert.Equal(new DateOnly(2026, 10, 13), DayOf(choices.Events.Single(e => e.Id == choices.SuggestedToEventId)));
    }

    [Fact]
    public async Task The_search_window_reaches_back_and_ahead_independently()
    {
        var api = CreateApiAt(PinnedNow, b =>
        {
            b.UseSetting("Statistics:EventSearchMonthsBack", "2");
            b.UseSetting("Statistics:EventSearchMonthsAhead", "3");
        });

        var choices = await api.GetEventChoicesAsync("demo-dojo");

        Assert.All(choices.Events, e => Assert.InRange(DayOf(e), new DateOnly(2026, 8, 13), new DateOnly(2027, 1, 13)));
        Assert.Contains(choices.Events, e => DayOf(e) <= new DateOnly(2026, 8, 20));
        Assert.Contains(choices.Events, e => DayOf(e) >= new DateOnly(2027, 1, 6));
    }

    [Fact]
    public async Task Events_of_an_unknown_group_are_a_404()
    {
        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => CreateApi().GetEventChoicesAsync("nope"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Two_events_define_the_same_span_as_their_two_days()
    {
        var api = CreateApiAt(PinnedNow);
        var choices = await api.GetEventChoicesAsync("demo-dojo");

        var byEvents = await api.GetAttendanceAsync(new AttendanceQuery(
            "demo-dojo", FromEventId: choices.SuggestedFromEventId, ToEventId: choices.SuggestedToEventId, MinAttendancePercent: 70));
        var byDates = await api.GetAttendanceAsync(new AttendanceQuery(
            "demo-dojo", LastExam, NextExam, MinAttendancePercent: 70));

        Assert.Equal(new AttendanceFilter("demo-dojo", LastExam, NextExam, MinAttendancePercent: 70), byEvents.Filter);
        Assert.Equal(byDates.Filter, byEvents.Filter);
        Assert.Equal(byDates.Summary, byEvents.Summary);
        Assert.Equal(
            byDates.Members.Select(m => (m.MemberId, m.Invited, m.Attended, m.Status)),
            byEvents.Members.Select(m => (m.MemberId, m.Invited, m.Attended, m.Status)));
        Assert.NotEmpty(byEvents.Events); // trainings since the last exam, up to "now"
        Assert.All(byEvents.Events, e => Assert.True(DateOnly.FromDateTime(e.Start.Date) >= LastExam));
    }

    [Fact]
    public async Task An_event_that_is_not_in_the_search_window_is_a_404()
    {
        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => CreateApiAt(PinnedNow).GetAttendanceAsync(
            new AttendanceQuery("demo-dojo", FromEventId: "demo-dojo-20250501-1", ToEventId: "does-not-exist")));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("demo-dojo-20250501-1", ex.Message); // a demo event from more than a year back
        Assert.Contains("12 months back and 6 months ahead", ex.Message);
    }

    [Fact]
    public async Task Events_in_the_wrong_order_are_rejected()
    {
        var api = CreateApiAt(PinnedNow);
        var choices = await api.GetEventChoicesAsync("demo-dojo");

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetAttendanceAsync(new AttendanceQuery(
            "demo-dojo", FromEventId: choices.SuggestedToEventId, ToEventId: choices.SuggestedFromEventId)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("fromEventId must not be after toEventId", ex.Message);
    }

    [Theory]
    [InlineData("", "The time span is required")]
    [InlineData("&from=2026-01-01", "from and to must be given together")]
    [InlineData("&toEventId=x", "fromEventId and toEventId must be given together")]
    [InlineData("&from=2026-01-01&to=2026-02-01&fromEventId=a&toEventId=b", "not both")]
    public async Task A_time_span_is_required_exactly_once(string span, string expectedMessage)
    {
        var response = await CreateClient().GetAsync($"/api/statistics/attendance?groupId=demo-dojo{span}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_spond_configuration_is_reported_as_503_not_a_crash()
    {
        var api = CreateApi(b => b.UseSetting("Spond:UseDemoData", "false"));

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetGroupsAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Contains("credentials", ex.Message);
    }

    [Fact]
    public async Task Api_key_is_enforced_when_configured_and_accepts_the_right_one()
    {
        void RequireKey(IWebHostBuilder b) => b.UseSetting("Api:ApiKey", "s3cret");

        var anonymous = await CreateClient(RequireKey).GetAsync("/api/info");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var wrong = CreateClient(RequireKey);
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/info")).StatusCode);

        var right = CreateClient(RequireKey);
        right.DefaultRequestHeaders.Add("X-Api-Key", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await right.GetAsync("/api/info")).StatusCode);

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => new PlannerApiClient(CreateClient(RequireKey)).GetInfoAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Configured_browser_origins_may_call_the_api_and_others_may_not()
    {
        var client = CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        allowed.Headers.Add("Origin", "http://localhost:5200");
        var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        foreign.Headers.Add("Origin", "https://evil.example");

        Assert.True((await client.SendAsync(allowed)).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await client.SendAsync(foreign)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Refresh_succeeds_and_data_can_be_read_again_afterwards()
    {
        var api = CreateApi();

        await api.RefreshAsync();

        Assert.NotEmpty(await api.GetGroupsAsync());
    }

    [Fact]
    public async Task Raw_json_uses_camel_case_and_string_enums()
    {
        var client = CreateClient();

        var json = await client.GetStringAsync(
            $"/api/statistics/attendance?groupId=demo-dojo&from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}");

        Assert.Contains("\"attendanceRate\":", json);
        Assert.Matches("\"status\":\"(Qualified|BelowThreshold|InsufficientData)\"", json);
        Assert.Contains($"\"from\":\"{From:yyyy-MM-dd}\"", json);
    }
}
