using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Statistics;

internal static class DashboardBuilder
{
    private const int ListSize = 5;
    private const int MaxUpcoming = 8;

    /// <param name="days">Look-back window; the figures cover the last <paramref name="days"/> days including today.</param>
    /// <param name="now">Current time in the club time zone.</param>
    public static DashboardDto Build(
        GroupInfo group,
        string? subGroupId,
        IReadOnlyCollection<EventInfo> events,
        int days,
        DateTimeOffset now,
        StatisticsOptions options)
    {
        var today = DateOnly.FromDateTime(now.Date);
        var filter = new AttendanceFilter(
            group.Id, today.AddDays(1 - days), today, string.IsNullOrEmpty(subGroupId) ? null : [subGroupId],
            options.DefaultMinAttendancePercent, options.DefaultMinInvitedEvents);

        var statistics = AttendanceCalculator.Calculate(group, events, filter, now);

        var reliable = statistics.Members.Where(m => m.Status != QualificationStatus.InsufficientData).ToList();
        var top = reliable
            .OrderByDescending(m => m.EngagementScore)
            .ThenByDescending(m => m.AttendanceRate)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListSize)
            .ToList();
        var needsAttention = reliable
            .Where(m => m.Level == EngagementLevel.AtRisk ||
                        (m.Trend == AttendanceTrend.Falling && m.Level != EngagementLevel.Excellent))
            .OrderBy(m => m.EngagementScore)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListSize)
            .ToList();

        return new DashboardDto(
            group.Id, group.Name, filter.From, filter.To, filter.MinAttendancePercent,
            statistics.Summary,
            BuildUpcoming(group, subGroupId, events, now, options.UpcomingDays),
            top,
            needsAttention,
            statistics.MonthlyTrend);
    }

    private static List<UpcomingEventDto> BuildUpcoming(
        GroupInfo group, string? subGroupId, IEnumerable<EventInfo> events, DateTimeOffset now, int upcomingDays)
    {
        var memberIds = group.Members
            .Where(m => string.IsNullOrEmpty(subGroupId) || m.SubGroupIds.Contains(subGroupId))
            .Select(m => m.Id)
            .ToHashSet();
        var horizon = now.AddDays(upcomingDays);

        var upcoming = new List<UpcomingEventDto>();
        foreach (var e in events.DistinctBy(e => e.Id)
                     .Where(e => !e.Cancelled && e.End > now && e.Start <= horizon)
                     .OrderBy(e => e.Start))
        {
            int invited = 0, accepted = 0, declined = 0, unanswered = 0;
            foreach (var (memberId, rsvp) in e.Participants)
            {
                if (!memberIds.Contains(memberId)) continue;
                invited++;
                switch (rsvp)
                {
                    case Rsvp.Accepted: accepted++; break;
                    case Rsvp.Declined: declined++; break;
                    case Rsvp.Unanswered: unanswered++; break;
                }
            }

            if (invited > 0) upcoming.Add(new UpcomingEventDto(e.Id, e.Name, e.Start, invited, accepted, declined, unanswered));
            if (upcoming.Count == MaxUpcoming) break;
        }

        return upcoming;
    }
}
