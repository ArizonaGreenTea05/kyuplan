using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>
/// The filter of the statistics page: remembered by the client between sessions, and saved by name on the server for
/// an exam. The fake API keeps exams in a list like the real one keeps them in its database.
/// </summary>
public class SavedFilterTests
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(350);
    private static readonly DateOnly ExamDay = new(2026, 9, 26);

    private readonly FakePlannerApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFilterSettingsStore _store = new();
    private readonly ShellViewModel _shell;
    private readonly AttendanceStatisticsViewModel _vm;

    public SavedFilterTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");
        _shell = new ShellViewModel(_api);
        _vm = new AttendanceStatisticsViewModel(_shell, _api, _time, _store);
    }

    private SavedFiltersViewModel Saved => _vm.SavedFilters;

    private async Task ActivateAsync()
    {
        await _vm.ActivateAsync();
        await Eventually(() => !_vm.IsBusy && _api.AttendanceRequests.Count > 0 && _api.ExamListRequests > 0 && !Saved.IsBusy);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    /// <summary>The filter of a group, as it would have been saved: by the two trainings of the sample events.</summary>
    private static FilterSettingsDto TrainingsFilter(string groupId = "g1", string[]? subGroups = null) =>
        new(groupId, new DateOnly(2026, 6, 20), new DateOnly(2026, 7, 2), "train-1", "train-2",
            subGroups ?? ["sg1"], 60, 2, "Training", "Offenes");

    // --- The filter the client remembers --------------------------------------------------------------

    [Fact]
    public async Task The_filter_in_effect_is_handed_to_the_store_with_every_load()
    {
        await ActivateAsync();
        Assert.NotNull(_store.Load()); // the defaults of the first load are remembered too

        _vm.MinAttendancePercent = 85;
        _vm.EventNameContains = " Training ";
        _vm.SubGroupOptions.Single().IsSelected = true;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Assert.Equivalent(
            new FilterSettingsDto("g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), "exam-1", "exam-2",
                ["sg1"], 85, 4, "Training", null),
            _store.Load(), strict: true);
    }

    [Fact]
    public async Task A_filter_by_dates_is_remembered_by_dates()
    {
        await ActivateAsync();

        _vm.SetRangeCommand.Execute("year");
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        var remembered = _store.Load()!;
        Assert.False(remembered.SpanByEvents);
        Assert.Null(remembered.FromEventId);
        Assert.Equal(new DateOnly(2026, 1, 1), remembered.From);
        Assert.Equal(new DateOnly(2026, 6, 30), remembered.To);
    }

    [Fact]
    public async Task A_new_session_opens_with_the_remembered_filter()
    {
        _store.Save(TrainingsFilter());

        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Equal("g1", request.GroupId);
        Assert.Equal("train-1", request.FromEventId);
        Assert.Equal("train-2", request.ToEventId);
        Assert.Equal(60, request.MinAttendancePercent);   // not the server's 70
        Assert.Equal(2, request.MinInvitedEvents);
        Assert.Equal(["sg1"], request.SubGroupIds ?? []);
        Assert.Equal("Training", request.EventNameContains);
        Assert.Equal("Offenes", request.EventNameExcludes);

        Assert.Equal(60, _vm.MinAttendancePercent);
        Assert.Equal(["sg1"], _vm.SelectedSubGroupIds);
        Assert.True(_vm.SpanByEvents);
        Assert.Equal("train-1", _vm.FromEventId);
        Assert.Equal("train-2", _vm.ToEventId);
    }

    [Fact]
    public async Task The_remembered_filter_of_another_group_brings_that_group_back_with_one_load()
    {
        _store.Save(TrainingsFilter("g2", subGroups: []));

        await ActivateAsync();

        Assert.Equal("g2", _shell.SelectedGroupId);
        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Equal("g2", request.GroupId);
        Assert.Equal(60, request.MinAttendancePercent);
        Assert.Equal("train-1", request.FromEventId);
        Assert.Equal(["g2"], _api.EventChoiceRequests);
    }

    [Fact]
    public async Task A_remembered_filter_of_a_group_that_is_gone_is_dropped_for_the_defaults()
    {
        _store.Save(TrainingsFilter("deleted-group"));

        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Equal("g1", request.GroupId);
        Assert.Equal(70, request.MinAttendancePercent);
        Assert.Equal("exam-1", request.FromEventId);
    }

    [Fact]
    public async Task Remembered_events_that_are_no_longer_offered_fall_back_to_the_days_they_stood_for()
    {
        _store.Save(TrainingsFilter() with { FromEventId = "gone-1", ToEventId = "gone-2" });

        await ActivateAsync();

        var request = Assert.Single(_api.AttendanceRequests);
        Assert.Null(request.FromEventId);
        Assert.Null(request.ToEventId);
        Assert.Equal(new DateOnly(2026, 6, 20), request.From);
        Assert.Equal(new DateOnly(2026, 7, 2), request.To);
        Assert.False(_vm.SpanByEvents);
    }

    [Fact]
    public async Task Without_a_server_there_is_nothing_to_apply_the_remembered_filter_to_and_it_is_kept()
    {
        _store.Save(TrainingsFilter("g2"));
        _api.FailWith = new PlannerApiException("down");

        await _vm.ActivateAsync();
        Assert.True(_shell.HasError);

        _api.FailWith = null;
        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => _api.AttendanceRequests.Count == 1 && !_vm.IsBusy);

        Assert.Equal("g2", _api.AttendanceRequests[0].GroupId); // still the filter from last time
        Assert.Equal(60, _api.AttendanceRequests[0].MinAttendancePercent);
    }

    [Fact]
    public void A_stored_filter_is_json_that_a_damaged_or_foreign_file_cannot_break()
    {
        var settings = TrainingsFilter();

        var json = FilterSettingsJson.Serialize(settings);
        Assert.Equivalent(settings, FilterSettingsJson.TryParse(json), strict: true);
        Assert.DoesNotContain("spanByEvents", json);

        Assert.Null(FilterSettingsJson.TryParse(null));
        Assert.Null(FilterSettingsJson.TryParse("  "));
        Assert.Null(FilterSettingsJson.TryParse("not json"));
        Assert.Null(FilterSettingsJson.TryParse("[1, 2]"));
        Assert.Null(FilterSettingsJson.TryParse("{}"));                       // no group: nothing to apply it to
        Assert.Null(FilterSettingsJson.TryParse("""{"groupId": ""}"""));
        Assert.Null(FilterSettingsJson.TryParse("""{"groupId": "g1", "from": "yesterday"}"""));
        Assert.Empty(FilterSettingsJson.TryParse("""{"groupId": "g1", "minAttendancePercent": 80}""")!.SubGroupIds); // an older file without subgroups
    }

    // --- Saving a filter for an exam ----------------------------------------------------------------

    [Fact]
    public async Task Saving_makes_the_exam_the_span_ends_at_and_the_filter_for_it()
    {
        await ActivateAsync();
        _vm.MinAttendancePercent = 85;
        _vm.EventNameContains = "Training";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Saved.BeginSaveCommand.Execute(null);

        // The span ends at the second exam: that is the exam the filter is most likely for, with a name to match.
        Assert.True(Saved.IsSaving);
        Assert.True(Saved.IsNewExam);
        Assert.Equal("exam-2", Saved.NewExamEventId);
        Assert.Equal($"Filter for Gürtelprüfung on {Display.Date(ExamDay)}", Saved.NewFilterName);

        Saved.NewFilterName = "Filter for Exam on 26.09.26";
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create-exam:exam-2", "create-filter:1:Filter for Exam on 26.09.26"], _api.ExamCalls);
        var exam = Assert.Single(_api.Exams);
        Assert.Equal(("g1", "exam-2", "Gürtelprüfung", ExamDay), (exam.GroupId, exam.EventId, exam.Name, exam.Date)); // the exam is the event
        var saved = Assert.Single(exam.Filters);
        Assert.Equivalent(
            new FilterSettingsDto("g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), "exam-1", "exam-2",
                [], 85, 4, "Training", null),
            saved.Settings, strict: true);

        Assert.False(Saved.IsSaving);
        Assert.Null(Saved.ErrorMessage);
        Assert.Equal(saved.Id, Saved.SelectedFilterId);
        Assert.Equal(["Filter for Exam on 26.09.26"], Saved.FilterChoices.Select(f => f.Name));
        Assert.Equal(2, _api.AttendanceRequests.Count); // the filter card was not touched, so nothing reloads
    }

    [Fact]
    public async Task Saving_for_the_exam_the_span_ends_at_adds_a_filter_to_it()
    {
        var exam = _api.AddExam("exam-2");
        await ActivateAsync();

        Saved.BeginSaveCommand.Execute(null);

        Assert.False(Saved.IsNewExam);
        Assert.Equal(exam.Id, Saved.SelectedExamId);
        Assert.Equal($"Filter for Gürtelprüfung on {Display.Date(ExamDay)}", Saved.NewFilterName);

        Saved.NewFilterName = "Kids";
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"create-filter:{exam.Id}:Kids"], _api.ExamCalls);
        Assert.Equal(["Kids"], _api.Exams.Single().Filters.Select(f => f.Name));
    }

    [Fact]
    public async Task Another_event_is_another_exam_and_the_name_suggestion_follows_it_until_the_name_is_edited()
    {
        _api.AddExam("exam-2");
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);

        Saved.SelectedExamId = 0; // "new exam …"
        Saved.NewExamEventId = "exam-1";
        Assert.Equal($"Filter for Gürtelprüfung on {Display.Date(new DateOnly(2026, 3, 28))}", Saved.NewFilterName);

        Saved.NewFilterName = "My own name";
        Saved.NewExamEventId = "train-1";
        Assert.Equal("My own name", Saved.NewFilterName); // an edited name is not overwritten

        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create-exam:train-1", "create-filter:2:My own name"], _api.ExamCalls);
        Assert.Equal("train-1", _api.Exams[^1].EventId);
    }

    [Fact]
    public async Task An_event_that_already_is_an_exam_is_used_instead_of_being_made_an_exam_twice()
    {
        var exam = _api.AddExam("exam-2");
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);

        Saved.SelectedExamId = 0;
        Saved.NewExamEventId = "exam-2";
        Saved.NewFilterName = "Again";
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"create-filter:{exam.Id}:Again"], _api.ExamCalls);
        Assert.Single(_api.Exams);
    }

    [Fact]
    public async Task When_dates_define_the_span_the_exam_on_its_last_day_is_chosen()
    {
        var exam = _api.AddExam("exam-2");
        await ActivateAsync();
        _vm.From = new DateTime(2026, 4, 1); // the dates now define the span; it still ends on the day of the exam (26 September)
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);

        Saved.BeginSaveCommand.Execute(null);

        Assert.False(_vm.SpanByEvents);
        Assert.Equal(exam.Id, Saved.SelectedExamId);
        Assert.Equal("", Saved.NewExamEventId); // no event to propose when dates define the span
    }

    [Fact]
    public async Task A_new_exam_is_made_of_the_events_the_page_offers()
    {
        await ActivateAsync();
        var changed = new List<string?>();
        Saved.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Same(_vm.EventOptions, Saved.EventOptions);
        Assert.Equal(["", "exam-1", "train-1", "train-2", "exam-2"], Saved.EventOptions.Select(o => o.Id)); // led by the empty entry

        _api.EventChoices = new EventChoicesDto([new("other-1", "Sommerfest", At(7, 11, 15))], null, null);
        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => Saved.EventOptions.Count == 2);

        Assert.Same(_vm.EventOptions, Saved.EventOptions);
        Assert.Contains(nameof(SavedFiltersViewModel.EventOptions), changed); // a bound list is told to read the new events
    }

    [Fact]
    public async Task Saving_asks_for_what_is_missing_before_it_calls_the_server()
    {
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);

        Saved.NewFilterName = "  ";
        await Saved.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Give the filter a name.", Saved.ErrorMessage);

        Saved.NewFilterName = "Spring";
        Saved.NewExamEventId = EventOption.NoneId;
        await Saved.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Choose the event that is the exam.", Saved.ErrorMessage);

        Saved.NewExamEventId = "an-event-of-another-group"; // not among the events on offer
        await Saved.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Choose the event that is the exam.", Saved.ErrorMessage);

        Assert.Empty(_api.ExamCalls);
        Assert.True(Saved.IsSaving);
    }

    [Fact]
    public async Task A_time_span_that_is_not_valid_cannot_be_saved()
    {
        await ActivateAsync();
        _vm.FromEventId = "exam-2";
        _vm.ToEventId = "exam-1";
        _time.Advance(DebounceWindow);
        await Eventually(() => _vm.FilterError is not null);

        Saved.BeginSaveCommand.Execute(null);
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal(_vm.FilterError, Saved.ErrorMessage);
        Assert.Empty(_api.ExamCalls);
    }

    [Fact]
    public async Task A_name_the_exam_already_has_shows_the_servers_message_and_keeps_the_form_for_another_try()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);

        Saved.NewFilterName = "spring";
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Contains("already has a filter named 'spring'", Saved.ErrorMessage);
        Assert.True(Saved.IsSaving);
        Assert.Single(_api.Exams.Single().Filters);

        Saved.NewFilterName = "Autumn";
        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Null(Saved.ErrorMessage);
        Assert.False(Saved.IsSaving);
        Assert.Equal(["Autumn", "Spring"], _api.Exams.Single().Filters.Select(f => f.Name).Order());
        Assert.Equal(exam.Id, _api.Exams.Single().Id);
    }

    [Fact]
    public async Task When_the_filter_fails_after_its_new_exam_was_created_the_next_try_reuses_that_exam()
    {
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);
        _api.FailNextCreateFilter = new PlannerApiException("The server went away.");

        await Saved.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The server went away.", Saved.ErrorMessage);
        Assert.True(Saved.IsSaving);
        Assert.False(Saved.IsNewExam); // the exam exists now and is the one chosen

        var name = Saved.NewFilterName;
        await Saved.SaveCommand.ExecuteAsync(null);

        // One exam, created once; the filter was tried twice.
        Assert.Equal(["create-exam:exam-2", $"create-filter:1:{name}", $"create-filter:1:{name}"], _api.ExamCalls);
        Assert.Single(_api.Exams);
        Assert.Single(_api.Exams.Single().Filters);
        Assert.False(Saved.IsSaving);
    }

    // --- Using a saved filter ----------------------------------------------------------------------

    [Fact]
    public async Task Choosing_a_saved_filter_puts_it_into_the_filter_card_and_reloads()
    {
        var settings = TrainingsFilter();
        var exam = _api.AddExam("exam-2", ("Spring", settings));
        await ActivateAsync();
        Assert.Equal(["Spring"], Saved.FilterChoices.Select(f => f.Name));
        Assert.Equal($"Gürtelprüfung · {Display.Date(ExamDay)}", Saved.FilterChoices[0].ExamText);

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal("g1", request.GroupId);
        Assert.Equal("train-1", request.FromEventId);
        Assert.Equal("train-2", request.ToEventId);
        Assert.Equal(60, request.MinAttendancePercent);
        Assert.Equal(2, request.MinInvitedEvents);
        Assert.Equal(["sg1"], request.SubGroupIds ?? []);
        Assert.Equal("Training", request.EventNameContains);
        Assert.Equal("Offenes", request.EventNameExcludes);

        Assert.Equal(60, _vm.MinAttendancePercent);
        Assert.Equal(["sg1"], _vm.SelectedSubGroupIds);
        Assert.True(_vm.SpanByEvents);
        Assert.Equal(new DateTime(2026, 7, 2), _vm.To);
        Assert.Contains("between \"Training\"", _vm.FilterSummary);
        Assert.Equal(exam.Filters[0].Id, Saved.SelectedFilterId); // still the chosen one
        Assert.Equivalent(settings, _store.Load(), strict: true); // and what the client remembers from now on
    }

    [Fact]
    public async Task A_saved_filter_of_another_group_switches_to_that_group()
    {
        var exam = _api.AddExam("exam-2", ("Team filter", TrainingsFilter("g2", subGroups: [])));
        await ActivateAsync();

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        Assert.Equal("g2", _shell.SelectedGroupId);
        var request = _api.AttendanceRequests[^1];
        Assert.Equal("g2", request.GroupId);
        Assert.Equal(60, request.MinAttendancePercent);
        Assert.Equal("train-1", request.FromEventId);
        Assert.Equal(exam.Filters[0].Id, Saved.SelectedFilterId);
    }

    [Fact]
    public async Task A_saved_filter_of_a_group_that_no_longer_exists_says_so_and_changes_nothing()
    {
        var exam = _api.AddExam("exam-2", ("Old", TrainingsFilter("deleted-group")));
        await ActivateAsync();

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Task.Delay(50);

        Assert.Equal("The group of this filter is no longer available.", Saved.ErrorMessage);
        Assert.Single(_api.AttendanceRequests);
        Assert.Equal(70, _vm.MinAttendancePercent);
    }

    [Fact]
    public async Task Saved_events_that_are_not_offered_any_more_load_as_the_days_they_stood_for()
    {
        var settings = TrainingsFilter() with { FromEventId = "gone-1", ToEventId = "gone-2" };
        var exam = _api.AddExam("exam-2", ("Old", settings));
        await ActivateAsync();

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        var request = _api.AttendanceRequests[^1];
        Assert.Null(request.FromEventId);
        Assert.Equal(new DateOnly(2026, 6, 20), request.From);
        Assert.Equal(new DateOnly(2026, 7, 2), request.To);
        Assert.False(_vm.SpanByEvents);
    }

    [Fact]
    public async Task Reloading_puts_the_chosen_filter_into_the_filter_card_again_and_takes_back_later_changes()
    {
        var settings = TrainingsFilter();
        var exam = _api.AddExam("exam-2", ("Spring", settings));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        _vm.MinAttendancePercent = 95;
        _vm.EventNameContains = "Something else";
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Saved.ReloadSelectedCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 4 && !_vm.IsBusy);

        var request = _api.AttendanceRequests[^1];
        Assert.Equal(60, request.MinAttendancePercent);
        Assert.Equal("Training", request.EventNameContains);
        Assert.Equal("train-1", request.FromEventId);
        Assert.Equal(60, _vm.MinAttendancePercent);
        Assert.Equal("Training", _vm.EventNameContains);
        Assert.Equal(exam.Filters[0].Id, Saved.SelectedFilterId); // still the chosen one
        Assert.Empty(_api.ExamCalls);                              // the server's filter was only read
        Assert.Equivalent(settings, _store.Load(), strict: true);
    }

    [Fact]
    public async Task Reloading_a_filter_of_a_group_that_no_longer_exists_says_so_and_changes_nothing()
    {
        var exam = _api.AddExam("exam-2", ("Old", TrainingsFilter("deleted-group")));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id; // refused, but chosen
        Assert.NotNull(Saved.ErrorMessage);
        Saved.ErrorMessage = null;

        Saved.ReloadSelectedCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal("The group of this filter is no longer available.", Saved.ErrorMessage);
        Assert.Single(_api.AttendanceRequests);
        Assert.Equal(70, _vm.MinAttendancePercent);
    }

    [Fact]
    public async Task Updating_replaces_the_chosen_filter_with_the_filter_card_as_it_is_now_after_the_question_was_answered_with_yes()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        _vm.MinAttendancePercent = 95;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Saved.AskUpdateCommand.Execute(null);
        Assert.True(Saved.IsConfirming);
        Assert.Equal("Replace the filter \"Spring\" with the filters as they are now?", Saved.ConfirmText);
        Assert.Equal("Yes, replace", Saved.ConfirmYesText);
        Assert.Empty(_api.ExamCalls); // nothing is replaced before the answer

        await Saved.ConfirmPendingCommand.ExecuteAsync(null);

        Assert.Equal([$"update-filter:{exam.Id}:{exam.Filters[0].Id}"], _api.ExamCalls);
        var stored = _api.Exams.Single().Filters.Single();
        Assert.Equal("Spring", stored.Name);
        Assert.Equal(95, stored.Settings.MinAttendancePercent);
        Assert.Equal("train-1", stored.Settings.FromEventId);
        Assert.Equal(stored.Id, Saved.SelectedFilterId);
        Assert.False(Saved.IsConfirming);
        Assert.Equal(3, _api.AttendanceRequests.Count);
    }

    [Fact]
    public async Task Updating_is_given_up_when_the_question_is_answered_with_cancel()
    {
        var settings = TrainingsFilter();
        var exam = _api.AddExam("exam-2", ("Spring", settings));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);
        _vm.MinAttendancePercent = 95;

        Saved.AskUpdateCommand.Execute(null);
        Saved.CancelPendingCommand.Execute(null);

        Assert.False(Saved.IsConfirming);
        Assert.Empty(_api.ExamCalls);
        Assert.Equal(60, _api.Exams.Single().Filters.Single().Settings.MinAttendancePercent);
    }

    [Fact]
    public void Reload_update_and_delete_need_a_chosen_filter()
    {
        Assert.False(Saved.ReloadSelectedCommand.CanExecute(null));
        Assert.False(Saved.AskUpdateCommand.CanExecute(null));
        Assert.False(Saved.AskDeleteFilterCommand.CanExecute(null));
        Assert.False(Saved.AskDeleteExamCommand.CanExecute(null)); // "new exam" is chosen: there is nothing to delete
    }

    [Fact]
    public async Task Reload_and_update_are_only_offered_while_the_filter_card_differs_from_the_chosen_filter()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        var changed = new List<string?>();
        var commandChanges = 0;
        Saved.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Saved.ReloadSelectedCommand.CanExecuteChanged += (_, _) => commandChanges++;

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        // The filter card holds what was chosen: reloading or replacing would change nothing. Deleting still can.
        Assert.False(Saved.CanReload);
        Assert.False(Saved.CanUpdate);
        Assert.False(Saved.ReloadSelectedCommand.CanExecute(null));
        Assert.False(Saved.AskUpdateCommand.CanExecute(null));
        Assert.True(Saved.AskDeleteFilterCommand.CanExecute(null));

        changed.Clear();
        commandChanges = 0;
        _vm.MinAttendancePercent = 95;

        Assert.True(Saved.CanReload);
        Assert.True(Saved.CanUpdate);
        Assert.True(Saved.ReloadSelectedCommand.CanExecute(null));
        Assert.True(Saved.AskUpdateCommand.CanExecute(null));
        Assert.Contains(nameof(SavedFiltersViewModel.CanReload), changed); // bound buttons are told to ask again
        Assert.Contains(nameof(SavedFiltersViewModel.CanUpdate), changed);
        Assert.True(commandChanges > 0);

        _vm.MinAttendancePercent = 60; // changed back by hand
        Assert.False(Saved.CanReload);
        Assert.False(Saved.CanUpdate);

        _vm.SubGroupOptions.Single().IsSelected = false;
        Assert.True(Saved.CanReload);
        _vm.SubGroupOptions.Single().IsSelected = true;
        Assert.False(Saved.CanReload);

        _vm.EventNameExcludes = " Offenes "; // the same once trimmed, as it is saved
        Assert.False(Saved.CanReload);
        _vm.EventNameExcludes = "";
        Assert.True(Saved.CanReload);

        Saved.ReloadSelectedCommand.Execute(null);
        await Eventually(() => !_vm.IsBusy && !Saved.CanReload);
        Assert.Equal("Offenes", _vm.EventNameExcludes);
    }

    [Fact]
    public async Task A_filter_card_that_cannot_be_saved_can_be_reloaded_but_not_replaced()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        _vm.FromEventId = "exam-2";
        _vm.ToEventId = "exam-1"; // ends before it starts

        Assert.True(Saved.CanReload);  // taking the change back is what is needed
        Assert.False(Saved.CanUpdate); // there is nothing valid to save
    }

    [Fact]
    public async Task Replacing_a_filter_leaves_nothing_to_reload_or_replace()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);
        _vm.MinAttendancePercent = 95;
        Assert.True(Saved.CanUpdate);

        Saved.AskUpdateCommand.Execute(null);
        await Saved.ConfirmPendingCommand.ExecuteAsync(null);

        Assert.False(Saved.CanReload); // the chosen filter is the card as it is now
        Assert.False(Saved.CanUpdate);
    }

    [Fact]
    public async Task Saved_events_that_are_not_offered_any_more_are_held_once_they_were_loaded_as_days()
    {
        var settings = TrainingsFilter() with { FromEventId = "gone-1", ToEventId = "gone-2" };
        var exam = _api.AddExam("exam-2", ("Old", settings));
        await ActivateAsync();

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        Assert.False(_vm.SpanByEvents);
        Assert.False(Saved.CanReload); // reloading would put the same days there again
    }

    [Fact]
    public async Task A_saved_filter_of_another_group_can_be_reloaded_until_that_group_is_shown()
    {
        var exam = _api.AddExam("exam-2", ("Team filter", TrainingsFilter("g2", subGroups: [])));
        await ActivateAsync();

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        Assert.Equal("g2", _shell.SelectedGroupId);
        Assert.False(Saved.CanReload);

        _shell.SelectedGroupId = "g1"; // the filter card is for another group now
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.True(Saved.CanReload);
    }

    [Fact]
    public async Task Reset_is_only_offered_while_something_differs_from_the_defaults()
    {
        await ActivateAsync();
        Assert.False(_vm.CanReset);
        Assert.False(_vm.ResetFiltersCommand.CanExecute(null));

        _vm.SearchText = "Anna"; // the member view is reset too
        Assert.True(_vm.CanReset);
        _vm.SearchText = "";
        Assert.False(_vm.CanReset);

        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.SubGroupOptions.Single().IsSelected = true;
        Assert.True(_vm.CanReset);
        Assert.Contains(nameof(AttendanceStatisticsViewModel.CanReset), changed);
        _vm.SubGroupOptions.Single().IsSelected = false;
        Assert.False(_vm.CanReset);

        _vm.SetRangeCommand.Execute("year"); // dates instead of the suggested exams
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);
        Assert.True(_vm.CanReset);
        Assert.True(_vm.ResetFiltersCommand.CanExecute(null));

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.True(_vm.SpanByEvents);
        Assert.False(_vm.CanReset);
    }

    [Fact]
    public async Task Reset_can_forget_a_chosen_filter_even_when_the_card_holds_the_defaults()
    {
        var defaults = new FilterSettingsDto(
            "g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), "exam-1", "exam-2", [], 70, 4, null, null);
        var exam = _api.AddExam("exam-2", ("Defaults", defaults));
        await ActivateAsync();
        Assert.False(_vm.CanReset);

        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        Assert.False(Saved.CanReload); // nothing to take back
        Assert.True(_vm.CanReset);     // but the choice can be forgotten

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3 && !_vm.IsBusy);

        Assert.Null(Saved.SelectedFilter);
        Assert.False(_vm.CanReset);
    }

    [Fact]
    public async Task A_filter_is_only_deleted_after_the_question_was_answered_with_yes()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()), ("Autumn", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters.Single(f => f.Name == "Spring").Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        Saved.AskDeleteFilterCommand.Execute(null);
        Assert.True(Saved.IsConfirming);
        Assert.Equal("Delete the filter \"Spring\"?", Saved.ConfirmText);
        Assert.Equal("Yes, delete", Saved.ConfirmYesText);
        Saved.CancelPendingCommand.Execute(null);
        Assert.False(Saved.IsConfirming);
        Assert.Empty(_api.ExamCalls);

        Saved.AskDeleteFilterCommand.Execute(null);
        await Saved.ConfirmPendingCommand.ExecuteAsync(null);

        Assert.Equal([$"delete-filter:{exam.Id}:{exam.Filters.Single(f => f.Name == "Spring").Id}"], _api.ExamCalls);
        Assert.Equal(["Autumn"], Saved.FilterChoices.Select(f => f.Name));
        Assert.Null(Saved.SelectedFilter);
        Assert.False(Saved.IsConfirming);
        Assert.Equal(2, _api.AttendanceRequests.Count); // deleting a saved filter leaves the filter card as it is
    }

    [Fact]
    public async Task Changing_the_choice_withdraws_a_pending_question()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()), ("Autumn", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        Saved.AskDeleteFilterCommand.Execute(null);

        Saved.SelectedFilterId = exam.Filters[1].Id;

        Assert.False(Saved.IsConfirming); // the question was about the other filter

        Saved.AskUpdateCommand.Execute(null);
        Saved.ReloadSelectedCommand.Execute(null);

        Assert.False(Saved.IsConfirming); // reloading changes the filter card the question was about
    }

    [Fact]
    public async Task Deleting_an_exam_names_how_many_filters_go_with_it_and_deletes_them_all()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()), ("Autumn", TrainingsFilter()));
        var other = _api.AddExam("exam-1", ("Keep", TrainingsFilter()));
        await ActivateAsync();
        Saved.BeginSaveCommand.Execute(null);
        Saved.SelectedExamId = exam.Id;

        Saved.AskDeleteExamCommand.Execute(null);
        Assert.Equal("Delete the exam \"Gürtelprüfung\" together with its saved filters (2)?", Saved.ConfirmText);
        await Saved.ConfirmPendingCommand.ExecuteAsync(null);

        Assert.Equal([$"delete-exam:{exam.Id}"], _api.ExamCalls);
        Assert.Equal([other.Id], _api.Exams.Select(e => e.Id));
        Assert.Equal(["Keep"], Saved.FilterChoices.Select(f => f.Name));
        Assert.True(Saved.IsNewExam); // the deleted exam cannot stay chosen
    }

    [Fact]
    public async Task Deleting_the_exam_of_the_chosen_filter_clears_the_choice()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);
        Saved.BeginSaveCommand.Execute(null);
        Saved.SelectedExamId = exam.Id; // the span now ends on 2 July, the day of no exam, so it has to be chosen

        Saved.AskDeleteExamCommand.Execute(null);
        await Saved.ConfirmPendingCommand.ExecuteAsync(null);

        Assert.Null(Saved.SelectedFilter);
        Assert.False(Saved.HasFilters);
    }

    [Fact]
    public async Task Resetting_the_filters_forgets_the_chosen_saved_filter()
    {
        var exam = _api.AddExam("exam-2", ("Spring", TrainingsFilter()));
        await ActivateAsync();
        Saved.SelectedFilterId = exam.Filters[0].Id;
        await Eventually(() => _api.AttendanceRequests.Count == 2 && !_vm.IsBusy);

        _vm.ResetFiltersCommand.Execute(null);
        await Eventually(() => _api.AttendanceRequests.Count == 3);

        Assert.Null(Saved.SelectedFilter);
        Assert.Equal(70, _api.AttendanceRequests[^1].MinAttendancePercent);
    }

    [Fact]
    public async Task The_exams_are_fetched_once_and_again_after_a_refresh_not_with_every_filter_change()
    {
        await ActivateAsync();
        _vm.MinAttendancePercent = 80;
        _time.Advance(DebounceWindow);
        await Eventually(() => _api.AttendanceRequests.Count == 2);
        Assert.Equal(1, _api.ExamListRequests);

        _api.AddExam("exam-2");
        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => _api.ExamListRequests == 2 && !Saved.IsBusy);

        Assert.Equal(["Gürtelprüfung · " + Display.Date(ExamDay)], Saved.Exams.Select(e => e.Label));
    }

    [Fact]
    public async Task An_exam_list_that_cannot_be_loaded_does_not_get_in_the_way_of_the_statistics()
    {
        _api.ExamListFailure = new PlannerApiException("No exams today.");

        await ActivateAsync();

        Assert.True(_vm.HasResults);
        Assert.Null(_vm.ErrorMessage);
        Assert.Equal("No exams today.", Saved.ErrorMessage);
        Assert.False(Saved.HasFilters);

        _api.ExamListFailure = null;
        await _vm.RefreshCommand.ExecuteAsync(null);
        await Eventually(() => !Saved.IsBusy && Saved.ErrorMessage is null);
    }

    // --- The member table ----------------------------------------------------------------------------

    [Fact]
    public void A_member_row_keeps_the_name_and_the_subgroups_apart()
    {
        var row = new MemberRowViewModel(MemberStats("Anna", subGroups: ["Kinder", "Jugend"]));

        Assert.Equal("Anna", row.Name);
        Assert.Equal("Kinder, Jugend", row.SubGroupsText);
        Assert.Equal("", new MemberRowViewModel(MemberStats("Ben")).SubGroupsText);
    }

    [Fact]
    public async Task The_subgroups_column_sorts_by_its_text_and_ties_by_name()
    {
        _api.AttendanceHandler = (query, _) => Task.FromResult(FakePlannerApi.Statistics(query,
            MemberStats("Anna", subGroups: ["Kinder"]),
            MemberStats("Ben", subGroups: ["Jugend"]),
            MemberStats("Clara"),
            MemberStats("Dora", subGroups: ["Kinder"]),
            MemberStats("Emil", subGroups: ["Erwachsene", "Jugend"])));
        await ActivateAsync();

        _vm.SortByCommand.Execute(MemberSortKey.SubGroups);

        Assert.False(_vm.SortDescending); // text columns start ascending, like the name
        Assert.Equal(["Clara", "Emil", "Ben", "Anna", "Dora"], _vm.VisibleMembers.Select(m => m.Name));

        _vm.SortByCommand.Execute(MemberSortKey.SubGroups);
        Assert.Equal(["Anna", "Dora", "Ben", "Emil", "Clara"], _vm.VisibleMembers.Select(m => m.Name));
    }
}

