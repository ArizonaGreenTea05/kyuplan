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

        app.MapPost("/api/sync/run", async (SpondSync sync, bool? confirmRemoval, CancellationToken ct) =>
            {
                await sync.SyncAllAsync(ct, confirmRemoval ?? false);
                return Results.NoContent();
            })
            .WithTags("Sync")
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Run the sync with the data source now. A run that would remove more than Sync:MaxRemovedShare (or Sync:MaxRemovedMembers) of the members is refused (409); confirmRemoval=true applies it anyway: they are hidden, and deleted with their belts, payments, comments and results after Sync:RemovedMemberRetention.")
            .RequirePermission(Permission.SettingsEdit);

        return app;
    }
}
