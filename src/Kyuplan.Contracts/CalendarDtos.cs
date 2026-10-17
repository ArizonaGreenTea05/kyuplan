namespace Kyuplan.Contracts;

/// <summary>What a calendar shows at once: a day, the week or the month a given day lies in.</summary>
public static class CalendarViews
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static bool IsValid(string? view) => view is Day or Week or Month;

    /// <summary>The first and last day shown for <paramref name="date"/>. Weeks start on Monday.</summary>
    public static (DateOnly From, DateOnly To) Range(string view, DateOnly date)
    {
        switch (view)
        {
            case Week:
                var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
                return (monday, monday.AddDays(6));
            case Month:
                var first = new DateOnly(date.Year, date.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            default:
                return (date, date);
        }
    }
}

/// <summary>An event of the calendar with how many of those invited answered what.</summary>
/// <param name="Start">Start in the club time zone.</param>
/// <param name="End">End in the club time zone.</param>
/// <param name="Invited">Everybody who was invited.</param>
public sealed record CalendarEventDto(
    string Id,
    string GroupId,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool Cancelled,
    int Invited,
    int Accepted,
    int Declined,
    int Unanswered);

/// <summary>The events of a group within a day, a week or a month.</summary>
/// <param name="View">One of <see cref="CalendarViews"/>.</param>
/// <param name="Date">The day the view was asked for.</param>
/// <param name="From">The first day shown.</param>
/// <param name="To">The last day shown.</param>
/// <param name="Today">Today in the club time zone.</param>
/// <param name="Events">By start; cancelled ones are in, marked.</param>
public sealed record CalendarDto(
    string View, DateOnly Date, DateOnly From, DateOnly To, DateOnly Today, IReadOnlyList<CalendarEventDto> Events);

/// <summary>One member invited to an event, what they answered and what has been written about them for this event.</summary>
/// <param name="MemberId">The planner's id of the member; <c>null</c> when the member is not known to the planner yet (comments need it).</param>
/// <param name="Comments">The comments on the member that carry the event's tag; empty for a user who may not read comments.</param>
public sealed record CalendarAttendeeDto(
    int? MemberId, string Name, AttendanceMark Answer, IReadOnlyList<CommentDto> Comments);

/// <summary>An event with everybody invited to it.</summary>
public sealed record CalendarEventDetailDto(CalendarEventDto Event, IReadOnlyList<CalendarAttendeeDto> Attendees);

/// <summary>A comment written from an event's page. The API tags it with the tag of the event, and makes that tag when the event has none.</summary>
/// <param name="ParentId">The comment of the same member this one replies to; <c>null</c> for a new comment.</param>
public sealed record CalendarCommentInputDto(string Body, int? ParentId = null);
