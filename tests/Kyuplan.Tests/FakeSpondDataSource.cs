using Kyuplan.Api.Spond;

namespace Kyuplan.Tests;

/// <summary>
/// A Spond that the test controls: the groups and events it serves can be replaced between two syncs (as people join, leave and
/// answer on the real one), and it can be made unreachable. Like the real data source it returns events by the day they end.
/// </summary>
internal sealed class FakeSpondDataSource : IDataSource
{
    private readonly object _lock = new();
    private IReadOnlyList<GroupInfo> _groups = [];
    private Dictionary<string, IReadOnlyList<EventInfo>> _events = [];
    private int _groupsCalls;

    public string Kind => "spond";

    /// <summary>How often the groups were asked for. A sync asks more than once, so this tells whether anything ran, not how often.</summary>
    public int GroupsCalls => Volatile.Read(ref _groupsCalls);

    /// <summary>While set, every call fails the way an unreachable Spond does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>While set, the groups can be read but the events cannot.</summary>
    public bool EventsUnreachable { get; set; }

    /// <summary>From this call of <see cref="GetGroupsAsync"/> on (1 = the first), Spond is unreachable. 0 = never.</summary>
    public int UnreachableFromGroupsCall { get; set; }

    public void SetGroups(params GroupInfo[] groups)
    {
        lock (_lock) _groups = groups;
    }

    public void SetEvents(string groupId, params EventInfo[] events)
    {
        lock (_lock) _events = new Dictionary<string, IReadOnlyList<EventInfo>>(_events) { [groupId] = events };
    }

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _groupsCalls);
        if (Unreachable || (UnreachableFromGroupsCall > 0 && call >= UnreachableFromGroupsCall)) throw Down();
        lock (_lock) return Task.FromResult(_groups);
    }

    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(
        string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (Unreachable || EventsUnreachable) throw Down();
        IReadOnlyList<EventInfo> events;
        lock (_lock) events = _events.GetValueOrDefault(groupId) ?? [];
        return Task.FromResult<IReadOnlyList<EventInfo>>(events.Where(e => e.End >= from && e.End <= to).ToList());
    }

    public void InvalidateCache()
    {
    }

    private static DataSourceUnavailableException Down() => new("Spond is unreachable (test).");
}
