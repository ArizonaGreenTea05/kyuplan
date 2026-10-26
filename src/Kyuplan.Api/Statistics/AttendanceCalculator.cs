using Kyuplan.Contracts;

namespace Kyuplan.Api.Statistics;

/// <summary>
/// Turns events and RSVP lists into attendance statistics. Pure and deterministic: everything it needs,
/// including "now", is passed in. Metric definitions are documented in docs/attendance-statistics.md.
/// </summary>
internal static class AttendanceCalculator
{
    /// <summary>Number of most recent invitations that count as "recent form".</summary>
    public const int RecentWindow = 8;

    /// <summary>Trend needs the recent window plus at least this many earlier events to compare with.</summary>
    public const int MinEventsForTrend = RecentWindow + 4;

    /// <summary>Recent vs. earlier attendance must differ by this many percentage points to count as rising/falling.</summary>
    public const double TrendThresholdPoints = 15;

    public const int FormStripLength = 10;

    private const double WeightAttendance = 0.60;
    private const double WeightRecent = 0.25;
    private const double WeightResponse = 0.15;

    private sealed record Invitation(EventInfo Event, Rsvp Rsvp)
    {
        public bool Attended => Rsvp == Rsvp.Accepted;
    }

    /// <param name="now">Current time in the club time zone; decides which events are over and what "today" is.</param>
    /// <param name="ratings">The star evaluations of the members at the events; none = nobody was evaluated, every attended event counts the default.</param>
    public static AttendanceStatisticsDto Calculate(
        GroupInfo group, IEnumerable<EventInfo> events, AttendanceFilter filter, DateTimeOffset now, RatingBook? ratings = null)
    {
        ratings ??= RatingBook.None();
        var heldEvents = SelectHeldEvents(events, filter, now);
        var members = group.Members
            .DistinctBy(m => m.Id)
            .Where(m => filter.SubGroupIds is not { Count: > 0 } ids || ids.Any(m.SubGroupIds.Contains))
            .ToList();
        var subGroupNames = group.SubGroups.ToDictionary(s => s.Id, s => s.Name);

        // One pass over the events; heldEvents is chronological, so each member's list is too.
        var invitations = members.ToDictionary(m => m.Id, _ => new List<Invitation>());
        foreach (var e in heldEvents)
        {
            foreach (var (memberId, rsvp) in e.Participants)
            {
                if (invitations.TryGetValue(memberId, out var list)) list.Add(new Invitation(e, rsvp));
            }
        }

        var today = DateOnly.FromDateTime(now.Date);
        var windowEnd = filter.To < today ? filter.To : today;

        var memberRows = members
            .Select(m => BuildMember(m, invitations[m.Id].ToArray(), filter, today, windowEnd, subGroupNames, ratings))
            .OfType<MemberAttendanceDto>()
            .OrderByDescending(m => m.AttendanceRate)
            .ThenByDescending(m => m.Invited)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var eventRows = BuildEventRows(heldEvents, invitations, ratings);
        var allStars = invitations
            .SelectMany(kv => kv.Value.Select(i => ratings.StarsOf(kv.Key, i.Event.Id, i.Attended)))
            .OfType<double>()
            .ToList();

        return new AttendanceStatisticsDto(
            filter,
            group.Name,
            now,
            BuildSummary(memberRows, eventRows, allStars),
            memberRows,
            eventRows.OrderByDescending(e => e.Start).ToList(),
            BuildDistribution(memberRows),
            BuildMonthlyTrend(eventRows));
    }

