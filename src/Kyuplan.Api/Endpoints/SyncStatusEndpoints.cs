using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>How the sync with the data source is doing (<c>/api/sync/status</c>).</summary>
public static class SyncStatusEndpoints
{
    public static IEndpointRouteBuilder MapSyncStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/status", (SyncStatusTracker tracker, IOptions<SyncOptions> options) =>
                Results.Ok(tracker.Snapshot(options.Value.Enabled, options.Value.Interval)))
            .WithTags("Sync")
            .Produces<SyncStatusDto>()
            .WithSummary("The last runs of the sync with Spond (or the other data source): when it last worked, how long runs took and what failed. Kept in memory, so it starts anew with the API.")
            .RequirePermission(Permission.SyncView);

        return app;
    }
}
