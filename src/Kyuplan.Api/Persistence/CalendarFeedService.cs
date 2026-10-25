using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class CalendarFeedNotFoundException(int feedId) : Exception($"No calendar feed with id {feedId}.");

/// <summary>
/// Reads and writes the calendar feeds and renders them as ICS (<see cref="IcsWriter"/>). A feed is a list of sources; each source selects
/// events of a Spond group (by who is invited and by name) or a yearly day of the members (birthday, start in the club, belt). The ICS of a
/// feed is made on every request from what Spond and the database say now; calendar apps ask again every few hours.
/// Input is expected to be validated by the endpoints (<see cref="CalendarFeedRules"/>).
/// </summary>
public sealed class CalendarFeedService(
    PlannerDbContext db, StatisticsService statistics, ClubClock clock, TimeProvider time, ILogger<CalendarFeedService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How long the feed list waits for the groups before it shows the feeds without the check for broken sources.</summary>
    internal static readonly TimeSpan GroupCheckTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<CalendarFeedDto>> ListAsync(CancellationToken cancellationToken)
    {
        var feeds = await db.CalendarFeeds.AsNoTracking().ToListAsync(cancellationToken);
        var known = feeds.Count == 0 ? null : await KnownGroupIdsAsync(cancellationToken);
        return [.. feeds.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => ToDto(f, known))];
    }

    /// <exception cref="CalendarFeedNotFoundException"/>
    public async Task<CalendarFeedDto> GetAsync(int feedId, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        return ToDto(feed, await KnownGroupIdsAsync(cancellationToken));
    }

    /// <summary>Makes the feed; the answer is the only time its address (<see cref="CalendarFeedDto.Url"/>) is told.</summary>
    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="InvalidInputException">A subgroup is not one of the group.</exception>
    /// <exception cref="AlreadyExistsException">Another feed has the name.</exception>
    public async Task<CalendarFeedDto> CreateAsync(CalendarFeedInputDto input, string baseUrl, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var feed = new CalendarFeed { TokenHash = HashToken(token), CreatedAt = time.GetUtcNow() };
        await ApplyAsync(feed, input, cancellationToken);
        db.CalendarFeeds.Add(feed);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed, null, AddressOf(baseUrl, token));
    }

    /// <summary>Replaces the name and the sources; the address stays (and is not told again).</summary>
    public async Task<CalendarFeedDto> UpdateAsync(int feedId, CalendarFeedInputDto input, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        await ApplyAsync(feed, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed);
    }

    /// <summary>Gives the feed a new secret: the old address stops working, e.g. because it got to somebody it should not have. The answer is the only time the new address is told.</summary>
    public async Task<CalendarFeedDto> RegenerateTokenAsync(int feedId, string baseUrl, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        var token = NewToken();
        feed.TokenHash = HashToken(token);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed, await KnownGroupIdsAsync(cancellationToken), AddressOf(baseUrl, token));
    }

    public async Task DeleteAsync(int feedId, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        db.CalendarFeeds.Remove(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The name and ICS text of the feed with that token, or <c>null</c> when there is none. A source whose group is gone (the account lost
    /// access, the group was deleted) is left out and logged, so that the other sources are still delivered: calendar apps drop a feed that answers 404.
    /// </summary>
    public async Task<(string Name, string Ics)?> RenderAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        var feed = await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.TokenHash == hash, cancellationToken);
        // The database may compare case-insensitively (collation); this exact, constant-time check makes sure only the precise hash matches.
        if (feed is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(feed.TokenHash), System.Text.Encoding.UTF8.GetBytes(hash))) return null;

        var entries = new List<IcsEntry>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in ReadSources(feed))
        {
            try
            {
                entries.AddRange(source.Kind == CalendarSourceKinds.Events
                    ? await EventEntriesAsync(source, cancellationToken)
                    : await MemberDayEntriesAsync(source, cancellationToken));
            }
            catch (GroupNotFoundException)
            {
                missing.Add(source.GroupId);
            }
        }

        if (missing.Count > 0)
            log.LogWarning("Calendar feed {Feed}: the groups '{GroupIds}' do not exist any more; their sources are left out.", feed.Name, string.Join("', '", missing));

        return (feed.Name, IcsWriter.Write(feed.Name, entries.DistinctBy(e => e.Uid), time.GetUtcNow()));
    }

    private async Task<List<IcsEntry>> EventEntriesAsync(CalendarSourceDto source, CancellationToken cancellationToken)
    {
        var group = await statistics.GetGroupInfoAsync(source.GroupId, cancellationToken);
        var events = await statistics.GetWindowEventsAsync(source.GroupId, cancellationToken);
        var selected = await SelectedAsync(source, group, cancellationToken);
        var words = SubGroupWords(source, group);
        var contains = Terms(source.NameContains);
        var excludes = Terms(source.NameExcludes);
        var exams = source.OnlyExams
            ? (await db.Exams.AsNoTracking().Where(e => e.GroupId == source.GroupId).Select(e => e.EventId).ToListAsync(cancellationToken)).ToHashSet()
            : null;

        return
        [
            .. events
                .Where(e => source.IncludeCancelled || !e.Cancelled)
                .Where(e => exams is null || exams.Contains(e.Id))
                .Where(e => contains.Length == 0 || contains.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Where(e => !excludes.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Where(e => selected is null || Invites(e, selected, source.Invitation))
                .Select(e => new IcsEntry($"event-{e.Id}@kyuplan", Title(source, words, e.Name, people => e.Participants.Any(r => people.Contains(r.Key))), e.Start, e.End, null, e.Cancelled)),
        ];
    }

    /// <summary>Who is invited: the participants, not the hosts of the event (they run it, they are no invitees).</summary>
    private static bool Invites(EventInfo e, HashSet<string> selected, string? invitation)
    {
        var invited = e.Participants.Select(r => r.Key).ToHashSet();
        return invitation switch
        {
            EventInvitations.All => selected.All(invited.Contains),
            EventInvitations.Only => invited.Count > 0 && invited.All(selected.Contains),
            _ => invited.Any(selected.Contains),
        };
    }

    /// <summary>The same day every year, for the year before this one, this one and the next, with the number of years in the title.</summary>
    private async Task<List<IcsEntry>> MemberDayEntriesAsync(CalendarSourceDto source, CancellationToken cancellationToken)
    {
        var group = await statistics.GetGroupInfoAsync(source.GroupId, cancellationToken);
        var selected = await SelectedAsync(source, group, cancellationToken);
        var members = await db.Members.AsNoTracking().Include(m => m.Belt).Where(m => m.GroupId == source.GroupId).ToListAsync(cancellationToken);
        var words = SubGroupWords(source, group);
        var thisYear = clock.Today.Year;
        var entries = new List<IcsEntry>();

        foreach (var member in members.Where(m => selected is null || selected.Contains(m.SpondId)))
        {
            var day = source.Kind switch
            {
                CalendarSourceKinds.Birthdays => member.BirthDate,
                CalendarSourceKinds.ClubAnniversaries => member.StartedAt,
                _ => member.Belt is null ? null : member.BeltSince,
            };
            if (day is not { } origin) continue;

            for (var year = thisYear - 1; year <= thisYear + 1; year++)
            {
                var years = year - origin.Year;
                if (years < 1) continue;

                // 29 February is celebrated on the 28th in the years that have no 29th.
                var date = new DateOnly(year, origin.Month, Math.Min(origin.Day, DateTime.DaysInMonth(year, origin.Month)));
                var title = source.Kind switch
                {
                    CalendarSourceKinds.Birthdays => $"\U0001F382 {member.Name} ({years})",
                    CalendarSourceKinds.ClubAnniversaries => $"\U0001F3C5 {member.Name} ({years})",
                    _ => $"\U0001F94B {member.Name}: {member.Belt!.Name} ({years})",
                };
                entries.Add(new IcsEntry($"{source.Kind}-{member.Id}-{year}@kyuplan", Title(source, words, title, people => people.Contains(member.SpondId)), null, null, date, false));
            }
        }

        return entries;
    }

    /// <summary>The Spond ids of the people a source is about; <c>null</c> = everybody.</summary>
    private async Task<HashSet<string>?> SelectedAsync(CalendarSourceDto source, GroupInfo group, CancellationToken cancellationToken)
    {
        var subGroups = source.SubGroupIds ?? [];
        var memberIds = source.MemberIds ?? [];
        if (subGroups.Count == 0 && memberIds.Count == 0) return null;

        var selected = group.Members.Where(m => m.SubGroupIds.Intersect(subGroups).Any()).Select(m => m.Id).ToHashSet();
        selected.UnionWith(await db.Members.AsNoTracking()
            .Where(m => m.GroupId == source.GroupId && memberIds.Contains(m.Id)).Select(m => m.SpondId).ToListAsync(cancellationToken));
        return selected;
    }

    private async Task ApplyAsync(CalendarFeed feed, CalendarFeedInputDto input, CancellationToken cancellationToken)
    {
        var name = input.Name.Trim();
        if (await db.CalendarFeeds.AnyAsync(f => f.Name == name && f.Id != feed.Id, cancellationToken))
            throw new AlreadyExistsException($"There already is a calendar named '{name}'.");

        var sources = input.Sources.Select(Clean).ToList();
        foreach (var source in sources)
        {
            var group = await statistics.GetGroupInfoAsync(source.GroupId, cancellationToken);
            var unknown = (source.SubGroupIds ?? []).FirstOrDefault(id => group.SubGroups.All(s => s.Id != id));
            if (unknown is not null) throw new InvalidInputException($"The group '{group.Name}' has no subgroup with id '{unknown}'.");
        }

        feed.Name = name;
        feed.SourcesJson = JsonSerializer.Serialize(sources, Json);
    }

    private static CalendarSourceDto Clean(CalendarSourceDto s) => s with
    {
        GroupId = s.GroupId.Trim(),
        SubGroupIds = [.. (s.SubGroupIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct()],
        MemberIds = [.. (s.MemberIds ?? []).Distinct()],
        Invitation = s.Invitation ?? EventInvitations.Any,
        NameContains = string.IsNullOrWhiteSpace(s.NameContains) ? null : s.NameContains.Trim(),
        NameExcludes = string.IsNullOrWhiteSpace(s.NameExcludes) ? null : s.NameExcludes.Trim(),
        Label = string.IsNullOrWhiteSpace(s.Label) ? null : s.Label.Trim(),
        SubGroupLabels = s.SubGroupLabels is null ? null : s.SubGroupLabels
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key.Trim(), p => p.Value.Trim()),
    };

    private async Task<CalendarFeed> FindAsync(int feedId, CancellationToken cancellationToken) =>
        await db.CalendarFeeds.FirstOrDefaultAsync(f => f.Id == feedId, cancellationToken) ?? throw new CalendarFeedNotFoundException(feedId);

    /// <summary>
    /// The ids of the groups the data source has now, or <c>null</c> when it cannot say (e.g. Spond is unreachable or takes longer than
    /// <see cref="GroupCheckTimeout"/>): the feed list does not depend on Spond being up or fast, so then no source is reported as broken.
    /// The groups are cached by the data source, so this is only slow when the cache is empty; a load that outlasts the timeout goes on
    /// in the background and fills the cache for the next request.
    /// </summary>
    private async Task<HashSet<string>?> KnownGroupIdsAsync(CancellationToken cancellationToken)
    {
        var load = statistics.GetGroupsAsync(cancellationToken);
        try
        {
            return (await load.WaitAsync(GroupCheckTimeout, time, cancellationToken)).Select(g => g.Id).ToHashSet();
        }
        catch (TimeoutException)
        {
            log.LogWarning("The groups were not there after {Seconds} seconds; the sources of the calendar feeds are not checked.", GroupCheckTimeout.TotalSeconds);
            _ = load.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the groups to check the sources of the calendar feeds.");
            return null;
        }
    }

    /// <param name="knownGroupIds">The groups that exist; <c>null</c> = not checked (no source is reported as broken).</param>
    /// <param name="url">The address, only while the secret is known (right after it was made).</param>
    private static CalendarFeedDto ToDto(CalendarFeed feed, HashSet<string>? knownGroupIds = null, string? url = null)
    {
        var sources = ReadSources(feed);
        var broken = knownGroupIds is null
            ? []
            : sources.Select((s, i) => (s, i)).Where(x => !knownGroupIds.Contains(x.s.GroupId)).Select(x => x.i).ToList();
        return new(feed.Id, feed.Name, url, sources, feed.CreatedAt, broken);
    }

    private static List<CalendarSourceDto> ReadSources(CalendarFeed feed) =>
        JsonSerializer.Deserialize<List<CalendarSourceDto>>(feed.SourcesJson, Json) ?? [];

    /// <summary>The subgroup words of a source, each with the Spond ids of the subgroup's members.</summary>
    private static List<(string Label, HashSet<string> People)> SubGroupWords(CalendarSourceDto source, GroupInfo group) =>
    [
        .. (source.SubGroupIds ?? [])
            .Where(id => source.SubGroupLabels?.GetValueOrDefault(id) is { Length: > 0 })
            .Select(id => (source.SubGroupLabels![id], group.Members.Where(m => m.SubGroupIds.Contains(id)).Select(m => m.Id).ToHashSet())),
    ];

    /// <summary>The source's word, then the words of the subgroups the entry concerns (<paramref name="concerns"/> tells whether it does): <c>Kids / Teens: Training</c>.</summary>
    private static string Title(CalendarSourceDto source, List<(string Label, HashSet<string> People)> words, string title, Func<HashSet<string>, bool> concerns)
    {
        var prefix = string.Join(" / ", new[] { source.Label }.Concat(words.Where(w => concerns(w.People)).Select(w => w.Label)).Where(l => !string.IsNullOrEmpty(l)));
        return prefix.Length > 0 ? $"{prefix}: {title}" : title;
    }

    private static string[] Terms(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string AddressOf(string baseUrl, string token) => $"{baseUrl}/calendar-feeds/{token}.ics";

    private const int HashLength = 64;

    /// <summary>
    /// What is stored of a token: SHA-256 as 64 lower-case hex characters. A fast hash is enough because the token is 256 random bits, which
    /// cannot be guessed or looked up in a table; the database, a backup or a mirror therefore does not hold anything that opens a feed.
    /// </summary>
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Feeds made before the tokens were hashed (or restored from a backup of those) still hold the token itself (43 characters, a hash has 64):
    /// hashes them, so that their addresses go on working and the secret is gone from the database. Returns how many were converted.
    /// </summary>
    public static async Task<int> HashLegacyTokensAsync(PlannerDbContext db, CancellationToken cancellationToken)
    {
        var legacy = await db.CalendarFeeds.Where(f => f.TokenHash.Length != HashLength).ToListAsync(cancellationToken);
        foreach (var feed in legacy) feed.TokenHash = HashToken(feed.TokenHash);
        await db.SaveChangesAsync(cancellationToken);
        return legacy.Count;
    }
}
