using Microsoft.EntityFrameworkCore;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>Writes and reads the audit log. Entries are only ever added.</summary>
public sealed class AuditService(PlannerDbContext db, TimeProvider time)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public async Task RecordAsync(int? userId, string userName, string area, string action, string? target, string? details, CancellationToken cancellationToken)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            At = time.GetUtcNow(),
            UserId = userId,
            UserName = Cut(userName, AccountRules.MaxUserNameLength),
            Area = area,
            Action = action,
            Target = target is null ? null : Cut(target, PlannerDbContext.NameLength),
            Details = details,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The newest entries first, optionally only those of one area and/or one user, and only those older than <paramref name="before"/>.</summary>
    public async Task<AuditPageDto> ListAsync(string? area, int? userId, long? before, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.AuditEntries.AsNoTracking().AsQueryable();
        if (area is not null) query = query.Where(e => e.Area == area);
        if (userId is { } user) query = query.Where(e => e.UserId == user);
        if (before is { } id) query = query.Where(e => e.Id < id);

        // One more than asked tells whether there is another page.
        var rows = await query.OrderByDescending(e => e.Id).Take(size + 1).ToListAsync(cancellationToken);
        var page = rows.Take(size).Select(e => new AuditEntryDto(e.Id, e.At, e.UserId, e.UserName, e.Area, e.Action, e.Target, e.Details)).ToList();
        return new AuditPageDto(page, rows.Count > size ? page[^1].Id : null);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}
