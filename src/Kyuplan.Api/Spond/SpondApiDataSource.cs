using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Spond.API.Models;
using Spond.API.Services;

namespace Kyuplan.Api.Spond;

/// <summary>
/// Reads groups and events through the Spond.API package.
/// <para>
/// Spond.API has a few traits this class works around: the client is not thread-safe (all calls are
/// serialised through <see cref="_gate"/>), it never refreshes its token (we log in again after
/// <see cref="SpondOptions.LoginLifetime"/>), and it reports every failed request as an empty list
/// (an empty group list is treated as a failed login, and empty event results are cached only briefly).
/// </para>
/// <para>
/// Spond also answers an account that sent too many requests with nothing at all. Several accounts can be configured
/// (<see cref="SpondOptions.Accounts"/>): when one yields no groups or events, the next is asked, and so on.
/// </para>
/// </summary>
public sealed class SpondApiDataSource : IDataSource, IDisposable
{
    private static readonly TimeSpan EmptyResultCacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>An empty result on a session older than this is retried after a fresh login.</summary>
    private static readonly TimeSpan SuspiciousSessionAge = TimeSpan.FromMinutes(5);

    private readonly SpondOptions _options;
    private readonly ClubClock _clock;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<SpondApiDataSource> _logger;
    private readonly List<AccountSession> _accounts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _invalidation = new();

    public SpondApiDataSource(
        IOptions<SpondOptions> options,
        ClubClock clock,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<SpondApiDataSource> logger,
        ILoggerFactory loggerFactory)
        : this(options, clock, cache, time, logger,
            account => new SpondAccountClient(account, new SpondClient(logger: loggerFactory.CreateLogger<SpondClient>())))
    {
    }

    internal SpondApiDataSource(
        IOptions<SpondOptions> options,
        ClubClock clock,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<SpondApiDataSource> logger,
        Func<SpondAccount, ISpondAccountClient> createClient)
    {
        _options = options.Value;
        _clock = clock;
        _cache = cache;
        _time = time;
        _logger = logger;
        _accounts = _options.GetAccounts().Select((a, i) => new AccountSession(i + 1, a, createClient(a))).ToList();
    }

