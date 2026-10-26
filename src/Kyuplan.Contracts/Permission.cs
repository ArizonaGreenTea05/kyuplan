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
    /// <summary>Define the calendar feeds (ICS addresses for calendar apps). A feed's address works without a login, so this is for administrators.</summary>
    public const string CalendarFeeds = "calendar.feeds";

    public const string ExamsView = "exams.view";
    public const string ExamsEdit = "exams.edit";
    public const string ExamsFilters = "exams.filters";
    public const string ExamsCandidates = "exams.candidates";
    public const string ExamsResults = "exams.results";

    public const string MembersView = "members.view";
    public const string MembersEdit = "members.edit";
    /// <summary>
    /// Open the list of hidden members on the members page and make them visible again. Hiding a member only takes <see cref="MembersEdit"/>; this is
    /// the only way back, so it is a permission of its own.
    /// </summary>
    public const string MembersHidden = "members.hidden";

    public const string BeltsView = "belts.view";
    public const string BeltsEdit = "belts.edit";

    public const string PaymentsView = "payments.view";
    /// <summary>Record what members paid for an exam.</summary>
    public const string PaymentsEdit = "payments.edit";
    /// <summary>Change the price list of the exams: the general fee and the stamp fee and belt price per belt.</summary>
    public const string PricesEdit = "prices.edit";

    public const string CommentsView = "comments.view";
    /// <summary>Read the comments about the members one is linked to (<see cref="UserDto.MemberIds"/>). Independent of <see cref="CommentsView"/>: that one never shows a user the comments about themselves.</summary>
    public const string CommentsViewOwn = "comments.viewOwn";
    public const string CommentsWrite = "comments.write";
    public const string CommentsEdit = "comments.edit";
    public const string CommentsDelete = "comments.delete";
    public const string CommentsDeleteAny = "comments.deleteAny";

    public const string RatingsView = "ratings.view";
    /// <summary>Read the evaluations of the members one is linked to; independent of <see cref="RatingsView"/>, like <see cref="CommentsViewOwn"/>.</summary>
    public const string RatingsViewOwn = "ratings.viewOwn";
    public const string RatingsWrite = "ratings.write";

    public const string TagsView = "tags.view";
    public const string TagsEdit = "tags.edit";

    public const string BrandingView = "branding.view";
    public const string BrandingEdit = "branding.edit";

    public const string ThemesEdit = "themes.edit";

    public const string PluginsView = "plugins.view";
    /// <summary>Install, update and remove plugins. A plugin is code that runs inside the API with all its rights, so this is for administrators who trust what they install.</summary>
    public const string PluginsEdit = "plugins.edit";

    public const string BackupsView = "backups.view";
    /// <summary>Define the backup databases and run them.</summary>
    public const string BackupsEdit = "backups.edit";
    /// <summary>Download a backup file: it holds every user's password hash, so it is a permission of its own.</summary>
    public const string BackupsDownload = "backups.download";
    /// <summary>Replace all data with an uploaded backup file.</summary>
    public const string BackupsRestore = "backups.restore";

    /// <summary>Keep the groups, members and events by hand when the data source is <see cref="DataSourceKinds.Manual"/>.</summary>
    public const string ManualDataEdit = "manualdata.edit";

    public const string SettingsView = "settings.view";
    public const string SettingsEdit = "settings.edit";

    public const string UsersView = "users.view";
    public const string UsersEdit = "users.edit";
    public const string UsersDelete = "users.delete";
    public const string UsersGrant = "users.grant";

    public const string RolesView = "roles.view";
    public const string RolesEdit = "roles.edit";

    /// <summary>Read the audit log: who changed exams, payments, belts, members, users and roles.</summary>
    public const string AuditView = "audit.view";

    /// <summary>Read how the sync with Spond is doing: when it last ran, how long it took and what failed.</summary>
    public const string SyncView = "sync.view";

    /// <summary>All permissions by domain, in the order the clients list them.</summary>
    public static IReadOnlyList<PermissionGroup> Groups { get; } =
    [
        new("dashboard", [DashboardView]),
        new("statistics", [StatisticsView]),
        new("calendar", [CalendarView, CalendarFeeds]),
        new("exams", [ExamsView, ExamsEdit, ExamsFilters, ExamsCandidates, ExamsResults]),
        new("members", [MembersView, MembersEdit, MembersHidden]),
        new("belts", [BeltsView, BeltsEdit]),
        new("payments", [PaymentsView, PaymentsEdit, PricesEdit]),
        new("comments", [CommentsView, CommentsViewOwn, CommentsWrite, CommentsEdit, CommentsDelete, CommentsDeleteAny]),
        new("ratings", [RatingsView, RatingsViewOwn, RatingsWrite]),
        new("tags", [TagsView, TagsEdit]),
        new("branding", [BrandingView, BrandingEdit]),
        new("themes", [ThemesEdit]),
        new("plugins", [PluginsView, PluginsEdit]),
        new("backups", [BackupsView, BackupsEdit, BackupsDownload, BackupsRestore]),
        new("manualdata", [ManualDataEdit]),
        new("settings", [SettingsView, SettingsEdit]),
        new("users", [UsersView, UsersEdit, UsersDelete, UsersGrant]),
        new("roles", [RolesView, RolesEdit]),
        new("audit", [AuditView]),
        new("sync", [SyncView]),
    ];

    public static IReadOnlyList<string> All { get; } = [.. Groups.SelectMany(g => g.Permissions)];

    /// <summary>
    /// A permission of the catalogue, or one that can belong to a plugin (<see cref="PluginRules.IsPluginPermission"/>): which plugins are installed changes,
    /// and a role keeps the permissions of a plugin that is removed for the day it is installed again.
    /// </summary>
    public static bool IsKnown(string? permission) => permission is not null && (All.Contains(permission) || PluginRules.IsPluginPermission(permission));

    /// <summary>These permissions, each once, in the order of the catalogue followed by those of plugins; what is neither is dropped.</summary>
    public static List<string> InCatalogueOrder(IEnumerable<string> permissions)
    {
        var wanted = permissions.ToHashSet();
        return [.. All.Where(wanted.Contains), .. wanted.Where(p => !All.Contains(p) && PluginRules.IsPluginPermission(p)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Whoever holds any of these may read what Spond knows (groups and events); the pages that show it need it.</summary>
    public static IReadOnlyList<string> ReadsSpond { get; } = [DashboardView, StatisticsView, CalendarView, CalendarFeeds, ExamsView, MembersView, TagsView, ManualDataEdit];

    /// <summary>Whoever holds any of these may read the belts: members and exams show them.</summary>
    public static IReadOnlyList<string> ReadsBelts { get; } = [BeltsView, MembersView, ExamsView, PaymentsView, PricesEdit];

    /// <summary>Whoever holds any of these may read the tags: comments carry them and choose among them.</summary>
    public static IReadOnlyList<string> ReadsTags { get; } = [TagsView, CommentsWrite, CommentsEdit];

    /// <summary>Whoever holds any of these may read the roles: they are listed to be edited and to be given to users.</summary>
    public static IReadOnlyList<string> ReadsRoles { get; } = [RolesView, UsersGrant];

    /// <summary>The name of the text of a permission in the language files: <c>Permission_members_view</c>.</summary>
    public static string ResourceKey(string permission) => "Permission_" + permission.Replace('.', '_');
}
