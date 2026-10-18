using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Backup;

/// <summary>Updates one backup database and remembers how it went; used by the worker and by the "run now" button.</summary>
public sealed class BackupRunner(IServiceScopeFactory scopes, BackupTargetStore store, ILogger<BackupRunner> logger)
{
    /// <summary>Copies all data into the target. A failure is not thrown: it is the target's last result. <c>null</c> when there is no such target.</summary>
    public async Task<BackupTargetDto?> RunAsync(int id, CancellationToken cancellationToken)
    {
        if (store.Find(id) is not { } target) return null;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BackupService>().CopyToAsync(target, cancellationToken);
            store.RecordRun(id, true, null);
            logger.LogInformation("The backup database '{Name}' is up to date.", target.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message goes to the page: what the database system says about its connection, not a stack.
            store.RecordRun(id, false, ex.InnerException?.Message ?? ex.Message);
            logger.LogError(ex, "Updating the backup database '{Name}' failed.", target.Name);
        }

        return store.Find(id);
    }
}

/// <summary>Looks every minute for backup databases whose interval is over and updates them one after the other.</summary>
public sealed class BackupWorker(BackupRunner runner, BackupTargetStore store, IOptions<BackupOptions> options, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        try
        {
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                foreach (var target in store.Due()) await runner.RunAsync(target.Id, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }
}
