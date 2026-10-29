using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// The calendar feeds: administrators define them (<c>/api/calendar-feeds</c>), calendar apps subscribe to the ICS address of each
/// (<c>/calendar-feeds/{token}.ics</c>). The ICS address has no login, because calendar apps cannot send one: the secret token in it is the key.
/// </summary>
public static class CalendarFeedEndpoints
{
    public const string IcsRoute = "/calendar-feeds/{token}.ics";
    public const string RateLimitPolicy = "calendar-feed";

    public static IEndpointRouteBuilder MapCalendarFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(IcsRoute, async (string token, CalendarFeedService service, HttpContext context, ILoggerFactory loggers, CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Kyuplan.Api.CalendarFeedAccess");
                var ip = context.Connection.RemoteIpAddress;
                var feed = await service.RenderAsync(token, ct);
                if (feed is null)
                {
                    log.LogWarning("Calendar feed request with an unknown token from {Ip}.", ip);
                    return Results.NotFound();
                }

                log.LogInformation("Calendar feed {Feed} read from {Ip} ({UserAgent}).", feed.Value.Name, ip, context.Request.Headers.UserAgent.ToString());
                context.Response.Headers.CacheControl = "private, max-age=300";
                return Results.Text(feed.Value.Ics, "text/calendar", System.Text.Encoding.UTF8);
            })
            .WithTags("Calendar feeds")
            .WithSummary("The calendar of a feed as an ICS file, to subscribe to in a calendar app. No login: the token in the address is the key. Unknown token: 404.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);

        var feeds = app.MapGroup("/api/calendar-feeds").WithTags("Calendar feeds").RequirePermission(Permission.CalendarFeeds);

        feeds.MapGet("/", (CalendarFeedService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("All calendar feeds with their sources (not their address: it is told only in the answers of POST and regenerate); brokenSources lists the positions of the sources whose group does not exist any more (the ICS leaves them out).");

        feeds.MapGet("/{id:int}", (int id, CalendarFeedService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("One calendar feed.");

        feeds.MapPost("/", async (CalendarFeedInputDto input, HttpRequest request, IOptions<ApiSecurityOptions> api, CalendarFeedService service, CancellationToken ct) =>
            {
                var errors = CalendarFeedRules.Check(input);
                if (errors.Count > 0) return Results.ValidationProblem(errors);

                return Results.Ok(await service.CreateAsync(input, BaseUrl(request, api.Value), ct));
            })
            .Audited(AuditAreas.CalendarFeeds, AuditActions.Create)
            .WithSummary("Make a calendar feed from sources: events of a group (narrowed by subgroups, members, invitation, name, exams, cancelled), birthdays, club anniversaries or belt anniversaries of members. The answer has the address (url) to give to calendar apps; it is told only this once, the server keeps just a hash of its secret.");

        feeds.MapPut("/{id:int}", async (int id, CalendarFeedInputDto input, CalendarFeedService service, CancellationToken ct) =>
            {
                var errors = CalendarFeedRules.Check(input);
                if (errors.Count > 0) return Results.ValidationProblem(errors);

                return Results.Ok(await service.UpdateAsync(id, input, ct));
            })
            .Audited(AuditAreas.CalendarFeeds, AuditActions.Update)
            .WithSummary("Replace the name and sources of a feed. The address stays the same (and is not in the answer).");

        feeds.MapPost("/{id:int}/regenerate", (int id, HttpRequest request, IOptions<ApiSecurityOptions> api, CalendarFeedService service, CancellationToken ct) =>
                service.RegenerateTokenAsync(id, BaseUrl(request, api.Value), ct))
            .Audited(AuditAreas.CalendarFeeds, AuditActions.Regenerate)
            .WithSummary("Give a feed a new secret address; the old one stops working (apps that subscribed must be given the new one). The new address (url) is told only in this answer.");

        feeds.MapDelete("/{id:int}", async (int id, CalendarFeedService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .Audited(AuditAreas.CalendarFeeds, AuditActions.Delete)
            .WithSummary("Delete a feed; its address stops working.");

        return app;
    }

    /// <summary>What feed addresses start with: <c>Api:PublicUrl</c> when it is set (and valid), else the request as it came in.</summary>
    internal static string BaseUrl(HttpRequest request, ApiSecurityOptions options) =>
        SettingsRules.IsPublicUrl(options.PublicUrl) && SettingsRules.NormalizePublicUrl(options.PublicUrl) is { } configured
            ? configured
            : $"{request.Scheme}://{request.Host}{request.PathBase}";
}
