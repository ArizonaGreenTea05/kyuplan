using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// The calendar: the events of a group by day, week or month, the events that are going on right now, and one event with everybody invited
/// to it. Events come from Spond (its search window, see <see cref="StatisticsService"/>), the members' comments for an event from the
/// tags connected to it. Comments are written here with the event's tag already on them.
/// </summary>
public sealed class CalendarService(
    PlannerDbContext db, StatisticsService statistics, TagService tags, CommentService comments, RatingService ratings, ClubClock clock)
{
    /// <summary>The events that start within the day, week or month of <paramref name="date"/> (today when it is <c>null</c>).</summary>
    /// <exception cref="GroupNotFoundException"/>
    public async Task<CalendarDto> GetAsync(string groupId, string view, DateOnly? date, CancellationToken cancellationToken)
    {
        var day = date ?? clock.Today;
        var (from, to) = CalendarViews.Range(view, day);
        var start = clock.StartOfDay(from);
        var end = clock.StartOfDay(to.AddDays(1));

        var events = await statistics.GetWindowEventsAsync(groupId, cancellationToken);
        var names = await MemberNamesAsync(groupId, cancellationToken);
        return new CalendarDto(view, day, from, to, clock.Today,
            [.. events.Where(e => e.Start >= start && e.Start < end).Select(e => ToDto(e, groupId, names))]);
    }

    /// <summary>The events that are going on right now: started and not over, cancelled ones left out.</summary>
    /// <exception cref="GroupNotFoundException"/>
    public async Task<IReadOnlyList<CalendarEventDto>> GetCurrentAsync(string groupId, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var events = await statistics.GetWindowEventsAsync(groupId, cancellationToken);
        var names = await MemberNamesAsync(groupId, cancellationToken);
        return [.. events.Where(e => !e.Cancelled && e.Start <= now && now < e.End).Select(e => ToDto(e, groupId, names))];
    }

    /// <summary>One event with everybody invited to it, by name. Comments are only given for the members <paramref name="mayReadComments"/> says yes to, evaluations for those <paramref name="mayReadRatings"/> does (<c>null</c> = for none).</summary>
    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="EventNotFoundException"/>
    public async Task<CalendarEventDetailDto> GetEventAsync(string groupId, string eventId, Func<int, bool>? mayReadComments, Func<int, bool>? mayReadRatings, CancellationToken cancellationToken)
    {
        var group = await statistics.GetGroupInfoAsync(groupId, cancellationToken);
        var info = await statistics.FindEventAsync(groupId, eventId, cancellationToken);

        var known = await db.Members.AsNoTracking().Where(m => m.GroupId == groupId).ToDictionaryAsync(m => m.SpondId, m => m.Id, cancellationToken);
        var written = mayReadComments is not null ? await CommentsOfEventAsync(groupId, eventId, cancellationToken) : [];
        var given = mayReadRatings is not null
            ? await ratings.ListForEventAsync(groupId, eventId, cancellationToken)
            : new Dictionary<int, IReadOnlyList<RatingDto>>();
        var names = group.Members.ToDictionary(m => m.Id, m => m.Name);

        var attendees = info.Participants
            .Select(r =>
            {
                int? memberId = known.TryGetValue(r.Key, out var id) ? id : null;
                return new CalendarAttendeeDto(
                    memberId, names.GetValueOrDefault(r.Key, r.Key), Mark(r.Value),
                    memberId is { } member && mayReadComments?.Invoke(member) == true ? [.. written.Where(c => c.MemberId == member)] : [],
                    memberId is { } rated && mayReadRatings?.Invoke(rated) == true && given.TryGetValue(rated, out var list) ? list : []);
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new CalendarEventDetailDto(ToDto(info, groupId, names), attendees);
    }

    /// <summary>Writes a comment on a member of the event, tagged with the event's tag (which is made when there is none).</summary>
    /// <exception cref="GroupNotFoundException"/>
    /// <exception cref="EventNotFoundException"/>
    /// <exception cref="MemberNotFoundException">The member is not known, or is not in the event's group.</exception>
    public async Task<CommentDto> CommentAsync(
        string groupId, string eventId, int memberId, CalendarCommentInputDto input, CommentUser user, CancellationToken cancellationToken)
    {
        var info = await statistics.FindEventAsync(groupId, eventId, cancellationToken);
        if (!await db.Members.AnyAsync(m => m.Id == memberId && m.GroupId == groupId, cancellationToken)) throw new MemberNotFoundException(memberId);

        var tagId = await tags.EnsureEventTagAsync(info, groupId, cancellationToken);
        return await comments.CreateAsync(memberId, new CommentInputDto(input.Body, [tagId], input.ParentId), user, cancellationToken);
    }

    /// <summary>Gives or changes the user's evaluation of a member of the event.</summary>
    public Task<RatingDto> RateAsync(string groupId, string eventId, int memberId, int stars, CommentUser user, CancellationToken cancellationToken) =>
        ratings.SetAsync(groupId, eventId, memberId, stars, user.Id, user.Name, cancellationToken);

    /// <summary>Takes the user's evaluation of a member of the event back.</summary>
    public Task UnrateAsync(string groupId, string eventId, int memberId, CommentUser user, CancellationToken cancellationToken) =>
        ratings.DeleteAsync(groupId, eventId, memberId, user.Id, cancellationToken);

    private async Task<IReadOnlyList<CommentDto>> CommentsOfEventAsync(string groupId, string eventId, CancellationToken cancellationToken)
    {
        var tagIds = await tags.TagIdsOfEventAsync(groupId, eventId, cancellationToken);
        return tagIds.Count == 0 ? [] : await comments.ListForTagsAsync(tagIds, cancellationToken);
    }

    private static AttendanceMark Mark(Rsvp rsvp) => rsvp switch
    {
        Rsvp.Accepted => AttendanceMark.Attended,
        Rsvp.Declined => AttendanceMark.Declined,
        Rsvp.Unanswered => AttendanceMark.Unanswered,
        _ => AttendanceMark.Other,
    };

    private async Task<IReadOnlyDictionary<string, string>> MemberNamesAsync(string groupId, CancellationToken cancellationToken) =>
        (await statistics.GetGroupInfoAsync(groupId, cancellationToken)).Members.ToDictionary(m => m.Id, m => m.Name);

    /// <summary>The names of the trainers, by name; one whose member is gone shows by id rather than not at all.</summary>
    private static CalendarEventDto ToDto(EventInfo e, string groupId, IReadOnlyDictionary<string, string> names) => new(
        e.Id, groupId, e.Name, e.Start, e.End, e.Cancelled, e.Participants.Count(),
        e.Participants.Count(r => r.Value == Rsvp.Accepted),
        e.Participants.Count(r => r.Value == Rsvp.Declined),
        e.Participants.Count(r => r.Value == Rsvp.Unanswered),
        [.. (e.Trainers ?? []).Select(id => names.GetValueOrDefault(id, id)).Order(StringComparer.CurrentCultureIgnoreCase)]);
}
