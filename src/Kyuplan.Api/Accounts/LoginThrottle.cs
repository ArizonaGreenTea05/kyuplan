using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;

namespace Kyuplan.Api.Accounts;

/// <summary>
/// Slows down guessing of passwords: failed logins are counted per network address and per user name within a window, and once a count is
/// reached, further logins from that address (or as that user) are refused until the window has passed, whatever password they bring.
/// The counts live in memory (they start over with the API, which is fine for a window of minutes). Behind a reverse proxy the address is only
/// the real one when the proxy's forwarded headers are switched on (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>).
/// </summary>
public sealed class LoginThrottle(IOptions<ApiSecurityOptions> options, TimeProvider time)
{
    private const int MaxEntries = 10_000;

    /// <summary>How long a network address that signed in as a user stays known to that user.</summary>
    private static readonly TimeSpan KnownFor = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, Window> _failures = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> _known = new();

    /// <summary>How long a refused login has to wait, or <c>null</c> when it may be tried.</summary>
    public TimeSpan? RetryAfter(string? address, string userName)
    {
        var settings = options.Value;
        var wait = Wait(AddressKey(address), settings.MaxLoginFailuresPerAddress, settings);
        // The lock of a user name does not keep out the user themself: whoever types a name wrong 30 times must not be able to lock its owner out.
        // An address the owner signed in from lately is let through (the lock of the address still counts).
        var userWait = IsKnown(address, userName) ? null : Wait(UserKey(userName), settings.MaxLoginFailuresPerUser, settings);
        return wait is null ? userWait : userWait is null ? wait : wait > userWait ? wait : userWait;
    }

    /// <summary>A login worked: this address is remembered as the user's.</summary>
    public void Succeeded(string? address, string userName)
    {
        var now = time.GetUtcNow();
        var key = KnownKey(address, userName);
        if (_known.TryGetValue(key, out var seen) && now - seen < TimeSpan.FromHours(1)) return;

        if (_known.Count > MaxEntries)
        {
            foreach (var (other, at) in _known)
                if (now - at >= KnownFor) _known.TryRemove(other, out _);
            if (_known.Count > MaxEntries) return;
        }

        _known[key] = now;
    }

    private bool IsKnown(string? address, string userName) =>
        _known.TryGetValue(KnownKey(address, userName), out var seen) && time.GetUtcNow() - seen < KnownFor;

    private static string KnownKey(string? address, string userName) => UserKey(userName) + "|" + (address ?? "?");

    public void Failed(string? address, string userName)
    {
        var now = time.GetUtcNow();
        if (_failures.Count > MaxEntries) Prune(now);

        Count(AddressKey(address), now);
        Count(UserKey(userName), now);
    }

    private TimeSpan? Wait(string key, int limit, ApiSecurityOptions settings)
    {
        if (limit <= 0 || !_failures.TryGetValue(key, out var window)) return null;

        lock (window)
        {
            var end = window.Start + settings.LoginFailureWindow;
            return time.GetUtcNow() < end && window.Count >= limit ? end - time.GetUtcNow() : null;
        }
    }

    private void Count(string key, DateTimeOffset now)
    {
        var window = _failures.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (now - window.Start >= options.Value.LoginFailureWindow)
            {
                window.Start = now;
                window.Count = 0;
            }

            window.Count++;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _failures)
        {
            if (now - window.Start >= options.Value.LoginFailureWindow) _failures.TryRemove(key, out _);
        }
    }

    private static string AddressKey(string? address) => "ip:" + (address ?? "?");

    private static string UserKey(string userName) => "user:" + userName.Trim().ToUpperInvariant();

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Count;
    }
}