public sealed class SavedFilterLanguageTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;

    public SavedFilterLanguageTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    [Fact]
    public async Task The_lists_stay_and_their_entries_write_dates_and_texts_in_the_new_language()
    {
        _api.AddExam("exam-2", ("Spring", new FilterSettingsDto(
            "g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), null, null, [], 75, 3, null, null)));
        var vm = new AttendanceStatisticsViewModel(_shell, _api, TimeProvider.System);
        await vm.ActivateAsync();
        for (var i = 0; i < 200 && (vm.IsBusy || vm.SavedFilters.IsBusy || !vm.SavedFilters.HasFilters); i++) await Task.Delay(10);

        var saved = vm.SavedFilters;
        var filters = saved.FilterChoices;
        var exam = saved.Exams.Single();
        var choice = filters.Single();
        saved.BeginSaveCommand.Execute(null);
        Assert.Equal("New exam …", saved.ExamChoices[^1].Label);
        Assert.Equal("Gürtelprüfung · " + Display.Date(new DateOnly(2026, 9, 26)), exam.Label);
        var examTextChanged = false;
        choice.PropertyChanged += (_, _) => examTextChanged = true;

        _language.Select("de");

        Assert.Same(filters, saved.FilterChoices); // a replaced list would lose the selection of a WPF combo box
        Assert.Same(exam, saved.Exams.Single());
        Assert.True(examTextChanged);
        Assert.Equal("Neue Prüfung …", saved.ExamChoices[^1].Label);
        Assert.Equal("Gürtelprüfung · " + Display.Date(new DateOnly(2026, 9, 26)), exam.Label);
        Assert.Contains("26. Sep", choice.ExamText);
    }
}
