using Kyuplan.Client.Api;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// Waits for the API to come back after it restarted itself to apply saved settings. It asks the open setup address (which needs no login) until
/// something answers; the API needs a few seconds. Used by the setup screen and the settings page.
/// </summary>
public sealed class ApiRestartWatcher(IPlannerApi api)
{
    public const int MaxAttempts = 60;

    /// <summary>How long to wait between two questions; tests set it to nothing.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Waits until the API answers again. The first question comes only after one interval, because the API is still answering as it stops. <c>false</c> when it did not come back in time.</summary>
    public async Task<bool> WaitUntilBackAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await Delay(Interval, cancellationToken);
            try
            {
                await api.GetSetupAsync(cancellationToken);
                return true;
            }
            catch (PlannerApiException)
            {
                // Not up yet.
            }
        }

        return false;
    }
}
