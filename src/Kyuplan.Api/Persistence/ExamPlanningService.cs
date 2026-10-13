using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// What is done around an exam besides keeping its event (<see cref="ExamService"/>): who is fit for it, who is saved for it, and who
/// received which belt at it. Input is expected to be validated by the endpoints.
/// </summary>
public sealed class ExamPlanningService(
    PlannerDbContext db, StatisticsService statistics, ClubClock clock, TimeProvider time, IOptions<StatisticsOptions> options)
{
    /// <summary>Longest time span of the default filter: the previous exam may be long ago, and Spond is asked for every month of the span.</summary>
    private const int DefaultSpanMonths = 12;

    // --- Who is in an exam ----------------------------------------------------------------------------------

    /// <summary>Everybody the exam knows: those invited on Spond, those saved for it, those who received a belt at it. By name.</summary>
    /// <exception cref="ExamNotFoundException"/>
    public async Task<IReadOnlyList<ExamMemberDto>> GetMembersAsync(int examId, CancellationToken cancellationToken)
    {
        await FindExamAsync(examId, cancellationToken);
        return await ListMembersAsync(examId, cancellationToken);
    }

    private async Task<IReadOnlyList<ExamMemberDto>> ListMembersAsync(int examId, CancellationToken cancellationToken)
    {
        var invitations = await db.ExamParticipants.AsNoTracking().Where(p => p.ExamId == examId)
            .ToDictionaryAsync(p => p.MemberId, p => p.Rsvp, cancellationToken);
        var preSaved = await db.ExamCandidates.AsNoTracking().Where(c => c.ExamId == examId)
            .Select(c => c.MemberId).ToListAsync(cancellationToken);
        var received = await db.ExamResults.AsNoTracking().Where(r => r.ExamId == examId)
            .ToDictionaryAsync(r => r.MemberId, r => r.BeltId, cancellationToken);

        var ids = invitations.Keys.Concat(preSaved).Concat(received.Keys).ToHashSet();
        var members = await db.Members.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(cancellationToken);

        return members
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id)
            .Select(m => new ExamMemberDto(
                m.Id, m.Name, m.BeltId,
                invitations.TryGetValue(m.Id, out var rsvp) ? Mark(rsvp) : null,
                preSaved.Contains(m.Id),
                received.TryGetValue(m.Id, out var belt) ? belt : null))
            .ToList();
    }

    private static AttendanceMark Mark(Rsvp rsvp) => rsvp switch
    {
        Rsvp.Accepted => AttendanceMark.Attended,
        Rsvp.Declined => AttendanceMark.Declined,
        Rsvp.Unanswered => AttendanceMark.Unanswered,
        _ => AttendanceMark.Other,
    };

    // --- Saving members for an exam ---------------------------------------------------------------------------

    /// <summary>
    /// Saves the members in <paramref name="add"/> for the exam and takes those in <paramref name="remove"/> off its list; whoever is not
    /// named stays as they are. Nobody is invited on Spond by it. A member named in both lists is saved. Nothing is changed when
    /// something is refused.
    /// </summary>
    /// <exception cref="ExamNotFoundException"/>
    /// <exception cref="InvalidInputException">A member to add does not exist or belongs to another group than the exam.</exception>
    public async Task<IReadOnlyList<ExamMemberDto>> SetCandidatesAsync(
        int examId, IReadOnlyList<int> add, IReadOnlyList<int> remove, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        var toAdd = add.ToHashSet();
        var toRemove = remove.Where(id => !toAdd.Contains(id)).ToHashSet();

        var found = await db.Members.AsNoTracking().Where(m => toAdd.Contains(m.Id)).Select(m => new { m.Id, m.GroupId }).ToListAsync(cancellationToken);
        var unknown = toAdd.Except(found.Select(m => m.Id)).ToList();
        if (unknown.Count > 0)
            throw new InvalidInputException($"There is no member with id {unknown.Min()}.");
        if (found.FirstOrDefault(m => m.GroupId != exam.GroupId) is { } stranger)
            throw new InvalidInputException($"The member {stranger.Id} does not belong to the group of this exam.");

        var saved = await db.ExamCandidates.Where(c => c.ExamId == examId).ToListAsync(cancellationToken);
        db.ExamCandidates.RemoveRange(saved.Where(c => toRemove.Contains(c.MemberId)));

        var now = time.GetUtcNow();
        foreach (var id in toAdd.Where(id => saved.All(c => c.MemberId != id)))
            db.ExamCandidates.Add(new ExamCandidate { ExamId = examId, MemberId = id, CreatedAt = now });

        await db.SaveChangesAsync(cancellationToken);
        return await ListMembersAsync(examId, cancellationToken);
    }

    // --- Belts handed out at an exam ----------------------------------------------------------------------

    /// <summary>
    /// Records the belt a member received at the exam, or takes the result back (<paramref name="beltId"/> <c>null</c>). A belt that is
    /// recorded is the belt the member holds from then on, and the day of the exam is the day they got it, <b>unless that would turn the
    /// clock back</b>: when the member got a belt after this exam (a later result, or a belt day after the exam day), the record is
    /// only history. Taking a result back leaves the belt the member holds as it is; that is corrected on the member.
    /// </summary>
    /// <exception cref="ExamNotFoundException"/>
    /// <exception cref="MemberNotFoundException"/>
    /// <exception cref="InvalidInputException">The exam has not taken place yet, the belt does not exist, or the member belongs to another group than the exam.</exception>
    public async Task<ExamMemberDto> SetResultAsync(int examId, int memberId, int? beltId, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);
        if (exam.Date > clock.Today)
            throw new InvalidInputException($"The exam is on {exam.Date:yyyy-MM-dd}; belts are recorded once it has taken place.");

        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken)
                     ?? throw new MemberNotFoundException(memberId);
        if (member.GroupId != exam.GroupId)
            throw new InvalidInputException($"The member {memberId} does not belong to the group of this exam.");
        if (beltId is { } id && !await db.Belts.AnyAsync(b => b.Id == id, cancellationToken))
            throw new InvalidInputException($"There is no belt with id {id}.");

        var result = await db.ExamResults.FirstOrDefaultAsync(r => r.ExamId == examId && r.MemberId == memberId, cancellationToken);
        if (beltId is null)
        {
            if (result is not null) db.ExamResults.Remove(result);
        }
        else
        {
            if (result is null)
                db.ExamResults.Add(new ExamResult { ExamId = examId, MemberId = memberId, BeltId = beltId.Value });
            else
                result.BeltId = beltId.Value;

            var gotOneLater = await db.ExamResults.AnyAsync(r => r.MemberId == memberId && r.ExamId != examId && r.Exam.Date > exam.Date, cancellationToken)
                              || member.BeltSince > exam.Date;
            if (!gotOneLater)
            {
                member.BeltId = beltId;
                member.BeltSince = exam.Date;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await ListMembersAsync(examId, cancellationToken)).FirstOrDefault(m => m.MemberId == memberId)
               ?? new ExamMemberDto(member.Id, member.Name, member.BeltId, null, false, null);
    }

    // --- Who is fit ------------------------------------------------------------------------------------------

    /// <summary>
    /// The members of the exam's group, judged for the exam: the belt each would take and whether they meet what it asks, on the day of
    /// the exam. The attendance is measured with a saved filter (any filter of the group, whichever exam it was saved for) or, without
    /// one, with the filter the statistics page opens with: from the previous exam to this one, at least the configured attendance.
    /// </summary>
    /// <exception cref="ExamNotFoundException"/>
    /// <exception cref="InvalidInputException">The filter does not exist or was saved for another group.</exception>
    /// <exception cref="Kyuplan.Api.Spond.SpondUnavailableException">Spond cannot be read.</exception>
    public async Task<ExamSuggestionsDto> SuggestAsync(int examId, int? filterId, CancellationToken cancellationToken)
    {
        var exam = await FindExamAsync(examId, cancellationToken);

        SavedFilter? saved = null;
        if (filterId is { } id)
        {
            saved = await db.SavedFilters.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
                    ?? throw new InvalidInputException($"There is no saved filter with id {id}.");
            if (saved.GroupId != exam.GroupId)
                throw new InvalidInputException($"The filter '{saved.Name}' was saved for another group than this exam's.");
        }

        var group = await statistics.GetGroupInfoAsync(exam.GroupId, cancellationToken);
        var filter = saved is null ? await DefaultFilterAsync(exam, cancellationToken) : await ResolveAsync(saved, cancellationToken);
        var attendance = await statistics.GetAttendanceAsync(filter, cancellationToken);

        var members = await db.Members.AsNoTracking().Where(m => m.GroupId == exam.GroupId).ToListAsync(cancellationToken);
        var bySpondId = members.ToDictionary(m => m.SpondId);

        var belts = BeltService.InProgressionOrder(await db.Belts.AsNoTracking().Include(b => b.Prerequisites).ToListAsync(cancellationToken));

        // The people to judge: the group's members as Spond lists them (it knows the subgroups), restricted like the statistics are,
        // and known to the database (a member who joined a minute ago is added by the next sync).
        var people = group.Members
            .DistinctBy(m => m.Id)
            .Where(m => filter.SubGroupIds is not { Count: > 0 } ids || ids.Any(m.SubGroupIds.Contains))
            .Where(m => bySpondId.ContainsKey(m.Id))
            .Select(m => (bySpondId[m.Id], m))
            .ToList();

        var invitations = await db.ExamParticipants.AsNoTracking().Where(p => p.ExamId == examId)
            .ToDictionaryAsync(p => p.MemberId, p => Mark(p.Rsvp), cancellationToken);
        var preSaved = (await db.ExamCandidates.AsNoTracking().Where(c => c.ExamId == examId).Select(c => c.MemberId).ToListAsync(cancellationToken)).ToHashSet();

        var rows = ExamSuggestionCalculator.Calculate(
            exam.Date, belts, people,
            attendance.Members.ToDictionary(m => m.MemberId),
            group.SubGroups.ToDictionary(s => s.Id, s => s.Name),
            await LastExamsAsync(exam, cancellationToken),
            invitations, preSaved);

        return new ExamSuggestionsDto(exam.Id, exam.Name, exam.Date, saved?.Id, saved?.Name, attendance.Filter, rows);
    }

    /// <summary>What the statistics page opens with for this exam: the previous exam of the group to this one, with the configured thresholds, for the subgroups chosen for the exam (none = everyone).</summary>
    private async Task<AttendanceFilter> DefaultFilterAsync(Exam exam, CancellationToken cancellationToken)
    {
        var previous = await db.Exams.AsNoTracking()
            .Where(e => e.GroupId == exam.GroupId && e.Date < exam.Date)
            .OrderByDescending(e => e.Date)
            .Select(e => (DateOnly?)e.Date)
            .FirstOrDefaultAsync(cancellationToken);

        // Without an earlier exam the span is the last six months, as on the statistics page.
        var earliest = exam.Date.AddMonths(-DefaultSpanMonths).AddDays(1);
        var from = previous is { } day ? (day > earliest ? day : earliest) : exam.Date.AddMonths(-6).AddDays(1);

        var settings = options.Value;
        IReadOnlyList<string>? subGroups = exam.SubGroupIds.Count > 0 ? exam.SubGroupIds : null;
        return new AttendanceFilter(exam.GroupId, from, exam.Date, subGroups, settings.DefaultMinAttendancePercent, settings.DefaultMinInvitedEvents);
    }

    /// <summary>The filter as it was saved: with its two events while both are still offered, otherwise with the two days they stood for.</summary>
    private async Task<AttendanceFilter> ResolveAsync(SavedFilter saved, CancellationToken cancellationToken)
    {
        var query = new AttendanceQuery(
            saved.GroupId, saved.From, saved.To, saved.FromEventId, saved.ToEventId,
            saved.SubGroupIds.ToArray(), saved.MinAttendancePercent, saved.MinInvitedEvents, saved.EventNameContains, saved.EventNameExcludes);

        try
        {
            return await statistics.ResolveAsync(query, cancellationToken);
        }
        catch (EventNotFoundException)
        {
            return await statistics.ResolveAsync(query with { FromEventId = null, ToEventId = null }, cancellationToken);
        }
    }

    /// <summary>
    /// The latest exam of the group before this one, and not in the future, that each member took part in: accepted the invitation, or
    /// received a belt. Nobody has taken part in an exam that has not taken place.
    /// </summary>
    private async Task<Dictionary<int, DateOnly>> LastExamsAsync(Exam exam, CancellationToken cancellationToken)
    {
        var dayBefore = exam.Date.AddDays(-1);
        var until = dayBefore < clock.Today ? dayBefore : clock.Today;

        var accepted = await db.ExamParticipants.AsNoTracking()
            .Where(p => p.Rsvp == Rsvp.Accepted && p.Exam.GroupId == exam.GroupId && p.Exam.Date <= until)
            .Select(p => new { p.MemberId, p.Exam.Date })
            .ToListAsync(cancellationToken);
        var received = await db.ExamResults.AsNoTracking()
            .Where(r => r.Exam.GroupId == exam.GroupId && r.Exam.Date <= until)
            .Select(r => new { r.MemberId, r.Exam.Date })
            .ToListAsync(cancellationToken);

        return accepted.Concat(received)
            .GroupBy(x => x.MemberId)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Date));
    }

    private async Task<Exam> FindExamAsync(int examId, CancellationToken cancellationToken) =>
        await db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == examId, cancellationToken)
        ?? throw new ExamNotFoundException(examId);
}
