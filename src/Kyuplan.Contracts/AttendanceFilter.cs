namespace Kyuplan.Contracts;

/// <summary>
/// The parameters an attendance analysis ran with, after the time span of the <see cref="AttendanceQuery"/> was
/// resolved to days. This is what the calculator works with and what <see cref="AttendanceStatisticsDto"/> echoes.
/// </summary>
/// <param name="GroupId">Spond group that was analysed.</param>
/// <param name="From">First day (inclusive, evaluated in the server's club time zone).</param>
/// <param name="To">Last day (inclusive).</param>
/// <param name="SubGroupIds">Only members of at least one of these subgroups were reported; <c>null</c> = everyone.</param>
/// <param name="MinAttendancePercent">A member is "qualified" when they attended at least this share of the events they were invited to.</param>
/// <param name="MinInvitedEvents">Members invited to fewer events than this get no verdict (<see cref="QualificationStatus.InsufficientData"/>).</param>
/// <param name="EventNameContains">Only events whose name contains any of these terms were counted (separated by <c>,</c> or <c>;</c>).</param>
/// <param name="EventNameExcludes">Events whose name contains any of these terms were ignored.</param>
public sealed record AttendanceFilter(
    string GroupId,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<string>? SubGroupIds = null,
    int MinAttendancePercent = 75,
    int MinInvitedEvents = 3,
    string? EventNameContains = null,
    string? EventNameExcludes = null);
