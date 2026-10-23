using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class BeltNotFoundException(int beltId) : Exception($"No belt with id {beltId}.");

public sealed class MemberNotFoundException(int memberId) : Exception($"No member with id {memberId}.");

/// <summary>A belt cannot be deleted because members hold it or other belts require it.</summary>
public sealed class BeltInUseException(string message) : Exception(message);

/// <summary>The request is well-formed but asks for something that makes no sense, e.g. a belt that does not exist.</summary>
public sealed class InvalidInputException(string message) : Exception(message);

/// <summary>
/// Reads and writes belts and what they require. Input is expected to be validated by the endpoints (<see cref="BeltRules"/>); what
/// can only be judged against the other belts (which belts exist, that no belt ends up requiring itself through others) is
/// checked here.
/// </summary>
public sealed class BeltService(PlannerDbContext db)
{
    /// <summary>In the order of the progression: a belt comes after the belts it requires, then by name.</summary>
    public async Task<IReadOnlyList<BeltDto>> ListAsync(CancellationToken cancellationToken)
    {
        var belts = await LoadBeltsAsync(cancellationToken);
        var holders = await CountHoldersAsync(cancellationToken);
        return InProgressionOrder(belts).Select(b => ToDto(b, holders)).ToList();
    }

    public async Task<BeltDto> GetAsync(int beltId, CancellationToken cancellationToken)
    {
        var belt = await FindAsync(beltId, cancellationToken);
        return ToDto(belt, await CountHoldersAsync(cancellationToken));
    }

    /// <exception cref="AlreadyExistsException">Another belt has this name.</exception>
    /// <exception cref="InvalidInputException">A required belt does not exist.</exception>
    public async Task<BeltDto> CreateAsync(BeltInputDto input, CancellationToken cancellationToken)
    {
        var belt = new Belt();
        Assign(belt, input, await LoadBeltsAsync(cancellationToken));
        db.Belts.Add(belt);

        await SaveAsync(NameTaken(belt.Name), cancellationToken);
        return ToDto(belt, await CountHoldersAsync(cancellationToken));
    }

    /// <exception cref="BeltNotFoundException"/>
    /// <exception cref="AlreadyExistsException">Another belt has this name.</exception>
    /// <exception cref="InvalidInputException">A required belt does not exist, or the belt would end up requiring itself.</exception>
    public async Task<BeltDto> UpdateAsync(int beltId, BeltInputDto input, CancellationToken cancellationToken)
    {
        var belts = await LoadBeltsAsync(cancellationToken);
        var belt = belts.FirstOrDefault(b => b.Id == beltId) ?? throw new BeltNotFoundException(beltId);
        Assign(belt, input, belts);

        await SaveAsync(NameTaken(belt.Name), cancellationToken);
        return ToDto(belt, await CountHoldersAsync(cancellationToken));
    }

