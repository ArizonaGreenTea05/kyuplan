using Kyuplan.Api.Domain;

namespace Kyuplan.Api.Spond;

/// <summary>Where groups, members and events come from: Spond itself, or generated demo data.</summary>
public interface ISpondDataSource
{
    bool IsDemo { get; }

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

/// <summary>Spond could not be reached, rejected the login, or is not configured. Mapped to HTTP 503.</summary>
public sealed class SpondUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
