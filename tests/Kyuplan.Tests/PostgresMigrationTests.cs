using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kyuplan.Tests;

/// <summary>
/// The PostgreSQL contexts have migrations of their own. No server is needed to check that they match the model: when a model change
/// has a SQLite migration but no PostgreSQL one, these fail (<c>dotnet ef migrations add ... --context PlannerPostgresDbContext</c>, see AGENTS.md).
/// </summary>
public sealed class PostgresMigrationTests
{
    private const string Connection = "Host=localhost;Database=never-opened";

    [Fact]
    public void The_planner_migrations_for_PostgreSQL_match_the_model()
    {
        using var db = new PlannerPostgresDbContext(new DbContextOptionsBuilder<PlannerPostgresDbContext>().UseNpgsql(Connection).Options);

        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void The_accounts_migrations_for_PostgreSQL_match_the_model()
    {
        using var db = new AccountsPostgresDbContext(new DbContextOptionsBuilder<AccountsPostgresDbContext>()
            .UseNpgsql(Connection, npgsql => npgsql.MigrationsHistoryTable(DatabaseRegistration.AccountsHistoryTable)).Options);

        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void The_SQLite_migrations_are_not_mixed_up_with_the_PostgreSQL_ones()
    {
        using var planner = new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var accounts = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite("Data Source=:memory:").Options);

        Assert.DoesNotContain(planner.Database.GetMigrations(), m => m.Contains("Postgres", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, planner.Database.GetMigrations().Count());
        Assert.Equal(5, accounts.Database.GetMigrations().Count());
        Assert.False(planner.Database.HasPendingModelChanges());
        Assert.False(accounts.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(DatabaseProvider.Sqlite, "Data Source=/x/kyuplan.db", "Data Source=/x/kyuplan.db")]
    public void A_connection_string_is_resolved_for_its_database_system(DatabaseProvider provider, string connection, string expected) =>
        Assert.Equal(expected, new DatabaseOptions { Provider = provider, ConnectionString = connection }.Resolve(Path.GetTempPath()));

    [Fact]
    public void A_PostgreSQL_connection_string_keeps_its_settings_and_turns_Kerberos_off_unless_asked_for()
    {
        var plain = new Npgsql.NpgsqlConnectionStringBuilder(new DatabaseOptions
        {
            Provider = DatabaseProvider.PostgreSql, ConnectionString = "Host=db;Database=kyuplan;Username=u;Password=\"a;b\"",
        }.Resolve(Path.GetTempPath()));
        Assert.Equal("db", plain.Host);
        Assert.Equal("a;b", plain.Password);
        Assert.Equal(Npgsql.GssEncryptionMode.Disable, plain.GssEncryptionMode);

        var asked = new Npgsql.NpgsqlConnectionStringBuilder(new DatabaseOptions
        {
            Provider = DatabaseProvider.PostgreSql, ConnectionString = "Host=db;Gss Encryption Mode=Prefer",
        }.Resolve(Path.GetTempPath()));
        Assert.Equal(Npgsql.GssEncryptionMode.Prefer, asked.GssEncryptionMode);
    }
}
