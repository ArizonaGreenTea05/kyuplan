using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Statistics;

namespace Kyuplan.Api.Persistence;

/// <summary>Reads the evaluations of a group out of the database for the statistics and the exam suggestions.</summary>
public static class RatingBooks
{
    /// <summary>Per member and event the average of the users' evaluations (an event counts once, however many users evaluated).</summary>
    /// <param name="defaultStars">What an event counts for a member who attended it and whom nobody evaluated.</param>
    public static async Task<RatingBook> LoadAsync(PlannerDbContext db, string groupId, double defaultStars, CancellationToken cancellationToken)
    {
        var rows = await db.MemberRatings.AsNoTracking()
            .Where(r => r.Member.GroupId == groupId)
            .Select(r => new { r.Member.SpondId, r.EventId, r.Stars })
            .ToListAsync(cancellationToken);

        return new RatingBook(
            rows.GroupBy(r => (r.SpondId, r.EventId)).ToDictionary(g => g.Key, g => g.Average(r => (double)r.Stars)),
            defaultStars);
    }
}
