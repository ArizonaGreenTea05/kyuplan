using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class ManualDataNotFoundException(string what, string id) : Exception($"No {what} with id '{id}'.");

/// <summary>
/// Reads and writes what the users keep by hand when the data source is <see cref="DataSourceKinds.Manual"/>: groups with subgroups, their
/// members and their events with the invitations and answers. <see cref="Sources.ManualDataSource"/> hands the same rows to the rest of the API as
/// if Spond had delivered them. Input is expected to be validated by the endpoints (<see cref="ManualDataRules"/>).
/// </summary>
public sealed class ManualDataService(PlannerDbContext db)
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    public async Task<IReadOnlyList<ManualGroupDto>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.ManualGroups.AsNoTracking().Include(g => g.SubGroups).ToListAsync(cancellationToken);
        var counts = await db.ManualMembers.AsNoTracking().GroupBy(m => m.GroupId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return [.. groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new ManualGroupDto(
            g.Id, g.Name, [.. g.SubGroups.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new SubGroupDto(s.Id, s.Name))], counts.GetValueOrDefault(g.Id)))];
    }

    /// <exception cref="AlreadyExistsException">Another group has the name.</exception>
    public async Task<ManualGroupDto> CreateGroupAsync(ManualNameInputDto input, CancellationToken cancellationToken)
    {
        var name = input.Name.Trim();
        if (await GroupNameTakenAsync(name, null, cancellationToken)) throw new AlreadyExistsException($"A group named '{name}' exists already.");

        var group = new ManualGroup { Id = NewId(), Name = name };
        db.ManualGroups.Add(group);
        await SaveNameAsync($"A group named '{name}' exists already.", cancellationToken);
        return new ManualGroupDto(group.Id, group.Name, [], 0);
    }

    /// <exception cref="ManualDataNotFoundException"/>
    /// <exception cref="AlreadyExistsException"/>
    public async Task<ManualGroupDto> RenameGroupAsync(string groupId, ManualNameInputDto input, CancellationToken cancellationToken)
    {
        var group = await FindGroupAsync(groupId, cancellationToken);
        var name = input.Name.Trim();
        if (await GroupNameTakenAsync(name, groupId, cancellationToken)) throw new AlreadyExistsException($"A group named '{name}' exists already.");

        group.Name = name;
        await SaveNameAsync($"A group named '{name}' exists already.", cancellationToken);
        return (await ListGroupsAsync(cancellationToken)).First(g => g.Id == groupId);
    }

    /// <summary>Deletes the group with its subgroups, members and events.</summary>
    public async Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        db.ManualGroups.Remove(await FindGroupAsync(groupId, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <exception cref="ManualDataNotFoundException"/>
    /// <exception cref="AlreadyExistsException"/>
    public async Task<SubGroupDto> CreateSubGroupAsync(string groupId, ManualNameInputDto input, CancellationToken cancellationToken)
    {
        await FindGroupAsync(groupId, cancellationToken);
        var name = input.Name.Trim();
        if (await SubGroupNameTakenAsync(groupId, name, null, cancellationToken)) throw new AlreadyExistsException($"A subgroup named '{name}' exists already.");

        var subGroup = new ManualSubGroup { Id = NewId(), GroupId = groupId, Name = name };
        db.ManualSubGroups.Add(subGroup);
        await SaveNameAsync($"A subgroup named '{name}' exists already.", cancellationToken);
        return new SubGroupDto(subGroup.Id, subGroup.Name);
    }

    public async Task<SubGroupDto> RenameSubGroupAsync(string subGroupId, ManualNameInputDto input, CancellationToken cancellationToken)
    {
        var subGroup = await FindSubGroupAsync(subGroupId, cancellationToken);
        var name = input.Name.Trim();
        if (await SubGroupNameTakenAsync(subGroup.GroupId, name, subGroupId, cancellationToken)) throw new AlreadyExistsException($"A subgroup named '{name}' exists already.");

        subGroup.Name = name;
        await SaveNameAsync($"A subgroup named '{name}' exists already.", cancellationToken);
        return new SubGroupDto(subGroup.Id, subGroup.Name);
    }

    /// <summary>Deletes the subgroup; its members stay in the group.</summary>
    public async Task DeleteSubGroupAsync(string subGroupId, CancellationToken cancellationToken)
    {
        db.ManualSubGroups.Remove(await FindSubGroupAsync(subGroupId, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManualMemberDto>> ListMembersAsync(string groupId, CancellationToken cancellationToken)
    {
        await FindGroupAsync(groupId, cancellationToken);
        var members = await db.ManualMembers.AsNoTracking().Include(m => m.SubGroups).Where(m => m.GroupId == groupId).ToListAsync(cancellationToken);
        return [.. members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToDto)];
    }

    /// <exception cref="ManualDataNotFoundException"/>
    /// <exception cref="InvalidInputException">A subgroup is not one of the group.</exception>
    public async Task<ManualMemberDto> CreateMemberAsync(string groupId, ManualMemberInputDto input, CancellationToken cancellationToken)
    {
        await FindGroupAsync(groupId, cancellationToken);
        var member = new ManualMember { Id = NewId(), GroupId = groupId };
        await ApplyAsync(member, input, cancellationToken);
        db.ManualMembers.Add(member);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(member);
    }

    public async Task<ManualMemberDto> UpdateMemberAsync(string memberId, ManualMemberInputDto input, CancellationToken cancellationToken)
    {
        var member = await db.ManualMembers.Include(m => m.SubGroups).FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken)
            ?? throw new ManualDataNotFoundException("member", memberId);
        await ApplyAsync(member, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(member);
    }

    public async Task DeleteMemberAsync(string memberId, CancellationToken cancellationToken)
    {
        db.ManualMembers.Remove(await db.ManualMembers.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken) ?? throw new ManualDataNotFoundException("member", memberId));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManualEventDto>> ListEventsAsync(string groupId, CancellationToken cancellationToken)
    {
        await FindGroupAsync(groupId, cancellationToken);
        var events = await db.ManualEvents.AsNoTracking().Include(e => e.Invitations).Include(e => e.Trainers).Where(e => e.GroupId == groupId).ToListAsync(cancellationToken);
        return [.. events.OrderByDescending(e => e.Start).Select(ToDto)];
    }

    /// <exception cref="ManualDataNotFoundException"/>
    /// <exception cref="InvalidInputException">An invited member is not one of the group.</exception>
    public async Task<ManualEventDto> CreateEventAsync(string groupId, ManualEventInputDto input, CancellationToken cancellationToken)
    {
        await FindGroupAsync(groupId, cancellationToken);
        var manualEvent = new ManualEvent { Id = NewId(), GroupId = groupId };
        await ApplyAsync(manualEvent, input, cancellationToken);
        db.ManualEvents.Add(manualEvent);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(manualEvent);
    }

    public async Task<ManualEventDto> UpdateEventAsync(string eventId, ManualEventInputDto input, CancellationToken cancellationToken)
    {
        var manualEvent = await db.ManualEvents.Include(e => e.Invitations).Include(e => e.Trainers).FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new ManualDataNotFoundException("event", eventId);
        await ApplyAsync(manualEvent, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(manualEvent);
    }

    public async Task DeleteEventAsync(string eventId, CancellationToken cancellationToken)
    {
        db.ManualEvents.Remove(await db.ManualEvents.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken) ?? throw new ManualDataNotFoundException("event", eventId));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyAsync(ManualMember member, ManualMemberInputDto input, CancellationToken cancellationToken)
    {
        var wanted = (input.SubGroupIds ?? []).Distinct().ToList();
        var valid = await db.ManualSubGroups.Where(s => s.GroupId == member.GroupId && wanted.Contains(s.Id)).CountAsync(cancellationToken);
        if (valid != wanted.Count) throw new InvalidInputException("subGroupIds: every subgroup must belong to the member's group.");

        member.Name = input.Name.Trim();
        member.BirthDate = input.BirthDate;
        member.SubGroups.RemoveAll(l => !wanted.Contains(l.SubGroupId));
        foreach (var id in wanted.Where(id => member.SubGroups.All(l => l.SubGroupId != id)))
            member.SubGroups.Add(new ManualMemberSubGroup { MemberId = member.Id, SubGroupId = id });
    }

    private async Task ApplyAsync(ManualEvent manualEvent, ManualEventInputDto input, CancellationToken cancellationToken)
    {
        var invitations = (input.Invitations ?? []).DistinctBy(i => i.MemberId).ToList();
        var ids = invitations.Select(i => i.MemberId).ToList();
        var valid = await db.ManualMembers.Where(m => m.GroupId == manualEvent.GroupId && ids.Contains(m.Id)).CountAsync(cancellationToken);
        if (valid != ids.Count) throw new InvalidInputException("invitations: every invited member must belong to the event's group.");

        var trainerIds = (input.TrainerIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList();
        var validTrainers = await db.ManualMembers.Where(m => m.GroupId == manualEvent.GroupId && trainerIds.Contains(m.Id)).CountAsync(cancellationToken);
        if (validTrainers != trainerIds.Count) throw new InvalidInputException("trainerIds: every trainer must be a member of the event's group.");

        manualEvent.Name = input.Name.Trim();
        manualEvent.Start = input.Start;
        manualEvent.End = input.End;
        manualEvent.Cancelled = input.Cancelled;
        manualEvent.Invitations.RemoveAll(i => !ids.Contains(i.MemberId));
        foreach (var invitation in invitations)
        {
            var rsvp = ParseRsvp(invitation.Rsvp);
            if (manualEvent.Invitations.FirstOrDefault(i => i.MemberId == invitation.MemberId) is { } existing) existing.Rsvp = rsvp;
            else manualEvent.Invitations.Add(new ManualInvitation { EventId = manualEvent.Id, MemberId = invitation.MemberId, Rsvp = rsvp });
        }

        manualEvent.Trainers.RemoveAll(t => !trainerIds.Contains(t.MemberId));
        foreach (var id in trainerIds.Where(id => manualEvent.Trainers.All(t => t.MemberId != id)))
            manualEvent.Trainers.Add(new ManualEventTrainer { EventId = manualEvent.Id, MemberId = id });
    }

    internal static Rsvp ParseRsvp(string rsvp) => rsvp switch
    {
        ManualRsvp.Accepted => Rsvp.Accepted,
        ManualRsvp.Declined => Rsvp.Declined,
        ManualRsvp.Unanswered => Rsvp.Unanswered,
        _ => Rsvp.Other,
    };

    private static string FormatRsvp(Rsvp rsvp) => rsvp switch
    {
        Rsvp.Accepted => ManualRsvp.Accepted,
        Rsvp.Declined => ManualRsvp.Declined,
        Rsvp.Unanswered => ManualRsvp.Unanswered,
        _ => ManualRsvp.Other,
    };

    private static ManualMemberDto ToDto(ManualMember m) => new(m.Id, m.GroupId, m.Name, m.BirthDate, [.. m.SubGroups.Select(l => l.SubGroupId).Order()]);

    private static ManualEventDto ToDto(ManualEvent e) => new(
        e.Id, e.GroupId, e.Name, e.Start, e.End, e.Cancelled, [.. e.Invitations.OrderBy(i => i.MemberId).Select(i => new ManualInvitationDto(i.MemberId, FormatRsvp(i.Rsvp)))],
        [.. e.Trainers.Select(t => t.MemberId).Order()]);

    private async Task<ManualGroup> FindGroupAsync(string groupId, CancellationToken cancellationToken) =>
        await db.ManualGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken) ?? throw new ManualDataNotFoundException("group", groupId);

    private async Task<ManualSubGroup> FindSubGroupAsync(string subGroupId, CancellationToken cancellationToken) =>
        await db.ManualSubGroups.FirstOrDefaultAsync(s => s.Id == subGroupId, cancellationToken) ?? throw new ManualDataNotFoundException("subgroup", subGroupId);

    /// <summary>The unique indexes decide what a duplicate is, so two requests racing for one name cannot both win; the check above gives the usual early answer.</summary>
    private async Task SaveNameAsync(string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AlreadyExistsException(conflictMessage);
        }
    }

    // Compared in code: SQLite's lower() does not know umlauts.
    private async Task<bool> GroupNameTakenAsync(string name, string? exceptId, CancellationToken cancellationToken) =>
        (await db.ManualGroups.AsNoTracking().Where(g => g.Id != exceptId).Select(g => g.Name).ToListAsync(cancellationToken))
            .Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase));

    private async Task<bool> SubGroupNameTakenAsync(string groupId, string name, string? exceptId, CancellationToken cancellationToken) =>
        (await db.ManualSubGroups.AsNoTracking().Where(s => s.GroupId == groupId && s.Id != exceptId).Select(s => s.Name).ToListAsync(cancellationToken))
            .Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase));
}
