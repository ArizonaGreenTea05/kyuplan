using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Belts and the belt and start day of the members, against the real API and a real (in-memory) SQLite database. The
/// <see cref="PlannerApiClient"/> calls double as a wire-format check, like in <see cref="ExamApiTests"/>.
/// </summary>
public sealed class BeltApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    /// <summary>The club's "today" is 13 October 2026 (the clock is pinned).</summary>
    private static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

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
            TestAccounts.Configure(builder, _accounts);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(PinnedNow)));
        });

    private static BeltInputDto Input(string name, string color = "#FFFFFF", string? stripe = null, BeltPrerequisiteDto[]? from = null) =>
        new(name, color, stripe, from ?? []);

    /// <summary>The way to a belt from <paramref name="previous"/>, with what is required of a member coming from it.</summary>
    private static BeltPrerequisiteDto From(BeltDto previous, int minAge = 0, int minWait = 0) => new(previous.Id, minAge, minWait);

    private static async Task<BeltDto> CreateAsync(HttpClient client, BeltInputDto input)
    {
        var response = await client.PostAsJsonAsync("/api/belts", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BeltDto>())!;
    }

    private static async Task<List<BeltDto>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<BeltDto>>("/api/belts"))!;

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    /// <summary>Puts members into the database as the sync with Spond would; nothing here is about Spond.</summary>
    private static async Task<int> AddMemberAsync(WebApplicationFactory<Program> host, string name, string groupId = "g1", int? beltId = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var member = new Member
        {
            GroupId = groupId, SpondId = $"spond-{Guid.NewGuid():N}", Name = name, BeltId = beltId, CreatedAt = PinnedNow,
        };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    // --- Belts: creating and reading ----------------------------------------------------------------------------

    [Fact]
    public async Task There_are_no_belts_to_begin_with()
    {
        using var host = CreateHost();

        Assert.Empty(await ListAsync(host.CreateSignedInClient()));
    }

    [Fact]
    public async Task A_belt_is_created_with_what_it_requires_and_found_by_its_location()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));

        var response = await client.PostAsJsonAsync("/api/belts", Input("  Yellow ", "#ffd800", " #000000 ", from: [From(white, minAge: 8, minWait: 3)]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var yellow = (await response.Content.ReadFromJsonAsync<BeltDto>())!;
        // Named and coloured the way the clients show them: trimmed, colours in capitals.
        Assert.Equivalent(new BeltDto(yellow.Id, "Yellow", "#FFD800", "#000000", [new(white.Id, 8, 3)], 0), yellow, strict: true);
        Assert.EndsWith($"/api/belts/{yellow.Id}", response.Headers.Location!.ToString());
        Assert.Equivalent(yellow, await client.GetFromJsonAsync<BeltDto>(response.Headers.Location), strict: true);
        Assert.Equal(white.Id, Assert.Single(yellow.Prerequisites).BeltId);
    }

    [Fact]
    public async Task What_a_belt_requires_depends_on_the_belt_it_is_taken_from()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var blue = await CreateAsync(client, Input("Blue"));
        var blueBrown = await CreateAsync(client, Input("Blue-brown", from: [From(blue, minAge: 12, minWait: 6)]));

        // Brown can be taken from Blue and from Blue-brown, and from each it takes another age and waiting time.
        var brown = await CreateAsync(client, Input("Brown", from: [From(blue, minAge: 14, minWait: 12), From(blueBrown, minAge: 13, minWait: 6)]));

        Assert.Equal([new BeltPrerequisiteDto(blue.Id, 14, 12), new BeltPrerequisiteDto(blueBrown.Id, 13, 6)], brown.Prerequisites);
        Assert.Equal(brown.Prerequisites, (await ListAsync(client)).Single(b => b.Id == brown.Id).Prerequisites);
        Assert.Equal([new BeltPrerequisiteDto(blue.Id, 12, 6)], (await ListAsync(client)).Single(b => b.Id == blueBrown.Id).Prerequisites);
    }

    [Fact]
    public async Task A_belt_with_a_blank_stripe_has_none_and_the_first_belt_requires_nothing()
    {
        using var host = CreateHost();

        var white = await CreateAsync(host.CreateSignedInClient(), Input("White", stripe: "  "));

        Assert.Null(white.SecondaryColor);
        Assert.Empty(white.Prerequisites);
        Assert.Equal(0, white.MemberCount);
    }

    [Fact]
    public async Task Belts_come_in_the_order_of_the_progression_and_count_who_holds_them()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        // Made out of order on purpose: by name alone Blue would come first.
        var white = await CreateAsync(client, Input("White"));
        var yellow = await CreateAsync(client, Input("Yellow", from: [From(white)]));
        var orange = await CreateAsync(client, Input("Orange", from: [From(white), From(yellow)]));
        await CreateAsync(client, Input("Blue", from: [From(orange)]));
        await CreateAsync(client, Input("Another second belt", from: [From(white)]));
        await AddMemberAsync(host, "Anna", beltId: yellow.Id);
        await AddMemberAsync(host, "Ben", beltId: yellow.Id);
        await AddMemberAsync(host, "Clara", groupId: "g2", beltId: white.Id);

        var belts = await ListAsync(client);

        // Same step by name: "Another second belt" before "Yellow".
        Assert.Equal(["White", "Another second belt", "Yellow", "Orange", "Blue"], belts.Select(b => b.Name));
        Assert.Equal([1, 0, 2, 0, 0], belts.Select(b => b.MemberCount));
        Assert.Equal([white.Id, yellow.Id], belts.Single(b => b.Name == "Orange").Prerequisites.Select(p => p.BeltId));
        Assert.Empty(belts[0].Prerequisites);
    }

    [Fact]
    public async Task A_belt_that_does_not_exist_is_not_found()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/belts/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/belts/999", Input("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/belts/999")).StatusCode);
    }

    // --- Belts: what is refused ---------------------------------------------------------------------------------

    public static TheoryData<BeltInputDto, string> InvalidBelts => new()
    {
        { Input(""), "Name" },
        { Input("   "), "Name" },
        { Input(new string('x', BeltRules.MaxNameLength + 1)), "Name" },
        { Input("A", color: "green"), "PrimaryColor" },
        { Input("A", color: "FFFFFF"), "PrimaryColor" },
        { Input("A", color: "#FFF"), "PrimaryColor" },
        { Input("A", color: ""), "PrimaryColor" },
        { Input("A", stripe: "red"), "SecondaryColor" },
        // The belts named here need not exist: what is wrong with the numbers and the list is said before that is looked at.
        { Input("A", from: [new(1, -1, 0)]), "Prerequisites[0].MinAgeYears" },
        { Input("A", from: [new(1, BeltRules.MaxMinAgeYears + 1, 0)]), "Prerequisites[0].MinAgeYears" },
        { Input("A", from: [new(1, 0, -1)]), "Prerequisites[0].MinWaitMonths" },
        { Input("A", from: [new(1, 0, BeltRules.MaxMinWaitMonths + 1)]), "Prerequisites[0].MinWaitMonths" },
        { Input("A", from: [new(1, 0, 0), new(2, 0, -1)]), "Prerequisites[1].MinWaitMonths" },
        { Input("A", from: [new(1, 8, 3), new(1, 9, 4)]), "Prerequisites[1].BeltId" }, // a belt is listed once
    };

    [Theory]
    [MemberData(nameof(InvalidBelts))]
    public async Task A_belt_that_breaks_a_rule_is_refused_and_says_which(BeltInputDto input, string field)
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        var created = await client.PostAsJsonAsync("/api/belts", input);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.True((await ProblemAsync(created)).GetProperty("errors").TryGetProperty(field, out _), $"no error for {field}");

        var white = await CreateAsync(client, Input("White"));
        var updated = await client.PutAsJsonAsync($"/api/belts/{white.Id}", input);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);

        Assert.Equal(["White"], (await ListAsync(client)).Select(b => b.Name));
    }

    [Fact]
    public async Task A_belt_without_the_list_of_prerequisites_is_refused()
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PostAsJsonAsync("/api/belts",
            new { name = "White", primaryColor = "#FFFFFF" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("Prerequisites", out _));
    }

    [Fact]
    public async Task A_name_is_unique_whatever_its_case_and_the_belt_may_still_be_saved_under_its_own()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var yellow = await CreateAsync(client, Input("Yellow"));

        var duplicate = await client.PostAsJsonAsync("/api/belts", Input("YELLOW"));
        var rename = await client.PutAsJsonAsync($"/api/belts/{white.Id}", Input(" yellow "));
        var recase = await client.PutAsJsonAsync($"/api/belts/{yellow.Id}", Input("YELLOW"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Already exists", (await ProblemAsync(duplicate)).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recase.StatusCode);
        Assert.Equal(["White", "YELLOW"], (await ListAsync(client)).Select(b => b.Name));
    }

    [Fact]
    public async Task A_required_belt_must_exist()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();

        var response = await client.PostAsJsonAsync("/api/belts", Input("Yellow", from: [new(999, 0, 0)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid input", (await ProblemAsync(response)).GetProperty("title").GetString());
        Assert.Empty(await ListAsync(client));
    }

    [Fact]
    public async Task A_belt_cannot_require_itself_nor_a_belt_that_requires_it()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var yellow = await CreateAsync(client, Input("Yellow", from: [From(white)]));
        var orange = await CreateAsync(client, Input("Orange", from: [From(yellow)]));

        var itself = await client.PutAsJsonAsync($"/api/belts/{white.Id}", Input("White", from: [From(white)]));
        var directly = await client.PutAsJsonAsync($"/api/belts/{white.Id}", Input("White", from: [From(yellow)]));
        var throughOthers = await client.PutAsJsonAsync($"/api/belts/{white.Id}", Input("White", from: [From(orange)]));

        Assert.All([itself, directly, throughOthers], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Empty((await ListAsync(client)).Single(b => b.Id == white.Id).Prerequisites);
    }

    // --- Belts: changing and deleting ---------------------------------------------------------------------------

    [Fact]
    public async Task Changing_a_belt_replaces_everything_about_it_including_what_it_requires()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var yellow = await CreateAsync(client, Input("Yellow", from: [From(white)]));
        var orange = await CreateAsync(client, Input("Orange", "#FF8C00", from: [From(white, 8, 6), From(yellow, 6, 3)]));

        // Drops White, keeps Yellow with other numbers, and changes the rest.
        var response = await client.PutAsJsonAsync($"/api/belts/{orange.Id}",
            Input("Dark orange", "#cc5500", "#ffffff", from: [From(yellow, minAge: 11, minWait: 9)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<BeltDto>())!;
        Assert.Equivalent(new BeltDto(orange.Id, "Dark orange", "#CC5500", "#FFFFFF", [new(yellow.Id, 11, 9)], 0), changed, strict: true);
        Assert.Equivalent(changed, (await ListAsync(client)).Single(b => b.Id == orange.Id), strict: true);

        // Saving the very same belt again (the pair of belts stays) and giving it back White, with numbers of its own, works too.
        var again = await client.PutAsJsonAsync($"/api/belts/{orange.Id}",
            Input("Dark orange", "#CC5500", "#FFFFFF", from: [From(yellow, 11, 9), From(white, 7, 2)]));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal([new BeltPrerequisiteDto(white.Id, 7, 2), new BeltPrerequisiteDto(yellow.Id, 11, 9)], (await again.Content.ReadFromJsonAsync<BeltDto>())!.Prerequisites);

        // Only the numbers of one way change: the pair of belts stays and so does the other way.
        var numbers = await client.PutAsJsonAsync($"/api/belts/{orange.Id}",
            Input("Dark orange", "#CC5500", "#FFFFFF", from: [From(yellow, 12, 10), From(white, 7, 2)]));
        var renumbered = (await numbers.Content.ReadFromJsonAsync<BeltDto>())!;
        Assert.Equal([new BeltPrerequisiteDto(white.Id, 7, 2), new BeltPrerequisiteDto(yellow.Id, 12, 10)], renumbered.Prerequisites);
        Assert.Equal(renumbered.Prerequisites, (await ListAsync(client)).Single(b => b.Id == orange.Id).Prerequisites);

        var none = await client.PutAsJsonAsync($"/api/belts/{orange.Id}", Input("Dark orange", "#CC5500"));
        Assert.Empty((await none.Content.ReadFromJsonAsync<BeltDto>())!.Prerequisites);
        Assert.Null((await ListAsync(client)).Single(b => b.Id == orange.Id).SecondaryColor);
    }

    [Fact]
    public async Task Deleting_a_belt_takes_its_own_requirements_with_it_and_leaves_the_belts_it_required()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var yellow = await CreateAsync(client, Input("Yellow", from: [From(white, 8, 3)]));

        var response = await client.DeleteAsync($"/api/belts/{yellow.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["White"], (await ListAsync(client)).Select(b => b.Name));
        using var scope = host.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PlannerDbContext>().BeltPrerequisites.ToListAsync());
    }

    [Fact]
    public async Task A_belt_that_another_belt_requires_cannot_be_deleted_and_the_reason_names_it()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        await CreateAsync(client, Input("Yellow", from: [From(white)]));

        var response = await client.DeleteAsync($"/api/belts/{white.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("Belt in use", problem.GetProperty("title").GetString());
        Assert.Contains("'Yellow'", problem.GetProperty("detail").GetString());
        Assert.Equal(2, (await ListAsync(client)).Count);
    }

    [Fact]
    public async Task A_belt_that_a_member_holds_cannot_be_deleted_until_they_hold_another()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var memberId = await AddMemberAsync(host, "Anna", beltId: white.Id);

        var refused = await client.DeleteAsync($"/api/belts/{white.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("1 member", (await ProblemAsync(refused)).GetProperty("detail").GetString());

        await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(null, null));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/belts/{white.Id}")).StatusCode);
    }

    // --- Members -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Members_are_listed_by_name_and_can_be_narrowed_to_a_group()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        await AddMemberAsync(host, "clara");
        await AddMemberAsync(host, "Anna");
        await AddMemberAsync(host, "Dora", groupId: "g2");
        await AddMemberAsync(host, "Ben");

        var all = (await client.GetFromJsonAsync<List<MemberDto>>("/api/members"))!;
        var g1 = (await client.GetFromJsonAsync<List<MemberDto>>("/api/members?groupId=g1"))!;
        var g2 = (await client.GetFromJsonAsync<List<MemberDto>>("/api/members?groupId=g2"))!;
        var none = (await client.GetFromJsonAsync<List<MemberDto>>("/api/members?groupId=nope"))!;

        Assert.Equal(["Anna", "Ben", "clara", "Dora"], all.Select(m => m.Name));
        Assert.Equal(["Anna", "Ben", "clara"], g1.Select(m => m.Name));
        Assert.Equal(["Dora"], g2.Select(m => m.Name));
        Assert.Empty(none);
        Assert.All(all, m => Assert.Null(m.BeltId));
        Assert.All(all, m => Assert.Null(m.StartedAt));
        Assert.All(all, m => Assert.Null(m.BirthDate));
        Assert.All(all, m => Assert.Null(m.BeltSince));
    }

    [Fact]
    public async Task A_member_gets_a_belt_and_a_start_day_and_loses_them_again()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var memberId = await AddMemberAsync(host, "Anna");

        var set = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(white.Id, new DateOnly(2024, 9, 2)));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(new MemberDto(memberId, "g1", "Anna", white.Id, new DateOnly(2024, 9, 2)), await set.Content.ReadFromJsonAsync<MemberDto>());
        Assert.Equal(1, (await ListAsync(client)).Single().MemberCount);
        Assert.Equal(white.Id, (await client.GetFromJsonAsync<List<MemberDto>>("/api/members"))!.Single().BeltId);

        var cleared = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(null, null));

        Assert.Equal(new MemberDto(memberId, "g1", "Anna", null, null), await cleared.Content.ReadFromJsonAsync<MemberDto>());
        Assert.Equal(0, (await ListAsync(client)).Single().MemberCount);
    }

    [Fact]
    public async Task A_member_also_gets_the_day_of_the_belt_and_a_birth_date()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var memberId = await AddMemberAsync(host, "Anna");

        var set = await client.PutAsJsonAsync($"/api/members/{memberId}",
            new MemberInputDto(white.Id, new DateOnly(2024, 9, 2), BirthDate: new DateOnly(2014, 5, 3), BeltSince: new DateOnly(2025, 1, 15)));

        Assert.Equal(
            new MemberDto(memberId, "g1", "Anna", white.Id, new DateOnly(2024, 9, 2), new DateOnly(2014, 5, 3), new DateOnly(2025, 1, 15)),
            await set.Content.ReadFromJsonAsync<MemberDto>());
        Assert.Equal(new DateOnly(2025, 1, 15), (await client.GetFromJsonAsync<List<MemberDto>>("/api/members"))!.Single().BeltSince);
    }

    [Fact]
    public async Task Without_a_belt_there_is_no_day_it_was_received()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");

        var set = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(null, null, BirthDate: new DateOnly(2014, 5, 3), BeltSince: new DateOnly(2025, 1, 15)));

        var member = (await set.Content.ReadFromJsonAsync<MemberDto>())!;
        Assert.Null(member.BeltSince);
        Assert.Equal(new DateOnly(2014, 5, 3), member.BirthDate); // the birth date does not depend on a belt
    }

    [Theory]
    [InlineData("1899-12-31", false)]
    [InlineData("1900-01-01", true)]
    [InlineData("2026-10-13", true)] // born today
    [InlineData("2026-10-14", false)] // tomorrow
    public async Task A_birth_date_must_be_since_1900_and_not_in_the_future(string day, bool accepted)
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");

        var response = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(null, null, BirthDate: DateOnly.Parse(day)));

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (!accepted) Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("BirthDate", out _));
    }

    [Theory]
    [InlineData("1949-12-31", false)]
    [InlineData("1950-01-01", true)]
    [InlineData("2026-10-13", true)] // today
    [InlineData("2026-10-14", false)] // tomorrow
    public async Task The_day_a_belt_was_received_must_be_since_1950_and_not_in_the_future(string day, bool accepted)
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var white = await CreateAsync(client, Input("White"));
        var memberId = await AddMemberAsync(host, "Anna");

        var response = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(white.Id, null, BeltSince: DateOnly.Parse(day)));

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (!accepted) Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("BeltSince", out _));
    }

    [Fact]
    public async Task A_member_who_does_not_exist_or_a_belt_that_does_not_exist_is_refused()
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");

        var noMember = await client.PutAsJsonAsync("/api/members/999", new MemberInputDto(null, null));
        var noBelt = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(999, null));

        Assert.Equal(HttpStatusCode.NotFound, noMember.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBelt.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<List<MemberDto>>("/api/members"))!.Single().BeltId);
    }

    [Theory]
    [InlineData("1949-12-31", false)]
    [InlineData("1950-01-01", true)]
    [InlineData("2026-10-13", true)] // today
    [InlineData("2026-10-14", false)] // tomorrow
    public async Task A_start_day_must_be_since_1950_and_not_in_the_future(string day, bool accepted)
    {
        using var host = CreateHost();
        var client = host.CreateSignedInClient();
        var memberId = await AddMemberAsync(host, "Anna");

        var response = await client.PutAsJsonAsync($"/api/members/{memberId}", new MemberInputDto(null, DateOnly.Parse(day)));

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (!accepted) Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("StartedAt", out _));
    }

    // --- Through the client ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_client_creates_changes_lists_and_deletes_belts_and_sets_members()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var white = await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var yellow = await api.CreateBeltAsync(new BeltInputDto("Yellow", "#FFD800", "#000000", [new(white.Id, 8, 3)]));
        var memberId = await AddMemberAsync(host, "Anna");

        var renamed = await api.UpdateBeltAsync(yellow.Id, new BeltInputDto("Yellow belt", "#FFD800", null, [new(white.Id, 9, 4)]));
        var member = await api.UpdateMemberAsync(memberId, new MemberInputDto(white.Id, new DateOnly(2025, 2, 3)));

        Assert.Equal(("Yellow belt", null, new BeltPrerequisiteDto(white.Id, 9, 4)), (renamed.Name, renamed.SecondaryColor, Assert.Single(renamed.Prerequisites)));
        Assert.Equal(new MemberDto(memberId, "g1", "Anna", white.Id, new DateOnly(2025, 2, 3)), member);
        Assert.Equal(["White", "Yellow belt"], (await api.GetBeltsAsync()).Select(b => b.Name));
        Assert.Equal([member], await api.GetMembersAsync("g1"));
        Assert.Empty(await api.GetMembersAsync("a group/with spaces"));

        await api.UpdateMemberAsync(memberId, new MemberInputDto(null, null));
        await api.DeleteBeltAsync(yellow.Id);
        Assert.Equal(["White"], (await api.GetBeltsAsync()).Select(b => b.Name));
    }

    [Fact]
    public async Task The_client_reports_what_the_api_refused_in_its_words()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());
        var white = await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        await api.CreateBeltAsync(new BeltInputDto("Yellow", "#FFD800", null, [new(white.Id, 0, 0)]));

        var taken = await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateBeltAsync(new BeltInputDto("white", "#FFFFFF", null, [])));
        var inUse = await Assert.ThrowsAsync<PlannerApiException>(() => api.DeleteBeltAsync(white.Id));
        var invalid = await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateBeltAsync(new BeltInputDto("Red", "red", null, [])));

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("'white'", taken.Message);
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("'Yellow'", inUse.Message);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("#RRGGBB", invalid.Message);
    }

    [Fact]
    public void The_rules_of_the_clients_and_the_database_agree_on_how_long_a_name_may_be()
    {
        Assert.Equal(PlannerDbContext.NameLength, BeltRules.MaxNameLength);
    }

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#00a650", true)]
    [InlineData("#00A650", true)]
    [InlineData("green", false)]
    [InlineData("00A650", false)]
    [InlineData("#0A5", false)]
    [InlineData("#00A6500", false)]
    [InlineData("#00A65G", false)]
    [InlineData("#00A65 ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_colour_is_written_RRGGBB(string? color, bool isColor) => Assert.Equal(isColor, BeltRules.IsColor(color));
}
