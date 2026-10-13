using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// Exams (Spond events that are exams) and the filters saved for them, against the real API and a real (in-memory)
/// SQLite database. The <see cref="PlannerApiClient"/> calls double as a wire-format check, like in <see cref="ApiTests"/>.
/// </summary>
public sealed class ExamApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";

    /// <summary>
    /// The demo club's exams are in March and September and its data is generated around "today", so the clock is
    /// pinned: on 13 October 2026 the exams within the search window are the ones below.
    /// </summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly March = new(2026, 3, 28);
    private static readonly DateOnly September = new(2026, 9, 26);
    private static readonly DateOnly NextMarch = new(2027, 3, 27);

    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(PinnedNow)));
            configure?.Invoke(builder);
        });

    private PlannerApiClient CreateApi() => new(CreateHost().CreateSignedInClient());

    private sealed record ExamEvents(EventChoiceDto March, EventChoiceDto September, EventChoiceDto NextMarch);

    /// <summary>The three exams of the demo club's search window, oldest first.</summary>
    private static async Task<ExamEvents> ExamEventsAsync(PlannerApiClient api)
    {
        var exams = (await api.GetEventChoicesAsync(Group)).Events
            .Where(e => e.Name == "Gürtelprüfung").OrderBy(e => e.Start).ToList();

        Assert.Equal([March, September, NextMarch], exams.Select(e => DateOnly.FromDateTime(e.Start.Date)));
        return new ExamEvents(exams[0], exams[1], exams[2]);
    }

    private static ExamInputDto Input(EventChoiceDto exam) => new(Group, exam.Id);

    private static FilterSettingsDto Settings(
        string groupId = Group, string[]? subGroups = null, string? fromEvent = "e-from", string? toEvent = "e-to",
        int percent = 75, int invited = 3, string? contains = null, string? excludes = null) =>
        new(groupId, new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), fromEvent, toEvent,
            subGroups ?? [], percent, invited, contains, excludes);

    [Fact]
    public async Task There_are_no_exams_to_begin_with()
    {
        Assert.Empty(await CreateApi().GetExamsAsync());
    }

    [Fact]
    public async Task An_exam_is_an_event_created_listed_and_found_by_its_location()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var api = new PlannerApiClient(client);
        var events = await ExamEventsAsync(api);

        var response = await client.PostAsJsonAsync("/api/exams", Input(events.September));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExamDto>())!;
        // Name and day are the event's: nobody typed them. The invitations were recorded when the exam was created: it knows who was invited and who accepted.
        Assert.Equivalent(
            new ExamDto(created.Id, Group, events.September.Id, "Gürtelprüfung", September, [], created.InvitedCount, created.AttendedCount, PreSavedCount: 0, BeltsAwardedCount: 0, SubGroupIds: []),
            created, strict: true);
        Assert.True(created.InvitedCount > 0);
        Assert.InRange(created.AttendedCount, 1, created.InvitedCount);
        Assert.EndsWith($"/api/exams/{created.Id}", response.Headers.Location!.ToString());

        Assert.Equivalent(created, await client.GetFromJsonAsync<ExamDto>(response.Headers.Location), strict: true);
        Assert.Equivalent(new[] { created }, await api.GetExamsAsync(), strict: true);
    }

    [Fact]
    public async Task Exams_come_newest_first_and_the_data_survives_a_restart_of_the_api()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);
        await api.CreateExamAsync(Input(events.March));
        await api.CreateExamAsync(Input(events.NextMarch));
        await api.CreateExamAsync(Input(events.September));

        // A second host on the same database, as after a restart: the schema is already there and the data is kept.
        var exams = await new PlannerApiClient(CreateHost().CreateSignedInClient()).GetExamsAsync();

        Assert.Equal([NextMarch, September, March], exams.Select(e => e.Date));
        Assert.Equal([events.NextMarch.Id, events.September.Id, events.March.Id], exams.Select(e => e.EventId));
    }

    [Fact]
    public async Task An_event_is_at_most_one_exam()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);
        await api.CreateExamAsync(Input(events.September));

        var duplicate = await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateExamAsync(Input(events.September)));
        var other = await api.CreateExamAsync(Input(events.March));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("is already an exam", duplicate.Message);
        Assert.Equal(2, (await api.GetExamsAsync()).Count);
        Assert.Equal(events.March.Id, other.EventId);
    }

    [Theory]
    [InlineData("", "an-event", "groupId is required")]
    [InlineData("   ", "an-event", "groupId is required")]
    [InlineData(Group, "", "eventId is required")]
    [InlineData(Group, "  ", "eventId is required")]
    public async Task An_exam_needs_a_group_and_an_event(string groupId, string eventId, string expected)
    {
        var client = CreateHost().CreateSignedInClient();

        var response = await client.PostAsJsonAsync("/api/exams", new { groupId, eventId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_id_longer_than_the_column_is_rejected_not_cut_off()
    {
        var ex = await Assert.ThrowsAsync<PlannerApiException>(() =>
            CreateApi().CreateExamAsync(new ExamInputDto(Group, new string('x', PlannerDbContext.IdLength + 1))));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("at most 200 characters", ex.Message);
    }

    [Fact]
    public async Task The_event_has_to_exist_in_that_group_within_the_search_window()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);

        var noGroup = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateExamAsync(new ExamInputDto("no-such-group", events.September.Id)));
        var noEvent = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateExamAsync(new ExamInputDto(Group, "no-such-event")));
        var otherGroup = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateExamAsync(new ExamInputDto("demo-team", events.September.Id))); // the dojo's event is not the team's

        Assert.All([noGroup, noEvent, otherGroup], ex => Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode));
        Assert.Contains("no-such-group", noGroup.Message);
        Assert.Contains("no-such-event", noEvent.Message);
        Assert.Contains("12 months back and 6 months ahead", noEvent.Message);
        Assert.Empty(await api.GetExamsAsync()); // nothing was stored
    }

    [Fact]
    public async Task An_exam_can_be_pointed_at_another_event_but_not_at_one_that_is_an_exam_already()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);
        var exam = await api.CreateExamAsync(Input(events.March));
        await api.CreateExamAsync(Input(events.September));
        await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Keep me", Settings()));

        var moved = await api.UpdateExamAsync(exam.Id, Input(events.NextMarch));
        var clash = await Assert.ThrowsAsync<PlannerApiException>(() => api.UpdateExamAsync(exam.Id, Input(events.September)));
        var noEvent = await Assert.ThrowsAsync<PlannerApiException>(() => api.UpdateExamAsync(exam.Id, new ExamInputDto(Group, "no-such-event")));
        var missing = await Assert.ThrowsAsync<PlannerApiException>(() => api.UpdateExamAsync(4711, Input(events.NextMarch)));

        Assert.Equal(exam.Id, moved.Id);
        Assert.Equal(events.NextMarch.Id, moved.EventId);
        Assert.Equal(NextMarch, moved.Date); // name and day are the new event's
        Assert.Equal(["Keep me"], moved.Filters.Select(f => f.Name)); // the filters stay with the exam
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noEvent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("4711", missing.Message);
        Assert.Equal(events.NextMarch.Id, (await api.GetExamsAsync()).Single(e => e.Id == exam.Id).EventId); // the failed attempts changed nothing
    }

    [Fact]
    public async Task The_subgroups_chosen_for_a_new_exam_are_kept_and_survive_pointing_it_at_another_event()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);

        var created = await api.CreateExamAsync(new ExamInputDto(Group, events.March.Id, ["sg1", "sg2", "sg1", " "]));
        var plain = await api.CreateExamAsync(Input(events.September));

        Assert.Equal(["sg1", "sg2"], created.SubGroupIds); // doubles and blanks are dropped
        Assert.Empty(plain.SubGroupIds!); // nothing chosen = everyone
        Assert.Equal(["sg1", "sg2"], (await api.GetExamsAsync()).Single(e => e.Id == created.Id).SubGroupIds);

        var moved = await api.UpdateExamAsync(created.Id, Input(events.NextMarch)); // no subgroups said: it keeps them
        var changed = await api.UpdateExamAsync(created.Id, new ExamInputDto(Group, events.NextMarch.Id, ["sg2"]));
        var cleared = await api.UpdateExamAsync(created.Id, new ExamInputDto(Group, events.NextMarch.Id, []));

        Assert.Equal(["sg1", "sg2"], moved.SubGroupIds);
        Assert.Equal(["sg2"], changed.SubGroupIds);
        Assert.Empty(cleared.SubGroupIds!);
    }

    [Fact]
    public async Task Too_many_or_too_long_subgroup_ids_are_rejected_for_an_exam()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);

        var tooMany = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateExamAsync(new ExamInputDto(Group, events.March.Id, [.. Enumerable.Range(0, 101).Select(i => $"sg{i}")])));
        var tooLong = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateExamAsync(new ExamInputDto(Group, events.March.Id, [new string('x', PlannerDbContext.IdLength + 1)])));

        Assert.All([tooMany, tooLong], ex => Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode));
        Assert.Contains("At most 100 subgroups", tooMany.Message);
        Assert.Empty(await api.GetExamsAsync()); // nothing was stored
    }

    [Fact]
    public async Task A_filter_keeps_every_setting_it_was_saved_with()
    {
        var api = CreateApi();
        var exam = await api.CreateExamAsync(Input((await ExamEventsAsync(api)).September));
        var settings = Settings(subGroups: ["sg-kids", "sg-youth"], percent: 80, invited: 5, contains: "Training", excludes: "Offenes");

        var saved = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("  Filter for Exam on 10.10.26 ", settings));

        Assert.Equal(exam.Id, saved.ExamId);
        Assert.Equal("Filter for Exam on 10.10.26", saved.Name);
        Assert.Equivalent(settings, saved.Settings, strict: true);
        Assert.True(saved.Settings.SpanByEvents);

        var listed = (await api.GetExamsAsync()).Single().Filters.Single();
        Assert.Equivalent(saved, listed, strict: true);
        Assert.Equivalent(settings, listed.Settings, strict: true);
    }

    [Fact]
    public async Task A_filter_for_a_span_of_dates_keeps_no_events()
    {
        var api = CreateApi();
        var exam = await api.CreateExamAsync(Input((await ExamEventsAsync(api)).September));

        var saved = await api.CreateFilterAsync(exam.Id,
            new SavedFilterInputDto("By dates", Settings(fromEvent: null, toEvent: null, contains: "  ", excludes: "")));

        Assert.False(saved.Settings.SpanByEvents);
        Assert.Null(saved.Settings.FromEventId);
        Assert.Null(saved.Settings.ToEventId);
        Assert.Null(saved.Settings.EventNameContains); // blank terms are no terms
        Assert.Null(saved.Settings.EventNameExcludes);
        Assert.Equal(new DateOnly(2026, 3, 28), saved.Settings.From);
    }

    [Fact]
    public async Task One_exam_holds_many_filters_listed_by_name_and_each_exam_has_its_own_names()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);
        var exam = await api.CreateExamAsync(Input(events.September));
        var other = await api.CreateExamAsync(Input(events.March));
        await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Kids", Settings(subGroups: ["sg-kids"])));
        await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Adults", Settings(subGroups: ["sg-adults"])));
        await api.CreateFilterAsync(other.Id, new SavedFilterInputDto("Kids", Settings())); // same name, other exam

        var duplicate = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("kids", Settings())));

        var exams = (await api.GetExamsAsync()).ToDictionary(e => e.Id);
        Assert.Equal(["Adults", "Kids"], exams[exam.Id].Filters.Select(f => f.Name));
        Assert.Equal(["Kids"], exams[other.Id].Filters.Select(f => f.Name));
        Assert.All(exams[exam.Id].Filters, f => Assert.Equal(exam.Id, f.ExamId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already has a filter named 'kids'", duplicate.Message);
    }

    [Fact]
    public async Task A_saved_filter_can_be_replaced_and_renamed()
    {
        var api = CreateApi();
        var exam = await api.CreateExamAsync(Input((await ExamEventsAsync(api)).September));
        var first = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Spring", Settings(percent: 70)));
        var second = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Autumn", Settings()));

        var updated = await api.UpdateFilterAsync(exam.Id, first.Id,
            new SavedFilterInputDto("Spring 2026", Settings(percent: 90, subGroups: ["sg-kids"])));
        var clash = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.UpdateFilterAsync(exam.Id, second.Id, new SavedFilterInputDto("spring 2026", Settings())));
        var keepsOwnName = await api.UpdateFilterAsync(exam.Id, first.Id, new SavedFilterInputDto("Spring 2026", Settings(percent: 60)));

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("Spring 2026", updated.Name);
        Assert.Equal(90, updated.Settings.MinAttendancePercent);
        Assert.Equal(["sg-kids"], updated.Settings.SubGroupIds);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(60, keepsOwnName.Settings.MinAttendancePercent); // saving under its own name is not a clash
        Assert.Empty(keepsOwnName.Settings.SubGroupIds);
    }

    [Fact]
    public async Task A_filter_is_only_found_under_its_own_exam()
    {
        var api = CreateApi();
        var events = await ExamEventsAsync(api);
        var exam = await api.CreateExamAsync(Input(events.September));
        var other = await api.CreateExamAsync(Input(events.March));
        var saved = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Mine", Settings()));

        var update = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.UpdateFilterAsync(other.Id, saved.Id, new SavedFilterInputDto("Mine", Settings())));
        var delete = await Assert.ThrowsAsync<PlannerApiException>(() => api.DeleteFilterAsync(other.Id, saved.Id));
        var noExam = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateFilterAsync(4711, new SavedFilterInputDto("Orphan", Settings())));

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noExam.StatusCode);
        Assert.Single((await api.GetExamsAsync()).First(e => e.Id == exam.Id).Filters);
    }

    [Fact]
    public async Task Deleting_a_filter_leaves_the_exam_and_its_other_filters()
    {
        var api = CreateApi();
        var exam = await api.CreateExamAsync(Input((await ExamEventsAsync(api)).September));
        var keep = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Keep", Settings()));
        var drop = await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Drop", Settings()));

        await api.DeleteFilterAsync(exam.Id, drop.Id);

        var remaining = (await api.GetExamsAsync()).Single();
        Assert.Equal(exam.Id, remaining.Id);
        Assert.Equal([keep.Id], remaining.Filters.Select(f => f.Id));
        await Assert.ThrowsAsync<PlannerApiException>(() => api.DeleteFilterAsync(exam.Id, drop.Id));
    }

    [Fact]
    public async Task Deleting_an_exam_deletes_its_filters_and_only_its_own_and_leaves_the_event_free_to_be_an_exam_again()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var events = await ExamEventsAsync(api);
        var exam = await api.CreateExamAsync(Input(events.September));
        var other = await api.CreateExamAsync(Input(events.March));
        await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("One", Settings()));
        await api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("Two", Settings()));
        await api.CreateFilterAsync(other.Id, new SavedFilterInputDto("Three", Settings()));

        await api.DeleteExamAsync(exam.Id);

        Assert.Equal([other.Id], (await api.GetExamsAsync()).Select(e => e.Id));

        // The rows are gone from the table, not just hidden from the listing.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        Assert.Equal(["Three"], await db.SavedFilters.Select(f => f.Name).ToListAsync());

        var again = await Assert.ThrowsAsync<PlannerApiException>(() => api.DeleteExamAsync(exam.Id));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(events.September.Id, (await api.CreateExamAsync(Input(events.September))).EventId);
    }

    [Fact]
    public async Task A_filter_that_cannot_work_is_rejected_with_every_reason()
    {
        var api = CreateApi();
        var exam = await api.CreateExamAsync(Input((await ExamEventsAsync(api)).September));
        var broken = new FilterSettingsDto(
            " ", new DateOnly(2026, 9, 26), new DateOnly(2026, 3, 28), "only-one-event", null,
            [], 150, 0, null, null);

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.CreateFilterAsync(exam.Id, new SavedFilterInputDto("", broken)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("name is required", ex.Message);
        Assert.Contains("groupId is required", ex.Message);
        Assert.Contains("from must not be after to", ex.Message);
        Assert.Contains("fromEventId and toEventId must be given together", ex.Message);
        Assert.Contains("between 0 and 100", ex.Message);
        Assert.Contains("between 1 and 1000", ex.Message);
        Assert.Empty((await api.GetExamsAsync()).Single().Filters);
    }

    [Fact]
    public async Task The_request_body_is_required()
    {
        var client = CreateHost().CreateSignedInClient();

        var response = await client.PostAsync("/api/exams", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exams_are_behind_the_api_key_like_everything_else()
    {
        using var host = CreateHost(b => b.UseSetting("Api:ApiKey", "s3cret"));
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/exams")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/exams", new ExamInputDto(Group, "an-event"))).StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/exams")).StatusCode);
    }

    [Fact]
    public async Task Raw_json_is_camel_case_with_dates_as_text()
    {
        var client = CreateHost().CreateSignedInClient();
        var events = await ExamEventsAsync(new PlannerApiClient(client));
        var exam = await (await client.PostAsJsonAsync("/api/exams", Input(events.September))).Content.ReadFromJsonAsync<ExamDto>();
        await client.PostAsJsonAsync($"/api/exams/{exam!.Id}/filters", new SavedFilterInputDto("Mine", Settings()));

        var json = await client.GetStringAsync("/api/exams");

        Assert.Contains("\"date\":\"2026-09-26\"", json);
        Assert.Contains($"\"groupId\":\"{Group}\"", json);
        Assert.Contains($"\"eventId\":\"{events.September.Id}\"", json);
        Assert.Contains("\"filters\":[{", json);
        Assert.Contains("\"minAttendancePercent\":75", json);
        Assert.Contains("\"subGroupIds\":[]", json);
        Assert.DoesNotContain("spanByEvents", json); // derived, not part of the wire format
    }

    // --- An exam is its event: it follows the event on Spond ---------------------------------------------

    private static EventInfo SpondEvent(string id, string name, int month, int day) => new(
        id, name,
        new DateTimeOffset(2026, month, day, 10, 0, 0, TimeSpan.FromHours(1)),
        new DateTimeOffset(2026, month, day, 13, 0, 0, TimeSpan.FromHours(1)),
        Cancelled: false, new Dictionary<string, Rsvp>());

    /// <summary>A Spond that can be told to rename and move events, and to go away. Events within the search window of 13 October 2026.</summary>
    private sealed class FakeSpond : ISpondDataSource
    {
        public List<EventInfo> Events { get; } =
        [
            SpondEvent("exam-a", "Gürtelprüfung", 11, 7),
            SpondEvent("exam-b", "Dan-Prüfung", 12, 12),
        ];

        public bool Down { get; set; }

        public bool IsDemo => true;

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) =>
            Down
                ? Task.FromException<IReadOnlyList<GroupInfo>>(new SpondUnavailableException("Spond is down."))
                : Task.FromResult<IReadOnlyList<GroupInfo>>([new GroupInfo("g1", "Group", [], [])]);

        public Task<IReadOnlyList<EventInfo>> GetEventsAsync(
            string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventInfo>>(Events.Where(e => e.End >= from && e.End <= to).ToList());

        public void InvalidateCache()
        {
        }

        public void Change(string id, Func<EventInfo, EventInfo> change) =>
            Events[Events.FindIndex(e => e.Id == id)] = change(Events.Single(e => e.Id == id));
    }

    private (HttpClient Client, PlannerApiClient Api) CreateHostWith(FakeSpond spond)
    {
        var client = CreateHost(b => b.ConfigureTestServices(s => s.AddSingleton<ISpondDataSource>(spond))).CreateSignedInClient();
        return (client, new PlannerApiClient(client));
    }

    [Fact]
    public async Task An_exam_takes_the_name_and_day_of_its_event()
    {
        var (_, api) = CreateHostWith(new FakeSpond());

        var exam = await api.CreateExamAsync(new ExamInputDto("g1", "exam-b"));

        Assert.Equal(("g1", "exam-b", "Dan-Prüfung", new DateOnly(2026, 12, 12)), (exam.GroupId, exam.EventId, exam.Name, exam.Date));
    }

    [Fact]
    public async Task An_exam_follows_its_event_when_the_event_is_renamed_or_moved()
    {
        var spond = new FakeSpond();
        var (client, api) = CreateHostWith(spond);
        var first = await api.CreateExamAsync(new ExamInputDto("g1", "exam-a"));
        await api.CreateExamAsync(new ExamInputDto("g1", "exam-b"));
        Assert.Equal(["Dan-Prüfung", "Gürtelprüfung"], (await api.GetExamsAsync()).Select(e => e.Name)); // newest first

        // The club renames the first exam and moves it behind the second one.
        spond.Change("exam-a", e => SpondEvent("exam-a", "Gürtelprüfung Herbst", 12, 20));

        var exams = await api.GetExamsAsync();
        Assert.Equal(["Gürtelprüfung Herbst", "Dan-Prüfung"], exams.Select(e => e.Name));
        Assert.Equal(new DateOnly(2026, 12, 20), exams[0].Date);
        Assert.Equal(first.Id, exams[0].Id); // still the same exam, so its filters stay with it

        var one = (await client.GetFromJsonAsync<ExamDto>($"/api/exams/{first.Id}"))!;
        Assert.Equal(("Gürtelprüfung Herbst", new DateOnly(2026, 12, 20)), (one.Name, one.Date));
    }

    [Fact]
    public async Task An_exam_keeps_what_it_last_knew_when_Spond_cannot_be_asked_or_the_event_is_out_of_sight()
    {
        var spond = new FakeSpond();
        var (_, api) = CreateHostWith(spond);
        await api.CreateExamAsync(new ExamInputDto("g1", "exam-a"));
        spond.Change("exam-a", e => SpondEvent("exam-a", "Renamed", 11, 14));
        Assert.Equal("Renamed", (await api.GetExamsAsync()).Single().Name); // seen once, so it is stored

        spond.Down = true;
        var whileDown = Assert.Single(await api.GetExamsAsync()); // listing exams does not need Spond
        spond.Down = false;

        spond.Events.RemoveAll(e => e.Id == "exam-a"); // out of the search window, or gone
        var whileGone = Assert.Single(await api.GetExamsAsync());

        Assert.Equal(("Renamed", new DateOnly(2026, 11, 14)), (whileDown.Name, whileDown.Date));
        Assert.Equal(("Renamed", new DateOnly(2026, 11, 14)), (whileGone.Name, whileGone.Date));
    }

    [Fact]
    public async Task Creating_an_exam_records_the_members_who_were_invited_to_its_event_and_what_they_answered()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var events = await ExamEventsAsync(api);

        var exam = await api.CreateExamAsync(Input(events.September));

        var source = host.Services.GetRequiredService<ISpondDataSource>();
        var invited = (await source.GetEventsAsync(Group, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None))
            .Single(e => e.Id == events.September.Id).Responses;
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var participants = await db.ExamParticipants.AsNoTracking().Include(p => p.Member).Where(p => p.ExamId == exam.Id).ToListAsync();

        Assert.NotEmpty(participants);
        Assert.Equal(invited.Count, participants.Count);
        Assert.All(participants, p => Assert.Equal(invited[p.Member.SpondId], p.Rsvp));
        Assert.Equal(invited.Count(r => r.Value == Rsvp.Accepted), participants.Count(p => p.Attended));
        // The members of the group came along, whether they were invited or not.
        var group = (await source.GetGroupsAsync(CancellationToken.None)).Single(g => g.Id == Group);
        Assert.Equal(group.Members.Count, await db.Members.CountAsync(m => m.GroupId == Group));
    }
}

public sealed class DatabaseOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pp-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_relative_database_file_lives_under_the_content_root_and_its_folder_is_created()
    {
        var resolved = new DatabaseOptions().Resolve(_root);

        var file = Path.Combine(_root, "data", "kyuplan.db");
        Assert.Contains($"Data Source={file}", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public void An_absolute_path_is_kept_and_an_in_memory_database_touches_no_disk()
    {
        var absolute = Path.Combine(_root, "elsewhere", "planner.db");

        Assert.Contains(absolute, new DatabaseOptions { ConnectionString = $"Data Source={absolute}" }.Resolve("ignored"));

        var memoryRoot = Path.Combine(_root, "memory-root");
        var memory = new DatabaseOptions { ConnectionString = "Data Source=shared;Mode=Memory;Cache=Shared" }.Resolve(memoryRoot);
        Assert.Contains("Mode=Memory", memory);
        Assert.False(Directory.Exists(memoryRoot));
    }
}
