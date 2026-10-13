using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

public class AttendanceStatisticsViewModelTests
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(350);

    private readonly FakePlannerApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly ShellViewModel _shell;
    private readonly AttendanceStatisticsViewModel _vm;

    public AttendanceStatisticsViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api);
        _vm = new AttendanceStatisticsViewModel(_shell, _api, _time);
    }

    private async Task ActivateAsync()
    {
        await _vm.ActivateAsync();
        await Eventually(() => !_vm.IsBusy && _api.AttendanceRequests.Count > 0);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    [Fact]
    public async Task Activating_picks_the_first_group_adopts_server_defaults_and_loads()
    {
        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Equal("g1", request.GroupId);
        Assert.Equal(70, request.MinAttendancePercent);   // from the server, not the VM's built-in 75
        Assert.Equal(4, request.MinInvitedEvents);
        Assert.Equal(4, _vm.VisibleMembers.Count);
        Assert.Equal("Meeting 70 %", _vm.Kpis[0].Label);
        Assert.Equal("1 of 3", _vm.Kpis[0].Value); // Clara has no verdict and is not part of "assessed"
        Assert.True(_vm.HasResults);
        Assert.False(_vm.HasNoResults);
    }

    [Fact]
    public async Task The_time_span_starts_as_the_events_the_server_suggests()
    {
        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Equal("exam-1", request.FromEventId);
        Assert.Equal("exam-2", request.ToEventId);
        Assert.Null(request.From); // the server resolves the events, the dates are not sent
        Assert.Null(request.To);
        Assert.Equal(["g1"], _api.EventChoiceRequests);

        Assert.True(_vm.SpanByEvents);
        Assert.Equal("exam-1", _vm.FromEventId);
        Assert.Equal("exam-2", _vm.ToEventId);
        Assert.Equal(["", "exam-1", "train-1", "train-2", "exam-2"], _vm.EventOptions.Select(o => o.Id));
        Assert.Equal("Gürtelprüfung · Sat, 28 Mar · 10:00", _vm.EventOptions[1].Label);
        Assert.Equal("Choose an event …", _vm.EventOptions[0].Label);

        // The advanced date fields show what the events resolved to.
        Assert.Equal(new DateTime(2026, 3, 28), _vm.From);
        Assert.Equal(new DateTime(2026, 9, 26), _vm.To);
        Assert.Contains(
            $"between \"Gürtelprüfung\" ({Display.Date(new DateOnly(2026, 3, 28))}) and \"Gürtelprüfung\" ({Display.Date(new DateOnly(2026, 9, 26))})",
            _vm.FilterSummary);
        Assert.Equal("The events chosen above define the time span. Changing a date replaces them.", _vm.SpanHint);
    }

    [Fact]
    public async Task Without_a_suggestion_the_last_six_months_are_requested()
    {
        _api.EventChoices = _api.EventChoices with { SuggestedFromEventId = null, SuggestedToEventId = null };

        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Null(request.FromEventId);
        Assert.Null(request.ToEventId);
        Assert.Equal(new DateOnly(2025, 12, 31), request.From); // last six months incl. today
        Assert.Equal(new DateOnly(2026, 6, 30), request.To);
        Assert.False(_vm.SpanByEvents);
        Assert.Equal("", _vm.FromEventId);
        Assert.Equal("", _vm.ToEventId);
        Assert.Equal("These dates define the time span. Choosing an event above replaces them.", _vm.SpanHint);
    }

    [Fact]
    public async Task Choosing_another_event_reloads_with_the_new_span_and_keeps_the_other_filters()
    {
        await ActivateAsync();

        _vm.ToEventId = "train-2";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal("exam-1", request.FromEventId);
        Assert.Equal("train-2", request.ToEventId);
        Assert.Equal(70, request.MinAttendancePercent);
        Assert.Equal(new DateTime(2026, 7, 2), _vm.To);
        Assert.Equal(["g1"], _api.EventChoiceRequests); // changing a filter does not fetch the event list again
    }

    [Fact]
    public async Task Changing_a_date_switches_to_dates_and_choosing_an_event_switches_back()
    {
        await ActivateAsync();

        _vm.From = new DateTime(2026, 5, 1);
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        var byDates = _api.AttendanceRequests[^1];
        Assert.Null(byDates.FromEventId);
        Assert.Null(byDates.ToEventId);
        Assert.Equal(new DateOnly(2026, 5, 1), byDates.From);
        Assert.Equal(new DateOnly(2026, 9, 26), byDates.To); // the other date is what the events resolved to
        Assert.False(_vm.SpanByEvents);
        Assert.DoesNotContain("between", _vm.FilterSummary);

        _vm.FromEventId = "train-1";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        var byEvents = _api.AttendanceRequests[^1];
        Assert.Equal("train-1", byEvents.FromEventId);
        Assert.Equal("exam-2", byEvents.ToEventId);
        Assert.Null(byEvents.From);
        Assert.True(_vm.SpanByEvents);
    }

    [Fact]
    public async Task Events_in_the_wrong_order_show_an_error_and_send_nothing()
    {
        await ActivateAsync();

        _vm.FromEventId = "exam-2";
        _vm.ToEventId = "exam-1";
        _time.Advance(DebounceWindow);
        await Eventually(() => _vm.FilterError is not null);

        Assert.Equal("The \"From\" event must not be after the \"To\" event.", _vm.FilterError);
        Assert.Single(_api.AttendanceRequests);
    }

    [Fact]
    public async Task Clearing_an_event_asks_for_both_and_sends_nothing()
    {
        await ActivateAsync();

        _vm.ToEventId = "";
        _time.Advance(DebounceWindow);
        await Eventually(() => _vm.FilterError is not null);

        Assert.Equal("Choose both events, or set the time span with dates under Advanced filters.", _vm.FilterError);
        Assert.Single(_api.AttendanceRequests);
    }

    [Fact]
    public async Task With_no_events_chosen_yet_the_first_pick_waits_for_the_second()
    {
        _api.EventChoices = _api.EventChoices with { SuggestedFromEventId = null, SuggestedToEventId = null };
        await ActivateAsync();

        _vm.FromEventId = "exam-1";
        _time.Advance(DebounceWindow);
        await Task.Delay(50);
        Assert.Single(_api.AttendanceRequests); // dates are still in effect, no request and no error yet
        Assert.False(_vm.SpanByEvents);
        Assert.Null(_vm.FilterError);

        _vm.ToEventId = "exam-2";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Assert.Equal("exam-1", _api.AttendanceRequests[^1].FromEventId);
        Assert.Equal("exam-2", _api.AttendanceRequests[^1].ToEventId);
        Assert.True(_vm.SpanByEvents);
    }

    [Fact]
    public async Task Refreshing_fetches_the_event_list_again_but_keeps_the_chosen_events()
    {
        await ActivateAsync();
        _vm.ToEventId = "train-2";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.Equal(["g1", "g1"], _api.EventChoiceRequests);
        Assert.Equal("train-2", _api.AttendanceRequests[^1].ToEventId);
    }

    [Fact]
    public async Task A_refresh_that_removes_a_chosen_event_falls_back_to_the_suggestion()
    {
        await ActivateAsync();
        _vm.ToEventId = "train-2";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        _api.EventChoices = _api.EventChoices with { Events = FakePlannerApi.SampleEvents.Where(e => e.Id != "train-2").ToList() };
        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.Equal("exam-2", _api.AttendanceRequests[^1].ToEventId);
        Assert.Null(_vm.FilterError);
    }

    [Fact]
    public async Task Server_side_filter_changes_reload_once_after_a_short_pause()
    {
        await ActivateAsync();

        _vm.MinAttendancePercent = 80;
        _vm.MinAttendancePercent = 85;
        _vm.MinInvitedEvents = 6;
        _time.Advance(DebounceWindow - TimeSpan.FromMilliseconds(1));
        Assert.Single(_api.AttendanceRequests); // still waiting

        _time.Advance(TimeSpan.FromMilliseconds(2));
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal(85, request.MinAttendancePercent);
        Assert.Equal(6, request.MinInvitedEvents);
    }

    [Fact]
    public async Task Event_name_and_subgroup_filters_are_sent_to_the_server()
    {
        await ActivateAsync();

        _vm.EventNameContains = " Training ";
        _vm.EventNameExcludes = "";
        _vm.SubGroupOptions.Single(o => o.Id == "sg1").IsSelected = true;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal("Training", request.EventNameContains);
        Assert.Null(request.EventNameExcludes);
        Assert.Equal(["sg1"], request.SubGroupIds ?? []);
    }

    [Fact]
    public async Task Several_subgroups_can_be_ticked_and_are_sent_together_after_one_pause()
    {
        _api.Groups[0] = _api.Groups[0] with { SubGroups = [new("sg1", "Kinder"), new("sg2", "Jugend"), new("sg3", "Erwachsene")] };
        await ActivateAsync();
        Assert.Equal(["sg1", "sg2", "sg3"], _vm.SubGroupOptions.Select(o => o.Id));
        Assert.Null(_api.AttendanceRequests[0].SubGroupIds); // nothing ticked = everyone
        Assert.True(_vm.IsEveryoneSelected);
        Assert.Equal("Everyone", _vm.SubGroupSummary);

        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.SubGroupOptions[0].IsSelected = true;
        _vm.SubGroupOptions[2].IsSelected = true;
        _time.Advance(DebounceWindow - TimeSpan.FromMilliseconds(1));
        Assert.Single(_api.AttendanceRequests); // each tick restarts the pause

        _time.Advance(TimeSpan.FromMilliseconds(2));
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Assert.Equal(["sg1", "sg3"], _api.AttendanceRequests[^1].SubGroupIds ?? []);
        Assert.Equal(["sg1", "sg3"], _vm.SelectedSubGroupIds);
        Assert.Equal("Kinder, Erwachsene", _vm.SubGroupSummary);
        Assert.False(_vm.IsEveryoneSelected);
        Assert.Contains(nameof(_vm.SubGroupSummary), changed);
        Assert.Contains(nameof(_vm.IsEveryoneSelected), changed);
    }

    [Fact]
    public async Task Choosing_everyone_unticks_every_subgroup_and_reloads_once()
    {
        _api.Groups[0] = _api.Groups[0] with { SubGroups = [new("sg1", "Kinder"), new("sg2", "Jugend")] };
        await ActivateAsync();
        foreach (var option in _vm.SubGroupOptions) option.IsSelected = true;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);
        Assert.Equal(["sg1", "sg2"], _api.AttendanceRequests[^1].SubGroupIds ?? []);

        _vm.ClearSubGroupsCommand.Execute(null);
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Assert.Null(_api.AttendanceRequests[^1].SubGroupIds);
        Assert.All(_vm.SubGroupOptions, o => Assert.False(o.IsSelected));
        Assert.True(_vm.IsEveryoneSelected);
        Assert.Equal("Everyone", _vm.SubGroupSummary);
    }

    [Fact]
    public async Task Resetting_the_filters_unticks_the_subgroups_too()
    {
        await ActivateAsync();
        _vm.SubGroupOptions.Single().IsSelected = true;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Assert.Null(_api.AttendanceRequests[^1].SubGroupIds);
        Assert.True(_vm.IsEveryoneSelected);
    }

    [Fact]
    public async Task Search_status_filter_and_sorting_work_locally_without_calling_the_api()
    {
        await ActivateAsync();

        Assert.Equal(["Clara", "Anna", "Dora", "Ben"], _vm.VisibleMembers.Select(m => m.Name)); // by rate, descending

        _vm.SearchText = "ann";
        Assert.Equal(["Anna"], _vm.VisibleMembers.Select(m => m.Name));
        Assert.Equal("1 of 4 members", _vm.MembersCaption);

        _vm.SearchText = "";
        _vm.StatusFilter = MemberStatusFilter.BelowThreshold;
        Assert.Equal(["Dora", "Ben"], _vm.VisibleMembers.Select(m => m.Name));

        _vm.StatusFilter = MemberStatusFilter.All;
        _vm.SortByCommand.Execute(MemberSortKey.Name);
        Assert.Equal(["Anna", "Ben", "Clara", "Dora"], _vm.VisibleMembers.Select(m => m.Name));
        _vm.SortByCommand.Execute(MemberSortKey.Name); // same column again flips the direction
        Assert.Equal(["Dora", "Clara", "Ben", "Anna"], _vm.VisibleMembers.Select(m => m.Name));

        _time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Single(_api.AttendanceRequests);
    }

    [Fact]
    public async Task Switching_the_group_reloads_for_it_and_resets_the_subgroups()
    {
        await ActivateAsync();
        _vm.SubGroupOptions.Single().IsSelected = true;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);
        Assert.Equal(["sg1"], _vm.SubGroupOptions.Select(o => o.Id));
        Assert.Equal(["sg1"], _api.AttendanceRequests[^1].SubGroupIds ?? []);

        _shell.SelectedGroupId = "g2";
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal("g2", request.GroupId);
        Assert.Null(request.SubGroupIds);
        Assert.Empty(_vm.SubGroupOptions);
        Assert.Empty(_vm.SelectedSubGroupIds);
        Assert.True(_vm.IsEveryoneSelected);
    }

    [Fact]
    public async Task Switching_the_group_loads_its_events_and_starts_from_its_suggestion_again()
    {
        await ActivateAsync();
        _vm.FromEventId = "train-1";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        _api.EventChoices = _api.EventChoices with { SuggestedFromEventId = "exam-1", SuggestedToEventId = "train-2" };
        _shell.SelectedGroupId = "g2";
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.Equal(["g1", "g2"], _api.EventChoiceRequests);
        var request = _api.AttendanceRequests[^1];
        Assert.Equal("g2", request.GroupId);
        Assert.Equal("exam-1", request.FromEventId);
        Assert.Equal("train-2", request.ToEventId);
    }

    [Fact]
    public async Task Range_presets_set_both_dates_and_reload_immediately()
    {
        await ActivateAsync();

        _vm.SetRangeCommand.Execute("year");
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Assert.Equal(new DateOnly(2026, 1, 1), _api.AttendanceRequests[^1].From);
        Assert.Equal(new DateOnly(2026, 6, 30), _api.AttendanceRequests[^1].To);
        Assert.Null(_api.AttendanceRequests[^1].FromEventId); // a quick range replaces the events
        Assert.False(_vm.SpanByEvents);

        _vm.SetRangeCommand.Execute("3m");
        await Eventually(() => _api.AttendanceRequests.Count == 3);
        Assert.Equal(new DateOnly(2026, 3, 31), _api.AttendanceRequests[^1].From); // three months back from 30 June, incl. today
    }

    [Fact]
    public async Task An_inverted_date_range_shows_an_error_and_sends_nothing()
    {
        await ActivateAsync();

        _vm.From = new DateTime(2026, 6, 30);
        _vm.To = new DateTime(2026, 1, 1);
        _time.Advance(DebounceWindow);
        await Eventually(() => _vm.FilterError is not null);

        Assert.Single(_api.AttendanceRequests);
    }

    [Fact]
    public async Task Reset_restores_the_default_filters_and_reloads()
    {
        await ActivateAsync();
        _vm.MinAttendancePercent = 95;
        _vm.EventNameContains = "x";
        _vm.SearchText = "zz";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal(70, request.MinAttendancePercent);
        Assert.Null(request.EventNameContains);
        Assert.Equal("", _vm.SearchText);
    }

    [Fact]
    public async Task Reset_returns_to_the_suggested_events_after_dates_were_chosen()
    {
        await ActivateAsync();
        _vm.SetRangeCommand.Execute("3m");
        await Eventually(() => _api.AttendanceRequests.Count == 2);
        Assert.False(_vm.SpanByEvents);

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal("exam-1", request.FromEventId);
        Assert.Equal("exam-2", request.ToEventId);
        Assert.Null(request.From);
        Assert.True(_vm.SpanByEvents);
    }

    [Fact]
    public async Task Reset_without_a_suggestion_returns_to_the_last_six_months()
    {
        _api.EventChoices = _api.EventChoices with { SuggestedFromEventId = null, SuggestedToEventId = null };
        await ActivateAsync();
        _vm.SetRangeCommand.Execute("year");
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Assert.Equal(new DateOnly(2025, 12, 31), _api.AttendanceRequests[^1].From);
        Assert.Equal(new DateOnly(2026, 6, 30), _api.AttendanceRequests[^1].To);
    }

    [Fact]
    public async Task A_failing_call_is_reported_and_retried_on_the_next_activation()
    {
        _api.AttendanceHandler = (_, _) => Task.FromException<AttendanceStatisticsDto>(new PlannerApiException("boom"));
        await _vm.ActivateAsync();
        await Eventually(() => _vm.HasError);
        Assert.Equal("boom", _vm.ErrorMessage);

        _api.AttendanceHandler = (filter, _) => Task.FromResult(FakePlannerApi.Statistics(filter));
        _vm.Deactivate();
        await _vm.ActivateAsync();
        await Eventually(() => !_vm.HasError && _vm.VisibleMembers.Count == 4);
    }

    [Fact]
    public async Task An_unreachable_api_leaves_the_shell_in_error_and_recovers_on_retry()
    {
        _api.FailWith = new PlannerApiException("Cannot reach the API");
        await _vm.ActivateAsync();

        Assert.Equal("Cannot reach the API", _shell.ErrorMessage);
        Assert.Empty(_api.AttendanceRequests);

        _api.FailWith = null;
        await _vm.ActivateAsync();
        await Eventually(() => _api.AttendanceRequests.Count == 1 && _vm.VisibleMembers.Count == 4);
        Assert.Null(_shell.ErrorMessage);
    }

    [Fact]
    public async Task A_slow_outdated_response_never_overwrites_a_newer_one()
    {
        var first = new TaskCompletionSource<AttendanceStatisticsDto>();
        var second = new TaskCompletionSource<AttendanceStatisticsDto>();
        var calls = 0;
        _api.AttendanceHandler = (_, _) => ++calls == 1 ? first.Task : second.Task;

        await _vm.ActivateAsync();                       // request 1 is now in flight
        _vm.SetRangeCommand.Execute("3m");               // request 2 supersedes it
        await Eventually(() => calls == 2);

        second.SetResult(FakePlannerApi.Statistics(_api.AttendanceRequests[1], MemberStats("Newer")));
        await Eventually(() => _vm.VisibleMembers.Count == 1);
        first.SetResult(FakePlannerApi.Statistics(_api.AttendanceRequests[0], MemberStats("Older")));
        await Task.Delay(50);

        Assert.Equal(["Newer"], _vm.VisibleMembers.Select(m => m.Name));
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task The_distribution_chart_highlights_buckets_at_or_above_the_target()
    {
        await ActivateAsync();

        Assert.Equal(10, _vm.Distribution.Count);
        Assert.Equal([false, false, false, false, false, false, false, true, true, true],
            _vm.Distribution.Select(b => b.IsHighlighted)); // target 70 %
        Assert.Equal("70–79", _vm.Distribution[7].Label);
        Assert.Equal("90–100", _vm.Distribution[9].Label);
        Assert.Equal(1.0, _vm.Distribution[5].Fraction); // tallest bar
    }

    [Fact]
    public async Task A_group_without_events_in_the_span_reports_no_results()
    {
        _api.AttendanceHandler = (filter, _) => Task.FromResult(FakePlannerApi.Statistics(filter) with
        {
            Summary = new AttendanceSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0),
            Members = [],
        });

        await ActivateAsync();

        Assert.False(_vm.HasResults);
        Assert.True(_vm.HasNoResults);
    }
}