    /// <summary>Deletes a belt and its own requirements. Members who hold it, and belts that require it, keep it from being deleted.</summary>
    /// <exception cref="BeltInUseException"/>
    public async Task DeleteAsync(int beltId, CancellationToken cancellationToken)
    {
        var belts = await LoadBeltsAsync(cancellationToken);
        var belt = belts.FirstOrDefault(b => b.Id == beltId) ?? throw new BeltNotFoundException(beltId);

        // Members that are hidden (gone from the data source) hold their belt, too: the foreign key stops the delete.
        if (await db.Members.IgnoreQueryFilters().CountAsync(m => m.BeltId == beltId, cancellationToken) is var holders and > 0)
            throw new BeltInUseException($"The belt '{belt.Name}' is held by {holders} member(s); give them another belt first.");

        var requiredBy = belts.Where(b => b.Prerequisites.Any(p => p.RequiredBeltId == beltId)).Select(b => $"'{b.Name}'").ToList();
        if (requiredBy.Count > 0)
            throw new BeltInUseException($"The belt '{belt.Name}' is required for {string.Join(", ", requiredBy)}; change that first.");

        if (await db.ExamResults.IgnoreQueryFilters().CountAsync(r => r.BeltId == beltId, cancellationToken) is var awarded and > 0)
            throw new BeltInUseException($"The belt '{belt.Name}' was handed out {awarded} time(s) at exams; it is part of their results.");

        db.Belts.Remove(belt);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsForeignKeyViolation())
        {
            // Somebody took the belt or made it a requirement between the checks above and the delete.
            throw new BeltInUseException($"The belt '{belt.Name}' is in use.");
        }
    }

    private static string NameTaken(string name) => $"There already is a belt named '{name}'.";

    private Task<List<Belt>> LoadBeltsAsync(CancellationToken cancellationToken) =>
        db.Belts.Include(b => b.Prerequisites).ToListAsync(cancellationToken);

    private async Task<Belt> FindAsync(int beltId, CancellationToken cancellationToken) =>
        await db.Belts.Include(b => b.Prerequisites).FirstOrDefaultAsync(b => b.Id == beltId, cancellationToken)
        ?? throw new BeltNotFoundException(beltId);

    private async Task<Dictionary<int, int>> CountHoldersAsync(CancellationToken cancellationToken) =>
        await db.Members.Where(m => m.BeltId != null).GroupBy(m => m.BeltId!.Value)
            .Select(g => new { BeltId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BeltId, g => g.Count, cancellationToken);

    /// <summary>The unique index decides what a duplicate is, so two requests racing for one name cannot both win.</summary>
    private async Task SaveAsync(string conflictMessage, CancellationToken cancellationToken)
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

    private static void Assign(Belt belt, BeltInputDto input, IReadOnlyList<Belt> all)
    {
        var required = input.Prerequisites ?? [];

        foreach (var p in required)
        {
            if (all.All(b => b.Id != p.BeltId)) throw new InvalidInputException($"There is no belt with id {p.BeltId} to require.");
        }

        // A belt cannot require itself, nor a belt that (through others) requires it: nobody could ever take either.
        if (belt.Id != 0 && required.Any(p => p.BeltId == belt.Id || Requires(all, p.BeltId, belt.Id)))
            throw new InvalidInputException($"The belt '{input.Name.Trim()}' cannot require a belt that requires it.");

        belt.Name = input.Name.Trim();
        belt.PrimaryColor = input.PrimaryColor.Trim().ToUpperInvariant();
        belt.SecondaryColor = string.IsNullOrWhiteSpace(input.SecondaryColor) ? null : input.SecondaryColor.Trim().ToUpperInvariant();

        // Only what changed is written: removing and re-adding the same pair in one save would be a key conflict.
        foreach (var gone in belt.Prerequisites.Where(p => required.All(r => r.BeltId != p.RequiredBeltId)).ToList())
            belt.Prerequisites.Remove(gone);
        foreach (var r in required)
        {
            if (belt.Prerequisites.FirstOrDefault(p => p.RequiredBeltId == r.BeltId) is { } kept)
                (kept.MinAgeYears, kept.MinWaitMonths) = (r.MinAgeYears, r.MinWaitMonths);
            else
                belt.Prerequisites.Add(new BeltPrerequisite { RequiredBeltId = r.BeltId, MinAgeYears = r.MinAgeYears, MinWaitMonths = r.MinWaitMonths });
        }
    }

    /// <summary>Whether <paramref name="beltId"/> requires <paramref name="otherId"/>, directly or through the belts it requires.</summary>
    private static bool Requires(IReadOnlyList<Belt> belts, int beltId, int otherId)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>([beltId]);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;

            foreach (var p in belts.First(b => b.Id == id).Prerequisites)
            {
                if (p.RequiredBeltId == otherId) return true;
                pending.Push(p.RequiredBeltId);
            }
        }

        return false;
    }

    /// <summary>
    /// Orders belts by how far they are into the progression (the longest chain of belts required before them), so that the first
    /// belt comes first and a belt never comes before one it requires; belts at the same step by name.
    /// </summary>
    internal static List<Belt> InProgressionOrder(List<Belt> belts)
    {
        var step = belts.ToDictionary(b => b.Id, _ => 0);

        // Cycles cannot be saved, but a database that was edited by hand must not hang the list: at most one pass per belt.
        for (var pass = 0; pass < belts.Count; pass++)
        {
            var changed = false;
            foreach (var belt in belts)
            {
                var next = belt.Prerequisites.Count == 0 ? 0 : 1 + belt.Prerequisites.Max(p => step[p.RequiredBeltId]);
                if (next == step[belt.Id]) continue;

                step[belt.Id] = next;
                changed = true;
            }

            if (!changed) break;
        }

        return belts.OrderBy(b => step[b.Id]).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.Id).ToList();
    }

    private static BeltDto ToDto(Belt belt, IReadOnlyDictionary<int, int> holders) => new(
        belt.Id, belt.Name, belt.PrimaryColor, belt.SecondaryColor,
        belt.Prerequisites.OrderBy(p => p.RequiredBeltId).Select(p => new BeltPrerequisiteDto(p.RequiredBeltId, p.MinAgeYears, p.MinWaitMonths)).ToList(),
        holders.GetValueOrDefault(belt.Id));
}
