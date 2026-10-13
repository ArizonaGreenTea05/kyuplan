using System.Text.Json.Serialization;

namespace Kyuplan.Contracts;

/// <summary>
/// Everything the filter card of the statistics page holds: what a client remembers as the filter it used last and what
/// the API stores as a saved filter. It mirrors <see cref="AttendanceQuery"/>, but keeps both ways of naming the time
/// span: the events (so that the filter follows them) and the days they stood for when it was saved (so that the
/// filter still works once the events have left the server's search window).
/// </summary>
/// <param name="GroupId">Spond group the filter was made for. Subgroup ids only make sense within it.</param>
/// <param name="From">First day of the time span (inclusive).</param>
/// <param name="To">Last day of the time span (inclusive).</param>
/// <param name="FromEventId">Event whose day starts the span; with <paramref name="ToEventId"/> it defines the span, otherwise the two days do.</param>
/// <param name="ToEventId">Event whose day ends the span.</param>
/// <param name="SubGroupIds">Only members of at least one of these subgroups; empty = everyone.</param>
/// <param name="MinAttendancePercent">Required share of attended invitations, 0–100.</param>
/// <param name="MinInvitedEvents">Members invited to fewer events than this get no verdict.</param>
/// <param name="EventNameContains">Only count events whose name contains any of these terms.</param>
/// <param name="EventNameExcludes">Ignore events whose name contains any of these terms.</param>
public sealed record FilterSettingsDto(
    string GroupId,
    DateOnly From,
    DateOnly To,
    string? FromEventId,
    string? ToEventId,
    IReadOnlyList<string> SubGroupIds,
    int MinAttendancePercent,
    int MinInvitedEvents,
    string? EventNameContains,
    string? EventNameExcludes)
{
    /// <summary>True when two events, not the two days, define the time span.</summary>
    [JsonIgnore]
    public bool SpanByEvents => !string.IsNullOrEmpty(FromEventId) && !string.IsNullOrEmpty(ToEventId);
}

/// <summary>A filter stored on the server. It belongs to exactly one exam.</summary>
public sealed record SavedFilterDto(
    int Id,
    int ExamId,
    string Name,
    FilterSettingsDto Settings,
    DateTimeOffset UpdatedAt);

/// <summary>What a client sends to create or replace a saved filter.</summary>
public sealed record SavedFilterInputDto(string Name, FilterSettingsDto Settings);

/// <summary>
/// An exam: a Spond event that is a belt exam, and the filters saved for it. The exam <i>is</i> that event, so it has
/// no name or day of its own: <paramref name="Name"/> and <paramref name="Date"/> are the event's, as the API last saw
/// them on Spond (it keeps them up to date, and keeps the last ones when the event has left its search window).
/// </summary>
/// <param name="GroupId">Spond group the event belongs to.</param>
/// <param name="EventId">The Spond event that is the exam; one event is at most one exam.</param>
/// <param name="Name">The event's name.</param>
/// <param name="Date">The day the event starts on, in the club's time zone.</param>
/// <param name="Filters">Sorted by name.</param>
/// <param name="InvitedCount">Members invited to the event on Spond, as of the last sync.</param>
/// <param name="AttendedCount">Of them, those who accepted: "attended" means "accepted the invitation".</param>
/// <param name="PreSavedCount">Members saved for the exam in the planner's own database (see <see cref="ExamCandidatesInputDto"/>), whether or not they were invited on Spond since.</param>
/// <param name="BeltsAwardedCount">Members who received a belt at the exam (see <see cref="ExamResultInputDto"/>).</param>
/// <param name="SubGroupIds">The subgroups the exam's default filter looks at; empty or <c>null</c> = everyone in the group.</param>
public sealed record ExamDto(
    int Id,
    string GroupId,
    string EventId,
    string Name,
    DateOnly Date,
    IReadOnlyList<SavedFilterDto> Filters,
    int InvitedCount = 0,
    int AttendedCount = 0,
    int PreSavedCount = 0,
    int BeltsAwardedCount = 0,
    IReadOnlyList<string>? SubGroupIds = null);

/// <summary>What a client sends to make an event an exam, or to point an exam at another event: the event, and for a new exam the subgroups its default filter looks at.</summary>
/// <param name="GroupId">Spond group the event belongs to.</param>
/// <param name="EventId">An event of the API's search window (see <see cref="EventChoicesDto"/>).</param>
/// <param name="SubGroupIds">Subgroups of the group the default filter of the exam looks at, so that the suggestions only judge their members; empty = everyone. <c>null</c> = not said: a new exam looks at everyone, an exam that is pointed at another event keeps what it has.</param>
public sealed record ExamInputDto(string GroupId, string EventId, IReadOnlyList<string>? SubGroupIds = null);
