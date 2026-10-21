using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The audit log against the real API: what is recorded, what is not, who may read it.</summary>
public sealed class AuditApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });

    private static async Task<AuditPageDto> ReadLogAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/audit" + query);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AuditPageDto>())!;
    }

    [Fact]
    public async Task Changes_to_belts_are_recorded_with_who_made_them()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var created = await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var belt = (await created.Content.ReadFromJsonAsync<BeltDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/belts/{belt.Id}", new BeltInputDto("Snow", "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/belts/{belt.Id}")).StatusCode);

        var log = await ReadLogAsync(admin, "?area=" + AuditAreas.Belts);

        // Newest first.
        Assert.Equal([AuditActions.Delete, AuditActions.Update, AuditActions.Create], log.Entries.Select(e => e.Action));
        Assert.All(log.Entries, e => Assert.Equal(AuditAreas.Belts, e.Area));
        Assert.All(log.Entries, e => Assert.NotEmpty(e.UserName));
        Assert.Equal($"id={belt.Id}", log.Entries[2].Target);
        Assert.Equal($"beltId={belt.Id}", log.Entries[0].Target);
        Assert.Contains("Snow", log.Entries[1].Details);
        Assert.Null(log.Entries[0].Details);
        Assert.Null(log.NextBefore);
    }

    [Fact]
    public async Task A_request_that_changed_nothing_leaves_nothing_behind()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        // Invalid (400), not found (404) and a duplicate name (409).
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("", "nope", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/belts/999")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto("white", "#FFFFFF", null, []))).StatusCode);

        var log = await ReadLogAsync(admin);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditActions.Create, entry.Action);
    }

    [Fact]
    public async Task Users_and_roles_are_recorded_but_never_a_password()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", UserPassword, "Anna", null, [], [Permission.ExamsView]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Trainer", [Permission.ExamsView, Permission.PaymentsEdit]))).StatusCode);

        var log = await ReadLogAsync(admin);

        Assert.Equal([AuditAreas.Roles, AuditAreas.Users], log.Entries.Select(e => e.Area));
        Assert.Contains(Permission.PaymentsEdit, log.Entries[0].Details);
        Assert.Contains("anna", log.Entries[1].Details);
        Assert.Contains(Permission.ExamsView, log.Entries[1].Details);
        Assert.All(log.Entries, e => Assert.DoesNotContain(UserPassword, e.Details));
        Assert.All(log.Entries, e => Assert.DoesNotContain("assword", e.Details));
    }

    [Fact]
    public async Task The_log_is_read_in_pages_and_can_be_narrowed_down()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        foreach (var name in new[] { "A", "B", "C" })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/belts", new BeltInputDto(name, "#FFFFFF", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("Trainer", [Permission.ExamsView]))).StatusCode);

        var first = await ReadLogAsync(admin, "?limit=3");
        var second = await ReadLogAsync(admin, $"?limit=3&before={first.NextBefore}");

        Assert.Equal(3, first.Entries.Count);
        Assert.NotNull(first.NextBefore);
        Assert.Single(second.Entries);
        Assert.Null(second.NextBefore);
        Assert.Equal(4, first.Entries.Concat(second.Entries).Select(e => e.Id).Distinct().Count());

        Assert.Equal(3, (await ReadLogAsync(admin, "?area=" + AuditAreas.Belts)).Entries.Count);
        Assert.Empty((await ReadLogAsync(admin, "?userId=9999")).Entries);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/audit?area=nope")).StatusCode);
    }

    [Fact]
    public async Task Only_whoever_has_audit_view_may_read_the_log()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("anna", UserPassword, null, null, [], [Permission.BeltsEdit]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("bob", UserPassword, null, null, [], [Permission.AuditView]))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);
        var bob = host.CreateSignedInClient("bob", UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await anna.PostAsJsonAsync("/api/belts", new BeltInputDto("White", "#FFFFFF", null, []))).StatusCode);

        var seen = await ReadLogAsync(bob, "?area=" + AuditAreas.Belts);

        // Whoever made the change is the one named, not whoever reads it.
        Assert.Single(seen.Entries);
        Assert.NotEqual("bob", seen.Entries[0].UserName);
    }
}
