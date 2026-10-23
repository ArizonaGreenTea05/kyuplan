using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The manual data source against the real API: what users enter (groups, subgroups, members, events with invitations and answers) is what the rest
/// of the API sees, as if Spond had delivered it.
/// </summary>
public sealed class ManualDataApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
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
            builder.UseSetting("DataSource:Kind", "manual");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
        });

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task What_users_enter_is_what_the_rest_of_the_api_reads_as_if_spond_had_delivered_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(DataSourceKinds.Manual, (await admin.GetFromJsonAsync<ServerInfoDto>("/api/info"))!.DataSource);
        Assert.Empty((await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups"))!);

        var group = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Dojo"));
        var kids = await PostAsync<SubGroupDto>(admin, $"/api/manual/groups/{group.Id}/subgroups", new ManualNameInputDto("Kids"));
        var anna = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", new DateOnly(2014, 5, 1), [kids.Id]));
        var ben = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Ben", null, null));
        var start = DateTimeOffset.UtcNow.AddDays(-7);
        var training = await PostAsync<ManualEventDto>(admin, $"/api/manual/groups/{group.Id}/events", new ManualEventInputDto(
            "Training", start, start.AddHours(1), false,
            [new ManualInvitationDto(anna.Id, ManualRsvp.Accepted), new ManualInvitationDto(ben.Id, ManualRsvp.Declined)]));

        var groups = (await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups"))!;
        var read = Assert.Single(groups);
        Assert.Equal(("Dojo", 2), (read.Name, read.MemberCount));
        Assert.Equal("Kids", Assert.Single(read.SubGroups).Name);

        // The members are in the planner's own list at once, with what was entered.
        var members = (await admin.GetFromJsonAsync<List<MemberDto>>($"/api/members?groupId={group.Id}"))!;
        Assert.Equal(["Anna", "Ben"], members.Select(m => m.Name).Order());
        Assert.Equal(new DateOnly(2014, 5, 1), members.Single(m => m.Name == "Anna").BirthDate);

        var events = (await admin.GetFromJsonAsync<EventChoicesDto>($"/api/events?groupId={group.Id}"))!;
        Assert.Equal(training.Id, Assert.Single(events.Events).Id);

        var listed = (await admin.GetFromJsonAsync<List<ManualEventDto>>($"/api/manual/groups/{group.Id}/events"))!;
        Assert.Equal([ManualRsvp.Accepted, ManualRsvp.Declined], Assert.Single(listed).Invitations.OrderBy(i => i.MemberId == anna.Id ? 0 : 1).Select(i => i.Rsvp));
    }

    [Fact]
    public async Task Changing_and_deleting_is_seen_at_once_and_a_deleted_member_leaves_the_events_and_the_member_list()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var group = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Dojo"));
        var anna = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, null));
        var start = DateTimeOffset.UtcNow.AddDays(-3);
        var training = await PostAsync<ManualEventDto>(admin, $"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", start, start.AddHours(1), false, [new ManualInvitationDto(anna.Id, ManualRsvp.Unanswered)]));

        var renamed = await admin.PutAsJsonAsync($"/api/manual/groups/{group.Id}", new ManualNameInputDto("Karate"));
        Assert.Equal("Karate", (await renamed.Content.ReadFromJsonAsync<ManualGroupDto>())!.Name);
        var cancelled = await admin.PutAsJsonAsync($"/api/manual/events/{training.Id}",
            new ManualEventInputDto("Training", start, start.AddHours(1), true, [new ManualInvitationDto(anna.Id, ManualRsvp.Accepted)]));
        Assert.True((await cancelled.Content.ReadFromJsonAsync<ManualEventDto>())!.Cancelled);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/manual/members/{anna.Id}")).StatusCode);

        Assert.Empty((await admin.GetFromJsonAsync<List<MemberDto>>($"/api/members?groupId={group.Id}"))!);
        Assert.Empty(Assert.Single((await admin.GetFromJsonAsync<List<ManualEventDto>>($"/api/manual/groups/{group.Id}/events"))!).Invitations);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/manual/groups/{group.Id}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/manual/groups/{group.Id}/members")).StatusCode);
    }

    [Fact]
    public async Task A_sync_that_would_drop_most_members_is_refused_until_it_is_confirmed()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var group = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Dojo"));
        var anna = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, null));
        var ben = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Ben", null, null));
        var cara = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Cara", null, null));

        // The source changes behind the planner's back (not through the endpoints, which sync and confirm).
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var manual = scope.ServiceProvider.GetRequiredService<ManualDataService>();
            await manual.DeleteMemberAsync(ben.Id, CancellationToken.None);
            await manual.DeleteMemberAsync(cara.Id, CancellationToken.None);
        }

        var refused = await admin.PostAsync("/api/sync/run", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("confirmRemoval=true", await refused.Content.ReadAsStringAsync());
        Assert.Equal(3, (await admin.GetFromJsonAsync<List<MemberDto>>($"/api/members?groupId={group.Id}"))!.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/sync/run?confirmRemoval=true", null)).StatusCode);
        Assert.Equal(anna.Name, Assert.Single((await admin.GetFromJsonAsync<List<MemberDto>>($"/api/members?groupId={group.Id}"))!).Name);
    }

    [Fact]
    public async Task Deleting_a_subgroup_keeps_its_members_in_the_group()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var group = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Dojo"));
        var kids = await PostAsync<SubGroupDto>(admin, $"/api/manual/groups/{group.Id}/subgroups", new ManualNameInputDto("Kids"));
        await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, [kids.Id]));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/manual/subgroups/{kids.Id}")).StatusCode);

        var member = Assert.Single((await admin.GetFromJsonAsync<List<ManualMemberDto>>($"/api/manual/groups/{group.Id}/members"))!);
        Assert.Empty(member.SubGroupIds);
        Assert.Empty(Assert.Single((await admin.GetFromJsonAsync<List<ManualGroupDto>>("/api/manual/groups"))!).SubGroups);
    }

    [Fact]
    public async Task Wrong_input_is_refused_with_the_reason()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var group = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Dojo"));
        var other = await PostAsync<ManualGroupDto>(admin, "/api/manual/groups", new ManualNameInputDto("Team"));
        var stranger = await PostAsync<ManualMemberDto>(admin, $"/api/manual/groups/{other.Id}/members", new ManualMemberInputDto("Stranger", null, null));
        var otherSubGroup = await PostAsync<SubGroupDto>(admin, $"/api/manual/groups/{other.Id}/subgroups", new ManualNameInputDto("Elite"));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto("  "))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto("dojo"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/manual/groups/nope/subgroups", new ManualNameInputDto("Kids"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("", null, null))).StatusCode);
        // A subgroup or a member of another group cannot be used.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/members", new ManualMemberInputDto("Anna", null, [otherSubGroup.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", now, now.AddHours(1), false, [new ManualInvitationDto(stranger.Id, ManualRsvp.Accepted)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", now, now.AddHours(-1), false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/manual/groups/{group.Id}/events",
            new ManualEventInputDto("Training", now, now.AddHours(1), false, [new ManualInvitationDto(stranger.Id, "maybe")]))).StatusCode);
    }

    [Fact]
    public async Task Only_users_with_the_permission_may_change_the_manual_data()
    {
        using var host = CreateHost();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/manual/groups")).StatusCode);
    }

    [Fact]
    public async Task The_manual_data_can_only_be_changed_while_it_is_the_data_source()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataSource:Kind", "spond");
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
        });
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/manual/groups", new ManualNameInputDto("Dojo"))).StatusCode);
    }
}
