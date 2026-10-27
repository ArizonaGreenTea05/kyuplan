using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Statistics;

public sealed class GroupNotFoundException(string groupId) : Exception($"No group with id '{groupId}'.");

public sealed class EventNotFoundException(string eventId, int monthsBack, int monthsAhead)
    : Exception($"No event with id '{eventId}' within {monthsBack} months back and {monthsAhead} months ahead of today.");

/// <summary>
/// Loads data from the configured source and runs the calculators over it. The members a user hid (<see cref="Member.Hidden"/>) are taken
/// out of every group and event it hands out, so that no statistic, list or calendar entry knows them; only
/// <see cref="GetWindowEventsAsync"/> with <c>includeHidden</c> (the sync, which keeps them up to date) sees them.
/// </summary>
public sealed class StatisticsService(
    IDataSource source,
    ClubClock clock,
    IOptions<StatisticsOptions> options,
    IServiceScopeFactory scopes)
{
    public const int MinDashboardDays = 14;
    public const int MaxDashboardDays = 365;
    public const int MaxEventSearchMonths = 24;

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await GetVisibleGroupsAsync(cancellationToken);
        return groups
            .Select(g => new GroupDto(
                g.Id, g.Name, g.Members.Count,
                g.SubGroups.Select(s => new SubGroupDto(s.Id, s.Name)).ToList()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The events of the search window (cancelled ones left out) and the span to preselect: from the last exam to the
    /// next one, or, without such a pair, the events of the last six months — so there is always a choice to start from.
    /// The window reaches <see cref="StatisticsOptions.EventSearchMonthsBack"/> months back and
    /// <see cref="StatisticsOptions.EventSearchMonthsAhead"/> ahead. Its events come from the data source's cache,
    /// which the statistics call that follows shares for spans inside the window.
    /// </summary>
    public async Task<EventChoicesDto> GetEventChoicesAsync(string groupId, CancellationToken cancellationToken)
    {
        var group = await FindGroupAsync(groupId, cancellationToken);
        var events = await GetSearchWindowEventsAsync(group.Id, includeHidden: false, cancellationToken);

        var span = ExamSpanFinder.Find(events, options.Value.ExamKeyword, clock.Now)
            ?? ExamSpanFinder.FindRecent(events, clock.Now);
        return new EventChoicesDto(
            events.Where(e => !e.Cancelled).Select(e => new EventChoiceDto(e.Id, e.Name, e.Start)).ToList(),
            span?.From.Id,
            span?.To.Id);
    }

    /// <summary>A group with its subgroups and members, as Spond has them.</summary>
    /// <exception cref="GroupNotFoundException">The group is not one of the account's.</exception>
    public Task<GroupInfo> GetGroupInfoAsync(string groupId, CancellationToken cancellationToken) =>
        FindGroupAsync(groupId, cancellationToken);

    /// <summary>
    /// The events of the search window of a group (cancelled ones too), chronologically, without the hidden members' answers and the
    /// hidden members among the trainers, unless <paramref name="includeHidden"/> says the caller needs them (<see cref="Persistence.SpondSync"/>).
    /// </summary>
    /// <exception cref="GroupNotFoundException">The group is not one of the account's.</exception>
    public async Task<IReadOnlyList<EventInfo>> GetWindowEventsAsync(string groupId, CancellationToken cancellationToken, bool includeHidden = false)
    {
        var group = await FindGroupAsync(groupId, cancellationToken);
        return await GetSearchWindowEventsAsync(group.Id, includeHidden, cancellationToken);
    }

    /// <summary>One event of the search window of a group, e.g. to make it an exam.</summary>
    /// <exception cref="GroupNotFoundException">The group is not one of the account's.</exception>
    /// <exception cref="EventNotFoundException">The event is not in the search window.</exception>
    public async Task<EventInfo> FindEventAsync(string groupId, string eventId, CancellationToken cancellationToken)
    {
        var events = await GetWindowEventsAsync(groupId, cancellationToken);
        return events.FirstOrDefault(e => e.Id == eventId) ?? throw new EventNotFoundException(eventId, MonthsBack, MonthsAhead);
    }

    /// <summary>Turns the time span of a query into days: an event stands for the day it starts on.</summary>
    /// <exception cref="EventNotFoundException">An event id is not in the search window.</exception>
    public async Task<AttendanceFilter> ResolveAsync(AttendanceQuery query, CancellationToken cancellationToken)
    {
        DateOnly from, to;
        if (!string.IsNullOrWhiteSpace(query.FromEventId) && !string.IsNullOrWhiteSpace(query.ToEventId))
        {
            var group = await FindGroupAsync(query.GroupId, cancellationToken);
            var events = await GetSearchWindowEventsAsync(group.Id, includeHidden: false, cancellationToken);
            from = StartDay(Find(events, query.FromEventId));
            to = StartDay(Find(events, query.ToEventId));
        }
        else if (query is { From: { } first, To: { } last })
        {
            (from, to) = (first, last);
        }
        else
        {
            throw new ArgumentException("A query needs two dates or two events; the endpoint validates this.", nameof(query));
        }

        var subGroupIds = query.SubGroupIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

        return new AttendanceFilter(
            query.GroupId, from, to, subGroupIds is { Count: > 0 } ? subGroupIds : null,
            query.MinAttendancePercent, query.MinInvitedEvents, query.EventNameContains, query.EventNameExcludes);

        EventInfo Find(IEnumerable<EventInfo> events, string id) =>
            events.FirstOrDefault(e => e.Id == id) ?? throw new EventNotFoundException(id, MonthsBack, MonthsAhead);

        // Event times are already in the club time zone.
        static DateOnly StartDay(EventInfo e) => DateOnly.FromDateTime(e.Start.Date);
    }

    /// <param name="ratings">The star evaluations of the group's members (see <c>RatingService.GetBookAsync</c>).</param>
    public async Task<AttendanceStatisticsDto> GetAttendanceAsync(AttendanceFilter filter, RatingBook ratings, CancellationToken cancellationToken)
    {
        var group = await FindGroupAsync(filter.GroupId, cancellationToken);
        var events = await WithoutHiddenAsync(group.Id, await source.GetEventsAsync(
            group.Id, clock.StartOfDay(filter.From), clock.StartOfDay(filter.To.AddDays(1)), cancellationToken), cancellationToken);

        return AttendanceCalculator.Calculate(group, events, filter, clock.Now, ratings);
    }

    public async Task<DashboardDto> GetDashboardAsync(
        string groupId, string? subGroupId, int? days, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var lookBack = Math.Clamp(days ?? settings.DashboardDays, MinDashboardDays, MaxDashboardDays);

        var group = await FindGroupAsync(groupId, cancellationToken);
        var now = clock.Now;
        var events = await WithoutHiddenAsync(group.Id, await source.GetEventsAsync(
            group.Id, clock.StartOfDay(clock.Today.AddDays(1 - lookBack)), now.AddDays(settings.UpcomingDays + 1), cancellationToken), cancellationToken);

        return DashboardBuilder.Build(group, subGroupId, events.ToList(), lookBack, now, settings);
    }

    private int MonthsBack => Math.Clamp(options.Value.EventSearchMonthsBack, 1, MaxEventSearchMonths);

    private int MonthsAhead => Math.Clamp(options.Value.EventSearchMonthsAhead, 1, MaxEventSearchMonths);

    /// <summary>Every event (cancelled ones too) that starts within the search window, chronologically.</summary>
    private async Task<List<EventInfo>> GetSearchWindowEventsAsync(string groupId, bool includeHidden, CancellationToken cancellationToken)
    {
        var from = clock.StartOfDay(clock.Today.AddMonths(-MonthsBack));
        var to = clock.StartOfDay(clock.Today.AddMonths(MonthsAhead).AddDays(1));

        var events = await source.GetEventsAsync(groupId, from, to, cancellationToken);
        if (!includeHidden) events = await WithoutHiddenAsync(groupId, events, cancellationToken);
        return events
            .DistinctBy(e => e.Id)
            .Where(e => e.Start >= from && e.Start < to)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<GroupInfo> FindGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        var groups = await GetVisibleGroupsAsync(cancellationToken);
        return groups.FirstOrDefault(g => g.Id == groupId) ?? throw new GroupNotFoundException(groupId);
    }

    /// <summary>The groups of the data source without the members a user hid.</summary>
    private async Task<IReadOnlyList<GroupInfo>> GetVisibleGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await source.GetGroupsAsync(cancellationToken);
        var hidden = await HiddenAsync(cancellationToken);
        if (hidden.Count == 0) return groups;

        return [.. groups.Select(g => hidden.TryGetValue(g.Id, out var ids) ? g with { Members = [.. g.Members.Where(m => !ids.Contains(m.Id))] } : g)];
    }

    /// <summary>The events without the answers of the members a user hid, and without them among the trainers.</summary>
    private async Task<IReadOnlyList<EventInfo>> WithoutHiddenAsync(string groupId, IReadOnlyList<EventInfo> events, CancellationToken cancellationToken)
    {
        if (!(await HiddenAsync(cancellationToken)).TryGetValue(groupId, out var ids)) return events;

        return [.. events.Select(e => e.Responses.Keys.Any(ids.Contains) || e.Trainers?.Any(ids.Contains) == true
            ? e with
            {
                Responses = e.Responses.Where(r => !ids.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value),
                Trainers = e.Trainers?.Where(id => !ids.Contains(id)).ToList(),
            }
            : e)];
    }

    /// <summary>The Spond ids of the members a user hid, by group. A member that is gone from the data source is not listed: the source does not list them either.</summary>
    private async Task<Dictionary<string, HashSet<string>>> HiddenAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();

        var hidden = await db.Members.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Hidden && m.RemovedAt == null).Select(m => new { m.GroupId, m.SpondId }).ToListAsync(cancellationToken);
        return hidden.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.Select(m => m.SpondId).ToHashSet());
    }
}
