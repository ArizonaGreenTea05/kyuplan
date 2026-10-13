using System.Globalization;

namespace Kyuplan.Contracts;

/// <summary>
/// What a client asks for: an attendance analysis of one group. Bound from the query string by the API
/// (<c>GET /api/statistics/attendance</c>) and produced by <see cref="ToQueryString"/> on the client.
/// <para>
/// The time span is given either as two dates (<see cref="From"/>, <see cref="To"/>) or as two events
/// (<see cref="FromEventId"/>, <see cref="ToEventId"/>), never both. The API answers with the
/// <see cref="AttendanceFilter"/> the span was resolved to.
/// </para>
/// </summary>
/// <param name="GroupId">Spond group to analyse.</param>
/// <param name="From">First day (inclusive, evaluated in the server's club time zone).</param>
/// <param name="To">Last day (inclusive).</param>
/// <param name="FromEventId">Event (id from <see cref="EventChoicesDto"/>) whose start day is the first day of the span.</param>
/// <param name="ToEventId">Event whose start day is the last day of the span.</param>
/// <param name="SubGroupIds">Only report members of at least one of these subgroups (<c>subGroupIds</c>, repeated for each). Empty or <c>null</c> = everyone. An array because the API binds repeated query values only to arrays.</param>
/// <param name="MinAttendancePercent">A member is "qualified" when they attended at least this share of the events they were invited to.</param>
/// <param name="MinInvitedEvents">Members invited to fewer events than this get no verdict (<see cref="QualificationStatus.InsufficientData"/>).</param>
/// <param name="EventNameContains">Only count events whose name contains any of these terms (separated by <c>,</c> or <c>;</c>).</param>
/// <param name="EventNameExcludes">Ignore events whose name contains any of these terms.</param>
public sealed record AttendanceQuery(
    string GroupId,
    DateOnly? From = null,
    DateOnly? To = null,
    string? FromEventId = null,
    string? ToEventId = null,
    string[]? SubGroupIds = null,
    int MinAttendancePercent = 75,
    int MinInvitedEvents = 3,
    string? EventNameContains = null,
    string? EventNameExcludes = null)
{
    public string ToQueryString()
    {
        var parts = new List<string> { Pair("groupId", GroupId) };

        if (From is { } from) parts.Add(Pair("from", Day(from)));
        if (To is { } to) parts.Add(Pair("to", Day(to)));
        if (!string.IsNullOrWhiteSpace(FromEventId)) parts.Add(Pair("fromEventId", FromEventId));
        if (!string.IsNullOrWhiteSpace(ToEventId)) parts.Add(Pair("toEventId", ToEventId));

        parts.Add(Pair("minAttendancePercent", MinAttendancePercent.ToString(CultureInfo.InvariantCulture)));
        parts.Add(Pair("minInvitedEvents", MinInvitedEvents.ToString(CultureInfo.InvariantCulture)));

        foreach (var subGroupId in SubGroupIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(subGroupId)) parts.Add(Pair("subGroupIds", subGroupId));
        }

        if (!string.IsNullOrWhiteSpace(EventNameContains)) parts.Add(Pair("eventNameContains", EventNameContains));
        if (!string.IsNullOrWhiteSpace(EventNameExcludes)) parts.Add(Pair("eventNameExcludes", EventNameExcludes));

        return string.Join('&', parts);

        static string Pair(string key, string value) => $"{key}={Uri.EscapeDataString(value)}";
        static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
