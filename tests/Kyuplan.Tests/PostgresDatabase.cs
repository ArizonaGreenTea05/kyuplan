using Npgsql;

namespace Kyuplan.Tests;

/// <summary>
/// A real PostgreSQL server for the tests that must not run on SQLite. It is named by the environment variable
/// <see cref="Variable"/> (a connection string with the right to create databases; CI sets it, see <c>ci.yml</c>); without it
/// the tests marked <see cref="PostgresFactAttribute"/> are skipped. Every instance creates a database of its own and drops it again.
/// </summary>
internal sealed class PostgresDatabase : IDisposable
{
    public const string Variable = "KYUPLAN_TEST_POSTGRES";

    private readonly string _server;
    private readonly string _name = $"pp_test_{Guid.NewGuid():N}";

    public PostgresDatabase()
    {
        _server = Environment.GetEnvironmentVariable(Variable) ?? throw new InvalidOperationException($"{Variable} is not set.");
        Execute("postgres", $"CREATE DATABASE \"{_name}\"");
        ConnectionString = new NpgsqlConnectionStringBuilder(_server) { Database = _name }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        Execute("postgres", $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private void Execute(string database, string sql)
    {
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = database }.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>A test that needs a PostgreSQL server (<see cref="PostgresDatabase.Variable"/>); skipped when there is none.</summary>
internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresDatabase.IsAvailable) Skip = $"No PostgreSQL server: set {PostgresDatabase.Variable} to a connection string.";
    }
}
