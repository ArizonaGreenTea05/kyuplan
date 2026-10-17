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
/// The endpoints around an exam (<c>/api/exams/{id}/members</c>, <c>/suggestions</c>, <c>/candidates</c>, <c>/results</c>) against the
/// real API, the demo club and a real (in-memory) SQLite database, through the client so that the wire format is checked, too. The rules
/// behind them are tested in <see cref="ExamPlanningTests"/> and <see cref="ExamSuggestionCalculatorTests"/>.
/// </summary>
public sealed class ExamPlanningApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Group = "demo-dojo";
    private const int DefaultMinAttendancePercent = 75;

    /// <summary>On 13 October 2026 the demo club's exams are the ones of March and September 2026 (past) and March 2027 (to come).</summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly September = new(2026, 9, 26);
    private static readonly DateOnly NextMarch = new(2027, 3, 27);

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
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            builder.UseSetting("Statistics:DefaultMinAttendancePercent", DefaultMinAttendancePercent.ToString()); // the shipped appsettings.json is the club's own value
            builder.UseSetting("Statistics:ExamMinAverageStars", "0"); // the fitness tests are about attendance, age and waiting time; stars have their own
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(PinnedNow)));
        });

    private sealed record Club(PlannerApiClient Api, HttpClient Http, ExamDto Past, ExamDto Next);

    /// <summary>A host with the exam of September (past) and the one of March 2027 (to come) as exams, and the members the sync made from them.</summary>
    private async Task<Club> StartAsync()
    {
        var http = CreateHost().CreateSignedInClient();
        var api = new PlannerApiClient(http);
        var events = (await api.GetEventChoicesAsync(Group)).Events.Where(e => e.Name == "Gürtelprüfung").ToList();

        var past = await api.CreateExamAsync(new ExamInputDto(Group, events.Single(e => DateOnly.FromDateTime(e.Start.Date) == September).Id));
        var next = await api.CreateExamAsync(new ExamInputDto(Group, events.Single(e => DateOnly.FromDateTime(e.Start.Date) == NextMarch).Id));
        return new Club(api, http, past, next);
    }

    private static FilterSettingsDto Settings(string group = Group, int percent = 75) =>
        new(group, new DateOnly(2026, 9, 26), new DateOnly(2027, 3, 27), null, null, [], percent, 3, null, null);

    private static async Task<HttpStatusCode> StatusOfAsync(Func<Task> call) =>
        (await Assert.ThrowsAsync<PlannerApiException>(call)).StatusCode ?? throw new InvalidOperationException("no status");

    // --- Who is in an exam ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_members_of_an_exam_are_those_invited_on_spond_with_their_answer()
    {
        var club = await StartAsync();

        var members = await club.Api.GetExamMembersAsync(club.Past.Id);

        Assert.Equal(club.Past.InvitedCount, members.Count);
        Assert.All(members, m =>
        {
            Assert.NotNull(m.Invitation);
            Assert.False(m.IsPreSaved);
            Assert.Null(m.ReceivedBeltId);
            Assert.Null(m.BeltId);
        });
        Assert.Equal(members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => m.MemberId), members.Select(m => m.MemberId));
        Assert.Equal(club.Past.AttendedCount, members.Count(m => m.Invitation == AttendanceMark.Attended));
    }

    [Fact]
    public async Task The_exam_list_counts_invitations_and_the_members_have_birth_dates_from_the_demo_club()
    {
        var club = await StartAsync();

        var exams = await club.Api.GetExamsAsync();

        Assert.Equal([NextMarch, September], exams.Select(e => e.Date));
        Assert.All(exams, e => Assert.True(e.InvitedCount > 0 && e.AttendedCount <= e.InvitedCount));
        Assert.All(exams, e => Assert.Equal((0, 0), (e.PreSavedCount, e.BeltsAwardedCount)));

        var members = await club.Api.GetMembersAsync(Group);
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.NotNull(m.BirthDate));
        Assert.All(members, m => Assert.InRange(m.BirthDate!.Value.Year, 1960, 2020));
    }

    // --- Who is fit ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_belts_nobody_can_be_suggested_and_everybody_needs_one()
    {
        var club = await StartAsync();

        var result = await club.Api.GetExamSuggestionsAsync(club.Next.Id);

        Assert.Equal((club.Next.Id, NextMarch), (result.ExamId, result.ExamDate));
        Assert.Null(result.FilterId);
        Assert.Equal((September, NextMarch, DefaultMinAttendancePercent, 3), (result.Filter.From, result.Filter.To, result.Filter.MinAttendancePercent, result.Filter.MinInvitedEvents));
        Assert.Equal((await club.Api.GetMembersAsync(Group)).Count, result.Members.Count);
        Assert.All(result.Members, m =>
        {
            Assert.Equal(SuggestionVerdict.NeedsData, m.Verdict);
            Assert.Null(m.NextBeltId);
        });
    }

    [Fact]
    public async Task With_belts_the_members_who_meet_everything_are_fit_and_the_list_says_why_the_others_are_not()
    {
        var club = await StartAsync();
        var white = await club.Api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        await club.Api.CreateBeltAsync(new BeltInputDto("Yellow", "#FFD800", null, [new BeltPrerequisiteDto(white.Id, 9, 0)]));
        foreach (var member in await club.Api.GetMembersAsync(Group))
            await club.Api.UpdateMemberAsync(member.Id, new MemberInputDto(white.Id, member.StartedAt, member.BirthDate, BeltSince: new DateOnly(2025, 1, 1)));

        var rows = (await club.Api.GetExamSuggestionsAsync(club.Next.Id)).Members;

        Assert.Contains(rows, r => r.Verdict == SuggestionVerdict.Fit);
        Assert.Contains(rows, r => r.Verdict == SuggestionVerdict.NotYet);
        Assert.All(rows, r =>
        {
            Assert.Equal(white.Id, r.BeltId);
            Assert.NotNull(r.NextBeltId);
            Assert.Equal(9, r.MinAgeYears);
            // The verdict follows from the parts: fit means that nothing is missed.
            var fit = r.AttendanceStatus == QualificationStatus.Qualified && r.AgeCheck == CheckState.Met && r.WaitCheck == CheckState.Met;
            Assert.Equal(fit, r.Verdict == SuggestionVerdict.Fit);
            // Age is counted to the day of the exam.
            Assert.Equal(r.AgeYears >= 9, r.AgeCheck == CheckState.Met);
        });
        // The fit come first.
        var firstOther = rows.ToList().FindIndex(r => r.Verdict != SuggestionVerdict.Fit);
        Assert.DoesNotContain(rows.Skip(firstOther), r => r.Verdict == SuggestionVerdict.Fit);
    }

    [Fact]
    public async Task A_saved_filter_is_chosen_by_its_id_and_reported_back()
    {
        var club = await StartAsync();
        var anything = await club.Api.CreateFilterAsync(club.Next.Id, new SavedFilterInputDto("Anything goes", Settings(percent: 0)));

        var result = await club.Api.GetExamSuggestionsAsync(club.Next.Id, anything.Id);

        Assert.Equal((anything.Id, "Anything goes"), (result.FilterId, result.FilterName));
        Assert.Equal(0, result.Filter.MinAttendancePercent);
    }

    [Fact]
    public async Task A_filter_of_another_group_or_one_that_does_not_exist_is_refused()
    {
        var club = await StartAsync();
        var other = await club.Api.CreateFilterAsync(club.Next.Id, new SavedFilterInputDto("Of the team", Settings(group: "demo-team")));

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(() => club.Api.GetExamSuggestionsAsync(club.Next.Id, other.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(() => club.Api.GetExamSuggestionsAsync(club.Next.Id, 9999)));
    }

    [Fact]
    public async Task Everything_around_an_exam_that_does_not_exist_is_not_found()
    {
        var club = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => club.Api.GetExamMembersAsync(9999)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => club.Api.GetExamSuggestionsAsync(9999)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => club.Api.SetExamCandidatesAsync(9999, new ExamCandidatesInputDto([], []))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => club.Api.SetExamResultAsync(9999, 1, new ExamResultInputDto(null))));
    }

    // --- Saving members for an exam ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Members_are_saved_for_an_exam_and_come_back_with_it()
    {
        var club = await StartAsync();
        var group = await club.Api.GetMembersAsync(Group);
        var chosen = group.Take(3).Select(m => m.Id).ToList();

        var saved = await club.Api.SetExamCandidatesAsync(club.Next.Id, new ExamCandidatesInputDto(chosen, []));

        Assert.Equal(chosen.Order(), saved.Where(m => m.IsPreSaved).Select(m => m.MemberId).Order());
        Assert.Equal(saved.Select(m => m.MemberId), (await club.Api.GetExamMembersAsync(club.Next.Id)).Select(m => m.MemberId));
        Assert.Equal(3, (await club.Api.GetExamsAsync()).Single(e => e.Id == club.Next.Id).PreSavedCount);
        // The suggestions show it, too.
        Assert.Equal(3, (await club.Api.GetExamSuggestionsAsync(club.Next.Id)).Members.Count(m => m.IsPreSaved));

        await club.Api.SetExamCandidatesAsync(club.Next.Id, new ExamCandidatesInputDto([], chosen.Take(2).ToList()));
        Assert.Equal(1, (await club.Api.GetExamsAsync()).Single(e => e.Id == club.Next.Id).PreSavedCount);
    }

    [Fact]
    public async Task Members_to_save_must_be_known_and_a_list_is_required()
    {
        var club = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(() => club.Api.SetExamCandidatesAsync(club.Next.Id, new ExamCandidatesInputDto([9999], []))));

        var url = $"/api/exams/{club.Next.Id}/candidates";
        var noLists = await club.Http.PutAsJsonAsync(url, new { });
        var noRemove = await club.Http.PutAsJsonAsync(url, new { add = Array.Empty<int>() });
        var tooMany = await club.Http.PutAsJsonAsync(url, new { add = Enumerable.Range(1, 2001), remove = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, noLists.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noRemove.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(0, (await club.Api.GetExamsAsync()).Single(e => e.Id == club.Next.Id).PreSavedCount);
    }

    // --- Belts received at an exam ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_belt_is_recorded_for_a_member_at_an_exam_that_has_taken_place_and_the_member_holds_it_from_then_on()
    {
        var club = await StartAsync();
        var white = await club.Api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var member = (await club.Api.GetExamMembersAsync(club.Past.Id)).First(m => m.Invitation == AttendanceMark.Attended);

        var row = await club.Api.SetExamResultAsync(club.Past.Id, member.MemberId, new ExamResultInputDto(white.Id));

        Assert.Equal((member.MemberId, white.Id, white.Id), (row.MemberId, row.ReceivedBeltId ?? 0, row.BeltId ?? 0));
        var held = (await club.Api.GetMembersAsync(Group)).Single(m => m.Id == member.MemberId);
        Assert.Equal((white.Id, September), (held.BeltId ?? 0, held.BeltSince ?? default));
        Assert.Equal(1, (await club.Api.GetExamsAsync()).Single(e => e.Id == club.Past.Id).BeltsAwardedCount);
        Assert.Equal(1, (await club.Api.GetBeltsAsync()).Single().MemberCount);

        // A belt that was handed out cannot be deleted, not even after the member holds another one.
        var inUse = await Assert.ThrowsAsync<PlannerApiException>(() => club.Api.DeleteBeltAsync(white.Id));
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);

        var taken = await club.Api.SetExamResultAsync(club.Past.Id, member.MemberId, new ExamResultInputDto(null));
        Assert.Null(taken.ReceivedBeltId);
        Assert.Equal(0, (await club.Api.GetExamsAsync()).Single(e => e.Id == club.Past.Id).BeltsAwardedCount);
    }

    [Fact]
    public async Task A_belt_cannot_be_recorded_before_the_exam_and_must_exist()
    {
        var club = await StartAsync();
        var white = await club.Api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var member = (await club.Api.GetMembersAsync(Group)).First();

        var early = await Assert.ThrowsAsync<PlannerApiException>(() => club.Api.SetExamResultAsync(club.Next.Id, member.Id, new ExamResultInputDto(white.Id)));
        var noBelt = await Assert.ThrowsAsync<PlannerApiException>(() => club.Api.SetExamResultAsync(club.Past.Id, member.Id, new ExamResultInputDto(9999)));
        var noMember = await Assert.ThrowsAsync<PlannerApiException>(() => club.Api.SetExamResultAsync(club.Past.Id, 9999, new ExamResultInputDto(white.Id)));

        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("taken place", early.Message);
        Assert.Equal(HttpStatusCode.BadRequest, noBelt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noMember.StatusCode);
        Assert.Null((await club.Api.GetMembersAsync(Group)).First().BeltId);
    }

    // --- The data and who may see it ---------------------------------------------------------------------------------------

    [Fact]
    public async Task What_the_planner_holds_survives_a_restart_of_the_api()
    {
        var club = await StartAsync();
        var white = await club.Api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var members = await club.Api.GetMembersAsync(Group);
        await club.Api.SetExamCandidatesAsync(club.Next.Id, new ExamCandidatesInputDto([members[0].Id], []));
        var attended = (await club.Api.GetExamMembersAsync(club.Past.Id)).First(m => m.Invitation == AttendanceMark.Attended);
        await club.Api.SetExamResultAsync(club.Past.Id, attended.MemberId, new ExamResultInputDto(white.Id));

        // A second host on the same database, as after a restart.
        var again = new PlannerApiClient(CreateHost().CreateSignedInClient());

        Assert.Equal([members[0].Id], (await again.GetExamMembersAsync(club.Next.Id)).Where(m => m.IsPreSaved).Select(m => m.MemberId));
        Assert.Equal(white.Id, (await again.GetExamMembersAsync(club.Past.Id)).Single(m => m.MemberId == attended.MemberId).ReceivedBeltId);
    }

    [Theory]
    [InlineData("/api/exams/1/members")]
    [InlineData("/api/exams/1/suggestions")]
    public async Task Without_a_login_nothing_around_an_exam_is_readable(string url)
    {
        var response = await CreateHost().CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
