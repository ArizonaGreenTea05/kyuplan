using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

public sealed class ExamNotFoundException(int examId) : Exception($"No exam with id {examId}.");

public sealed class SavedFilterNotFoundException(int examId, int filterId)
    : Exception($"Exam {examId} has no saved filter with id {filterId}.");

/// <summary>Something that must be unique is already there: a filter name within an exam, or an event that already is an exam.</summary>
public sealed class AlreadyExistsException(string message) : Exception(message);

/// <summary>
/// Reads and writes exams and their saved filters. Input is expected to be validated by the endpoints. An exam is a
/// Spond event: creating one looks the event up on Spond and takes its name and day, and reading exams keeps those
/// current. The members invited to it are recorded too (<see cref="SpondSync"/>).
/// </summary>
public sealed class ExamService(
    PlannerDbContext db, StatisticsService statistics, SpondSync sync, TimeProvider time, ILogger<ExamService> logger)
{
    /// <summary>Newest exam first: the one coming up is the one that matters.</summary>
    public async Task<IReadOnlyList<ExamDto>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exams = await db.Exams.Include(e => e.Filters).ToListAsync(cancellationToken);
            await FollowEventsAsync(exams, cancellationToken);
            var counts = await CountsAsync(exams, cancellationToken);

            return exams
                .OrderByDescending(e => e.Date).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Id)
                .Select(e => ToDto(e, counts))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<ExamDto> GetAsync(int examId, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        await FollowEventsAsync([exam], cancellationToken);
        return ToDto(exam, await CountsAsync([exam], cancellationToken));
    }

    /// <summary>Makes a Spond event an exam.</summary>
    /// <exception cref="GroupNotFoundException">The group is not one of the account's.</exception>
    /// <exception cref="EventNotFoundException">The event is not in the search window of the group.</exception>
    /// <exception cref="AlreadyExistsException">The event already is an exam.</exception>
    public async Task<ExamDto> CreateAsync(ExamInputDto input, CancellationToken cancellationToken)
    {
        var exam = new Exam { CreatedAt = time.GetUtcNow() };
        await PointAtEventAsync(exam, input, cancellationToken);
        db.Exams.Add(exam);
        await SaveAsync(EventTaken(exam), cancellationToken);
        await SyncInvitationsAsync(exam, cancellationToken);
        return ToDto(exam, await CountsAsync([exam], cancellationToken));
    }

    /// <summary>Points the exam at another event, e.g. because the exam was moved to a new event on Spond; its filters stay, and so do its subgroups unless new ones are given.</summary>
    public async Task<ExamDto> UpdateAsync(int examId, ExamInputDto input, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        await PointAtEventAsync(exam, input, cancellationToken);
        await SaveAsync(EventTaken(exam), cancellationToken);
        await SyncInvitationsAsync(exam, cancellationToken);
        return ToDto(exam, await CountsAsync([exam], cancellationToken));
    }

    /// <summary>Deletes the exam and, with it, its filters, the members saved for it and the belts recorded at it. The Spond event is not touched.</summary>
    public async Task DeleteAsync(int examId, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        db.Exams.Remove(exam);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SavedFilterDto> GetFilterAsync(int examId, int filterId, CancellationToken cancellationToken) =>
        ToDto(await FindFilterAsync(examId, filterId, tracking: false, cancellationToken));

    public async Task<SavedFilterDto> CreateFilterAsync(int examId, SavedFilterInputDto input, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        var now = time.GetUtcNow();

        var filter = new SavedFilter { CreatedAt = now };
        Assign(filter, input, now);
        exam.Filters.Add(filter);

        await SaveAsync(NameTaken(filter.Name), cancellationToken);
        return ToDto(filter);
    }

    public async Task<SavedFilterDto> UpdateFilterAsync(int examId, int filterId, SavedFilterInputDto input, CancellationToken cancellationToken)
    {
        var filter = await FindFilterAsync(examId, filterId, tracking: true, cancellationToken);
        Assign(filter, input, time.GetUtcNow());

        await SaveAsync(NameTaken(filter.Name), cancellationToken);
        return ToDto(filter);
    }

    public async Task DeleteFilterAsync(int examId, int filterId, CancellationToken cancellationToken)
    {
        var filter = await FindFilterAsync(examId, filterId, tracking: true, cancellationToken);
        db.SavedFilters.Remove(filter);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NameTaken(string name) => $"This exam already has a filter named '{name}'.";

    private static string EventTaken(Exam exam) => $"The event '{exam.Name}' on {exam.Date:yyyy-MM-dd} is already an exam.";

    private async Task<Exam> FindExamAsync(int examId, CancellationToken cancellationToken) =>
        await db.Exams.Include(e => e.Filters).FirstOrDefaultAsync(e => e.Id == examId, cancellationToken)
        ?? throw new ExamNotFoundException(examId);

    private async Task<SavedFilter> FindFilterAsync(int examId, int filterId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.SavedFilters : db.SavedFilters.AsNoTracking();
        return await query.FirstOrDefaultAsync(f => f.ExamId == examId && f.Id == filterId, cancellationToken)
               ?? throw new SavedFilterNotFoundException(examId, filterId);
    }

    /// <summary>Looks the event up on Spond (it must be in the search window) and makes the exam that event.</summary>
    private async Task PointAtEventAsync(Exam exam, ExamInputDto input, CancellationToken cancellationToken)
    {
        var current = await statistics.FindEventAsync(input.GroupId, input.EventId, cancellationToken);
        exam.GroupId = input.GroupId;
        exam.EventId = current.Id;
        if (input.SubGroupIds is { } subGroups) exam.SubGroupIds = Ids(subGroups);
        Follow(exam, current);
    }

    private static List<string> Ids(IEnumerable<string> ids) => ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

    /// <summary>
    /// Brings the name and day of the exams in line with their events on Spond, so that an event that was renamed or
    /// moved is still the same exam. An exam whose event is outside the search window, or whose group cannot be read
    /// (Spond is down), keeps what was stored: listing exams must not depend on Spond.
    /// </summary>
    private async Task FollowEventsAsync(IReadOnlyList<Exam> exams, CancellationToken cancellationToken)
    {
        foreach (var group in exams.GroupBy(e => e.GroupId))
        {
            IReadOnlyList<EventInfo> events;
            try
            {
                events = await statistics.GetWindowEventsAsync(group.Key, cancellationToken);
            }
            catch (Exception ex) when (ex is DataSourceUnavailableException or GroupNotFoundException)
            {
                logger.LogWarning(ex, "Could not read the events of group {GroupId} from Spond; its exams keep the name and day stored.", group.Key);
                continue;
            }

            foreach (var exam in group)
            {
                if (events.FirstOrDefault(e => e.Id == exam.EventId) is { } current) Follow(exam, current);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records who is invited to the exam's event. The exam is saved by now, and the same sync runs regularly, so a failure here
    /// (Spond fails between the two reads) is logged and not allowed to undo the exam.
    /// </summary>
    private async Task SyncInvitationsAsync(Exam exam, CancellationToken cancellationToken)
    {
        try
        {
            await sync.SyncExamAsync(exam, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the invitations of exam {ExamId}; the next sync with Spond will.", exam.Id);
        }
    }

    internal static void Follow(Exam exam, EventInfo current)
    {
        exam.Name = current.Name;
        exam.Date = DateOnly.FromDateTime(current.Start.Date); // event times are already in the club time zone
    }

    /// <summary>The unique indexes decide what a duplicate is, so two requests racing for one name or event cannot both win.</summary>
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

    private static void Assign(SavedFilter filter, SavedFilterInputDto input, DateTimeOffset now)
    {
        var s = input.Settings;
        var byEvents = s.SpanByEvents;

        filter.Name = input.Name.Trim();
        filter.GroupId = s.GroupId;
        filter.From = s.From;
        filter.To = s.To;
        filter.FromEventId = byEvents ? s.FromEventId : null;
        filter.ToEventId = byEvents ? s.ToEventId : null;
        filter.SubGroupIds = Ids(s.SubGroupIds ?? []);
        filter.MinAttendancePercent = s.MinAttendancePercent;
        filter.MinInvitedEvents = s.MinInvitedEvents;
        filter.EventNameContains = Terms(s.EventNameContains);
        filter.EventNameExcludes = Terms(s.EventNameExcludes);
        filter.MinAverageStars = s.MinAverageStars;
        filter.UpdatedAt = now;

        static string? Terms(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>How many members an exam has invited, attended, saved and awarded a belt.</summary>
    private sealed record ExamCounts(int Invited, int Attended, int PreSaved, int BeltsAwarded);

    /// <summary>Counted by the database, so that an exam list does not load every invitation of every exam.</summary>
    private async Task<IReadOnlyDictionary<int, ExamCounts>> CountsAsync(IReadOnlyCollection<Exam> exams, CancellationToken cancellationToken)
    {
        var ids = exams.Select(e => e.Id).ToList();

        var invitations = await db.ExamParticipants.AsNoTracking()
            .Where(p => ids.Contains(p.ExamId))
            .GroupBy(p => p.ExamId)
            .Select(g => new { ExamId = g.Key, Invited = g.Count(), Attended = g.Count(p => p.Rsvp == Rsvp.Accepted) })
            .ToDictionaryAsync(g => g.ExamId, cancellationToken);
        var preSaved = await db.ExamCandidates.AsNoTracking()
            .Where(c => ids.Contains(c.ExamId))
            .GroupBy(c => c.ExamId)
            .Select(g => new { ExamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ExamId, g => g.Count, cancellationToken);
        var awarded = await db.ExamResults.AsNoTracking()
            .Where(r => ids.Contains(r.ExamId))
            .GroupBy(r => r.ExamId)
            .Select(g => new { ExamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ExamId, g => g.Count, cancellationToken);

        return ids.ToDictionary(id => id, id => new ExamCounts(
            invitations.TryGetValue(id, out var i) ? i.Invited : 0,
            invitations.TryGetValue(id, out i) ? i.Attended : 0,
            preSaved.GetValueOrDefault(id),
            awarded.GetValueOrDefault(id)));
    }

    private static ExamDto ToDto(Exam exam, IReadOnlyDictionary<int, ExamCounts> counts)
    {
        var count = counts.GetValueOrDefault(exam.Id) ?? new ExamCounts(0, 0, 0, 0);
        return new ExamDto(
            exam.Id, exam.GroupId, exam.EventId, exam.Name, exam.Date,
            exam.Filters.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToDto).ToList(),
            count.Invited, count.Attended, count.PreSaved, count.BeltsAwarded, exam.SubGroupIds.ToList());
    }

    private static SavedFilterDto ToDto(SavedFilter f) => new(
        f.Id, f.ExamId, f.Name,
        new FilterSettingsDto(
            f.GroupId, f.From, f.To, f.FromEventId, f.ToEventId, f.SubGroupIds.ToList(),
            f.MinAttendancePercent, f.MinInvitedEvents, f.EventNameContains, f.EventNameExcludes, f.MinAverageStars),
        f.UpdatedAt);
}