    public bool IsDemo => false;

    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue("groups", out IReadOnlyList<GroupInfo>? cached) && cached is not null) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue("groups", out cached) && cached is not null) return cached;

            var groups = await QueryAccountsAsync(async account =>
            {
                await EnsureLoggedInAsync(account, force: false);
                var result = await account.Client.GetGroupsAsync();
                if (result.Count == 0)
                {
                    // Spond.API swallows HTTP errors, so "no groups" most likely means an expired token.
                    await EnsureLoggedInAsync(account, force: true);
                    result = await account.Client.GetGroupsAsync();
                }

                return result;
            });

            if (groups.Count == 0)
            {
                throw new SpondUnavailableException(_accounts.Count == 1
                    ? "Spond returned no groups for this account."
                    : $"Spond returned no groups for any of the {_accounts.Count} accounts.");
            }

            var mapped = groups.Select(g => MapGroup(g)).ToList();
            Store(ProfilesKey, MemberIdsByProfile(groups));
            Store("groups", mapped);
            return mapped;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EventInfo>> GetEventsAsync(
        string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Pad by a day: Spond filters on the event's *end* time, the statistics on its start date.
        var first = _clock.ToClub(from).AddDays(-1);
        var last = _clock.ToClub(to).AddDays(1);

        var events = new Dictionary<string, EventInfo>();
        var month = new DateOnly(first.Year, first.Month, 1);
        var lastMonth = new DateOnly(last.Year, last.Month, 1);
        for (; month <= lastMonth; month = month.AddMonths(1))
        {
            foreach (var e in await GetMonthAsync(groupId, month, cancellationToken))
                events[e.Id] = e;
        }

        return events.Values.ToList();
    }

    public void InvalidateCache()
    {
        var old = Interlocked.Exchange(ref _invalidation, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public void Dispose()
    {
        _gate.Dispose();
        _invalidation.Dispose();
    }

    private async Task<IReadOnlyList<EventInfo>> GetMonthAsync(string groupId, DateOnly month, CancellationToken cancellationToken)
    {
        var key = $"events:{groupId}:{month:yyyy-MM}";
        if (_cache.TryGetValue(key, out IReadOnlyList<EventInfo>? cached) && cached is not null) return cached;
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch
        {
            return [];
        }
        
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;

            // Kind must be Utc: Spond.API calls ToUniversalTime(), which would reinterpret Unspecified values as machine-local.
            var minEnd = _clock.StartOfDay(month).UtcDateTime;
            var maxEnd = _clock.StartOfDay(month.AddMonths(1)).UtcDateTime;

            var spondEvents = await QueryAccountsAsync(async account =>
            {
                await EnsureLoggedInAsync(account, force: false);
                var result = await account.Client.GetEventsAsync(groupId, minEnd, maxEnd);

                // A failed request looks exactly like "no events", and an expired token is the usual cause. Before
                // trusting an empty month on an older session, log in again and ask once more (at most every 5 minutes).
                if (result.Count == 0 && _time.GetUtcNow() - account.LoggedInAt > SuspiciousSessionAge)
                {
                    await EnsureLoggedInAsync(account, force: true);
                    result = await account.Client.GetEventsAsync(groupId, minEnd, maxEnd);
                }

                return result;
            });

            _cache.TryGetValue(ProfilesKey, out IReadOnlyDictionary<string, string>? members);
            var mapped = spondEvents.Select(e => MapEvent(e, members)).ToList();
            Store(key, mapped, mapped.Count == 0 ? EmptyResultCacheDuration : _options.CacheDuration);
            return mapped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Store<T>(string key, T value, TimeSpan? duration = null) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(duration ?? _options.CacheDuration)
            .AddExpirationToken(new CancellationChangeToken(_invalidation.Token)));

    /// <summary>
    /// Asks the accounts one after the other until one delivers something and returns that answer. An account that came back empty
    /// (or failed) while another one delivered is asked after the others for <see cref="SpondOptions.AccountCooldown"/>, so that an
    /// account Spond ignores is not asked again and again. If none delivers, the answer is empty, or, if every account failed
    /// outright (login rejected, Spond unreachable), the last failure is thrown. Must be called with <see cref="_gate"/> held.
    /// </summary>
    private async Task<List<T>> QueryAccountsAsync<T>(Func<AccountSession, Task<List<T>>> query)
    {
        if (_accounts.Count == 0)
        {
            throw new SpondUnavailableException(
                "Spond credentials are not configured. Enter them on the settings page of the web client, or set Spond:Email (or Spond:PhoneNumber) and Spond:Password " +
                "(more accounts in Spond:Accounts), or enable Spond:UseDemoData.");
        }

        var now = _time.GetUtcNow();
        var cameBackEmpty = new List<AccountSession>();
        SpondUnavailableException? lastFailure = null;

        // OrderBy is stable: the accounts keep their configured order, those in their cooldown go last.
        foreach (var account in _accounts.OrderBy(a => a.AskedLastUntil > now))
        {
            List<T> result;
            try
            {
                result = await CallSpondAsync(() => query(account));
            }
            catch (SpondUnavailableException ex)
            {
                _logger.LogWarning("{Account} failed: {Message}", account.Name, ex.Message);
                account.AskedLastUntil = now + _options.AccountCooldown;
                lastFailure = ex;
                continue;
            }

            if (result.Count > 0)
            {
                foreach (var empty in cameBackEmpty)
                {
                    _logger.LogWarning(
                        "{Account} returned nothing, {Other} delivered: it is asked last for the next {Cooldown}",
                        empty.Name, account.Name, _options.AccountCooldown);
                    empty.AskedLastUntil = now + _options.AccountCooldown;
                }

                return result;
            }

            cameBackEmpty.Add(account);
        }

        if (cameBackEmpty.Count == 0 && lastFailure is not null) throw lastFailure;
        return [];
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private async Task EnsureLoggedInAsync(AccountSession account, bool force)
    {
        if (!force && _time.GetUtcNow() - account.LoggedInAt < _options.LoginLifetime) return;

        _logger.LogInformation("Logging in to Spond ({Account})", account.Name);
        if (!await account.Client.LoginAsync())
        {
            account.LoggedInAt = DateTimeOffset.MinValue;
            throw new SpondUnavailableException(
                $"Spond rejected the login of {account.Name}. Check the credentials; accounts that require two-factor authentication are not supported.");
        }

        account.LoggedInAt = _time.GetUtcNow();
    }

    private static async Task<T> CallSpondAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            throw new SpondUnavailableException($"Could not reach Spond: {ex.Message}", ex);
        }
    }

    /// <summary>One configured account with its client, its login and when it is asked last until. Only used with <see cref="_gate"/> held.</summary>
    private sealed class AccountSession(int number, SpondAccount account, ISpondAccountClient client)
    {
        public string Name { get; } = $"Spond account {number} ({(string.IsNullOrWhiteSpace(account.Email) ? account.PhoneNumber : account.Email)})";

        public ISpondAccountClient Client { get; } = client;

        public DateTimeOffset LoggedInAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset AskedLastUntil { get; set; } = DateTimeOffset.MinValue;
    }

    internal static GroupInfo MapGroup(SpondGroup group) => new(
        group.Id,
        group.Name ?? group.Id,
        (group.SubGroups ?? []).Select(s => new SubGroupInfo(s.Id, s.Name ?? s.Id)).ToList(),
        (group.Members ?? [])
            .Select(m => new MemberInfo(
                m.Id,
                $"{m.FirstName} {m.LastName}".Trim(),
                m.SubGroups ?? [],
                ParseBirthDate(m.DateOfBirth))).ToList());

    /// <summary>
    /// Spond sends the date of birth as an ISO timestamp (<c>2012-05-03T00:00:00Z</c>) or a plain date. Only the day is taken, as written:
    /// converting the timestamp to a time zone could move it to the day before. Anything that is not a date is no birth date.
    /// </summary>
    internal static DateOnly? ParseBirthDate(string? text) =>
        text is { Length: >= 10 } && DateOnly.TryParseExact(text.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private const string ProfilesKey = "profiles";

    /// <summary>
    /// Spond names the owner of an event by the id of their user profile, not by the id of their member in the group. Maps both to the member id,
    /// so that an owner is found whichever of the two the event carries.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> MemberIdsByProfile(IEnumerable<SpondGroup> groups)
    {
        var ids = new Dictionary<string, string>();
        foreach (var member in groups.SelectMany(g => g.Members ?? []))
        {
            ids[member.Id] = member.Id;
            if (!string.IsNullOrEmpty(member.Profile?.Id)) ids.TryAdd(member.Profile.Id, member.Id);
        }

        return ids;
    }

    /// <param name="memberIds">The member ids by profile or member id (<see cref="MemberIdsByProfile"/>); the owners are turned into members with it, and one who is no member of the groups is no trainer. <c>null</c> = not known (yet): the owners' ids are kept.</param>
    internal EventInfo MapEvent(SpondEvent e, IReadOnlyDictionary<string, string>? memberIds = null)
    {
        var start = ParseTimestamp(e.StartTimestamp, e.StartTime);
        var end = ParseTimestamp(e.EndTimestamp, e.EndTime);

        var responses = new Dictionary<string, Rsvp>();
        if (e.Responses is { } r)
        {
            // TryAdd in priority order: if Spond ever lists a member twice, the clearest answer wins.
            foreach (var id in r.AcceptedIds ?? []) responses.TryAdd(id, Rsvp.Accepted);
            foreach (var id in r.DeclinedIds ?? []) responses.TryAdd(id, Rsvp.Declined);
            foreach (var id in r.UnansweredIds ?? []) responses.TryAdd(id, Rsvp.Unanswered);
            foreach (var id in r.WaitinglistIds ?? []) responses.TryAdd(id, Rsvp.Other);
            foreach (var id in r.UnconfirmedIds ?? []) responses.TryAdd(id, Rsvp.Other);
        }

        // The owners are the hosts of the event; they are not asked to take part, so they may be in the responses or not.
        var trainers = (e.Owners ?? []).Select(o => o.Id).Where(id => !string.IsNullOrEmpty(id))
            .Select(id => memberIds is null ? id : memberIds.GetValueOrDefault(id))
            .OfType<string>().Distinct().ToList();

        // SpondEvent.Name is a read-only alias of Heading.
        return new EventInfo(e.Id, e.Name ?? string.Empty, start, end < start ? start : end, e.Cancelled ?? false, responses, trainers);
    }

    /// <summary>Prefers the raw ISO timestamp: <c>SpondEvent.StartTime</c> is converted to the *machine's* time zone.</summary>
    private DateTimeOffset ParseTimestamp(string? iso, DateTime fallback) =>
        _clock.ToClub(
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : new DateTimeOffset(fallback));
}
