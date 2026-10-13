using System.Text.Json.Serialization;

namespace Kyuplan.Contracts;

/// <summary>Whether a member meets the attendance requirements of the current filter.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualificationStatus>))]
public enum QualificationStatus
{
    /// <summary>Attended at least the required percentage of the events they were invited to.</summary>
    Qualified,

    /// <summary>Enough events to judge, but below the required percentage.</summary>
    BelowThreshold,

    /// <summary>Invited to fewer events than the required minimum, so no reliable verdict.</summary>
    InsufficientData,
}

/// <summary>Coarse engagement band derived from <see cref="MemberAttendanceDto.EngagementScore"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngagementLevel>))]
public enum EngagementLevel
{
    Excellent,
    Good,
    Irregular,
    AtRisk,
}

/// <summary>Direction of a member's recent attendance compared to their earlier attendance.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttendanceTrend>))]
public enum AttendanceTrend
{
    /// <summary>Not enough events to compare recent with earlier attendance.</summary>
    Unknown,
    Falling,
    Stable,
    Rising,
}

/// <summary>Outcome of a single invitation, used for the per-member form strip.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttendanceMark>))]
public enum AttendanceMark
{
    Attended,
    Declined,
    Unanswered,

    /// <summary>Waiting list or unconfirmed; counts as not attended.</summary>
    Other,
}

