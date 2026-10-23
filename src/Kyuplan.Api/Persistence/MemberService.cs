using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// Reads the members <see cref="SpondSync"/> keeps in the database and changes what Spond does not know about them: the belt they
/// hold, the day they got it, the day they started and (when Spond has none) the day they were born. Members are neither added nor
/// removed here, that is Spond's say.
/// </summary>
public sealed class MemberService(PlannerDbContext db)
{
    /// <summary>The members of a group (or of every group), by name.</summary>
    public async Task<IReadOnlyList<MemberDto>> ListAsync(string? groupId, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(groupId) ? db.Members : db.Members.Where(m => m.GroupId == groupId);
        var members = await query.AsNoTracking().ToListAsync(cancellationToken);
        var comments = await db.Comments.AsNoTracking()
            .GroupBy(c => c.MemberId).Select(g => new { MemberId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.MemberId, g => g.Count, cancellationToken);

        return members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id)
            .Select(m => ToDto(m, comments.GetValueOrDefault(m.Id))).ToList();
    }

    /// <summary>Replaces the belt, the day it was received, the start day and the birth date of a member. Without a belt there is no day it was received.</summary>
    /// <exception cref="MemberNotFoundException"/>
    /// <exception cref="InvalidInputException">The belt does not exist.</exception>
    public async Task<MemberDto> UpdateAsync(int memberId, MemberInputDto input, CancellationToken cancellationToken)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken)
                     ?? throw new MemberNotFoundException(memberId);

        if (input.BeltId is { } beltId && !await db.Belts.AnyAsync(b => b.Id == beltId, cancellationToken))
            throw new InvalidInputException($"There is no belt with id {beltId}.");

        member.BeltId = input.BeltId;
        member.BeltSince = input.BeltId is null ? null : input.BeltSince;
        member.StartedAt = input.StartedAt;
        member.BirthDate = input.BirthDate;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(member, await db.Comments.CountAsync(c => c.MemberId == memberId, cancellationToken));
    }

    /// <summary>Every member by name, with the user who is it, for choosing the members of a user. Unlike the list it carries nothing private about the member.</summary>
    public async Task<IReadOnlyList<MemberChoiceDto>> ChoicesAsync(CancellationToken cancellationToken) =>
        [.. (await db.Members.AsNoTracking().Select(m => new MemberChoiceDto(m.Id, m.GroupId, m.Name, m.UserId)).ToListAsync(cancellationToken))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id)];

    /// <summary>The members a user is, by name, as they are told to the user (usually one).</summary>
    public async Task<IReadOnlyList<MemberChoiceDto>> OfUserAsync(int userId, CancellationToken cancellationToken) =>
        [.. (await db.Members.AsNoTracking().Where(m => m.UserId == userId).Select(m => new MemberChoiceDto(m.Id, m.GroupId, m.Name, m.UserId)).ToListAsync(cancellationToken))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id)];

    /// <summary>The members a user is (usually one).</summary>
    public async Task<IReadOnlySet<int>> IdsOfUserAsync(int userId, CancellationToken cancellationToken) =>
        (await db.Members.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.Id).ToListAsync(cancellationToken)).ToHashSet();

    /// <summary>The members of every user who is a member, by user id.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> IdsByUserAsync(CancellationToken cancellationToken) =>
        (await db.Members.AsNoTracking().Where(m => m.UserId != null).Select(m => new { UserId = m.UserId!.Value, m.Id }).ToListAsync(cancellationToken))
        .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(m => m.Id).Order()]);

    /// <summary>Whether a member is this user.</summary>
    public Task<bool> IsUserAsync(int memberId, int userId, CancellationToken cancellationToken) =>
        db.Members.AsNoTracking().AnyAsync(m => m.Id == memberId && m.UserId == userId, cancellationToken);

    /// <summary>
    /// Makes the user exactly these members; the members they were are released. The user's existence is for the caller to check (users live in
    /// another database). <c>ponytail:</c> two administrators linking one member to two users at the same moment can both pass the check: the
    /// last write wins, which is the one that shows afterwards.
    /// </summary>
    /// <exception cref="MemberNotFoundException"/>
    /// <exception cref="AlreadyExistsException">A member is already another user's.</exception>
    public async Task SetUserMembersAsync(int userId, IReadOnlyList<int> memberIds, CancellationToken cancellationToken)
    {
        var wanted = memberIds.Distinct().ToList();
        var members = await db.Members.Where(m => wanted.Contains(m.Id) || m.UserId == userId).ToListAsync(cancellationToken);

        foreach (var id in wanted.Where(id => members.All(m => m.Id != id))) throw new MemberNotFoundException(id);
        if (members.FirstOrDefault(m => wanted.Contains(m.Id) && m.UserId is { } other && other != userId) is { } taken)
            throw new AlreadyExistsException($"The member '{taken.Name}' is already linked to another user.");

        foreach (var member in members.Where(m => !wanted.Contains(m.Id))) member.UserId = null;
        foreach (var member in members.Where(m => wanted.Contains(m.Id))) member.UserId = userId;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A user is gone: they are no longer any member.</summary>
    public Task ReleaseUserAsync(int userId, CancellationToken cancellationToken) =>
        db.Members.IgnoreQueryFilters().Where(m => m.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(m => m.UserId, (int?)null), cancellationToken);

    private static MemberDto ToDto(Member m, int commentCount) =>
        new(m.Id, m.GroupId, m.Name, m.BeltId, m.StartedAt, m.BirthDate, m.BeltSince, commentCount, m.Registered);
}
