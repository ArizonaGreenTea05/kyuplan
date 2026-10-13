using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Kyuplan.Api.Infrastructure;

internal static class DatabaseSetup
{
    /// <summary>
    /// Creates the database or brings it up to date. Returns whether it was new: none of its migrations had been applied before, so it
    /// is the one that gets the default data (a database that exists already has what its users made of it).
    /// </summary>
    public static async Task<bool> EnsureUpToDateAsync(this DbContext db, CancellationToken cancellationToken)
    {
        var isNew = !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any();
        await db.Database.MigrateAsync(cancellationToken);
        return isNew;
    }

    /// <summary>Whether a save failed because a unique index would have held a value twice (SQLite: SQLITE_CONSTRAINT_UNIQUE; PostgreSQL: 23505).</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception) => exception.InnerException switch
    {
        SqliteException { SqliteExtendedErrorCode: 2067 } => true,
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
        _ => false,
    };

    /// <summary>Whether a save failed because a row is still referenced by another (SQLite: SQLITE_CONSTRAINT_FOREIGNKEY; PostgreSQL: 23503).</summary>
    public static bool IsForeignKeyViolation(this DbUpdateException exception) => exception.InnerException switch
    {
        SqliteException { SqliteExtendedErrorCode: 787 } => true,
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => true,
        _ => false,
    };
}
