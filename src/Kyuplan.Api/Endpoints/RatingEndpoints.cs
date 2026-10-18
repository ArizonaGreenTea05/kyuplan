using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// The star evaluations of members. They are given and taken back from the calendar page of an event
/// (<c>/api/calendar/events/{eventId}/members/{memberId}/rating</c>, see <c>CalendarEndpoints</c>); this reads everything one member got.
/// </summary>
public static class RatingEndpoints
{
    public static IEndpointRouteBuilder MapRatingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/members/{memberId:int}/ratings", async (int memberId, ClaimsPrincipal principal, RatingService service, MemberService members, CancellationToken ct) =>
            {
                await OwnData.EnsureMayReadAsync(principal, memberId, Permission.RatingsView, Permission.RatingsViewOwn, members, "evaluations", ct);
                return await service.ListForMemberAsync(memberId, ct);
            })
            .WithTags("Ratings")
            .WithSummary("Every user's star evaluations (0 to 5) of a member at the events, the latest first, each with the event id, the user and when it was given. Needs ratings.view, or for a member the user is themselves ratings.viewOwn instead: each works without the other (403 otherwise).")
            .RequirePermission(Permission.RatingsView, Permission.RatingsViewOwn);

        return app;
    }
}
