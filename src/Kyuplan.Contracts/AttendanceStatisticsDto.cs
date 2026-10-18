namespace Kyuplan.Contracts;

/// <summary>
/// Attendance figures of one member. "Invited" always means: events in the analysed window that were
/// held (not cancelled, already finished) and addressed to this member. All percentages are 0–100.
/// </summary>
/// <param name="Attended">Invitations the member accepted.</param>
/// <param name="Declined">Invitations the member explicitly declined.</param>
/// <param name="Unanswered">Invitations the member never answered.</param>
/// <param name="AttendanceRate">Attended / Invited.</param>
/// <param name="ResponseRate">Share of invitations that got an answer (accepted or declined).</param>
/// <param name="CurrentStreak">Consecutive attended events counting back from the most recent one.</param>
/// <param name="LongestStreak">Longest run of consecutive attended events in the window.</param>
/// <param name="SessionsPerWeek">Attended events per week since the member's first invitation in the window.</param>
/// <param name="RecentRate">Attendance rate of the last few invitations; <c>null</c> without enough events.</param>
/// <param name="TrendDelta">Recent minus earlier attendance rate in percentage points; <c>null</c> without enough events.</param>
/// <param name="EngagementScore">Composite 0–100 score, see docs/attendance-statistics.md.</param>
/// <param name="RecentMarks">Outcome of the last invitations, oldest first.</param>
/// <param name="AverageStars">Average evaluation (0–5) over the events the member attended or was evaluated at; an event counts once, as the average of its evaluations, or as the default when nobody evaluated it. <c>null</c> without such an event.</param>
/// <param name="StarEvents">How many events <paramref name="AverageStars"/> is over.</param>
/// <param name="EvaluatedEvents">Of them, those somebody evaluated.</param>
public sealed record MemberAttendanceDto(
    string MemberId,
    string Name,
    IReadOnlyList<string> SubGroups,
    int Invited,
    int Attended,
    int Declined,
    int Unanswered,
    double AttendanceRate,
    double ResponseRate,
    int CurrentStreak,
    int LongestStreak,
    DateOnly FirstInvited,
    DateOnly? LastAttended,
    int? DaysSinceLastAttended,
    double SessionsPerWeek,
    double? RecentRate,
    double? TrendDelta,
    AttendanceTrend Trend,
    int EngagementScore,
    EngagementLevel Level,
    QualificationStatus Status,
    IReadOnlyList<AttendanceMark> RecentMarks,
    double? AverageStars = null,
    int StarEvents = 0,
    int EvaluatedEvents = 0);

/// <summary>Attendance of a single held event, counted over the members that are part of the analysis.</summary>
/// <param name="AverageStars">Average evaluation of the members who attended or were evaluated at the event (see <see cref="MemberAttendanceDto.AverageStars"/>); <c>null</c> when there are none.</param>
public sealed record EventAttendanceDto(
    string Id,
    string Name,
    DateTimeOffset Start,
    int Invited,
    int Attended,
    int Declined,
    int Unanswered,
    double AttendanceRate,
    double? AverageStars = null);

/// <summary>One month of the attendance trend.</summary>
/// <param name="Month">First day of the month.</param>
public sealed record MonthlyTrendPointDto(
    DateOnly Month,
    int Events,
    double AverageAttendees,
    double AttendanceRate);

/// <summary>Number of members whose attendance rate lies in [<paramref name="LowerPercent"/>, <paramref name="UpperPercent"/>).</summary>
public sealed record DistributionBucketDto(int LowerPercent, int UpperPercent, int Members);

public sealed record AttendanceSummaryDto(
    int EventCount,
    int MemberCount,
    int QualifiedCount,
    int BelowThresholdCount,
    int InsufficientDataCount,
    int AtRiskCount,
    double OverallAttendanceRate,
    double AverageResponseRate,
    double AverageAttendeesPerEvent,
    double? AverageStars = null);

/// <param name="Filter">What the analysis ran with: the query with its time span resolved to days.</param>
public sealed record AttendanceStatisticsDto(
    AttendanceFilter Filter,
    string GroupName,
    DateTimeOffset GeneratedAt,
    AttendanceSummaryDto Summary,
    IReadOnlyList<MemberAttendanceDto> Members,
    IReadOnlyList<EventAttendanceDto> Events,
    IReadOnlyList<DistributionBucketDto> Distribution,
    IReadOnlyList<MonthlyTrendPointDto> MonthlyTrend);
