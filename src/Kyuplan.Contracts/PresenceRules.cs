namespace Kyuplan.Contracts;

/// <summary>
/// When a user counts as online. Every request carries the login, so the API notes when a user was last seen; it writes that at most once per
/// <see cref="WriteInterval"/>, so a user is online while their last note is younger than <see cref="OnlineWindow"/>.
/// </summary>
public static class PresenceRules
{
    /// <summary>A note about a user who was seen is only renewed when it is older than this (saves a database write per request).</summary>
    public static readonly TimeSpan WriteInterval = TimeSpan.FromMinutes(1);

    /// <summary>Longer than <see cref="WriteInterval"/> plus the time between two requests of an open client.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(5);

    public static bool IsOnline(DateTimeOffset? lastSeenAt, DateTimeOffset now) => lastSeenAt is { } seen && now - seen < OnlineWindow;
}
