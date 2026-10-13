using Kyuplan.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Infrastructure;

internal static class DatabaseRegistration
{
    /// <summary>
    /// Migration history table of the accounts database on PostgreSQL. Both databases may be one PostgreSQL database, and each context
    /// needs a history of its own then.
    /// </summary>
    public const string AccountsHistoryTable = "__EFMigrationsHistory_Accounts";

    /// <summary>
    /// Registers <typeparamref name="TContext"/>, which the services ask for, on the system that <c>Database:Provider</c> names: itself on SQLite,
    /// <typeparamref name="TPostgres"/> (the same model, migrations of its own) on PostgreSQL. The settings are read when the context
    /// is first needed, not now, so that a host that adds its configuration later (tests) is respected.
    /// </summary>
    public static IServiceCollection AddProviderDbContext<TContext, TPostgres>(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        string? postgresHistoryTable = null)
        where TContext : DbContext
        where TPostgres : TContext
    {
        services.AddDbContext<TContext>((sp, options) => options.UseSqlite(connectionString(sp)));
        services.AddDbContext<TPostgres>((sp, options) => options.UseNpgsql(
            connectionString(sp), npgsql => { if (postgresHistoryTable is not null) npgsql.MigrationsHistoryTable(postgresHistoryTable); }));

        // The service TContext is the SQLite context built by AddDbContext above, or the PostgreSQL one.
        services.AddScoped<TContext>(sp => sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Provider == DatabaseProvider.Sqlite
            ? ActivatorUtilities.CreateInstance<TContext>(sp)
            : sp.GetRequiredService<TPostgres>());
        return services;
    }
}