    /// <summary>Events that count as "held": not cancelled, already over, inside the window and matching the name filters.</summary>
    internal static List<EventInfo> SelectHeldEvents(IEnumerable<EventInfo> events, AttendanceFilter filter, DateTimeOffset now)
    {
        var contains = ParseTerms(filter.EventNameContains);
        var excludes = ParseTerms(filter.EventNameExcludes);

        return events
            .DistinctBy(e => e.Id)
            .Where(e => !e.Cancelled && e.End <= now)
            .Where(e =>
            {
                var day = DateOnly.FromDateTime(e.Start.Date);
                return day >= filter.From && day <= filter.To;
            })
            .Where(e => contains.Length == 0 || contains.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Where(e => !excludes.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static string[] ParseTerms(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static MemberAttendanceDto? BuildMember(
        MemberInfo member,
        Invitation[] invitations,
        AttendanceFilter filter,
        DateOnly today,
        DateOnly windowEnd,
        IReadOnlyDictionary<string, string> subGroupNames,
        RatingBook ratings)
    {
        var invited = invitations.Length;
        if (invited == 0) return null;

        // An event counts once, however many users evaluated the member; only events the member attended or was evaluated at count.
        var stars = invitations.Select(i => ratings.StarsOf(member.Id, i.Event.Id, i.Attended)).OfType<double>().ToList();
        var evaluated = invitations.Count(i => ratings.IsEvaluated(member.Id, i.Event.Id));

        var attended = invitations.Count(i => i.Attended);
        var declined = invitations.Count(i => i.Rsvp == Rsvp.Declined);
        var unanswered = invitations.Count(i => i.Rsvp == Rsvp.Unanswered);

        var rate = Percent(attended, invited);
        var responseRate = Percent(invited - unanswered, invited);

        var longestStreak = 0;
        var streak = 0;
        foreach (var invitation in invitations)
        {
            streak = invitation.Attended ? streak + 1 : 0;
            longestStreak = Math.Max(longestStreak, streak);
        }

        var firstInvited = DateOnly.FromDateTime(invitations[0].Event.Start.Date);
        var lastAttendedEvent = invitations.LastOrDefault(i => i.Attended);
        DateOnly? lastAttended = lastAttendedEvent is null ? null : DateOnly.FromDateTime(lastAttendedEvent.Event.Start.Date);
        int? daysSince = lastAttended is { } last ? Math.Max(0, today.DayNumber - last.DayNumber) : null;

        var weeks = Math.Max(1.0, (windowEnd.DayNumber - firstInvited.DayNumber + 1) / 7.0);
        var perWeek = Math.Round(attended / weeks, 2);

        double? recentRate = null;
        double? trendDelta = null;
        var trend = AttendanceTrend.Unknown;
        if (invited >= RecentWindow)
        {
            recentRate = Percent(invitations[^RecentWindow..].Count(i => i.Attended), RecentWindow);
            if (invited >= MinEventsForTrend)
            {
                var earlier = invitations[..^RecentWindow];
                trendDelta = Math.Round(recentRate.Value - Percent(earlier.Count(i => i.Attended), earlier.Length), 1);
                trend = trendDelta >= TrendThresholdPoints ? AttendanceTrend.Rising
                    : trendDelta <= -TrendThresholdPoints ? AttendanceTrend.Falling
                    : AttendanceTrend.Stable;
            }
        }

        var score = (int)Math.Round(
            WeightAttendance * rate + WeightRecent * (recentRate ?? rate) + WeightResponse * responseRate,
            MidpointRounding.AwayFromZero);
        score = Math.Clamp(score, 0, 100);

        // Integer comparison so that exactly 3 of 4 qualifies for a 75 % threshold.
        var status = invited < filter.MinInvitedEvents ? QualificationStatus.InsufficientData
            : attended * 100 >= filter.MinAttendancePercent * invited ? QualificationStatus.Qualified
            : QualificationStatus.BelowThreshold;

        var subGroups = member.SubGroupIds
            .Select(id => subGroupNames.GetValueOrDefault(id))
            .OfType<string>()
            .ToList();

        var marks = invitations
            .TakeLast(FormStripLength)
            .Select(i => i.Rsvp switch
            {
                Rsvp.Accepted => AttendanceMark.Attended,
                Rsvp.Declined => AttendanceMark.Declined,
                Rsvp.Unanswered => AttendanceMark.Unanswered,
                _ => AttendanceMark.Other,
            })
            .ToList();

        return new MemberAttendanceDto(
            member.Id, member.Name, subGroups,
            invited, attended, declined, unanswered,
            rate, responseRate,
            streak, longestStreak,
            firstInvited, lastAttended, daysSince,
            perWeek, recentRate, trendDelta, trend,
            score, LevelFor(score), status, marks,
            stars.Count == 0 ? null : Math.Round(stars.Average(), 2), stars.Count, evaluated);
    }

    internal static EngagementLevel LevelFor(int score) => score switch
    {
        >= 80 => EngagementLevel.Excellent,
        >= 60 => EngagementLevel.Good,
        >= 40 => EngagementLevel.Irregular,
        _ => EngagementLevel.AtRisk,
    };

    private static List<EventAttendanceDto> BuildEventRows(
        List<EventInfo> heldEvents, Dictionary<string, List<Invitation>> invitationsByMember, RatingBook ratings)
    {
        // Count each event over the members that are part of the analysis only (respects the subgroup filter).
        var included = invitationsByMember
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Key)
            .ToHashSet();

        var rows = new List<EventAttendanceDto>();
        foreach (var e in heldEvents)
        {
            int invited = 0, attended = 0, declined = 0, unanswered = 0;
            List<double> stars = [];
            foreach (var (memberId, rsvp) in e.Participants)
            {
                if (!included.Contains(memberId)) continue;
                invited++;
                if (ratings.StarsOf(memberId, e.Id, rsvp == Rsvp.Accepted) is { } given) stars.Add(given);
                switch (rsvp)
                {
                    case Rsvp.Accepted: attended++; break;
                    case Rsvp.Declined: declined++; break;
                    case Rsvp.Unanswered: unanswered++; break;
                }
            }

            if (invited == 0) continue;
            rows.Add(new EventAttendanceDto(e.Id, e.Name, e.Start, invited, attended, declined, unanswered, Percent(attended, invited),
                stars.Count == 0 ? null : Math.Round(stars.Average(), 2)));
        }

        return rows;
    }

    private static AttendanceSummaryDto BuildSummary(
        IReadOnlyList<MemberAttendanceDto> members, IReadOnlyList<EventAttendanceDto> events, IReadOnlyList<double> stars)
    {
        var totalInvited = members.Sum(m => m.Invited);
        var totalAttended = members.Sum(m => m.Attended);
        var totalUnanswered = members.Sum(m => m.Unanswered);

        return new AttendanceSummaryDto(
            events.Count,
            members.Count,
            members.Count(m => m.Status == QualificationStatus.Qualified),
            members.Count(m => m.Status == QualificationStatus.BelowThreshold),
            members.Count(m => m.Status == QualificationStatus.InsufficientData),
            members.Count(m => m.Status != QualificationStatus.InsufficientData && m.Level == EngagementLevel.AtRisk),
            Percent(totalAttended, totalInvited),
            Percent(totalInvited - totalUnanswered, totalInvited),
            events.Count == 0 ? 0 : Math.Round(events.Average(e => (double)e.Attended), 1),
            stars.Count == 0 ? null : Math.Round(stars.Average(), 2));
    }

    private static List<DistributionBucketDto> BuildDistribution(IReadOnlyList<MemberAttendanceDto> members)
    {
        const int buckets = 10;
        var counts = new int[buckets];
        foreach (var m in members.Where(m => m.Status != QualificationStatus.InsufficientData))
        {
            counts[Math.Min(buckets - 1, m.Attended * buckets / m.Invited)]++;
        }

        return Enumerable.Range(0, buckets)
            .Select(i => new DistributionBucketDto(i * 10, i == buckets - 1 ? 100 : (i + 1) * 10, counts[i]))
            .ToList();
    }

    private static List<MonthlyTrendPointDto> BuildMonthlyTrend(IReadOnlyList<EventAttendanceDto> events) =>
        events
            .GroupBy(e => (e.Start.Year, e.Start.Month))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyTrendPointDto(
                new DateOnly(g.Key.Year, g.Key.Month, 1),
                g.Count(),
                Math.Round(g.Average(e => (double)e.Attended), 1),
                Percent(g.Sum(e => e.Attended), g.Sum(e => e.Invited))))
            .ToList();

    internal static double Percent(int part, int whole) =>
        whole == 0 ? 0 : Math.Round(100.0 * part / whole, 1);
}
