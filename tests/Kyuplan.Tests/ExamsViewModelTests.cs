using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The exams page: the exams of the group, who is fit for the next one (and the members saved for it), and who attended a past one and
/// received which belt. "Now" is 30 June 2026, so the exam of 28 March is past and the one of 26 September is to come.
/// </summary>
public sealed class ExamsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly ExamsViewModel _vm;

    private readonly BeltDto _white;
    private readonly BeltDto _yellow;
    private readonly MemberDto _anna;
    private readonly MemberDto _ben;
    private readonly MemberDto _clara;
    private readonly ExamDto _past;
    private readonly ExamDto _next;

    public ExamsViewModelTests()
    {
        _language.Select("en");
        _white = _api.AddBelt("White");
        _yellow = _api.AddBelt("Yellow", "#FFD800", "#000000", requires: [new(_white.Id, 8, 3)]);
        _anna = _api.AddMember("Anna", beltId: _white.Id);
        _ben = _api.AddMember("Ben", beltId: _white.Id);
        _clara = _api.AddMember("Clara");
        _api.AddMember("Dora", groupId: "g2");

        _past = _api.AddExam("exam-1");
        _next = _api.AddExam("exam-2");

        _shell = new ShellViewModel(_api, _language);
        _vm = new ExamsViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private ExamDetailViewModel Detail => _vm.Detail;

    /// <summary>26 September 2026 as the page writes it (en-GB abbreviates September to "Sept", so the tests do not spell it out).</summary>
    private static string Sep26 => Display.Date(new DateOnly(2026, 9, 26));

    private async Task<ExamsViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        await _vm.Detail.Loaded;
        return _vm;
    }

    private async Task SelectAsync(ExamDto exam)
    {
        _vm.SelectedExam = _vm.Exams.Single(e => e.Id == exam.Id);
        await _vm.Detail.Loaded;
    }

    private static ExamSuggestionDto Suggest(
        int memberId, string name, SuggestionVerdict verdict = SuggestionVerdict.Fit, int? belt = 1, int? next = 2,
        AttendanceMark? invitation = null, bool saved = false, CheckState age = CheckState.Met, CheckState wait = CheckState.Met,
        QualificationStatus attendance = QualificationStatus.Qualified, int invited = 10, int attended = 9, int? ageYears = 11, int? waited = 7) =>
        new(memberId, name, ["Kinder"], belt, next, 8, ageYears, age, 3, waited, wait,
            new DateOnly(2025, 12, 1), null, new DateOnly(2025, 12, 1), invited, attended, 100.0 * attended / Math.Max(1, invited),
            attendance, 80, verdict, invitation, saved);

    private void Suggestions(ExamDto exam, params ExamSuggestionDto[] members) =>
        _api.ExamSuggestions[exam.Id] = new ExamSuggestionsDto(
            exam.Id, exam.Name, exam.Date, null, null,
            new AttendanceFilter("g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), null, 75, 3), members);

    /// <summary>Three members: Anna is fit, Ben misses the waiting time, Clara has no belt.</summary>
    private void DefaultSuggestions()
    {
        Suggestions(_next,
            Suggest(_anna.Id, "Anna"),
            Suggest(_ben.Id, "Ben", SuggestionVerdict.NotYet, wait: CheckState.NotMet, waited: 1),
            Suggest(_clara.Id, "Clara", SuggestionVerdict.NeedsData, belt: null, next: null));
    }

    // --- The list ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_exams_of_the_group_come_with_the_one_to_come_first_and_the_next_one_is_chosen()
    {
        _api.Exams.Add(_past with { Id = 900, GroupId = "g2", EventId = "other" }); // an exam of the other group

        var vm = await ShownAsync();

        Assert.Equal("Exams", vm.Title);
        Assert.Equal([_next.Id, _past.Id], vm.Exams.Select(e => e.Id)); // the one to come first
        Assert.Equal(_next.Id, vm.SelectedExam?.Id);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task An_exam_row_says_when_it_is_and_who_took_part()
    {
        _api.Exams[0] = _api.Exams[0] with { InvitedCount = 8, AttendedCount = 6, BeltsAwardedCount = 4 };
        _api.Exams[1] = _api.Exams[1] with { InvitedCount = 10, AttendedCount = 3, PreSavedCount = 5 };

        var vm = await ShownAsync();

        var next = vm.Exams[0];
        var past = vm.Exams[1];
        Assert.Equal(("Upcoming", Sep26, "in 88 days"), (next.StatusText, next.DateText, next.WhenText));
        Assert.Equal(("3 of 10 accepted", "5 saved"), (next.PeopleText, next.ExtraText));
        Assert.Equal(("Past", "94 days ago"), (past.StatusText, past.WhenText));
        Assert.Equal(("6 of 8 accepted", "4 belts"), (past.PeopleText, past.ExtraText));
        Assert.Equal($"Gürtelprüfung · {Sep26}", next.Label);
    }

    [Fact]
    public async Task A_group_without_exams_says_so_but_only_once_it_has_loaded()
    {
        _api.Exams.Clear();
        Assert.False(_vm.IsEmpty);

        var vm = await ShownAsync();

        Assert.True(vm.IsEmpty);
        Assert.Null(vm.SelectedExam);
        Assert.False(Detail.HasExam);
    }

    [Fact]
    public async Task Another_group_is_another_list_and_closes_what_was_started_on_the_old_one()
    {
        var vm = await ShownAsync();
        await vm.BeginAddCommand.ExecuteAsync(null);
        Assert.True(vm.IsAdding);

        _shell.SelectedGroupId = "g2";
        await Eventually(() => vm.IsEmpty);

        Assert.False(vm.IsAdding);
        Assert.False(Detail.HasExam);
    }

    [Fact]
    public async Task Refreshing_keeps_the_group_although_the_picker_writes_nothing_back_when_its_list_is_replaced()
    {
        var vm = await ShownAsync();
        var changes = new List<string?>();
        _shell.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // What the WPF group picker does when the refresh gives it another list: it loses its choice and writes "nothing" back.
        _api.Exams.Add(_past with { Id = 901, EventId = "exam-3" });
        _shell.Groups = [.. _shell.Groups];
        _shell.SelectedGroupId = null!;
        await _shell.RefreshDataCommand.ExecuteAsync(null);
        await Eventually(() => vm.Exams.Count == 3);

        Assert.Equal("g1", _shell.SelectedGroupId);
        Assert.False(vm.HasError);
        Assert.Contains(nameof(ShellViewModel.SelectedGroupId), changes); // so that the picker shows the group again
    }

    [Fact]
    public async Task A_failed_list_can_be_loaded_again()
    {
        _api.ExamListFailure = new PlannerApiException("The list is not available.");
        var vm = await ShownAsync();
        Assert.Equal("The list is not available.", vm.ErrorMessage);
        Assert.False(vm.IsEmpty);

        _api.ExamListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);
        await vm.Detail.Loaded;

        Assert.False(vm.HasError);
        Assert.Equal(2, vm.Exams.Count);
    }

    // --- Who is fit ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exam_to_come_shows_who_is_fit_and_a_past_exam_does_not_ask()
    {
        DefaultSuggestions();

        await ShownAsync();

        Assert.True(Detail.ShowsPlan);
        Assert.False(Detail.ShowsResults);
        Assert.Equal([(_next.Id, (int?)null)], _api.SuggestionRequests);
        Assert.Equal(0, _api.ExamMemberRequests);
        Assert.Equal(["Anna"], Detail.VisibleSuggestions.Select(r => r.Name)); // the fit are what is shown first
        Assert.Equal($"Gürtelprüfung · {Sep26}", Detail.Heading);

        await SelectAsync(_past);

        Assert.False(Detail.ShowsPlan);
        Assert.True(Detail.ShowsResults);
        Assert.Single(_api.SuggestionRequests); // nothing was asked about the exam that took place
        Assert.Equal(1, _api.ExamMemberRequests);
        Assert.Empty(Detail.Suggestions);
    }

    [Fact]
    public async Task The_list_filter_shows_the_fit_by_default_and_counts_every_kind()
    {
        DefaultSuggestions();
        await ShownAsync();

        Assert.Equal(["Fit 1", "Needs data 1", "Not yet 1", "Top belt 0", "Everyone 3"], Detail.FilterOptions.Select(o => o.Label));
        Assert.Equal(SuggestionFilter.Fit, Detail.SelectedFilter.Kind);

        Detail.SelectedFilter = Detail.FilterOptions.Single(o => o.Kind == SuggestionFilter.NotYet);
        Assert.Equal(["Ben"], Detail.VisibleSuggestions.Select(r => r.Name));

        Detail.SelectedFilter = Detail.FilterOptions.Single(o => o.Kind == SuggestionFilter.All);
        Assert.Equal(["Anna", "Ben", "Clara"], Detail.VisibleSuggestions.Select(r => r.Name));

        Detail.SearchText = "  CLA ";
        Assert.Equal(["Clara"], Detail.VisibleSuggestions.Select(r => r.Name));
    }

    [Fact]
    public async Task When_nobody_is_fit_the_list_says_so_and_where_the_others_are()
    {
        Suggestions(_next, Suggest(_ben.Id, "Ben", SuggestionVerdict.NotYet), Suggest(_clara.Id, "Clara", SuggestionVerdict.NeedsData));
        await ShownAsync();

        Assert.Empty(Detail.VisibleSuggestions);
        Assert.True(Detail.NoneMatch);
        Assert.Equal("Nobody is fit for this exam yet. 2 other members are listed under the other filters.", Detail.EmptyText);

        Detail.SearchText = "zzz";
        Assert.Equal("Nobody matches.", Detail.EmptyText);
    }

    [Fact]
    public async Task A_row_tells_what_the_next_belt_asks_for_and_what_is_missing()
    {
        DefaultSuggestions();
        await ShownAsync();
        var anna = Detail.Suggestions.Single(r => r.Name == "Anna");
        var ben = Detail.Suggestions.Single(r => r.Name == "Ben");
        var clara = Detail.Suggestions.Single(r => r.Name == "Clara");

        Assert.Equal(("White", "Yellow"), (anna.Belt.Label, anna.NextBelt?.Label));
        Assert.Equal(("11 (min. 8)", "7 mo. (min. 3)", "90 %", "9 / 10"), (anna.AgeText, anna.WaitText, anna.AttendanceText, anna.AttendedOfInvitedText));
        Assert.Equal("Fit", anna.VerdictText);
        Assert.Equal("Meets all requirements for: Yellow.", anna.ReasonText);

        Assert.Equal(("1 mo. (min. 3)", CheckState.NotMet), (ben.WaitText, ben.WaitState));
        Assert.Equal("Waited 1 months by the exam day, 3 needed.", ben.ReasonText);

        // Without a belt there is nothing it asks for.
        Assert.False(clara.HasBelt);
        Assert.False(clara.HasNextBelt);
        Assert.Equal(("–", "–"), (clara.AgeText, clara.WaitText));
        Assert.Equal("No belt assigned yet: set it on the Members page.", clara.ReasonText);
    }

    [Fact]
    public async Task What_is_not_known_is_a_question_mark_and_what_attendance_misses_is_named()
    {
        Suggestions(_next,
            Suggest(_anna.Id, "Anna", SuggestionVerdict.NeedsData, age: CheckState.Unknown, ageYears: null, wait: CheckState.Unknown, waited: null),
            Suggest(_ben.Id, "Ben", SuggestionVerdict.NotYet, attendance: QualificationStatus.BelowThreshold, invited: 10, attended: 4),
            Suggest(_clara.Id, "Clara", SuggestionVerdict.NotYet, attendance: QualificationStatus.InsufficientData, invited: 2, attended: 2));
        await ShownAsync();

        var anna = Detail.Suggestions.Single(r => r.Name == "Anna");
        Assert.Equal(("? (min. 8)", "? (min. 3)"), (anna.AgeText, anna.WaitText));
        Assert.Equal("Birth date not known (8 years needed). Waiting time not known: no belt date and no earlier exam.", anna.ReasonText);
        Assert.Equal("Attendance 40 % (4 of 10 events), 75 % needed.", Detail.Suggestions.Single(r => r.Name == "Ben").ReasonText);
        Assert.Equal("Invited to 2 events, at least 3 are needed for a verdict.", Detail.Suggestions.Single(r => r.Name == "Clara").ReasonText);
    }

    [Fact]
    public async Task The_waiting_time_says_where_it_counts_from()
    {
        Suggestions(_next, Suggest(_anna.Id, "Anna") with { LastExam = new DateOnly(2026, 3, 28), BeltSince = null, WaitingSince = new DateOnly(2026, 3, 28) });
        await ShownAsync();

        Assert.Equal("The day the belt was received is not known: counted from the last exam, 28 Mar 2026.", Detail.Suggestions[0].WaitTip);
        Assert.Equal("28 Mar 2026", Detail.Suggestions[0].LastExamText);
    }

    // --- What the suggestions are based on ----------------------------------------------------------------------------

    private static FilterSettingsDto Settings(string group = "g1", int percent = 75) =>
        new(group, new DateOnly(2026, 3, 28), new DateOnly(2026, 9, 26), null, null, [], percent, 3, null, null);

    [Fact]
    public async Task Without_a_saved_filter_the_default_is_the_basis()
    {
        DefaultSuggestions();

        await ShownAsync();

        Assert.Equal(["Default: previous exam to this exam"], Detail.BasisOptions.Select(o => o.Label));
        Assert.True(Detail.SelectedBasis.IsDefault);
        Assert.Equal((_next.Id, (int?)null), Assert.Single(_api.SuggestionRequests));
        Assert.Equal($"Attendance counted from 28 Mar 2026 to {Sep26}: at least 75 % of the events a member was invited to, and at least 3 invitations.", Detail.BasisSummary);
    }

    [Fact]
    public async Task The_filters_saved_for_the_exam_are_offered_first_then_those_of_other_exams_and_never_those_of_other_groups()
    {
        // Filters: two for the exam planned (the newer one is "Lenient"), one for the past exam, one for a group that is not this one.
        _api.Exams[_api.Exams.IndexOf(_next)] = _next with
        {
            Filters =
            [
                new SavedFilterDto(101, _next.Id, "Strict", Settings(percent: 100), new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
                new SavedFilterDto(102, _next.Id, "Lenient", Settings(percent: 50), new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
                new SavedFilterDto(103, _next.Id, "Of the team", Settings(group: "g2"), new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero)),
            ],
        };
        _api.Exams[_api.Exams.IndexOf(_past)] = _past with
        {
            Filters = [new SavedFilterDto(104, _past.Id, "Spring", Settings(), new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))],
        };
        DefaultSuggestions();

        await ShownAsync();

        Assert.Equal(
            ["Default: previous exam to this exam", "Lenient", "Strict", "Spring (saved for Gürtelprüfung, 28 Mar 2026)"],
            Detail.BasisOptions.Select(o => o.Label));
        // The filter saved last for this exam is the one the user made it for.
        Assert.Equal("Lenient", Detail.SelectedBasis.Label);
        Assert.Equal((_next.Id, (int?)102), Assert.Single(_api.SuggestionRequests));
    }

    [Fact]
    public async Task A_new_exam_without_filters_of_its_own_starts_with_the_default_although_other_exams_have_filters()
    {
        _api.Exams[_api.Exams.IndexOf(_past)] = _past with
        {
            Filters = [new SavedFilterDto(104, _past.Id, "Spring", Settings(), new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))],
        };
        DefaultSuggestions();

        await ShownAsync();

        Assert.Equal(["Default: previous exam to this exam", "Spring (saved for Gürtelprüfung, 28 Mar 2026)"], Detail.BasisOptions.Select(o => o.Label));
        Assert.True(Detail.SelectedBasis.IsDefault);
        Assert.Equal((_next.Id, (int?)null), Assert.Single(_api.SuggestionRequests));
    }

    [Fact]
    public async Task Defining_a_filter_asks_the_client_to_show_the_statistics_page_if_the_user_may_see_it()
    {
        await ShownAsync();
        var requests = 0;
        _vm.OpenStatisticsRequested += (_, _) => requests++;

        Assert.True(_vm.CanOpenStatistics);
        _vm.OpenStatisticsCommand.Execute(null);
        Assert.Equal(1, requests);

        _shell.Session.User = TestAccounts.WithPermissions(1, "anna", Permission.ExamsView); // may not use that page
        Assert.False(_vm.CanOpenStatistics);
        _vm.OpenStatisticsCommand.Execute(null);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Choosing_another_basis_asks_again_and_the_choice_is_remembered_for_the_exam()
    {
        _api.Exams[_api.Exams.IndexOf(_next)] = _next with
        {
            Filters = [new SavedFilterDto(101, _next.Id, "Strict", Settings(percent: 100), new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))],
        };
        DefaultSuggestions();
        await ShownAsync();

        Detail.SelectedBasis = Detail.BasisOptions[0]; // the default instead of "Strict"
        await Detail.Loaded;
        await SelectAsync(_past);
        await SelectAsync(_next);

        Assert.Equal([(_next.Id, (int?)101), (_next.Id, (int?)null), (_next.Id, (int?)null)], _api.SuggestionRequests);
        Assert.True(Detail.SelectedBasis.IsDefault); // what was chosen for this exam is chosen again
    }

    [Fact]
    public async Task Without_belts_the_page_says_the_suggestions_need_them()
    {
        _api.Belts.Clear();
        _api.Members.Clear();
        DefaultSuggestions();

        await ShownAsync();

        Assert.True(Detail.HasNoBelts);
    }

    // --- Saving members for the exam --------------------------------------------------------------------------------

    [Fact]
    public async Task The_ticks_start_as_what_is_saved_and_the_buttons_are_only_enabled_when_they_would_change_something()
    {
        Suggestions(_next,
            Suggest(_anna.Id, "Anna"),
            Suggest(_ben.Id, "Ben", saved: true),
            Suggest(_clara.Id, "Clara", SuggestionVerdict.NotYet));
        await ShownAsync();

        // Ben is saved and ticked; Anna is fit and not ticked.
        Assert.Equal([false, true, false], Detail.Suggestions.Select(r => r.IsSelected));
        Assert.Equal(1, Detail.SelectedCount);
        Assert.True(Detail.SelectFitCommand.CanExecute(null));
        Assert.True(Detail.ClearSelectionCommand.CanExecute(null));
        Assert.False(Detail.DiscardChangesCommand.CanExecute(null)); // nothing is different from what is saved
        Assert.False(Detail.SaveSelectionCommand.CanExecute(null));

        Detail.SelectFitCommand.Execute(null);

        Assert.Equal([true, true, false], Detail.Suggestions.Select(r => r.IsSelected));
        Assert.False(Detail.SelectFitCommand.CanExecute(null)); // every fit member is ticked
        Assert.True(Detail.DiscardChangesCommand.CanExecute(null));
        Assert.True(Detail.SaveSelectionCommand.CanExecute(null));
        Assert.Equal("2 selected", Detail.SelectionText);

        Detail.DiscardChangesCommand.Execute(null);

        Assert.Equal([false, true, false], Detail.Suggestions.Select(r => r.IsSelected));
        Assert.False(Detail.SaveSelectionCommand.CanExecute(null));

        Detail.ClearSelectionCommand.Execute(null);
        Assert.False(Detail.ClearSelectionCommand.CanExecute(null));
        Assert.True(Detail.SaveSelectionCommand.CanExecute(null)); // Ben would be taken off the list
    }

    [Fact]
    public async Task Saving_sends_only_what_changed_and_the_page_shows_what_the_server_holds_afterwards()
    {
        Suggestions(_next, Suggest(_anna.Id, "Anna"), Suggest(_ben.Id, "Ben", saved: true), Suggest(_clara.Id, "Clara", SuggestionVerdict.NotYet));
        _api.ExamMembers[_next.Id] = [new ExamMemberDto(_ben.Id, "Ben", _white.Id, null, true, null)];
        await ShownAsync();
        var listRequests = _api.ExamListRequests;
        var changed = 0;
        Detail.Changed += (_, _) => changed++;

        Detail.Suggestions.Single(r => r.Name == "Anna").IsSelected = true;
        Detail.Suggestions.Single(r => r.Name == "Ben").IsSelected = false;
        await Detail.SaveSelectionCommand.ExecuteAsync(null);

        var sent = Assert.Single(_api.CandidateInputs);
        Assert.Equal([_anna.Id], sent.Add);
        Assert.Equal([_ben.Id], sent.Remove);
        Assert.Equal([true, false, false], Detail.Suggestions.Select(r => r.IsSaved));
        Assert.Equal([true, false, false], Detail.Suggestions.Select(r => r.IsSelected));
        Assert.False(Detail.SaveSelectionCommand.CanExecute(null));
        Assert.Equal("1 members saved for Gürtelprüfung.", Detail.NoticeText);
        Assert.Equal(1, changed);
        // The list counts the saved members, so it reads them again.
        await Eventually(() => _api.ExamListRequests > listRequests);
    }

    [Fact]
    public async Task A_refused_save_keeps_the_ticks_and_says_why()
    {
        DefaultSuggestions();
        await ShownAsync();
        Detail.Suggestions[0].IsSelected = true;
        _api.FailWith = new PlannerApiException("The member does not belong to the group.", HttpStatusCode.BadRequest);

        await Detail.SaveSelectionCommand.ExecuteAsync(null);

        Assert.Equal("The member does not belong to the group.", Detail.ErrorMessage);
        Assert.True(Detail.Suggestions[0].IsSelected);
        Assert.False(Detail.Suggestions[0].IsSaved);
        Assert.True(Detail.SaveSelectionCommand.CanExecute(null)); // it can be tried again
    }

    [Fact]
    public async Task Who_is_saved_and_not_invited_on_spond_yet_is_counted()
    {
        Suggestions(_next,
            Suggest(_anna.Id, "Anna", saved: true, invitation: AttendanceMark.Unanswered),
            Suggest(_ben.Id, "Ben", saved: true),
            Suggest(_clara.Id, "Clara"));

        await ShownAsync();

        Assert.Equal(1, Detail.SavedNotInvitedCount);
        Assert.True(Detail.HasSavedNotInvited);
        Assert.Equal("1 saved members are not invited on Spond yet.", Detail.SavedNotInvitedText);
        Assert.Equal(["No answer yet", "Not invited", "Not invited"], Detail.Suggestions.Select(r => r.InvitationText));
    }

    [Fact]
    public async Task What_was_ticked_survives_choosing_another_basis_and_reading_the_same_exam_again()
    {
        _api.Exams[_api.Exams.IndexOf(_next)] = _next with
        {
            Filters = [new SavedFilterDto(101, _next.Id, "Strict", Settings(percent: 100), new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))],
        };
        DefaultSuggestions();
        await ShownAsync();
        Detail.Suggestions.Single(r => r.Name == "Anna").IsSelected = true;

        Detail.SelectedBasis = Detail.BasisOptions[0];
        await Detail.Loaded;

        Assert.True(Detail.Suggestions.Single(r => r.Name == "Anna").IsSelected);
        Assert.True(Detail.HasChanges);

        // Ticks that are not saved keep the page from reading again while the user is at it.
        await _vm.ActivateAsync();
        Assert.Equal(1, _api.ExamListRequests);
    }

    [Fact]
    public async Task Without_unsaved_ticks_the_page_reads_again_every_time_it_is_shown()
    {
        DefaultSuggestions();
        await ShownAsync();

        _vm.Deactivate();
        await _vm.ActivateAsync();

        Assert.Equal(2, _api.ExamListRequests);
    }

    // --- Who attended ----------------------------------------------------------------------------------------------------

    private void PastPeople()
    {
        _api.ExamMembers[_past.Id] =
        [
            new ExamMemberDto(_anna.Id, "Anna", _white.Id, AttendanceMark.Attended, false, null),
            new ExamMemberDto(_ben.Id, "Ben", _white.Id, AttendanceMark.Attended, false, _yellow.Id),
            new ExamMemberDto(_clara.Id, "Clara", null, AttendanceMark.Declined, false, null),
        ];
    }

    [Fact]
    public async Task A_past_exam_lists_who_attended_with_the_belt_each_received_and_counts_the_rest()
    {
        PastPeople();
        await ShownAsync();

        await SelectAsync(_past);

        Assert.Equal(["Anna", "Ben"], Detail.Attendees.Select(a => a.Name)); // Clara declined
        Assert.Equal(["No belt", "Yellow"], Detail.Attendees.Select(a => a.SelectedBelt!.Label));
        Assert.Equal(["Invited 3", "Attended 2", "Declined 1", "No answer 0", "Belts handed out 1"],
            Detail.ResultKpis.Select(k => $"{k.Label} {k.Value}"));
        Assert.False(Detail.NobodyAttended);
    }

    [Fact]
    public async Task Somebody_who_got_a_belt_without_accepting_is_listed_too()
    {
        _api.ExamMembers[_past.Id] = [new ExamMemberDto(_clara.Id, "Clara", _yellow.Id, AttendanceMark.Declined, false, _yellow.Id)];
        await ShownAsync();

        await SelectAsync(_past);

        Assert.Equal(["Clara"], Detail.Attendees.Select(a => a.Name));
        Assert.Equal("Declined", Detail.Attendees[0].AnswerText);
    }

    [Fact]
    public async Task A_past_exam_nobody_accepted_says_so()
    {
        _api.ExamMembers[_past.Id] = [new ExamMemberDto(_clara.Id, "Clara", null, AttendanceMark.Declined, false, null)];
        await ShownAsync();

        await SelectAsync(_past);

        Assert.True(Detail.NobodyAttended);
        Assert.Empty(Detail.Attendees);
    }

    [Fact]
    public async Task Choosing_a_belt_records_it_at_once_and_the_row_and_the_counts_follow()
    {
        PastPeople();
        await ShownAsync();
        await SelectAsync(_past);
        var anna = Detail.Attendees.Single(a => a.Name == "Anna");

        anna.SelectedBelt = Detail.BeltOptions.Single(o => o.Id == _yellow.Id);
        await Eventually(() => Detail.HasNotice);

        Assert.Equal(["set-result:" + _past.Id + ":" + _anna.Id], _api.PlanningCalls);
        Assert.Equal(_yellow.Id, Assert.Single(_api.ResultInputs).BeltId);
        Assert.Equal("Anna received: Yellow.", Detail.NoticeText);
        Assert.Equal(_yellow.Id, anna.Member.ReceivedBeltId);
        Assert.Equal("2", Detail.ResultKpis.Single(k => k.Label == "Belts handed out").Value);
        // The member holds the belt from now on: the members page will show it.
        Assert.Equal(_yellow.Id, _api.Members.Single(m => m.Id == _anna.Id).BeltId);
    }

    [Fact]
    public async Task A_list_control_that_clears_its_selection_when_the_exams_are_read_again_does_not_empty_the_detail()
    {
        PastPeople();
        await ShownAsync();
        await SelectAsync(_past);
        // What a WPF list box does when it is given another list: it loses its selection and writes "nothing" back.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExamsViewModel.Exams)) _vm.SelectedExam = null;
        };
        var requests = _api.ExamListRequests;

        // Choosing a belt makes the page read the counts of the list again.
        Detail.Attendees.Single(a => a.Name == "Anna").SelectedBelt = Detail.BeltOptions.Single(o => o.Id == _yellow.Id);
        await Eventually(() => _api.ExamListRequests > requests);
        await Task.Delay(100);

        Assert.Equal(_past.Id, _vm.SelectedExam?.Id);
        Assert.True(Detail.HasExam);
        Assert.Equal(["Anna", "Ben"], Detail.Attendees.Select(a => a.Name));
    }

    [Fact]
    public async Task Taking_a_belt_back_records_nothing_for_the_member_and_says_what_stays()
    {
        PastPeople();
        await ShownAsync();
        await SelectAsync(_past);
        var ben = Detail.Attendees.Single(a => a.Name == "Ben");

        ben.SelectedBelt = Detail.BeltOptions[0];
        await Eventually(() => Detail.HasNotice);

        Assert.Null(Assert.Single(_api.ResultInputs).BeltId);
        Assert.Null(ben.Member.ReceivedBeltId);
        Assert.StartsWith("Result of Ben removed.", Detail.NoticeText);
        Assert.Equal("0", Detail.ResultKpis.Single(k => k.Label == "Belts handed out").Value);
    }

    [Fact]
    public async Task A_refused_belt_is_taken_back_in_the_row_and_the_reason_is_shown()
    {
        PastPeople();
        await ShownAsync();
        await SelectAsync(_past);
        var anna = Detail.Attendees.Single(a => a.Name == "Anna");
        _api.FailWith = new PlannerApiException("The exam has not taken place yet.", HttpStatusCode.BadRequest);

        anna.SelectedBelt = Detail.BeltOptions.Single(o => o.Id == _yellow.Id);
        await Eventually(() => Detail.HasError);

        Assert.Equal("The exam has not taken place yet.", Detail.ErrorMessage);
        Assert.True(anna.SelectedBelt.IsNone); // the row shows what the server holds
        Assert.Null(anna.Member.ReceivedBeltId);
    }

    [Fact]
    public async Task The_exam_of_today_is_planned_and_given_results_at_once()
    {
        var todays = _next with { Id = 800, EventId = "today", Date = new DateOnly(2026, 6, 30), Name = "Prüfung heute" };
        _api.Exams.Add(todays);
        _api.ExamMembers[800] = [new ExamMemberDto(_anna.Id, "Anna", _white.Id, AttendanceMark.Attended, false, null)];
        _api.ExamSuggestions[800] = new ExamSuggestionsDto(800, todays.Name, todays.Date, null, null,
            new AttendanceFilter("g1", new DateOnly(2026, 3, 28), new DateOnly(2026, 6, 30)), [Suggest(_anna.Id, "Anna")]);

        await ShownAsync();
        Assert.Equal(800, _vm.SelectedExam?.Id); // today is the next one to come
        await Detail.Loaded;

        Assert.True(Detail.ShowsPlan);
        Assert.True(Detail.ShowsResults);
        Assert.Equal(["Anna"], Detail.Attendees.Select(a => a.Name));
        Assert.Equal(["Anna"], Detail.Suggestions.Select(r => r.Name));
        Assert.Equal("today", _vm.SelectedExam!.WhenText);
    }

    // --- Adding and deleting exams ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exam_is_added_by_choosing_an_event_that_is_not_an_exam_yet_and_it_becomes_the_chosen_one()
    {
        DefaultSuggestions();
        var vm = await ShownAsync();
        Assert.True(vm.BeginAddCommand.CanExecute(null));

        await vm.BeginAddCommand.ExecuteAsync(null);

        Assert.True(vm.IsAdding);
        Assert.False(vm.BeginAddCommand.CanExecute(null)); // the form is open already
        Assert.Equal(["train-1", "train-2"], vm.EventOptions.Where(o => o.Id != EventOption.NoneId).Select(o => o.Id)); // exam-1 and exam-2 are exams
        Assert.False(vm.CanAdd);
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.NewEventId = "train-2";
        Assert.True(vm.AddCommand.CanExecute(null));
        await vm.AddCommand.ExecuteAsync(null);
        await vm.Detail.Loaded;

        Assert.Contains("create-exam:train-2", _api.ExamCalls);
        Assert.False(vm.IsAdding);
        Assert.Equal("Training", vm.SelectedExam?.Name);
        Assert.Equal("Training was added.", vm.NoticeText);
        Assert.Equal(3, vm.Exams.Count);
    }

    [Fact]
    public async Task An_exam_that_could_not_be_added_keeps_the_form_open_with_the_reason()
    {
        var vm = await ShownAsync();
        await vm.BeginAddCommand.ExecuteAsync(null);
        vm.NewEventId = "train-1";
        _api.FailWith = new PlannerApiException("The event is already an exam.", HttpStatusCode.Conflict);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.IsAdding);
        Assert.Equal("The event is already an exam.", vm.ErrorMessage);
        Assert.Equal("train-1", vm.NewEventId);

        vm.CancelAddCommand.Execute(null);
        Assert.False(vm.IsAdding);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task The_subgroups_of_the_group_can_be_chosen_for_the_new_exam_and_are_sent_with_it()
    {
        var vm = await ShownAsync();
        Assert.False(vm.HasSubGroups); // nothing to choose before the form is open

        await vm.BeginAddCommand.ExecuteAsync(null);

        Assert.True(vm.HasSubGroups);
        Assert.Equal(["Kinder"], vm.SubGroupOptions.Select(o => o.Name));
        Assert.True(vm.IsEveryoneSelected);
        Assert.Equal("Everyone", vm.SubGroupSummary);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SubGroupOptions[0].IsSelected = true;

        Assert.Contains(nameof(ExamsViewModel.SubGroupSummary), changed);
        Assert.Contains(nameof(ExamsViewModel.IsEveryoneSelected), changed);
        Assert.False(vm.IsEveryoneSelected);
        Assert.Equal("Kinder", vm.SubGroupSummary);
        Assert.Equal(["sg1"], vm.SelectedSubGroupIds);

        vm.NewEventId = "train-2";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(["sg1"], _api.Exams.Single(e => e.EventId == "train-2").SubGroupIds);
        Assert.False(vm.HasSubGroups); // the form is closed
    }

    [Fact]
    public async Task Choosing_everyone_unticks_the_subgroups_and_a_new_exam_without_ticks_is_for_everyone()
    {
        var vm = await ShownAsync();
        await vm.BeginAddCommand.ExecuteAsync(null);
        vm.SubGroupOptions[0].IsSelected = true;

        vm.ClearSubGroupsCommand.Execute(null);

        Assert.True(vm.IsEveryoneSelected);
        Assert.Empty(vm.SelectedSubGroupIds);

        vm.NewEventId = "train-1";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Empty(_api.Exams.Single(e => e.EventId == "train-1").SubGroupIds!);
    }

    [Fact]
    public async Task The_subgroup_choice_does_not_outlive_the_form_and_a_group_without_subgroups_offers_none()
    {
        var vm = await ShownAsync();
        await vm.BeginAddCommand.ExecuteAsync(null);
        vm.SubGroupOptions[0].IsSelected = true;

        vm.CancelAddCommand.Execute(null);
        await vm.BeginAddCommand.ExecuteAsync(null);

        Assert.True(vm.IsEveryoneSelected); // the tick of the form before is gone

        vm.CancelAddCommand.Execute(null);
        _shell.SelectedGroupId = "g2";
        await Eventually(() => vm.IsEmpty);
        await vm.BeginAddCommand.ExecuteAsync(null);

        Assert.True(vm.IsAdding);
        Assert.False(vm.HasSubGroups); // the team has no subgroups: nothing to choose
    }

    [Fact]
    public async Task An_exam_is_only_deleted_after_a_question_and_the_next_one_is_chosen()
    {
        DefaultSuggestions();
        var vm = await ShownAsync();
        Assert.True(vm.AskDeleteCommand.CanExecute(null));

        vm.AskDeleteCommand.Execute(null);

        Assert.True(vm.IsConfirmingDelete);
        Assert.False(vm.AskDeleteCommand.CanExecute(null)); // asking again would change nothing
        Assert.StartsWith($"Delete Gürtelprüfung · {Sep26}?", vm.DeleteQuestion);
        Assert.Empty(_api.ExamCalls);

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Empty(_api.ExamCalls);

        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        await vm.Detail.Loaded;

        Assert.Equal([$"delete-exam:{_next.Id}"], _api.ExamCalls);
        Assert.Equal([_past.Id], vm.Exams.Select(e => e.Id));
        Assert.Equal(_past.Id, vm.SelectedExam?.Id);
        Assert.Equal($"Gürtelprüfung · {Sep26} was deleted.", vm.NoticeText);
    }

    [Fact]
    public async Task The_delete_question_is_dropped_when_another_exam_is_chosen()
    {
        DefaultSuggestions();
        var vm = await ShownAsync();
        vm.AskDeleteCommand.Execute(null);

        await SelectAsync(_past);

        Assert.False(vm.IsConfirmingDelete);
    }

    [Fact]
    public async Task Nothing_to_delete_without_an_exam()
    {
        _api.Exams.Clear();
        var vm = await ShownAsync();

        Assert.False(vm.AskDeleteCommand.CanExecute(null));
    }

    // --- Language ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language_without_asking_again()
    {
        DefaultSuggestions();
        PastPeople();
        var vm = await ShownAsync();
        var requests = _api.SuggestionRequests.Count;

        _language.Select("de");

        Assert.Equal("Prüfungen", vm.Title);
        Assert.Equal("Anstehend", vm.Exams[0].StatusText);
        Assert.Equal("in 88 Tagen", vm.Exams[0].WhenText);
        Assert.Equal("Geeignet 1", Detail.FilterOptions[0].Label);
        Assert.Equal("Erfüllt alle Anforderungen für: Yellow.", Detail.Suggestions[0].ReasonText);
        Assert.Equal("Standard: letzte Prüfung bis zu dieser", Detail.BasisOptions[0].Label);
        Assert.StartsWith("Anwesenheit gezählt vom", Detail.BasisSummary);
        Assert.Equal(requests, _api.SuggestionRequests.Count);

        await SelectAsync(_past);
        Assert.Equal("Eingeladen", Detail.ResultKpis[0].Label);
        _language.Select("en");
        Assert.Equal("Invited", Detail.ResultKpis[0].Label);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }
}
