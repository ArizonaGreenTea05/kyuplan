namespace Kyuplan.Api.Infrastructure;

/// <summary>Restarts the application so that changed settings apply: the options are read once, when the application is built.</summary>
public interface IAppRestarter
{
    /// <summary>Restarts soon, after the answer to the request that asked for it has been sent.</summary>
    void Request();
}

/// <summary>
/// Stops the running application and lets <c>Program</c> build and start a new one in the same process (the same arguments, the
/// settings read anew), so that nothing outside the process has to restart it.
/// </summary>
public sealed class AppRestarter(IHostApplicationLifetime lifetime) : IAppRestarter
{
    /// <summary>Set when the application stopped to be started again, not to end.</summary>
    internal static volatile bool Requested;

    public void Request()
    {
        Requested = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // the answer to the request goes out first
            lifetime.StopApplication();
        });
    }
}

/// <summary>Whether the API still has to be set up: it has no user yet, so nobody can sign in. The first start's answers create the first administrator.</summary>
public sealed class SetupState
{
    private volatile bool _needsSetup;

    /// <summary>Held while the first administrator is created, so that two visitors cannot both do it.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool NeedsSetup => _needsSetup;

    public void Set(bool needsSetup) => _needsSetup = needsSetup;
}
