using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// Every API call needs a login. A test host gets an accounts database of its own (in memory, like the planner database)
/// with one known administrator, and the clients the tests create sign in as that user unless a test says otherwise.
/// </summary>
internal static class TestAccounts
{
    public const string AdminName = "admin";
    public const string AdminPassword = "test-admin-password";

    public static void Configure(IWebHostBuilder builder, InMemoryDatabase accounts)
    {
        builder.UseSetting("Accounts:ConnectionString", accounts.ConnectionString);
        builder.UseSetting("Accounts:InitialAdminUserName", AdminName);
        builder.UseSetting("Accounts:InitialAdminPassword", AdminPassword);
    }

    /// <summary>The ids of the roles the accounts migration makes: the administrator role first, then the role "User".</summary>
    public const int AdministratorRoleId = 1;

    public const int UserRoleId = 2;

    public const string UserRoleName = "User";

    /// <summary>What the role "User" of the accounts migration allows: what a user without the old administrator flag could do.</summary>
    public static readonly IReadOnlyList<string> UserRolePermissions =
    [
        Permission.DashboardView, Permission.StatisticsView,
        Permission.ExamsView, Permission.ExamsEdit, Permission.ExamsFilters, Permission.ExamsCandidates, Permission.ExamsResults,
        Permission.MembersView, Permission.MembersEdit, Permission.BeltsView, Permission.BeltsEdit,
        Permission.CommentsView, Permission.CommentsWrite, Permission.CommentsEdit, Permission.CommentsDelete,
        Permission.TagsView, Permission.TagsEdit,
    ];

    /// <summary>An administrator (every permission), or a user with the role "User" (what a user could do before permissions existed), as the API describes them.</summary>
    public static UserDto Dto(int id, string userName, string displayName, IReadOnlyList<string> aliases, bool isAdmin = false) =>
        new(id, userName, displayName, aliases,
            [isAdmin ? new RoleRefDto(AdministratorRoleId, RoleRules.AdministratorName) : new RoleRefDto(UserRoleId, UserRoleName)],
            [], isAdmin ? Permission.All : UserRolePermissions);

    /// <summary>A user without any role or permission: they may manage their own account and nothing else.</summary>
    public static UserDto Nobody(int id, string userName) => new(id, userName, userName, [], [], [], []);

    /// <summary>A user who is given exactly these permissions directly.</summary>
    public static UserDto WithPermissions(int id, string userName, params string[] permissions) =>
        new(id, userName, userName, [], [], Permission.InCatalogueOrder(permissions), Permission.InCatalogueOrder(permissions));

    /// <summary>What an administrator sends for a user with the administrator role or the role "User" (the roles and permissions are as given by the migration).</summary>
    public static UserInputDto Input(string userName, string? password, bool isAdmin, string? displayName = null, IReadOnlyList<string>? aliases = null) =>
        new(userName, password, displayName, aliases, [isAdmin ? AdministratorRoleId : UserRoleId]);

    /// <summary>A user in one line: a <see cref="UserDto"/> holds lists, so two users with the same values are not <c>Equal</c>.</summary>
    public static string Show(this UserDto? user) =>
        user is null
            ? "(nobody)"
            : $"{user.Id}|{user.UserName}|{user.DisplayName}|{string.Join(",", user.Aliases)}|{string.Join(",", user.Roles.Select(r => r.Name))}|{string.Join(",", user.Permissions)}";

    /// <summary>What a client sent, in one line, for the same reason as <see cref="Show(UserDto?)"/>. No aliases, roles or permissions at all (keep them) reads differently from an empty list.</summary>
    public static string Show(this UserInputDto input) =>
        $"{input.UserName}|{input.Password}|{input.DisplayName}|{ShowAliases(input.Aliases)}|{ShowList(input.RoleIds)}|{ShowList(input.Permissions)}";

    public static string Show(this AccountUpdateDto update) =>
        $"{update.UserName}|{update.CurrentPassword}|{update.NewPassword}|{update.DisplayName}|{ShowAliases(update.Aliases)}";

    private static string ShowAliases(IReadOnlyList<string>? aliases) => aliases is null ? "(keep)" : string.Join(",", aliases);

    private static string ShowList<T>(IReadOnlyList<T>? list) => list is null ? "(keep)" : string.Join(",", list);

    public static string BasicHeader(string userName, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));

    /// <summary>Makes the client send this login with every request, as a browser or the WPF client would.</summary>
    public static HttpClient SignedIn(this HttpClient client, string userName = AdminName, string password = AdminPassword)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader(userName, password));
        return client;
    }

    public static HttpClient CreateSignedInClient(this WebApplicationFactory<Program> host, string userName = AdminName, string password = AdminPassword) =>
        host.CreateClient().SignedIn(userName, password);
}
