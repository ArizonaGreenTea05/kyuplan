namespace Kyuplan.Contracts;

/// <summary>A domain (members, comments, ...) and what a user can be allowed to do in it.</summary>
/// <param name="Name">The domain, as the keys of its permissions start: <c>members</c> for <c>members.view</c>.</param>
public sealed record PermissionGroup(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Everything a user can be allowed to do, by domain. A permission is given to a user directly or through a role (<see cref="RoleDto"/>);
/// the API checks it on every request, and the clients hide what their user may not use. Each page needs its <c>.view</c> permission,
/// and each change needs a permission of its own. The administrator role has all of them, also those added later.
/// </summary>
public static class Permission
{
    /// <summary>The claim of the signed-in user that carries one permission.</summary>
    public const string ClaimType = "permission";

    public const string DashboardView = "dashboard.view";

    public const string StatisticsView = "statistics.view";

    public const string CalendarView = "calendar.view";

    public const string ExamsView = "exams.view";
    public const string ExamsEdit = "exams.edit";
    public const string ExamsFilters = "exams.filters";
    public const string ExamsCandidates = "exams.candidates";
    public const string ExamsResults = "exams.results";

    public const string MembersView = "members.view";
    public const string MembersEdit = "members.edit";

    public const string BeltsView = "belts.view";
    public const string BeltsEdit = "belts.edit";

    public const string CommentsView = "comments.view";
    public const string CommentsWrite = "comments.write";
    public const string CommentsEdit = "comments.edit";
    public const string CommentsDelete = "comments.delete";
    public const string CommentsDeleteAny = "comments.deleteAny";

    public const string TagsView = "tags.view";
    public const string TagsEdit = "tags.edit";

    public const string BrandingView = "branding.view";
    public const string BrandingEdit = "branding.edit";

    public const string SettingsView = "settings.view";
    public const string SettingsEdit = "settings.edit";

    public const string UsersView = "users.view";
    public const string UsersEdit = "users.edit";
    public const string UsersDelete = "users.delete";
    public const string UsersGrant = "users.grant";

    public const string RolesView = "roles.view";
    public const string RolesEdit = "roles.edit";

    /// <summary>All permissions by domain, in the order the clients list them.</summary>
    public static IReadOnlyList<PermissionGroup> Groups { get; } =
    [
        new("dashboard", [DashboardView]),
        new("statistics", [StatisticsView]),
        new("calendar", [CalendarView]),
        new("exams", [ExamsView, ExamsEdit, ExamsFilters, ExamsCandidates, ExamsResults]),
        new("members", [MembersView, MembersEdit]),
        new("belts", [BeltsView, BeltsEdit]),
        new("comments", [CommentsView, CommentsWrite, CommentsEdit, CommentsDelete, CommentsDeleteAny]),
        new("tags", [TagsView, TagsEdit]),
        new("branding", [BrandingView, BrandingEdit]),
        new("settings", [SettingsView, SettingsEdit]),
        new("users", [UsersView, UsersEdit, UsersDelete, UsersGrant]),
        new("roles", [RolesView, RolesEdit]),
    ];

    public static IReadOnlyList<string> All { get; } = [.. Groups.SelectMany(g => g.Permissions)];

    public static bool IsKnown(string? permission) => permission is not null && All.Contains(permission);

    /// <summary>These permissions, each once, in the order of the catalogue; what is not in the catalogue is dropped.</summary>
    public static List<string> InCatalogueOrder(IEnumerable<string> permissions) => [.. All.Where(permissions.ToHashSet().Contains)];

    /// <summary>Whoever holds any of these may read what Spond knows (groups and events); the pages that show it need it.</summary>
    public static IReadOnlyList<string> ReadsSpond { get; } = [DashboardView, StatisticsView, CalendarView, ExamsView, MembersView, TagsView];

    /// <summary>Whoever holds any of these may read the belts: members and exams show them.</summary>
    public static IReadOnlyList<string> ReadsBelts { get; } = [BeltsView, MembersView, ExamsView];

    /// <summary>Whoever holds any of these may read the tags: comments carry them and choose among them.</summary>
    public static IReadOnlyList<string> ReadsTags { get; } = [TagsView, CommentsWrite, CommentsEdit];

    /// <summary>Whoever holds any of these may read the roles: they are listed to be edited and to be given to users.</summary>
    public static IReadOnlyList<string> ReadsRoles { get; } = [RolesView, UsersGrant];

    /// <summary>The name of the text of a permission in the language files: <c>Permission_members_view</c>.</summary>
    public static string ResourceKey(string permission) => "Permission_" + permission.Replace('.', '_');
}
