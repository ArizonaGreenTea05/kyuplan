using System.Reflection;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

public static class PlannerEndpoints
{
    /// <summary>Longest analysable window; keeps a single request from pulling years of Spond history.</summary>
    internal const int MaxRangeDays = 5 * 366;

    public static IEndpointRouteBuilder MapPlannerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (IDataSource source, IOptions<StatisticsOptions> options, ClientInstaller installer, DocumentationFiles documentation, IConfiguration configuration) => new ServerInfoDto(
                "Kyuplan",
                typeof(PlannerEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0",
                source.IsDemo,
                options.Value.DefaultMinAttendancePercent,
                options.Value.DefaultMinInvitedEvents,
                options.Value.TimeZone,
                installer.FindAll().ToDictionary(i => i.Key, i => i.Value.Version),
                documentation.Exists,
                configuration["Repository:Url"],
                options.Value.ExamMinAverageStars,
                source.Kind))
            .WithSummary("Server metadata and defaults for the filter controls.");

        api.MapGet("/groups", (StatisticsService service, CancellationToken ct) => service.GetGroupsAsync(ct))
            .WithSummary("Spond groups (with subgroups) the configured account can see.")
            .RequirePermission(Permission.ReadsSpond);

        api.MapGet("/dashboard", (
                string groupId, string? subGroupId, int? days, StatisticsService service, CancellationToken ct) =>
                service.GetDashboardAsync(groupId, subGroupId, days, ct))
            .WithSummary("Key figures of the last weeks, upcoming events and members to look at.")
            .RequirePermission(Permission.DashboardView);

        api.MapGet("/events", (string groupId, StatisticsService service, CancellationToken ct) =>
                service.GetEventChoicesAsync(groupId, ct))
            .WithSummary("Events of the last year and the next six months, and the time span the statistics page preselects (exam to exam, or the last six months).")
            .RequirePermission(Permission.ReadsSpond);

        api.MapGet("/statistics/attendance", async (
                [AsParameters] AttendanceQuery query, StatisticsService service, RatingService ratings, CancellationToken ct) =>
            {
                if (Validate(query) is { } errors) return Results.ValidationProblem(errors);

                var filter = await service.ResolveAsync(query, ct);
                // Dates were checked above; the days of two events are only known now.
                if (HasEventSpan(query) && ValidateSpan(filter, nameof(AttendanceQuery.FromEventId), "fromEventId must not be after toEventId.") is { } spanErrors)
                    return Results.ValidationProblem(spanErrors);

                return Results.Ok(await service.GetAttendanceAsync(filter, await ratings.GetBookAsync(filter.GroupId, ct), ct));
            })
            .WithSummary("Per-member and per-event attendance for a time span (two dates or two events), with qualification against a minimum percentage.")
            .RequirePermission(Permission.StatisticsView);

        api.MapPost("/refresh", (IDataSource source, TimeProvider time) =>
            {
                // Every drop costs a login and requests at Spond, which answers an account that asks too often with nothing: one drop per
                // cooldown is enough for everybody, a request in between finds the data fresh already.
                var now = time.GetTimestamp();
                var last = Interlocked.Read(ref _lastRefresh);
                if (last == 0 || time.GetElapsedTime(last, now) >= RefreshCooldown)
                {
                    Interlocked.Exchange(ref _lastRefresh, now);
                    source.InvalidateCache();
                }

                return Results.NoContent();
            })
            .WithSummary("Drop cached Spond data so the next request fetches fresh data (at most once every 30 seconds; a request in between changes nothing). Needs a permission that reads Spond data.")
            .RequirePermission(Permission.ReadsSpond);

        return app;
    }

    private const string SpanKey = "TimeSpan";

    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(30);
    private static long _lastRefresh;

    private static bool HasDateSpan(AttendanceQuery query) => query.From is not null || query.To is not null;

    private static bool HasEventSpan(AttendanceQuery query) =>
        !string.IsNullOrWhiteSpace(query.FromEventId) || !string.IsNullOrWhiteSpace(query.ToEventId);

    /// <summary>Everything that can be checked without data. The days of an event span follow in <see cref="ValidateSpan"/>.</summary>
    internal static Dictionary<string, string[]>? Validate(AttendanceQuery query)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(query.GroupId))
            errors[nameof(query.GroupId)] = ["groupId is required."];

        if (HasDateSpan(query) && HasEventSpan(query))
        {
            errors[SpanKey] = ["Give the time span either as from and to, or as fromEventId and toEventId, not both."];
        }
        else if (HasDateSpan(query))
        {
            if (query is { From: { } from, To: { } to })
                AddSpanErrors(errors, from, to, nameof(query.From), "from must not be after to.");
            else
                errors[SpanKey] = ["from and to must be given together."];
        }
        else if (HasEventSpan(query))
        {
            if (string.IsNullOrWhiteSpace(query.FromEventId) || string.IsNullOrWhiteSpace(query.ToEventId))
                errors[SpanKey] = ["fromEventId and toEventId must be given together."];
        }
        else
        {
            errors[SpanKey] = ["The time span is required: from and to, or fromEventId and toEventId."];
        }

        if (query.MinAttendancePercent is < 0 or > 100)
            errors[nameof(query.MinAttendancePercent)] = ["minAttendancePercent must be between 0 and 100."];
        if (query.MinInvitedEvents is < 1 or > 1000)
            errors[nameof(query.MinInvitedEvents)] = ["minInvitedEvents must be between 1 and 1000."];

        return errors.Count == 0 ? null : errors;
    }

    /// <summary>Order and length of a time span that was resolved from two events.</summary>
    internal static Dictionary<string, string[]>? ValidateSpan(AttendanceFilter filter, string key, string orderMessage)
    {
        var errors = new Dictionary<string, string[]>();
        AddSpanErrors(errors, filter.From, filter.To, key, orderMessage);
        return errors.Count == 0 ? null : errors;
    }

    private static void AddSpanErrors(Dictionary<string, string[]> errors, DateOnly from, DateOnly to, string key, string orderMessage)
    {
        if (from > to)
            errors[key] = [orderMessage];
        else if (to.DayNumber - from.DayNumber > MaxRangeDays)
            errors[key] = [$"The time span must not exceed {MaxRangeDays} days."];
    }
}
