namespace Kyuplan.Contracts;

/// <summary>One run of the sync with the data source (Spond), as the sync status lists it.</summary>
/// <param name="StartedAt">When the run began.</param>
/// <param name="DurationSeconds">How long it took.</param>
/// <param name="Succeeded">Whether it finished without an error.</param>
/// <param name="Error">What went wrong; <c>null</c> for a run that succeeded.</param>
public sealed record SyncRunDto(DateTimeOffset StartedAt, double DurationSeconds, bool Succeeded, string? Error);

/// <summary>How the sync with the data source is doing: the last runs, when it last worked and what failed since.</summary>
/// <param name="Enabled">Whether the API syncs by itself (<c>Sync:Enabled</c>); when not, only changes in the manual data start a run.</param>
/// <param name="IntervalMinutes">The wait between two runs; 0 = only once, when the API starts.</param>
/// <param name="IsRunning">Whether a run is in progress now.</param>
/// <param name="ConsecutiveFailures">How many runs in a row failed since the last one that worked.</param>
/// <param name="LastSuccess">The last run that worked; <c>null</c> if none did since the API started.</param>
/// <param name="LastFailure">The last run that failed; <c>null</c> if none did since the API started.</param>
/// <param name="Recent">The latest runs, newest first. Kept in memory: an API restart starts the list anew.</param>
public sealed record SyncStatusDto(
    bool Enabled,
    int IntervalMinutes,
    bool IsRunning,
    int ConsecutiveFailures,
    SyncRunDto? LastSuccess,
    SyncRunDto? LastFailure,
    IReadOnlyList<SyncRunDto> Recent);
