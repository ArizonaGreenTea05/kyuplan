using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One run of the sync in the list of recent runs.</summary>
public sealed class SyncRunRow(SyncRunDto run)
{
    public bool Succeeded => run.Succeeded;

    /// <summary>"Sat, 26 Sep · 10:00 · 1.2 s · succeeded", or "... · failed: reason".</summary>
    public string Text => string.Format(
        run.Succeeded ? Strings.SyncStatus_RunOk : Strings.SyncStatus_RunFailed,
        Display.EventStart(run.StartedAt.ToLocalTime()), SyncStatusViewModel.Seconds(run.DurationSeconds), run.Error);
}

/// <summary>
/// The sync status page: when the sync with Spond last worked, how long its runs take and what went wrong. Read-only; it reads the status again
/// every time it is shown. Used by both clients.
/// </summary>
public sealed partial class SyncStatusViewModel(ShellViewModel shell, IPlannerApi api) : PageViewModel(shell)
{
    public override string Title => Strings.Nav_SyncStatus;

    protected override string? ViewPermission => Permission.SyncView;

    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoaded), nameof(IsFailing), nameof(Headline), nameof(IntervalText), nameof(LastSuccessText), nameof(LastFailureText), nameof(Runs), nameof(HasRuns))]
    private SyncStatusDto? _status;

    public bool HasLoaded => Status is not null;

    public bool IsFailing => Status is { ConsecutiveFailures: > 0 };

    public bool HasRuns => Runs.Count > 0;

    public IReadOnlyList<SyncRunRow> Runs => Status is null ? [] : [.. Status.Recent.Select(r => new SyncRunRow(r))];

    /// <summary>The one line that says whether things are fine.</summary>
    public string Headline => Status switch
    {
        null => "",
        { ConsecutiveFailures: > 0 } s => string.Format(Strings.SyncStatus_Failing, s.ConsecutiveFailures),
        { IsRunning: true } => Strings.SyncStatus_Running,
        { LastSuccess: null } => Strings.SyncStatus_NoRunYet,
        _ => Strings.SyncStatus_Healthy,
    };

    /// <summary>"Runs every 15 minutes", "Runs only once, when the API starts" or "Automatic sync is switched off".</summary>
    public string IntervalText => Status switch
    {
        null => "",
        { Enabled: false } => Strings.SyncStatus_Off,
        { IntervalMinutes: <= 0 } => Strings.SyncStatus_Once,
        { IntervalMinutes: var minutes } => string.Format(Strings.SyncStatus_Every, minutes),
    };

    public string LastSuccessText => Status switch
    {
        null => "",
        { LastSuccess: { } run } => string.Format(Strings.SyncStatus_LastSuccess, Display.EventStart(run.StartedAt.ToLocalTime()), Seconds(run.DurationSeconds)),
        _ => Strings.SyncStatus_NeverSucceeded,
    };

    public string LastFailureText => Status is { LastFailure: { } run }
        ? string.Format(Strings.SyncStatus_LastFailure, Display.EventStart(run.StartedAt.ToLocalTime()), run.Error)
        : "";

    /// <summary>Seconds with one decimal in the current culture: "1.2" or "1,2".</summary>
    public static string Seconds(double seconds) => seconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    protected override void OnPermissionsChanged()
    {
        if (!CanView) Status = null;

        base.OnPermissionsChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Asks the API again, e.g. after a failed load or to see a run that just ended.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync() => await LoadAsync(async ct => Status = await api.GetSyncStatusAsync(ct));
}
