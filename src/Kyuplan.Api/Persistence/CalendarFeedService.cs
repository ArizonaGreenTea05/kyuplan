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
public sealed class CalendarFeedService(PlannerDbContext db, StatisticsService statistics, ClubClock clock, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CalendarFeedDto>> ListAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var feeds = await db.CalendarFeeds.AsNoTracking().ToListAsync(cancellationToken);
        return [.. feeds.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => ToDto(f, baseUrl))];
    }

    /// <exception cref="CalendarFeedNotFoundException"/>
    public async Task<CalendarFeedDto> GetAsync(int feedId, string baseUrl, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(feedId, cancellationToken), baseUrl);

    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="InvalidInputException">A subgroup is not one of the group.</exception>
    /// <exception cref="AlreadyExistsException">Another feed has the name.</exception>
    public async Task<CalendarFeedDto> CreateAsync(CalendarFeedInputDto input, string baseUrl, CancellationToken cancellationToken)
    {
        var feed = new CalendarFeed { Token = NewToken(), CreatedAt = time.GetUtcNow() };
        await ApplyAsync(feed, input, cancellationToken);
        db.CalendarFeeds.Add(feed);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed, baseUrl);
    }

    /// <summary>Replaces the name and the sources; the address stays.</summary>
    public async Task<CalendarFeedDto> UpdateAsync(int feedId, CalendarFeedInputDto input, string baseUrl, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        await ApplyAsync(feed, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed, baseUrl);
    }

    /// <summary>Gives the feed a new secret: the old address stops working, e.g. because it got to somebody it should not have.</summary>
    public async Task<CalendarFeedDto> RegenerateTokenAsync(int feedId, string baseUrl, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        feed.Token = NewToken();
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(feed, baseUrl);
    }

    public async Task DeleteAsync(int feedId, CancellationToken cancellationToken)
    {
        var feed = await FindAsync(feedId, cancellationToken);
        db.CalendarFeeds.Remove(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The name and ICS text of the feed with that token, or <c>null</c> when there is none.</summary>
    public async Task<(string Name, string Ics)?> RenderAsync(string token, CancellationToken cancellationToken)
    {
        var feed = await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.Token == token, cancellationToken);
        // The database may compare case-insensitively (collation); this exact, constant-time check makes sure only the precise token matches.
        if (feed is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(feed.Token), System.Text.Encoding.UTF8.GetBytes(token))) return null;

        var entries = new List<IcsEntry>();
        foreach (var source in ReadSources(feed))
        {
            entries.AddRange(source.Kind == CalendarSourceKinds.Events
                ? await EventEntriesAsync(source, cancellationToken)
                : await MemberDayEntriesAsync(source, cancellationToken));
        }

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
                .Select(e => new IcsEntry($"event-{e.Id}@kyuplan", Title(source, words, e.Name, people => e.Responses.Keys.Any(people.Contains)), e.Start, e.End, null, e.Cancelled)),
        ];
    }

    private static bool Invites(EventInfo e, HashSet<string> selected, string? invitation) => invitation switch
    {
        EventInvitations.All => selected.All(e.Responses.ContainsKey),
        EventInvitations.Only => e.Responses.Count > 0 && e.Responses.Keys.All(selected.Contains),
        _ => e.Responses.Keys.Any(selected.Contains),
    };

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

    private static CalendarFeedDto ToDto(CalendarFeed feed, string baseUrl) =>
        new(feed.Id, feed.Name, feed.Token, $"{baseUrl}/calendar-feeds/{feed.Token}.ics", ReadSources(feed), feed.CreatedAt);

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
}
