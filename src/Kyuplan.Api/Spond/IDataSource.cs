using Kyuplan.Api.Domain;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Spond;

/// <summary>
/// Where groups, members and events come from: Spond (<see cref="SpondApiDataSource"/>), a script of the administrator
/// (<see cref="Sources.CustomScriptDataSource"/>), the users' own entries (<see cref="Sources.ManualDataSource"/>) or generated demo data. Which one runs is
/// decided when the API starts (<c>DataSource:Kind</c>, see Program.cs); everything else only knows this interface.
/// </summary>
public interface IDataSource
{
    bool IsDemo { get; }

    /// <summary>One of <see cref="DataSourceKinds"/>.</summary>
    string Kind => IsDemo ? DataSourceKinds.Demo : DataSourceKinds.Spond;

    Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Events of <paramref name="groupId"/> ending within [<paramref name="from"/>, <paramref name="to"/>].
    /// Implementations may return a superset; callers filter again.
    /// </summary>
    Task<IReadOnlyList<EventInfo>> GetEventsAsync(
        string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Forget everything cached so the next call fetches fresh data.</summary>
    void InvalidateCache();
}

/// <summary>The data source could not be reached (Spond rejected the login, the script failed, ...) or is not configured. Mapped to HTTP 503.</summary>
public sealed class SpondUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
