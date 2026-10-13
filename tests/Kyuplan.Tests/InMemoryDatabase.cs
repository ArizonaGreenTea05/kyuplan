using Microsoft.Data.Sqlite;

namespace Kyuplan.Tests;

/// <summary>
/// A SQLite database that lives in memory and is shared by every connection that names it. It exists as long as one
/// connection is open, so this keeps one open for the lifetime of the test, while the API under test opens and closes
/// its own per request. Every instance is a database of its own.
/// </summary>
internal sealed class InMemoryDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public InMemoryDatabase()
    {
        ConnectionString = $"Data Source=pp-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    public void Dispose() => _keepAlive.Dispose();
}
