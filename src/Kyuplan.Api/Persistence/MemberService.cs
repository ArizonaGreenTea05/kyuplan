using Microsoft.EntityFrameworkCore;
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

    private static MemberDto ToDto(Member m, int commentCount) =>
        new(m.Id, m.GroupId, m.Name, m.BeltId, m.StartedAt, m.BirthDate, m.BeltSince, commentCount);
}
