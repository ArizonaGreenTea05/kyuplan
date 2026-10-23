using System.Diagnostics.Metrics;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// Remembers how the runs of <see cref="SpondSync.SyncAllAsync"/> went, for the sync status page (<c>/api/sync/status</c>) and the metrics
/// (<c>/metrics</c>). It lives in memory only: after an API restart the list starts anew.
/// </summary>
public sealed class SyncStatusTracker : IDisposable
{
    public const string MeterName = "Kyuplan";

    private const int KeptRuns = 20;
    private const int MaxErrorLength = 500;

    private readonly TimeProvider _time;
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Lock _lock = new();
    private readonly LinkedList<SyncRunDto> _recent = new();
    private SyncRunDto? _lastSuccess;
    private SyncRunDto? _lastFailure;
    private int _consecutiveFailures;
    private bool _removalRefused;
    private int _running;

    public SyncStatusTracker(TimeProvider time)
    {
        _time = time;
        _runs = _meter.CreateCounter<long>("kyuplan.sync.runs", description: "Runs of the sync with the data source, by outcome.");
        _duration = _meter.CreateHistogram<double>("kyuplan.sync.duration", unit: "s", description: "How long a run of the sync took.");
        _meter.CreateObservableGauge("kyuplan.sync.last_success_timestamp", () => LastSuccessUnixSeconds, unit: "s",
            description: "When the last successful run began (Unix time); 0 if none did yet.");
        _meter.CreateObservableGauge("kyuplan.sync.consecutive_failures", () => ConsecutiveFailures,
            description: "Runs in a row that failed since the last one that worked.");
    }

    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    private long LastSuccessUnixSeconds
    {
        get { lock (_lock) return _lastSuccess?.StartedAt.ToUnixTimeSeconds() ?? 0; }
    }

    /// <summary>Starts counting a run. Call <see cref="Run.Succeed"/>, <see cref="Run.Fail"/> or <see cref="Run.Abandon"/> when it ends.</summary>
    public Run Begin()
    {
        lock (_lock) _running++;
        return new Run(this, _time.GetUtcNow(), _time.GetTimestamp());
    }

    public SyncStatusDto Snapshot(bool enabled, TimeSpan interval)
    {
        lock (_lock)
            return new SyncStatusDto(enabled, interval > TimeSpan.Zero ? (int)Math.Ceiling(interval.TotalMinutes) : 0, _running > 0,
                _consecutiveFailures, _lastSuccess, _lastFailure, [.. _recent], _removalRefused);
    }

    public void Dispose() => _meter.Dispose();

    private void Finish(SyncRunDto? run)
    {
        lock (_lock)
        {
            _running--;
            if (run is null) return;

            _recent.AddFirst(run);
            _removalRefused = run.RemovalRefused; // a run after the refused one (it worked, or failed otherwise) settles it
            if (_recent.Count > KeptRuns) _recent.RemoveLast();

            if (run.Succeeded)
            {
                _lastSuccess = run;
                _consecutiveFailures = 0;
            }
            else
            {
                _lastFailure = run;
                _consecutiveFailures++;
            }
        }

        if (run is null) return;
        _runs.Add(1, new KeyValuePair<string, object?>("outcome", run.Succeeded ? "success" : "failure"));
        _duration.Record(run.DurationSeconds);
    }

    /// <summary>One run in progress.</summary>
    public sealed class Run
    {
        private readonly SyncStatusTracker _owner;
        private readonly DateTimeOffset _startedAt;
        private readonly long _startTimestamp;
        private bool _done;

        internal Run(SyncStatusTracker owner, DateTimeOffset startedAt, long startTimestamp)
        {
            _owner = owner;
            _startedAt = startedAt;
            _startTimestamp = startTimestamp;
        }

        public void Succeed() => End(null);

        public void Fail(Exception error)
        {
            var text = error.Message.Length > MaxErrorLength ? error.Message[..MaxErrorLength] : error.Message;
            End(text.Length == 0 ? error.GetType().Name : text, error is SyncRefusedException);
        }

        /// <summary>The run was cancelled (the API stops): it is neither a success nor a failure.</summary>
        public void Abandon()
        {
            if (_done) return;
            _done = true;
            _owner.Finish(null);
        }

        private void End(string? error, bool refused = false)
        {
            if (_done) return;
            _done = true;
            var seconds = _owner._time.GetElapsedTime(_startTimestamp).TotalSeconds;
            _owner.Finish(new SyncRunDto(_startedAt, Math.Round(seconds, 3), error is null, error, refused));
        }
    }
}
