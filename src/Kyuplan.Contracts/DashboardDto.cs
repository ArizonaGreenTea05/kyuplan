namespace Kyuplan.Contracts;

/// <summary>An upcoming event together with how far the RSVP round has progressed.</summary>
public sealed record UpcomingEventDto(
    string Id,
    string Name,
    DateTimeOffset Start,
    int Invited,
    int Accepted,
    int Declined,
    int Unanswered);

/// <param name="From">Start of the look-back window the figures are based on.</param>
/// <param name="To">End of the look-back window (today).</param>
public sealed record DashboardDto(
    string GroupId,
    string GroupName,
    DateOnly From,
    DateOnly To,
    int MinAttendancePercent,
    AttendanceSummaryDto Summary,
    IReadOnlyList<UpcomingEventDto> Upcoming,
    IReadOnlyList<MemberAttendanceDto> TopMembers,
    IReadOnlyList<MemberAttendanceDto> NeedsAttention,
    IReadOnlyList<MonthlyTrendPointDto> MonthlyTrend);
