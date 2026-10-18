using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// The star evaluations users give members at events: one per user, member and event (0–5; 0 is an evaluation, no row is none). Everybody
/// who may read evaluations sees everybody's; a user changes and takes back only their own. Users live in the accounts database: names
/// are the users' current display names, and the ones written down at the time for users who are gone. Evaluations can only be given
/// once the event has started. Input is expected to be validated by the endpoints (<see cref="RatingRules"/>).
/// </summary>
public sealed class RatingService(
    PlannerDbContext db, AccountsDbContext accounts, StatisticsService statistics, TimeProvider time, ClubClock clock,
    IOptions<StatisticsOptions> options, ILogger<RatingService> logger)
{
    /// <summary>The evaluations of an event by member id, each member's by user name.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<RatingDto>>> ListForEventAsync(string groupId, string eventId, CancellationToken cancellationToken)
    {
        var rows = await db.MemberRatings.AsNoTracking()
            .Where(r => r.EventId == eventId && r.Member.GroupId == groupId)
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(rows, cancellationToken);
        return rows
            .Select(r => ToDto(r, names))
            .GroupBy(r => r.MemberId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RatingDto>)[.. g.OrderBy(r => r.UserName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.UserId)]);
    }

    /// <summary>Gives or changes the user's evaluation of a member at an event.</summary>
    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="EventNotFoundException"/>
    /// <exception cref="MemberNotFoundException">The member is not known, or is not in the event's group.</exception>
    /// <exception cref="InvalidInputException">The event has not started yet.</exception>
    public async Task<RatingDto> SetAsync(
        string groupId, string eventId, int memberId, int stars, int userId, string userName, CancellationToken cancellationToken)
    {
        await CheckAsync(groupId, eventId, memberId, cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            var now = time.GetUtcNow();
            var rating = await db.MemberRatings.FirstOrDefaultAsync(r => r.MemberId == memberId && r.EventId == eventId && r.UserId == userId, cancellationToken);
            if (rating is null)
            {
                rating = new MemberRating { MemberId = memberId, EventId = eventId, UserId = userId, CreatedAt = now };
                db.MemberRatings.Add(rating);
            }

            rating.UserName = userName;
            rating.Stars = stars;
            rating.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToDto(rating, new Dictionary<int, string> { [userId] = await DisplayNameAsync(userId, userName, cancellationToken) });
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.IsUniqueViolation())
            {
                // The same user evaluated at the same moment from another window: update what they just wrote.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Takes the user's evaluation back (nothing happens when there is none).</summary>
    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="EventNotFoundException"/>
    /// <exception cref="MemberNotFoundException"/>
    /// <exception cref="InvalidInputException">The event has not started yet.</exception>
    public async Task DeleteAsync(string groupId, string eventId, int memberId, int userId, CancellationToken cancellationToken)
    {
        await CheckAsync(groupId, eventId, memberId, cancellationToken);

        await db.MemberRatings.Where(r => r.MemberId == memberId && r.EventId == eventId && r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>The evaluations of a member at every event, by user, the latest first.</summary>
    /// <exception cref="MemberNotFoundException"/>
    public async Task<IReadOnlyList<RatingDto>> ListForMemberAsync(int memberId, CancellationToken cancellationToken)
    {
        if (!await db.Members.AnyAsync(m => m.Id == memberId, cancellationToken)) throw new MemberNotFoundException(memberId);

        // SQLite cannot sort by a moment, so the rows are sorted here.
        var rows = (await db.MemberRatings.AsNoTracking().Where(r => r.MemberId == memberId).ToListAsync(cancellationToken))
            .OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id).ToList();
        var names = await NamesAsync(rows, cancellationToken);
        return [.. rows.Select(r => ToDto(r, names))];
    }

    /// <summary>The evaluations of a group as the statistics read them (see <see cref="RatingBooks.LoadAsync"/>).</summary>
    public Task<RatingBook> GetBookAsync(string groupId, CancellationToken cancellationToken) =>
        RatingBooks.LoadAsync(db, groupId, options.Value.DefaultEventStars, cancellationToken);

    private async Task CheckAsync(string groupId, string eventId, int memberId, CancellationToken cancellationToken)
    {
        var info = await statistics.FindEventAsync(groupId, eventId, cancellationToken);
        if (!await db.Members.AnyAsync(m => m.Id == memberId && m.GroupId == groupId, cancellationToken)) throw new MemberNotFoundException(memberId);
        if (info.Start > clock.Now) throw new InvalidInputException($"The event starts on {info.Start:yyyy-MM-dd HH:mm}; members are evaluated once it has started.");
    }

    private async Task<string> DisplayNameAsync(int userId, string fallback, CancellationToken cancellationToken) =>
        await accounts.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken) ?? fallback;

    private async Task<Dictionary<int, string>> NamesAsync(IReadOnlyList<MemberRating> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.UserId).ToHashSet();
        try
        {
            return await accounts.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error in {nameof(RatingService)}.{nameof(NamesAsync)}");
            return [];
        }
    }

    private RatingDto ToDto(MemberRating r, IReadOnlyDictionary<int, string> names) =>
        new(r.MemberId, r.EventId, r.UserId, names.GetValueOrDefault(r.UserId, r.UserName), r.Stars, clock.ToClub(r.UpdatedAt));
}
