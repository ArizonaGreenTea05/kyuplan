using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using static Kyuplan.Tests.SyncTestClub;

namespace Kyuplan.Tests;

/// <summary>What the sync status remembers of the runs, what the metrics say about them, and that <see cref="SpondSync"/> reports to it.</summary>
public sealed class SyncStatusTrackerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(PinnedNow);
    private readonly SyncStatusTracker _tracker;

    public SyncStatusTrackerTests() => _tracker = new SyncStatusTracker(_time);

    public void Dispose() => _tracker.Dispose();

    private void Run(bool succeed, TimeSpan took, string error = "boom")
    {
        var run = _tracker.Begin();
        _time.Advance(took);
        if (succeed) run.Succeed();
        else run.Fail(new InvalidOperationException(error));
    }

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    [Fact]
    public void Before_any_run_there_is_nothing_to_show()
    {
        var status = _tracker.Snapshot(true, Interval);

        Assert.True(status.Enabled);
        Assert.Equal(15, status.IntervalMinutes);
        Assert.False(status.IsRunning);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastSuccess);
        Assert.Null(status.LastFailure);
        Assert.Empty(status.Recent);
    }

    [Fact]
    public void A_run_that_worked_is_remembered_with_its_start_and_duration()
    {
        Run(true, TimeSpan.FromSeconds(2.5));

        var status = _tracker.Snapshot(true, Interval);
        Assert.Equal(PinnedNow, status.LastSuccess!.StartedAt);
        Assert.Equal(2.5, status.LastSuccess.DurationSeconds);
        Assert.True(status.LastSuccess.Succeeded);
        Assert.Null(status.LastSuccess.Error);
        Assert.Single(status.Recent);
        Assert.Null(status.LastFailure);
    }

    [Fact]
    public void Failures_are_counted_in_a_row_until_a_run_works()
    {
        Run(false, TimeSpan.FromSeconds(1), "Spond said no");
        Run(false, TimeSpan.FromSeconds(1), "Spond said no again");

        var failing = _tracker.Snapshot(true, Interval);
        Assert.Equal(2, failing.ConsecutiveFailures);
        Assert.Equal("Spond said no again", failing.LastFailure!.Error);
        Assert.False(failing.LastFailure.Succeeded);
        Assert.Null(failing.LastSuccess);

        Run(true, TimeSpan.FromSeconds(1));

        var healed = _tracker.Snapshot(true, Interval);
        Assert.Equal(0, healed.ConsecutiveFailures);
        Assert.NotNull(healed.LastSuccess);
        Assert.Equal("Spond said no again", healed.LastFailure!.Error); // what failed last stays visible
    }

    [Fact]
    public void The_runs_are_listed_newest_first_and_only_the_latest_twenty_are_kept()
    {
        for (var i = 0; i < 25; i++) Run(i % 2 == 0, TimeSpan.FromSeconds(i + 1));

        var recent = _tracker.Snapshot(true, Interval).Recent;
        Assert.Equal(20, recent.Count);
        Assert.Equal(25.0, recent[0].DurationSeconds);
        Assert.Equal(6.0, recent[^1].DurationSeconds);
        Assert.Equal(recent.OrderByDescending(r => r.StartedAt), recent);
    }

    [Fact]
    public void A_run_in_progress_is_reported_and_one_that_was_cancelled_leaves_no_trace()
    {
        var run = _tracker.Begin();
        Assert.True(_tracker.Snapshot(true, Interval).IsRunning);

        run.Abandon();

        var status = _tracker.Snapshot(true, Interval);
        Assert.False(status.IsRunning);
        Assert.Empty(status.Recent);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public void A_run_ends_only_once()
    {
        var run = _tracker.Begin();
        run.Succeed();
        run.Fail(new InvalidOperationException("late"));
        run.Abandon();

        var status = _tracker.Snapshot(true, Interval);
        Assert.Single(status.Recent);
        Assert.False(status.IsRunning);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public void Two_runs_at_once_keep_the_page_running_until_both_ended()
    {
        var first = _tracker.Begin();
        var second = _tracker.Begin();
        first.Succeed();
        Assert.True(_tracker.Snapshot(true, Interval).IsRunning);

        second.Succeed();
        Assert.False(_tracker.Snapshot(true, Interval).IsRunning);
    }

    [Fact]
    public void A_very_long_error_is_cut_and_an_error_without_text_is_named_by_its_type()
    {
        var long_ = _tracker.Begin();
        long_.Fail(new InvalidOperationException(new string('x', 2000)));
        var empty = _tracker.Begin();
        empty.Fail(new InvalidOperationException(""));

        var recent = _tracker.Snapshot(true, Interval).Recent;
        Assert.Equal(nameof(InvalidOperationException), recent[0].Error);
        Assert.Equal(500, recent[1].Error!.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(15, 15)]
    [InlineData(0.5, 1)] // a sub-minute wait must not show as "only once"
    public void The_interval_is_shown_in_whole_minutes_and_zero_means_only_once(double minutes, int expected)
    {
        Assert.Equal(expected, _tracker.Snapshot(true, TimeSpan.FromMinutes(minutes)).IntervalMinutes);
    }

    [Fact]
    public void A_switched_off_sync_is_reported_as_such()
    {
        Assert.False(_tracker.Snapshot(false, Interval).Enabled);
    }

    [Fact]
    public void The_metrics_count_the_runs_by_outcome_and_tell_when_it_last_worked()
    {
        var counts = new Dictionary<string, long>();
        var durations = new List<double>();
        var gauges = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SyncStatusTracker.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "kyuplan.sync.runs") counts[(string)tags[0].Value!] = counts.GetValueOrDefault((string)tags[0].Value!) + value;
            else gauges[instrument.Name] = value;
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => gauges[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => durations.Add(value));
        listener.Start();

        Run(true, TimeSpan.FromSeconds(2));
        Run(false, TimeSpan.FromSeconds(4));
        listener.RecordObservableInstruments();

        Assert.Equal(1, counts["success"]);
        Assert.Equal(1, counts["failure"]);
        Assert.Equal([2.0, 4.0], durations);
        Assert.Equal(PinnedNow.ToUnixTimeSeconds(), gauges["kyuplan.sync.last_success_timestamp"]);
        Assert.Equal(1, gauges["kyuplan.sync.consecutive_failures"]);
    }

    [Fact]
    public void The_last_success_gauge_is_zero_until_a_run_worked()
    {
        var value = -1L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "kyuplan.sync.last_success_timestamp") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => value = v);
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Equal(0, value);
    }

    // --- SpondSync reports to it ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_sync_reports_its_success_and_a_failed_one_its_error()
    {
        using var club = new SyncTestClub();
        club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        var tracker = club.Services.GetRequiredService<SyncStatusTracker>();

        await club.SyncAsync();
        Assert.NotNull(tracker.Snapshot(true, Interval).LastSuccess);

        club.Spond.Unreachable = true;
        await Assert.ThrowsAsync<SpondUnavailableException>(club.SyncAsync);

        var status = tracker.Snapshot(true, Interval);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Contains("Spond is unreachable", status.LastFailure!.Error);
        Assert.False(status.IsRunning);
        Assert.Equal(2, status.Recent.Count);
    }

    [Fact]
    public async Task A_cancelled_sync_is_neither_a_success_nor_a_failure()
    {
        using var club = new SyncTestClub();
        club.Spond.SetGroups(Group(Dojo, Person("m-anna", "Anna Beck")));
        var tracker = club.Services.GetRequiredService<SyncStatusTracker>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            club.RunAsync(sp => sp.GetRequiredService<SpondSync>().SyncAllAsync(cancelled.Token)));

        var status = tracker.Snapshot(true, Interval);
        Assert.Empty(status.Recent);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.False(status.IsRunning);
    }
}
