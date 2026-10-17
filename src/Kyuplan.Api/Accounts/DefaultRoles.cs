using Microsoft.EntityFrameworkCore;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

/// <summary>A role of <see cref="DefaultRoles"/>.</summary>
public sealed record DefaultRole(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The roles a new accounts database starts with, besides the administrator role (which the migrations create): the people around a
/// club's exams, from the one who runs everything to the one who only looks. They are an ordinary start, not a rule: the Roles page
/// edits them like roles of one's own.
/// </summary>
public static class DefaultRoles
{
    /// <summary>The role the migration <c>UserRolesAndPermissions</c> makes for the users that existed before roles did.</summary>
    private const string MigrationRoleName = "User";

    public static IReadOnlyList<DefaultRole> All { get; } =
    [
        // Everything about the exams, members, belts and tags, and all of the comments but those of others.
        new("Leading Trainer",
        [
            Permission.DashboardView, Permission.StatisticsView, Permission.CalendarView,
            Permission.ExamsView, Permission.ExamsEdit, Permission.ExamsFilters, Permission.ExamsCandidates, Permission.ExamsResults,
            Permission.MembersView, Permission.MembersEdit, Permission.BeltsView, Permission.BeltsEdit,
            Permission.CommentsView, Permission.CommentsWrite, Permission.CommentsEdit, Permission.CommentsDelete,
            Permission.RatingsView, Permission.RatingsWrite,
            Permission.TagsView, Permission.TagsEdit,
        ]),

        // Looks at everything of the club, writes comments and tags, but changes nothing else.
        new("Trainer",
        [
            Permission.DashboardView, Permission.StatisticsView, Permission.CalendarView, Permission.ExamsView, Permission.MembersView, Permission.BeltsView,
            Permission.CommentsView, Permission.CommentsWrite, Permission.CommentsEdit, Permission.CommentsDelete,
            Permission.RatingsView, Permission.RatingsWrite,
            Permission.TagsView, Permission.TagsEdit,
        ]),

        // Only looks.
        new("Passive User",
        [
            Permission.DashboardView, Permission.StatisticsView, Permission.CalendarView, Permission.ExamsView, Permission.MembersView, Permission.BeltsView,
            Permission.CommentsView, Permission.RatingsView, Permission.TagsView,
        ]),
    ];

    /// <summary>
    /// Adds the roles to a database that has just been created. The migration's role "User" is replaced by them: it is for the users of
    /// an older version, a new database has none, and it would only be the Leading Trainer under another name.
    /// The caller decides when the database is new (see <c>Program</c>).
    /// </summary>
    public static async Task SeedAsync(AccountsDbContext db, CancellationToken cancellationToken)
    {
        await db.Roles.Where(r => !r.IsSystem && r.Name == MigrationRoleName && !r.Users.Any()).ExecuteDeleteAsync(cancellationToken);

        db.Roles.AddRange(All.Select(r => new Role { Name = r.Name, Permissions = Permission.InCatalogueOrder(r.Permissions) }));
        await db.SaveChangesAsync(cancellationToken);
    }
}