public class DashboardViewModelTests
{
    private readonly FakePlannerApi _api = new();
    private readonly ShellViewModel _shell;
    private readonly DashboardViewModel _vm;

    public DashboardViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api);
        _vm = new DashboardViewModel(_shell, _api);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    [Fact]
    public async Task Loads_for_the_first_group_and_maps_the_figures()
    {
        await _vm.ActivateAsync();
        await Eventually(() => !_vm.IsBusy && _api.DashboardRequests.Count == 1);

        Assert.Equal(("g1", (int?)90), _api.DashboardRequests.Single());
        Assert.Equal("Dojo", _vm.GroupName);
        Assert.Equal("2 Apr 2026 – 30 Jun 2026", _vm.PeriodText);
        Assert.Equal("20", _vm.Kpis.Single(k => k.Label == "Events held").Value);
        Assert.Equal("66.6 %", _vm.Kpis.Single(k => k.Label == "Attendance").Value);
        Assert.Equal("4 of 9", _vm.Kpis.Single(k => k.Label == "Meeting 75 %").Value);
        Assert.Equal(KpiTone.Warning, _vm.Kpis.Single(k => k.Label == "At risk").Tone);
        Assert.Equal("6 of 10 coming", _vm.Upcoming.Single().AcceptedText);
        Assert.Equal("2 open", _vm.Upcoming.Single().OpenText);
        Assert.Equal(["Anna"], _vm.TopMembers.Select(m => m.Name));
        Assert.Equal(["Ben"], _vm.NeedsAttention.Select(m => m.Name));
        Assert.Single(_vm.Trend);
        Assert.True(_vm.HasData);
    }

    [Fact]
    public async Task Changing_the_period_reloads_with_the_chosen_number_of_days()
    {
        await _vm.ActivateAsync();
        await Eventually(() => _api.DashboardRequests.Count == 1);

        _vm.SelectedDays = 365;
        await Eventually(() => _api.DashboardRequests.Count == 2);

        Assert.Equal(365, _api.DashboardRequests[^1].Days);
    }

    [Fact]
    public async Task A_hidden_page_does_no_work_until_it_is_shown()
    {
        await _shell.InitializeAsync();

        _shell.SelectedGroupId = "g2";
        await Task.Delay(50);
        Assert.Empty(_api.DashboardRequests);

        await _vm.ActivateAsync();
        await Eventually(() => _api.DashboardRequests.Count == 1);
        Assert.Equal("g2", _api.DashboardRequests.Single().GroupId);
    }

    [Fact]
    public async Task Refresh_asks_the_server_to_drop_its_cache_and_reloads_the_visible_page()
    {
        await _vm.ActivateAsync();
        await Eventually(() => _api.DashboardRequests.Count == 1);

        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => _api.DashboardRequests.Count == 2);

        Assert.Equal(1, _api.RefreshCalls);
    }
}

