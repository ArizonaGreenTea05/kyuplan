using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The belts and roles a new database starts with, against the real API and real (in-memory) databases. Every other test host
/// switches the defaults off (<c>DefaultData:Enabled</c>) and fills its own tables; these are the ones that want them.
/// </summary>
public sealed class DefaultDataTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    /// <summary>
    /// A start of the API on the two databases of this test: the first creates them, later ones find them there. In memory they live
    /// as long as the test, so "the API is started again" is another call.
    /// </summary>
    private HttpClient StartApi(bool defaults = true) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", defaults ? "true" : "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
        }).CreateSignedInClient();

    private static async Task<List<BeltDto>> BeltsAsync(HttpClient client) =>
        await client.GetFromJsonAsync<List<BeltDto>>("/api/belts") ?? throw new InvalidOperationException("No belts.");

    private static async Task<List<RoleDto>> RolesAsync(HttpClient client) =>
        await client.GetFromJsonAsync<List<RoleDto>>("/api/roles") ?? throw new InvalidOperationException("No roles.");

    [Fact]
    public async Task A_new_planner_database_starts_with_the_default_belts_in_the_order_of_the_progression()
    {
        var belts = await BeltsAsync(StartApi());

        Assert.Equal(DefaultBelts.All.Select(b => b.Name), belts.Select(b => b.Name));
        Assert.Equal(DefaultBelts.All.Select(b => (b.PrimaryColor, b.SecondaryColor)), belts.Select(b => (b.PrimaryColor, b.SecondaryColor)));

        var names = belts.ToDictionary(b => b.Id, b => b.Name);
        foreach (var expected in DefaultBelts.All)
        {
            var actual = belts.Single(b => b.Name == expected.Name);
            Assert.Equal(
                expected.Prerequisites.OrderBy(p => p.RequiredBelt, StringComparer.Ordinal),
                actual.Prerequisites.Select(p => new DefaultBeltPrerequisite(names[p.BeltId], p.MinAgeYears, p.MinWaitMonths)).OrderBy(p => p.RequiredBelt, StringComparer.Ordinal));
        }

        Assert.All(belts, b => Assert.Equal(0, b.MemberCount));
    }

    [Fact]
    public void The_default_belts_are_what_the_belt_pages_would_accept()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var belt in DefaultBelts.All)
        {
            Assert.Null(BeltRules.CheckName(belt.Name));
            Assert.Null(BeltRules.CheckPrimaryColor(belt.PrimaryColor));
            Assert.Null(BeltRules.CheckSecondaryColor(belt.SecondaryColor));
            Assert.All(belt.Prerequisites, p =>
            {
                Assert.Null(BeltRules.CheckMinAgeYears(p.MinAgeYears));
                Assert.Null(BeltRules.CheckMinWaitMonths(p.MinWaitMonths));
                Assert.Contains(p.RequiredBelt, seen); // listed earlier: nothing requires itself, directly or in a circle
            });
            Assert.Equal(belt.Prerequisites.Count, belt.Prerequisites.Select(p => p.RequiredBelt).Distinct().Count());
            Assert.True(seen.Add(belt.Name), $"'{belt.Name}' is listed twice.");
        }
    }

    [Fact]
    public async Task A_new_accounts_database_starts_with_the_default_roles_in_place_of_the_one_of_the_migration()
    {
        var roles = await RolesAsync(StartApi());

        Assert.Equal([RoleRules.AdministratorName, "Leading Trainer", "Passive User", "Trainer"], roles.Select(r => r.Name));
        Assert.Equal(DefaultRoles.All.Count + 1, roles.Count);
        Assert.DoesNotContain(roles, r => r.Name == TestAccounts.UserRoleName);
        Assert.Single(roles, r => r.IsSystem);
        Assert.All(DefaultRoles.All, expected =>
        {
            var actual = roles.Single(r => r.Name == expected.Name);
            Assert.False(actual.IsSystem);
            Assert.Equal(Permission.InCatalogueOrder(expected.Permissions), actual.Permissions);
            Assert.All(actual.Permissions, p => Assert.True(Permission.IsKnown(p), p));
        });
    }

    [Fact]
    public void The_default_roles_grow_with_what_their_people_do()
    {
        var leading = DefaultRoles.All.Single(r => r.Name == "Leading Trainer").Permissions;
        var trainer = DefaultRoles.All.Single(r => r.Name == "Trainer").Permissions;
        var passive = DefaultRoles.All.Single(r => r.Name == "Passive User").Permissions;

        Assert.All(passive, p => Assert.Contains(p, trainer));
        Assert.All(trainer, p => Assert.Contains(p, leading));
        Assert.All(passive, p => Assert.EndsWith(".view", p)); // only looks
        Assert.Contains(Permission.CommentsWrite, trainer);
        Assert.DoesNotContain(Permission.CommentsWrite, passive);
        Assert.DoesNotContain(Permission.ExamsEdit, trainer);
        Assert.Contains(Permission.ExamsEdit, leading);

        // Nobody below the administrator manages users, roles or the branding, or deletes the comments of others.
        foreach (var role in DefaultRoles.All)
        {
            Assert.DoesNotContain(role.Permissions, p => p.StartsWith("users.") || p.StartsWith("roles.") || p.StartsWith("branding."));
            Assert.DoesNotContain(Permission.CommentsDeleteAny, role.Permissions);
        }
    }

    [Fact]
    public async Task Without_the_setting_a_new_database_stays_empty_and_one_that_exists_does_not_get_the_defaults_later()
    {
        var first = StartApi(defaults: false);
        Assert.Empty(await BeltsAsync(first));
        Assert.Equal([RoleRules.AdministratorName, TestAccounts.UserRoleName], (await RolesAsync(first)).Select(r => r.Name));

        var second = StartApi(defaults: true);

        Assert.Empty(await BeltsAsync(second));
        Assert.Equal([RoleRules.AdministratorName, TestAccounts.UserRoleName], (await RolesAsync(second)).Select(r => r.Name));
    }

    [Fact]
    public async Task What_an_administrator_deletes_does_not_come_back_when_the_API_is_started_again()
    {
        var client = StartApi();
        var belts = await BeltsAsync(client);
        var roles = await RolesAsync(client);
        var lastBelt = belts[^1];
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/belts/{lastBelt.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/roles/{roles.Single(r => r.Name == "Passive User").Id}")).StatusCode);

        var again = StartApi();

        var beltsNow = await BeltsAsync(again);
        Assert.Equal(belts.Count - 1, beltsNow.Count);
        Assert.DoesNotContain(beltsNow, b => b.Name == lastBelt.Name);
        var rolesNow = await RolesAsync(again);
        Assert.Equal([RoleRules.AdministratorName, "Leading Trainer", "Trainer"], rolesNow.Select(r => r.Name));
    }

    [Fact]
    public async Task The_belts_and_roles_a_user_made_are_left_alone()
    {
        var first = StartApi(defaults: false);
        var created = await first.PostAsJsonAsync("/api/belts", new BeltInputDto("Eigener Gürtel", "#123456", null, []));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await first.PostAsJsonAsync("/api/roles", new RoleInputDto("Kassenwart", [Permission.DashboardView]))).StatusCode);

        var again = StartApi(defaults: true);

        Assert.Equal(["Eigener Gürtel"], (await BeltsAsync(again)).Select(b => b.Name));
        Assert.Equal([RoleRules.AdministratorName, "Kassenwart", TestAccounts.UserRoleName], (await RolesAsync(again)).Select(r => r.Name));
    }
}
