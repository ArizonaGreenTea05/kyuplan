namespace Kyuplan.Plugins;

/// <summary>
/// Where groups, members and events come from: a data source of a plugin (Spond, for example), a script of the administrator, the users' own
/// entries or generated demo data. Which one runs is decided when the API starts (<c>DataSource:Kind</c>); everything else only knows this interface.
/// A plugin offers its data sources with <see cref="IPluginBuilder.AddDataSource"/>.
/// </summary>
public interface IDataSource
{
    /// <summary>Whether the data is generated sample data. A data source of a plugin answers <c>false</c>.</summary>
    bool IsDemo => false;

    /// <summary>The id of the data source, as the plugin declared it in <c>plugin.json</c> (<c>spond</c>); the built-in ones are <c>demo</c>, <c>custom</c> and <c>manual</c>.</summary>
    string Kind { get; }

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

/// <summary>
/// The data source could not be reached (the service rejected the login, the script failed, ...) or is not configured. Mapped to HTTP 503; the message
/// is shown to the user, so it says what to do.
/// </summary>
public sealed class DataSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