public class DisplayTests
{
    public DisplayTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");

    [Theory]
    [InlineData(66.7, "66.7 %")]
    [InlineData(80, "80 %")]
    [InlineData(0, "0 %")]
    public void Percent_drops_a_trailing_zero(double value, string expected) =>
        Assert.Equal(expected, Display.Percent(value));

    [Fact]
    public void Percent_follows_the_users_culture()
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        Assert.Equal("66,7 %", Display.Percent(66.7));
    }

    [Fact]
    public void Trend_text_shows_direction_and_size()
    {
        Assert.Equal("▲ +18.5 pts vs. earlier", Display.TrendText(AttendanceTrend.Rising, 18.5));
        Assert.Equal("▼ -22 pts vs. earlier", Display.TrendText(AttendanceTrend.Falling, -22));
        Assert.Equal("Not enough events yet", Display.TrendText(AttendanceTrend.Unknown, null));
    }

    [Fact]
    public void A_member_who_never_attended_sorts_as_the_longest_absent()
    {
        var never = new MemberRowViewModel(MemberStats("Nora") with { LastAttended = null, DaysSinceLastAttended = null });

        Assert.Equal(int.MaxValue, never.DaysSinceLastAttended);
        Assert.Equal("Not in this period", never.LastAttendedText);
    }
}
