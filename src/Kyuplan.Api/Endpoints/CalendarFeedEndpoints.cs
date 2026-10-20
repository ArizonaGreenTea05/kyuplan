using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// The calendar feeds: administrators define them (<c>/api/calendar-feeds</c>), calendar apps subscribe to the ICS address of each
/// (<c>/calendar-feeds/{token}.ics</c>). The ICS address has no login, because calendar apps cannot send one: the secret token in it is the key.
/// </summary>
public static class CalendarFeedEndpoints
{
    public const string IcsRoute = "/calendar-feeds/{token}.ics";

    public static IEndpointRouteBuilder MapCalendarFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(IcsRoute, async (string token, CalendarFeedService service, HttpContext context, CancellationToken ct) =>
            {
                var ics = await service.RenderAsync(token, ct);
                if (ics is null) return Results.NotFound();

                context.Response.Headers.CacheControl = "private, max-age=300";
                return Results.Text(ics, "text/calendar", System.Text.Encoding.UTF8);
            })
            .WithTags("Calendar feeds")
            .WithSummary("The calendar of a feed as an ICS file, to subscribe to in a calendar app. No login: the token in the address is the key. Unknown token: 404.")
            .AllowAnonymous();

        var feeds = app.MapGroup("/api/calendar-feeds").WithTags("Calendar feeds").RequirePermission(Permission.CalendarFeeds);

        feeds.MapGet("/", (HttpRequest request, CalendarFeedService service, CancellationToken ct) => service.ListAsync(BaseUrl(request), ct))
            .WithSummary("All calendar feeds with their ICS address and sources.");

        feeds.MapGet("/{id:int}", (int id, HttpRequest request, CalendarFeedService service, CancellationToken ct) => service.GetAsync(id, BaseUrl(request), ct))
            .WithSummary("One calendar feed.");

        feeds.MapPost("/", async (CalendarFeedInputDto input, HttpRequest request, CalendarFeedService service, CancellationToken ct) =>
            {
                var errors = CalendarFeedRules.Check(input);
                if (errors.Count > 0) return Results.ValidationProblem(errors);

                return Results.Ok(await service.CreateAsync(input, BaseUrl(request), ct));
            })
            .WithSummary("Make a calendar feed from sources: events of a group (narrowed by subgroups, members, invitation, name, exams, cancelled), birthdays, club anniversaries or belt anniversaries of members. The answer has the address (url) to give to calendar apps.");

        feeds.MapPut("/{id:int}", async (int id, CalendarFeedInputDto input, HttpRequest request, CalendarFeedService service, CancellationToken ct) =>
            {
                var errors = CalendarFeedRules.Check(input);
                if (errors.Count > 0) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(id, input, BaseUrl(request), ct));
            })
            .WithSummary("Replace the name and sources of a feed. The address stays the same.");

        feeds.MapPost("/{id:int}/regenerate", (int id, HttpRequest request, CalendarFeedService service, CancellationToken ct) =>
                service.RegenerateTokenAsync(id, BaseUrl(request), ct))
            .WithSummary("Give a feed a new secret address; the old one stops working (apps that subscribed must be given the new one).");

        feeds.MapDelete("/{id:int}", async (int id, CalendarFeedService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a feed; its address stops working.");

        return app;
    }

    private static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";
}
