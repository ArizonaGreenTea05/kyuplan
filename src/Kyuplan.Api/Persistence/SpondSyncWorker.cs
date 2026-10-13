using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Spond;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// Runs <see cref="SpondSync"/> when the API starts and then every <see cref="SyncOptions.Interval"/>, so that members who joined or
/// left and exam invitations that changed on Spond find their way into the database without anybody asking. A failed run (Spond
/// unreachable, wrong login) is logged and tried again at the next one.
/// </summary>
public sealed class SpondSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<SpondSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        try
        {
            await SyncAsync(stoppingToken);
            if (settings.Interval <= TimeSpan.Zero) return;

            using var timer = new PeriodicTimer(settings.Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SyncAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SpondSync>().SyncAllAsync(cancellationToken);
        }
        catch (SpondUnavailableException ex)
        {
            logger.LogWarning("Could not sync the members and exams with Spond: {Message}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Syncing the members and exams with Spond failed.");
        }
    }
}
