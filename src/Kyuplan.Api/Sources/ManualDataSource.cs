using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Sources;

/// <summary>
/// The data source of <see cref="DataSourceKinds.Manual"/>: what the users entered (<see cref="ManualDataService"/>) is read from the
/// database and handed on as if Spond had delivered it. Nothing is cached, so a change is there at once.
/// </summary>
public sealed class ManualDataSource(IServiceScopeFactory scopes, ClubClock clock) : IDataSource
{
    public bool IsDemo => false;

    public string Kind => DataSourceKinds.Manual;

    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();

        var groups = await db.ManualGroups.AsNoTracking().Include(g => g.SubGroups).Include(g => g.Members).ThenInclude(m => m.SubGroups).ToListAsync(cancellationToken);
        return [.. groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new GroupInfo(
            g.Id,
            g.Name,
            [.. g.SubGroups.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new SubGroupInfo(s.Id, s.Name))],
            [.. g.Members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => new MemberInfo(m.Id, m.Name, [.. m.SubGroups.Select(l => l.SubGroupId)], m.BirthDate))]))];
    }

    public async Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();

        // DateTimeOffset cannot be compared in SQL on SQLite; a club's events are few enough to filter here.
        var events = await db.ManualEvents.AsNoTracking().Include(e => e.Invitations).Where(e => e.GroupId == groupId).ToListAsync(cancellationToken);
        return [.. events.Where(e => e.End >= from && e.End <= to).OrderBy(e => e.Start).Select(e => new EventInfo(
            e.Id,
            e.Name,
            clock.ToClub(e.Start),
            clock.ToClub(e.End),
            e.Cancelled,
            e.Invitations.ToDictionary(i => i.MemberId, i => i.Rsvp)))];
    }

    public void InvalidateCache()
    {
    }
}
