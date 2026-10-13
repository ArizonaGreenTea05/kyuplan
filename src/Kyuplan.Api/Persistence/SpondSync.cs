using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>Lets one <see cref="SpondSync"/> run at a time: two of them would both add the same member or invitation.</summary>
public sealed class SyncGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

    public void Release() => _gate.Release();
}

/// <summary>
/// Keeps what the database holds of Spond in line with Spond: the members of every group (added when Spond has them, removed when
/// it no longer does, the name kept current, the birth date taken over when it is not known yet), the day each member started, and
/// who was invited to each exam and what they answered. Whatever Spond does not know (a member's belt, the exams' candidates and
/// results) is left alone. It is run by <see cref="SpondSyncWorker"/> from time to time and
/// for an exam that was just created or changed; it needs a read of the groups and of the events in the search window, which come
/// from the data source's cache.
/// </summary>
public sealed class SpondSync(
    PlannerDbContext db,
    ISpondDataSource source,
    StatisticsService statistics,
    ClubClock clock,
    TimeProvider time,
    SyncGate gate,
    ILogger<SpondSync> logger)
{
    /// <summary>Syncs every group and every exam.</summary>
    /// <exception cref="SpondUnavailableException">Spond cannot be read at all; nothing was changed.</exception>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var groups = await source.GetGroupsAsync(cancellationToken);

            // A group the account does not see any more has no members in Spond either.
            var groupIds = groups.Select(g => g.Id).ToList();
            db.Members.RemoveRange(await db.Members.Where(m => !groupIds.Contains(m.GroupId)).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);

            foreach (var group in groups)
            {
                var exams = await db.Exams.Where(e => e.GroupId == group.Id).ToListAsync(cancellationToken);
                await SyncGroupAsync(group, exams, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Syncs the group of an exam (its members must be there to be invited) and then the exam itself. Used when an exam is created
    /// or points at another event, so that its invitations are there without waiting for the next <see cref="SyncAllAsync"/>.
    /// </summary>
    public async Task SyncExamAsync(Exam exam, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var groups = await source.GetGroupsAsync(cancellationToken);
            if (groups.FirstOrDefault(g => g.Id == exam.GroupId) is { } group)
                await SyncGroupAsync(group, [exam], cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SyncGroupAsync(GroupInfo group, IReadOnlyList<Exam> exams, CancellationToken cancellationToken)
    {
        var members = await SyncMembersAsync(group, cancellationToken);

        IReadOnlyList<EventInfo> events;
        try
        {
            events = await statistics.GetWindowEventsAsync(group.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is SpondUnavailableException or GroupNotFoundException)
        {
            logger.LogWarning(ex, "Could not read the events of group {GroupId} from Spond; the start dates of its members and its exams stay as they are.", group.Id);
            return;
        }

        FillStartDates(members.Values, events);

        foreach (var exam in exams)
        {
            var participants = db.Entry(exam).Collection(e => e.Participants);
            if (!participants.IsLoaded) await participants.LoadAsync(cancellationToken);
            SyncParticipants(exam, events, members);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Adds the members of the group that are new, renames the ones that were renamed and removes the ones that are gone. Saves.</summary>
    /// <returns>The members of the group as they are now, by Spond id.</returns>
    private async Task<Dictionary<string, Member>> SyncMembersAsync(GroupInfo group, CancellationToken cancellationToken)
    {
        var stored = await db.Members.Where(m => m.GroupId == group.Id).ToDictionaryAsync(m => m.SpondId, cancellationToken);
        var members = new Dictionary<string, Member>();
        var now = time.GetUtcNow();
        var added = 0;

        var today = clock.Today;

        foreach (var info in group.Members.DistinctBy(m => m.Id))
        {
            if (stored.Remove(info.Id, out var member))
            {
                member.Name = info.Name;
            }
            else
            {
                member = new Member { GroupId = group.Id, SpondId = info.Id, Name = info.Name, CreatedAt = now };
                db.Members.Add(member);
                added++;
            }

            // Spond's birth date fills a gap and nothing else: a day that is there, set by hand or taken earlier, is kept. A date that makes no sense is not taken.
            if (member.BirthDate is null && info.BirthDate is { } birth && MemberRules.CheckBirthDate(birth, today) is null)
                member.BirthDate = birth;

            members[info.Id] = member;
        }

        // What is left was not in Spond's list; the exams they were invited to forget them (cascade).
        db.Members.RemoveRange(stored.Values);
        await db.SaveChangesAsync(cancellationToken);

        if (added + stored.Count > 0)
            logger.LogInformation("Members of group {GroupId} synced with Spond: {Added} added, {Removed} removed.", group.Id, added, stored.Count);

        return members;
    }

    /// <summary>
    /// A member whose start day is not known gets the day of the first event they attended: one that was not cancelled, is over, and
    /// that they accepted. Only the search window is looked at, so a member who started earlier gets the first day they appear in it
    /// (<c>Statistics:EventSearchMonthsBack</c> reaches further back). A day that is there is never changed.
    /// </summary>
    private void FillStartDates(IEnumerable<Member> members, IReadOnlyList<EventInfo> events)
    {
        var waiting = members.Where(m => m.StartedAt is null).ToDictionary(m => m.SpondId);
        if (waiting.Count == 0) return;

        var now = clock.Now;
        foreach (var e in events.Where(e => !e.Cancelled && e.End <= now)) // chronological: the first match is the first event
        {
            foreach (var (spondId, rsvp) in e.Responses)
            {
                if (rsvp == Rsvp.Accepted && waiting.Remove(spondId, out var member))
                    member.StartedAt = DateOnly.FromDateTime(e.Start.Date);
            }
        }
    }

    /// <summary>Makes the invitations of the exam those of its event: who was invited, and what each answered.</summary>
    private void SyncParticipants(Exam exam, IReadOnlyList<EventInfo> events, IReadOnlyDictionary<string, Member> members)
    {
        // An event outside the search window cannot be read: the exam keeps what it has, like its name and day.
        if (events.FirstOrDefault(e => e.Id == exam.EventId) is not { } current) return;

        ExamService.Follow(exam, current);

        var stored = exam.Participants.ToDictionary(p => p.MemberId);
        foreach (var (spondId, rsvp) in current.Responses)
        {
            // Somebody who is invited but is not a member of the group (any more) cannot be recorded.
            if (!members.TryGetValue(spondId, out var member)) continue;

            if (stored.Remove(member.Id, out var participant))
                participant.Rsvp = rsvp;
            else
                exam.Participants.Add(new ExamParticipant { MemberId = member.Id, Rsvp = rsvp });
        }

        // What is left was not invited (any more).
        db.ExamParticipants.RemoveRange(stored.Values);
    }
}
