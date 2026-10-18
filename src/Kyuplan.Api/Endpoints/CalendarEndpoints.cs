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

        calendar.MapGet("/events/{eventId}", async (string eventId, string groupId, ClaimsPrincipal principal, CalendarService service, MemberService members, CancellationToken ct) =>
            {
                // What is written about a member the user is themselves needs the "own" permission, about everybody else the general one.
                var own = await members.IdsOfUserAsync(principal.UserId(), ct);
                var anyComments = principal.Has(Permission.CommentsView) || principal.Has(Permission.CommentsViewOwn);
                var anyRatings = principal.Has(Permission.RatingsView) || principal.Has(Permission.RatingsViewOwn);
                return await service.GetEventAsync(
                    groupId, eventId,
                    anyComments ? OwnData.MayRead(principal, own, Permission.CommentsView, Permission.CommentsViewOwn) : null,
                    anyRatings ? OwnData.MayRead(principal, own, Permission.RatingsView, Permission.RatingsViewOwn) : null,
                    ct);
            })
            .WithSummary("One event with everybody invited to it and what they answered; with comments.view also the comments on them that carry the tag of the event, with ratings.view everybody's star evaluations of them. For a member the user is themselves comments.viewOwn / ratings.viewOwn decide instead (each works without the other).")
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

        calendar.MapPut("/events/{eventId}/members/{memberId:int}/rating", async (
                string eventId, int memberId, string groupId, RatingInputDto input, ClaimsPrincipal principal, CalendarService service, CancellationToken ct) =>
            {
                if (input is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["input"] = ["A request body is required."] });
                if (!RatingRules.IsStars(input.Stars))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(input.Stars)] = [$"stars must be between {RatingRules.MinStars} and {RatingRules.MaxStars}."] });

                return Results.Ok(await service.RateAsync(groupId, eventId, memberId, input.Stars, CommentUser.From(principal), ct));
            })
            .WithSummary("Evaluate a member at an event with 0 to 5 stars as the signed-in user; replaces the earlier evaluation of that member at that event by the same user (a user has one). The event must have started.")
            .RequirePermission(Permission.RatingsWrite);

        calendar.MapDelete("/events/{eventId}/members/{memberId:int}/rating", async (
                string eventId, int memberId, string groupId, ClaimsPrincipal principal, CalendarService service, CancellationToken ct) =>
            {
                await service.UnrateAsync(groupId, eventId, memberId, CommentUser.From(principal), ct);
                return Results.NoContent();
            })
            .WithSummary("Take back the signed-in user's evaluation of a member at an event (only one's own can be taken back).")
            .RequirePermission(Permission.RatingsWrite);

        return app;
    }
}
