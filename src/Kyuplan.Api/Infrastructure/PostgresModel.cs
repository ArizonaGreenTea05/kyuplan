using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// What differs in the model when the databases are PostgreSQL instead of SQLite. The model itself is written for SQLite (its
/// snapshot and migrations are the SQLite ones); the PostgreSQL contexts (<see cref="Persistence.PlannerPostgresDbContext"/>,
/// <see cref="Accounts.AccountsPostgresDbContext"/>) have migrations of their own, made from the model as adapted here.
/// </summary>
internal static class PostgresModel
{
    /// <summary>SQLite's <c>NOCASE</c> collation, which the model uses for names that are unique regardless of case.</summary>
    public const string NoCase = "NOCASE";

    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> Utc = new(v => v.ToUniversalTime(), v => v);

    public static void Adapt(ModelBuilder modelBuilder)
    {
        // citext compares case-insensitively, like NOCASE does (an extension that has to be allowed on the server: see docs/deployment.md).
        modelBuilder.HasPostgresExtension("citext");

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.GetCollation() == NoCase)
            {
                property.SetCollation(null);
                property.SetColumnType("citext");
            }

            // timestamptz takes UTC only; SQLite keeps the offset.
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(Utc);
        }
    }
}
