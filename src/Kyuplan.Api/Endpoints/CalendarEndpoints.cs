using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>The calendar (<c>/api/calendar</c>): events by day, week or month, the events going on now, and one event with its invited members.</summary>
public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var calendar = app.MapGroup("/api/calendar").WithTags("Calendar");

        calendar.MapGet("/", async (string groupId, string? view, DateOnly? date, CalendarService service, CancellationToken ct) =>
            {
                view ??= CalendarViews.Week;
                if (string.IsNullOrWhiteSpace(groupId)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["groupId"] = ["groupId is required."] });
                if (!CalendarViews.IsValid(view)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["view"] = ["view must be day, week or month."] });

                return Results.Ok(await service.GetAsync(groupId, view, date, ct));
            })
            .WithSummary("The events of a group within the day, week (Monday to Sunday) or month of a date (today when left out), cancelled ones marked. Events are those of the search window.")
            .RequirePermission(Permission.CalendarView);

        calendar.MapGet("/current", (string groupId, CalendarService service, CancellationToken ct) => service.GetCurrentAsync(groupId, ct))
            .WithSummary("The events of a group that are going on right now.")
            .RequirePermission(Permission.CalendarView);

        calendar.MapGet("/events/{eventId}", (string eventId, string groupId, ClaimsPrincipal principal, CalendarService service, CancellationToken ct) =>
                service.GetEventAsync(groupId, eventId, principal.Has(Permission.CommentsView), ct))
            .WithSummary("One event with everybody invited to it and what they answered; with comments.view also the comments on them that carry the event's tag.")
            .RequirePermission(Permission.CalendarView);

        calendar.MapPost("/events/{eventId}/members/{memberId:int}/comments", async (
                string eventId, int memberId, string groupId, CalendarCommentInputDto input, ClaimsPrincipal principal, CalendarService service, CancellationToken ct) =>
            {
                if (input is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["input"] = ["A request body is required."] });
                var error = CommentRules.CheckBody(input.Body);
                if (error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(input.Body)] = [error] });

                return Results.Ok(await service.CommentAsync(groupId, eventId, memberId, input, CommentUser.From(principal), ct));
            })
            .WithSummary("Write a comment on a member of an event as the signed-in user. It is tagged with the tag of the event, which is made when the event has none.")
            .RequirePermission(Permission.CommentsWrite);

        return app;
    }
}
