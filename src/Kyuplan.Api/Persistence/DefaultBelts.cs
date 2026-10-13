using Microsoft.EntityFrameworkCore;

namespace Kyuplan.Api.Persistence;

/// <summary>A belt of <see cref="DefaultBelts"/>; the belts it requires are named, so the list reads without ids.</summary>
public sealed record DefaultBelt(string Name, string PrimaryColor, string? SecondaryColor, IReadOnlyList<DefaultBeltPrerequisite> Prerequisites);

/// <summary>Coming from the belt <paramref name="RequiredBelt"/> (by name), a member must be <paramref name="MinAgeYears"/> old and have held it for <paramref name="MinWaitMonths"/>.</summary>
public sealed record DefaultBeltPrerequisite(string RequiredBelt, int MinAgeYears, int MinWaitMonths);

/// <summary>
/// The belts a new planner database starts with: the grades of the club (9th Kyu to 3rd Dan), their colours, and what it takes to
/// advance from one to the next. They are an ordinary start, not a rule: the Belts page edits them like belts of one's own.
/// </summary>
public static class DefaultBelts
{
    /// <summary>In the order of the progression: a belt comes after the belts it requires.</summary>
    public static IReadOnlyList<DefaultBelt> All { get; } =
    [
        new("9th Kyu (white)", "#FFFFFF", null, []),
        new("9th Kyu (white-yellow)", "#FFFFFF", "#FFD800", [new("9th Kyu (white)", 7, 5)]),
        new("8th Kyu (yellow)", "#FFD800", null, [new("9th Kyu (white)", 14, 5), new("9th Kyu (white-yellow)", 0, 5)]),
        new("8th Kyu (yellow-orange)", "#FFD800", "#FF8C00", [new("8th Kyu (yellow)", 7, 5)]),
        new("7th Kyu (orange)", "#FF8C00", null, [new("8th Kyu (yellow)", 14, 5), new("8th Kyu (yellow-orange)", 0, 5)]),
        new("7th Kyu (orange-green)", "#FF8C00", "#00A650", [new("7th Kyu (orange)", 0, 5)]),
        new("6th Kyu (green)", "#00A650", null, [new("7th Kyu (orange)", 14, 5), new("7th Kyu (orange-green)", 9, 5)]),
        new("6th Kyu (green-blue)", "#00A650", "#1E64C8", [new("6th Kyu (green)", 10, 5)]),
        new("5th Kyu (blue)", "#1E64C8", null, [new("6th Kyu (green)", 14, 5), new("6th Kyu (green-blue)", 11, 5)]),
        new("4th Kyu (purple)", "#7D3C98", null, [new("5th Kyu (blue)", 12, 5)]),
        new("4th Kyu (purple-brown)", "#7D3C98", "#7B4A1E", [new("4th Kyu (purple)", 13, 5)]),
        new("3rd Kyu (1st brown)", "#7B4A1E", null, [new("4th Kyu (purple)", 14, 5), new("4th Kyu (purple-brown)", 14, 5)]),
        new("2nd Kyu (2nd brown)", "#7B4A1E", null, [new("3rd Kyu (1st brown)", 15, 5)]),
        new("1st Kyu (3rd brown)", "#7B4A1E", null, [new("2nd Kyu (2nd brown)", 16, 5)]),
        new("1st Dan", "#000000", null, [new("1st Kyu (3rd brown)", 18, 12)]),
        new("2nd Dan", "#000000", null, [new("1st Dan", 18, 24)]),
        new("3rd Dan", "#000000", null, [new("2nd Dan", 18, 36)]),
        new("4th Dan", "#000000", null, [new("3rd Dan", 18, 48)])
    ];

    /// <summary>Adds the belts to a database that has none. The caller decides when that is (see <c>Program</c>).</summary>
    public static async Task SeedAsync(PlannerDbContext db, CancellationToken cancellationToken)
    {
        var belts = All.ToDictionary(b => b.Name, b => new Belt { Name = b.Name, PrimaryColor = b.PrimaryColor, SecondaryColor = b.SecondaryColor });
        foreach (var d in All)
        {
            belts[d.Name].Prerequisites =
            [
                .. d.Prerequisites.Select(p => new BeltPrerequisite { RequiredBelt = belts[p.RequiredBelt], MinAgeYears = p.MinAgeYears, MinWaitMonths = p.MinWaitMonths }),
            ];
        }

        db.Belts.AddRange(belts.Values);
        await db.SaveChangesAsync(cancellationToken);
    }
}
