using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Kyuplan.Api.Accounts;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>An instance whose users were made when there was only an administrator flag: nobody may lose what they could do.</summary>
public sealed class AccountsMigrationTests : IDisposable
{
    private const string Before = "20261015123205_UserAliasesAndDisplayName";

    private readonly InMemoryDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private AccountsDbContext Open() => new(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite(_database.ConnectionString).Options);

    private static void Insert(AccountsDbContext db, int id, string userName, bool isAdmin) => db.Database.ExecuteSql(
        $"""INSERT INTO "Users" ("Id", "UserName", "DisplayName", "PasswordHash", "IsAdmin", "CreatedAt") VALUES ({id}, {userName}, {userName}, 'hash', {isAdmin}, '2026-10-14 10:00:00+00:00')""");

    [Fact]
    public void Administrators_get_the_administrator_role_and_everybody_else_the_role_User_with_what_they_could_do()
    {
        using var db = Open();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Before);
        Insert(db, 1, "admin", isAdmin: true);
        Insert(db, 2, "Anna", isAdmin: false);
        Insert(db, 3, "ben", isAdmin: false);

        migrator.Migrate();

        var roles = db.Roles.AsNoTracking().OrderBy(r => r.Id).ToList();
        Assert.Equal(["Administrator", "User"], roles.Select(r => r.Name));
        Assert.Equal([true, false], roles.Select(r => r.IsSystem));
        Assert.Equal(UserRolePermissions, roles[1].Permissions);
        var users = db.Users.AsNoTracking().Include(u => u.Roles).OrderBy(u => u.Id).ToList();
        Assert.Equal([["Administrator"], ["User"], ["User"]], users.Select(u => u.Roles.Select(r => r.Name).ToArray()));
        Assert.All(users, u => Assert.Empty(u.Permissions));
        Assert.Equal(Permission.All, users[0].EffectivePermissions());
        Assert.Equal(UserRolePermissions, users[1].EffectivePermissions());
    }

    [Fact]
    public void Every_permission_of_the_role_User_is_still_in_the_catalogue_and_none_of_the_administrators_things_are_in_it()
    {
        // The migration writes its keys out (a migration describes the catalogue of its day): they must still be what the catalogue calls them.
        Assert.All(UserRolePermissions, p => Assert.True(Permission.IsKnown(p), p));
        Assert.DoesNotContain(UserRolePermissions, p => p.StartsWith("users.") || p.StartsWith("roles.") || p.StartsWith("branding.") || p == Permission.CommentsDeleteAny);
    }

    [Fact]
    public void A_new_database_has_the_two_roles_and_no_user()
    {
        using var db = Open();

        db.Database.Migrate();

        Assert.Equal(["Administrator", "User"], db.Roles.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Name).ToList());
        Assert.Empty(db.Users.AsNoTracking().ToList());
    }

    [Fact]
    public void Going_back_gives_the_administrator_flag_to_whoever_has_the_administrator_role()
    {
        using var db = Open();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Before);
        Insert(db, 1, "admin", isAdmin: true);
        Insert(db, 2, "Anna", isAdmin: false);
        migrator.Migrate();

        migrator.Migrate(Before);

        var flags = db.Database.SqlQuery<bool>($"""SELECT "IsAdmin" AS "Value" FROM "Users" ORDER BY "Id" """).ToList();
        Assert.Equal([true, false], flags);
    }
}
