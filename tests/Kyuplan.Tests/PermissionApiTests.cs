using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>
/// What a user may do is decided by their permissions, given directly or through roles, and checked on every request: against the real API
/// and real (in-memory) SQLite databases. The users here are made by the administrator of the test host.
/// </summary>
public sealed class PermissionApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "a good password";
    private const string Group = "demo-dojo";

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
            builder.UseSetting("Sync:Enabled", "false"); // a background write would collide with the test's own reads of the shared in-memory database
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            Configure(builder, _accounts);
        });

    /// <summary>A user with exactly these permissions of their own (no role); the client that is signed in as them.</summary>
    private static async Task<HttpClient> UserWithAsync(WebApplicationFactory<Program> host, string userName, params string[] permissions)
    {
        var response = await host.CreateSignedInClient().PostAsJsonAsync(
            "/api/users", new UserInputDto(userName, Password, null, null, [], permissions));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return host.CreateSignedInClient(userName, Password);
    }

    private static async Task<int> AddMemberAsync(WebApplicationFactory<Program> host, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var member = new Member { GroupId = Group, SpondId = $"spond-{Guid.NewGuid():N}", Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<RoleDto> CreateRoleAsync(HttpClient admin, string name, params string[] permissions)
    {
        var response = await admin.PostAsJsonAsync("/api/roles", new RoleInputDto(name, permissions));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string url) => (await client.GetAsync(url)).StatusCode;

    // --- Nobody gets more than they have ---------------------------------------------------------------------------

    [Fact]
    public async Task A_user_editor_cannot_take_over_an_administrator()
    {
        using var host = CreateHost();
        var editor = await UserWithAsync(host, "editor", Permission.UsersView, Permission.UsersEdit, Permission.UsersDelete);
        var admin = (await editor.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.UserName == AdminName);

        var reset = await editor.PutAsJsonAsync($"/api/users/{admin.Id}", new UserInputDto(AdminName, "taken over by editor"));
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.CreateSignedInClient().GetAsync("/api/account")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"/api/users/{admin.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_user_editor_still_manages_users_with_no_more_permissions_than_their_own()
    {
        using var host = CreateHost();
        var editor = await UserWithAsync(host, "editor", Permission.UsersView, Permission.UsersEdit, Permission.ExamsView);
        var admin = host.CreateSignedInClient();
        await admin.PostAsJsonAsync("/api/users", new UserInputDto("helper", Password, null, null, [], [Permission.ExamsView]));
        var helper = (await editor.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.UserName == "helper");

        var reset = await editor.PutAsJsonAsync($"/api/users/{helper.Id}", new UserInputDto("helper", "a new good password"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task Permissions_cannot_be_given_that_the_giver_lacks()
    {
        using var host = CreateHost();
        var granter = await UserWithAsync(host, "granter", Permission.UsersEdit, Permission.UsersGrant, Permission.ExamsView);

        var tooMuch = await granter.PostAsJsonAsync("/api/users", new UserInputDto("other", Password, null, null, [], [Permission.SettingsEdit]));
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);

        var fine = await granter.PostAsJsonAsync("/api/users", new UserInputDto("other", Password, null, null, [], [Permission.ExamsView]));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task A_role_editor_cannot_give_a_role_permissions_they_lack()
    {
        using var host = CreateHost();
        var editor = await UserWithAsync(host, "roleeditor", Permission.RolesEdit, Permission.ExamsView);

        var create = await editor.PostAsJsonAsync("/api/roles", new RoleInputDto("sneaky", [Permission.UsersGrant]));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var role = await CreateRoleAsync(host.CreateSignedInClient(), "viewers", Permission.ExamsView);
        var update = await editor.PutAsJsonAsync($"/api/roles/{role.Id}", new RoleInputDto("viewers", [Permission.ExamsView, Permission.UsersGrant]));
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);

        var allowed = await editor.PutAsJsonAsync($"/api/roles/{role.Id}", new RoleInputDto("viewers", []));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // --- Every endpoint asks for its permission -------------------------------------------------------------------

    /// <summary>The endpoint, and the permissions of which one is enough. Ids and bodies are made up: only 403 (or not) is looked at.</summary>
    public static TheoryData<string, string, string?, string> Endpoints()
    {
        string Any(IEnumerable<string> permissions) => string.Join(",", permissions);

        return new()
        {
            { "GET", "/api/groups", null, Any(Permission.ReadsSpond) },
            { "GET", $"/api/dashboard?groupId={Group}", null, Permission.DashboardView },
            { "GET", $"/api/events?groupId={Group}", null, Any(Permission.ReadsSpond) },
            { "GET", $"/api/statistics/attendance?groupId={Group}&from=2026-01-01&to=2026-06-01", null, Permission.StatisticsView },
            { "GET", "/api/exams", null, Permission.ExamsView },
            { "GET", "/api/exams/999", null, Permission.ExamsView },
            { "POST", "/api/exams", "{}", Permission.ExamsEdit },
            { "PUT", "/api/exams/999", "{}", Permission.ExamsEdit },
            { "DELETE", "/api/exams/999", null, Permission.ExamsEdit },
            { "GET", "/api/exams/999/members", null, Permission.ExamsView },
            { "GET", "/api/exams/999/suggestions", null, Permission.ExamsView },
            { "PUT", "/api/exams/999/candidates", """{"add":[],"remove":[]}""", Permission.ExamsCandidates },
            { "PUT", "/api/exams/999/results/1", """{"beltId":null}""", Permission.ExamsResults },
            { "GET", "/api/exams/999/filters/1", null, Permission.ExamsView },
            { "POST", "/api/exams/999/filters", "{}", Permission.ExamsFilters },
            { "PUT", "/api/exams/999/filters/1", "{}", Permission.ExamsFilters },
            { "DELETE", "/api/exams/999/filters/1", null, Permission.ExamsFilters },
            { "GET", "/api/belts", null, Any(Permission.ReadsBelts) },
            { "GET", "/api/belts/999", null, Any(Permission.ReadsBelts) },
            { "POST", "/api/belts", "{}", Permission.BeltsEdit },
            { "PUT", "/api/belts/999", "{}", Permission.BeltsEdit },
            { "DELETE", "/api/belts/999", null, Permission.BeltsEdit },
            { "GET", "/api/members", null, Permission.MembersView },
            { "PUT", "/api/members/999", "{}", Permission.MembersEdit },
            { "GET", "/api/members/999/comments", null, Permission.CommentsView },
            { "POST", "/api/members/999/comments", "{}", Permission.CommentsWrite },
            { "PUT", "/api/comments/999", "{}", Permission.CommentsEdit },
            { "DELETE", "/api/comments/999", null, Any([Permission.CommentsDelete, Permission.CommentsDeleteAny]) },
            { "GET", "/api/members/999/ratings", null, Permission.RatingsView },
            { "PUT", "/api/calendar/events/e1/members/999/rating?groupId=g", "{\"stars\":3}", Permission.RatingsWrite },
            { "DELETE", "/api/calendar/events/e1/members/999/rating?groupId=g", null, Permission.RatingsWrite },
            { "GET", "/api/tags", null, Any(Permission.ReadsTags) },
            { "GET", "/api/tags/999", null, Any(Permission.ReadsTags) },
            { "POST", "/api/tags", "{}", Permission.TagsEdit },
            { "PUT", "/api/tags/999", "{}", Permission.TagsEdit },
            { "DELETE", "/api/tags/999", null, Permission.TagsEdit },
            { "GET", "/api/branding/settings", null, Permission.BrandingView },
            { "PUT", "/api/branding", "{}", Permission.BrandingEdit },
            { "PUT", "/api/branding/logo", null, Permission.BrandingEdit },
            { "DELETE", "/api/branding/logo", null, Permission.BrandingEdit },
            { "DELETE", "/api/branding", null, Permission.BrandingEdit },
            { "GET", "/api/settings", null, Permission.SettingsView },
            { "PUT", "/api/settings", "{}", Permission.SettingsEdit },
            { "GET", "/api/users", null, Permission.UsersView },
            { "GET", "/api/users/999", null, Permission.UsersView },
            { "POST", "/api/users", "{}", Permission.UsersEdit },
            { "PUT", "/api/users/999", "{}", Permission.UsersEdit },
            { "DELETE", "/api/users/999", null, Permission.UsersDelete },
            { "GET", "/api/roles", null, Any(Permission.ReadsRoles) },
            { "GET", "/api/roles/999", null, Any(Permission.ReadsRoles) },
            { "POST", "/api/roles", "{}", Permission.RolesEdit },
            { "PUT", "/api/roles/999", "{}", Permission.RolesEdit },
            { "DELETE", "/api/roles/999", null, Permission.RolesEdit },
        };
    }

    private static HttpRequestMessage Request(string method, string url, string? body) =>
        new(new HttpMethod(method), url) { Content = body is null ? null : Json(body) };

    [Fact]
    public async Task An_endpoint_is_refused_to_a_user_who_has_every_permission_but_the_ones_it_needs_and_open_to_one_who_has_only_one_of_them()
    {
        // One host for the whole table (a host and a password hash per row would take minutes); a user is made once per set of permissions.
        using var host = CreateHost();
        var users = new Dictionary<string, HttpClient>();
        async Task<HttpClient> UserAsync(params string[] permissions)
        {
            var key = string.Join(",", permissions);
            if (!users.TryGetValue(key, out var client)) users[key] = client = await UserWithAsync(host, $"user{users.Count}", permissions);
            return client;
        }

        var failures = new List<string>();
        foreach (var row in Endpoints())
        {
            var (method, url, body, anyOf) = ((string)row[0]!, (string)row[1]!, (string?)row[2], (string)row[3]!);
            var needed = anyOf.Split(',');

            var refused = await (await UserAsync([.. Permission.All.Except(needed)])).SendAsync(Request(method, url, body));
            var allowed = await (await UserAsync(needed[0])).SendAsync(Request(method, url, body));

            if (refused.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{method} {url}: without {anyOf} -> {(int)refused.StatusCode}, expected 403");
            if (allowed.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) failures.Add($"{method} {url}: with {needed[0]} -> {(int)allowed.StatusCode}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Every_permission_of_the_catalogue_is_asked_for_by_an_endpoint_of_the_table_above()
    {
        // A permission that nothing asks for would be a promise that nothing keeps.
        var covered = Endpoints().SelectMany(row => ((string)row[3]!).Split(',')).ToHashSet();

        Assert.Empty(Permission.All.Except(covered));
    }

    [Fact]
    public async Task The_account_of_a_user_without_any_permission_still_works_for_its_own_names_and_nothing_else_does()
    {
        using var host = CreateHost();
        var nobody = await UserWithAsync(host, "eve");

        var account = await nobody.GetFromJsonAsync<UserDto>("/api/account");
        Assert.Empty(account!.EffectivePermissions);
        Assert.Empty(account.Roles);
        Assert.Equal(HttpStatusCode.OK, (await nobody.PutAsJsonAsync("/api/account", new AccountUpdateDto("eve", null, null, "Eve E."))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(nobody, "/api/info"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(nobody, "/api/members"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(nobody, "/api/users"));
    }

    [Fact]
    public async Task The_administrator_has_every_permission_and_the_branding_stays_public()
    {
        using var host = CreateHost();

        var account = await host.CreateSignedInClient().GetFromJsonAsync<UserDto>("/api/account");

        Assert.Equal(Permission.All, account!.EffectivePermissions);
        Assert.Empty(account.Permissions); // not stored: also what is added to the catalogue later
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(host.CreateClient(), "/api/branding"));
    }

    // --- The examples: comments are not members -------------------------------------------------------------------

    [Fact]
    public async Task Whoever_may_view_members_does_not_see_the_comments_on_them_nor_how_many_there_are()
    {
        using var host = CreateHost();
        var memberId = await AddMemberAsync(host, "Clara");
        await new PlannerApiClient(host.CreateSignedInClient()).CreateCommentAsync(memberId, new CommentInputDto("Private words", []));
        var onlyMembers = await UserWithAsync(host, "ben", Permission.MembersView);
        var both = await UserWithAsync(host, "mia", Permission.MembersView, Permission.CommentsView);

        var hidden = await new PlannerApiClient(onlyMembers).GetMembersAsync(Group);
        var shown = await new PlannerApiClient(both).GetMembersAsync(Group);

        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(onlyMembers, $"/api/members/{memberId}/comments"));
        Assert.Equal(0, hidden.Single(m => m.Id == memberId).CommentCount);
        Assert.Equal(1, shown.Single(m => m.Id == memberId).CommentCount);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(both, $"/api/members/{memberId}/comments"));
    }

    [Fact]
    public async Task Whoever_may_read_comments_may_not_write_change_or_delete_them_without_the_permissions_for_it()
    {
        using var host = CreateHost();
        var memberId = await AddMemberAsync(host, "Clara");
        var reader = await UserWithAsync(host, "reader", Permission.CommentsView);
        var writer = await UserWithAsync(host, "writer", Permission.CommentsWrite);
        var editor = await UserWithAsync(host, "editor", Permission.CommentsEdit);
        var url = $"/api/members/{memberId}/comments";

        // Writing and replying; reading alone does not.
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(reader, url));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(url, new CommentInputDto("Words", []))).StatusCode);
        var written = await writer.PostAsJsonAsync(url, new CommentInputDto("Words", []));
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        var comment = (await written.Content.ReadFromJsonAsync<CommentDto>())!;

        // Changing is a permission of its own; so is deleting one's own comment.
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PutAsJsonAsync($"/api/comments/{comment.Id}", new CommentUpdateDto("Other words", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.DeleteAsync($"/api/comments/{comment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync($"/api/comments/{comment.Id}", new CommentUpdateDto("Other words", []))).StatusCode);
    }

    [Fact]
    public async Task A_comment_is_deleted_by_its_author_with_the_permission_for_their_own_and_by_others_only_with_the_permission_for_all()
    {
        using var host = CreateHost();
        var memberId = await AddMemberAsync(host, "Clara");
        var author = await UserWithAsync(host, "author", Permission.CommentsView, Permission.CommentsWrite, Permission.CommentsDelete);
        var other = await UserWithAsync(host, "other", Permission.CommentsView, Permission.CommentsDelete);
        var moderator = await UserWithAsync(host, "moderator", Permission.CommentsView, Permission.CommentsDeleteAny);
        var api = new PlannerApiClient(author);
        var first = await api.CreateCommentAsync(memberId, new CommentInputDto("One", []));
        var second = await api.CreateCommentAsync(memberId, new CommentInputDto("Two", []));
        var third = await api.CreateCommentAsync(memberId, new CommentInputDto("Three", []));

        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/comments/{first.Id}")).StatusCode); // theirs to delete only their own
        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync($"/api/comments/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await moderator.DeleteAsync($"/api/comments/{second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await moderator.DeleteAsync($"/api/comments/{second.Id}")).StatusCode);
        Assert.Equal([third.Id], (await api.GetCommentsAsync(memberId)).Select(c => c.Id));
    }

    [Fact]
    public async Task Tags_can_be_read_by_whoever_writes_or_changes_comments_but_made_only_with_the_tag_permission()
    {
        using var host = CreateHost();
        var writer = await UserWithAsync(host, "writer", Permission.CommentsView, Permission.CommentsWrite);
        var viewer = await UserWithAsync(host, "viewer", Permission.CommentsView);
        var tagger = await UserWithAsync(host, "tagger", Permission.TagsView, Permission.TagsEdit);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(writer, "/api/tags"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(viewer, "/api/tags"));
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync("/api/tags", new TagInputDto("Topic"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await tagger.PostAsJsonAsync("/api/tags", new TagInputDto("Topic"))).StatusCode);
    }

    // --- Permissions are checked on every request ---------------------------------------------------------------

    [Fact]
    public async Task A_permission_given_to_a_user_applies_from_their_next_request_and_so_does_taking_it_away()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var ben = await UserWithAsync(host, "ben");
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(ben, "/api/belts"));
        var id = (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.UserName == "ben").Id;

        await admin.PutAsJsonAsync($"/api/users/{id}", new UserInputDto("ben", null, null, null, null, [Permission.BeltsView]));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(ben, "/api/belts"));
        Assert.Equal([Permission.BeltsView], (await ben.GetFromJsonAsync<UserDto>("/api/account"))!.EffectivePermissions);

        await admin.PutAsJsonAsync($"/api/users/{id}", new UserInputDto("ben", null, null, null, null, []));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(ben, "/api/belts"));
    }

    [Fact]
    public async Task A_role_gives_its_permissions_to_everybody_who_has_it_and_changes_for_all_of_them_at_once()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var trainer = await CreateRoleAsync(admin, "Trainer", Permission.MembersView, Permission.BeltsView);
        var api = new PlannerApiClient(admin);
        await api.CreateUserAsync(new UserInputDto("anna", Password, RoleIds: [trainer.Id]));
        await api.CreateUserAsync(new UserInputDto("ben", Password, RoleIds: [trainer.Id], Permissions: [Permission.TagsView]));
        var anna = host.CreateSignedInClient("anna", Password);
        var ben = host.CreateSignedInClient("ben", Password);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(anna, "/api/members"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(anna, "/api/tags"));
        // Their own permissions and those of the role add up.
        Assert.Equal([Permission.BeltsView, Permission.MembersView, Permission.TagsView], (await ben.GetFromJsonAsync<UserDto>("/api/account"))!.EffectivePermissions.Order());

        var changed = await admin.PutAsJsonAsync($"/api/roles/{trainer.Id}", new RoleInputDto("Coach", [Permission.TagsView]));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(anna, "/api/members"));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(anna, "/api/tags"));
        Assert.Equal(["Coach"], (await anna.GetFromJsonAsync<UserDto>("/api/account"))!.Roles.Select(r => r.Name));
    }

    [Fact]
    public async Task Deleting_a_role_takes_it_from_its_users_and_leaves_their_own_permissions()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var trainer = await CreateRoleAsync(admin, "Trainer", Permission.MembersView);
        await new PlannerApiClient(admin).CreateUserAsync(new UserInputDto("anna", Password, RoleIds: [trainer.Id], Permissions: [Permission.BeltsView]));
        var anna = host.CreateSignedInClient("anna", Password);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(anna, "/api/members"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/roles/{trainer.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(anna, "/api/members"));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(anna, "/api/belts"));
        Assert.Empty((await anna.GetFromJsonAsync<UserDto>("/api/account"))!.Roles);
    }

    [Fact]
    public async Task A_permission_that_is_no_longer_in_the_catalogue_is_ignored()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var role = await CreateRoleAsync(admin, "Old", Permission.MembersView);
        await new PlannerApiClient(admin).CreateUserAsync(new UserInputDto("anna", Password, RoleIds: [role.Id]));
        using (var scope = host.Services.CreateScope())
        {
            // What an older version may have written.
            var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
            db.Roles.Single(r => r.Id == role.Id).Permissions = ["members.view", "reports.print"];
            await db.SaveChangesAsync();
        }

        var account = await host.CreateSignedInClient("anna", Password).GetFromJsonAsync<UserDto>("/api/account");

        Assert.Equal([Permission.MembersView], account!.EffectivePermissions);
    }

    // --- Giving permissions -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Roles_and_permissions_of_a_user_can_only_be_given_with_the_permission_to_give_them()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        // Both have everything the target is given below, so that only the permission to give is under test.
        var editor = await UserWithAsync(host, "editor", [.. Permission.All.Where(p => p != Permission.UsersGrant)]);
        var granter = await UserWithAsync(host, "granter", [.. Permission.All]);
        var targetId = (await new PlannerApiClient(admin).CreateUserAsync(new UserInputDto("target", Password))).Id;

        // Asked for without the permission: refused, not silently ignored.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/users", new UserInputDto("new1", Password, RoleIds: [AdministratorRoleId]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/users", new UserInputDto("new1", Password, Permissions: [Permission.UsersView]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"/api/users/{targetId}", new UserInputDto("target", null, RoleIds: [UserRoleId]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"/api/users/{targetId}", new UserInputDto("target", null, Permissions: []))).StatusCode);

        // Names and password are for editing; leaving the roles out keeps them.
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync("/api/users", new UserInputDto("new2", Password))).StatusCode);
        var renamed = await editor.PutAsJsonAsync($"/api/users/{targetId}", new UserInputDto("target", null, "Target T."));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var granted = await granter.PutAsJsonAsync($"/api/users/{targetId}", new UserInputDto("target", null, null, null, [UserRoleId], [Permission.RolesView]));
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var user = (await granted.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(["User"], user.Roles.Select(r => r.Name));
        Assert.Equal([Permission.RolesView], user.Permissions);
        Assert.Contains(Permission.MembersEdit, user.EffectivePermissions);
        Assert.Contains(Permission.RolesView, user.EffectivePermissions);

        // And a change of names afterwards keeps what was given.
        await editor.PutAsJsonAsync($"/api/users/{targetId}", new UserInputDto("target", null, "Target U."));
        Assert.Equal([Permission.RolesView], (await admin.GetFromJsonAsync<UserDto>($"/api/users/{targetId}"))!.Permissions);
    }

    [Fact]
    public async Task Roles_can_be_read_by_whoever_may_give_them_and_a_role_is_validated()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var granter = await UserWithAsync(host, "granter", Permission.UsersGrant);

        var roles = (await granter.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!;
        Assert.Equal(["Administrator", "User"], roles.Select(r => r.Name));
        Assert.Equal(UserRolePermissions, roles[1].Permissions);
        Assert.True(roles[0].IsSystem);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto(" ", []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("X", ["no.such.permission"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("X", [Permission.BeltsView, Permission.BeltsView]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto(new string('x', RoleRules.MaxNameLength + 1), []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("new", Password, Permissions: ["no.such.permission"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new UserInputDto("new", Password, RoleIds: [999]))).StatusCode);
    }

    [Fact]
    public async Task A_role_is_stored_trimmed_in_the_order_of_the_catalogue_with_its_user_count_and_its_name_is_unique_in_any_case()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var role = await CreateRoleAsync(admin, "  Trainer ", Permission.TagsEdit, Permission.TagsView, Permission.BeltsView);
        await new PlannerApiClient(admin).CreateUserAsync(new UserInputDto("anna", Password, RoleIds: [role.Id]));

        Assert.Equal("Trainer", role.Name);
        Assert.Equal([Permission.BeltsView, Permission.TagsView, Permission.TagsEdit], role.Permissions);
        var listed = (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!;
        Assert.Equal([1, 1, 0], listed.Select(r => r.UserCount)); // Administrator (the admin), Trainer (Anna), User
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("TRAINER", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/roles", new RoleInputDto("administrator", []))).StatusCode);
        var other = await CreateRoleAsync(admin, "Other");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{other.Id}", new RoleInputDto("trainer", []))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/roles/{other.Id}", new RoleInputDto("OTHER", []))).StatusCode); // its own name in another case
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/roles/999", new RoleInputDto("Ghost", []))).StatusCode);
    }

    [Fact]
    public async Task The_administrator_role_cannot_be_changed_or_deleted_and_its_last_holder_cannot_lose_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{AdministratorRoleId}", new RoleInputDto("Boss", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/roles/{AdministratorRoleId}")).StatusCode);

        // The roles are left out: the user keeps them. Given as none, the only administrator would lose the role.
        var keep = await admin.PutAsJsonAsync("/api/users/1", new UserInputDto("admin", null));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var lose = await admin.PutAsJsonAsync("/api/users/1", new UserInputDto("admin", null, RoleIds: []));
        Assert.Equal(HttpStatusCode.Conflict, lose.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/users/1", new UserInputDto("admin", null, RoleIds: [UserRoleId]))).StatusCode);
        Assert.Equal(["Administrator"], (await admin.GetFromJsonAsync<UserDto>("/api/account"))!.Roles.Select(r => r.Name));

        // With a second administrator the first may give the role up.
        await new PlannerApiClient(admin).CreateUserAsync(new UserInputDto("boss", Password, RoleIds: [AdministratorRoleId]));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/users/1", new UserInputDto("admin", null, RoleIds: [UserRoleId]))).StatusCode);
    }

    [Fact]
    public async Task A_user_who_has_the_roles_permission_may_edit_roles_but_not_see_users_unless_given_that_too()
    {
        using var host = CreateHost();
        var roleEditor = await UserWithAsync(host, "roleeditor", Permission.RolesView, Permission.RolesEdit, Permission.MembersView);

        var created = await roleEditor.PostAsJsonAsync("/api/roles", new RoleInputDto("Mine", [Permission.MembersView]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(roleEditor, "/api/users"));
    }

    [Fact]
    public async Task The_clients_roles_calls_work_over_the_wire()
    {
        using var host = CreateHost();
        var api = new PlannerApiClient(host.CreateSignedInClient());

        var role = await api.CreateRoleAsync(new RoleInputDto("Trainer", [Permission.MembersView]));
        var updated = await api.UpdateRoleAsync(role.Id, new RoleInputDto("Trainers", [Permission.MembersView, Permission.MembersEdit]));
        var roles = await api.GetRolesAsync();
        await api.DeleteRoleAsync(role.Id);

        Assert.Equal("Trainers", updated.Name);
        Assert.Equal(["Administrator", "Trainers", "User"], roles.Select(r => r.Name));
        Assert.Equal(["Administrator", "User"], (await api.GetRolesAsync()).Select(r => r.Name));
        var conflict = await Assert.ThrowsAsync<PlannerApiException>(() => api.CreateRoleAsync(new RoleInputDto("user", [])));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }
}
