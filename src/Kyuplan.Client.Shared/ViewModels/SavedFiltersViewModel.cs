using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>What the saved filters need from the statistics page that owns them.</summary>
internal interface IFilterHost
{
    /// <summary>The filter card as it stands, or <c>null</c> while it cannot be saved (see <see cref="FilterProblem"/>).</summary>
    FilterSettingsDto? CaptureFilter();

    /// <summary>Why <see cref="CaptureFilter"/> gives nothing, in the user's language.</summary>
    string? FilterProblem { get; }

    /// <summary>Whether the filter card already holds this filter, i.e. putting it there would change nothing.</summary>
    bool HoldsFilter(FilterSettingsDto filter);

    /// <summary>Whether a group with this id is offered by the server.</summary>
    bool KnowsGroup(string groupId);

    /// <summary>Puts a saved filter into the filter card (switching the group if it belongs to another one) and reloads.</summary>
    void ApplyFilter(FilterSettingsDto filter);

    /// <summary>The events of the selected group, led by the empty entry: what a new exam can be made of.</summary>
    IReadOnlyList<EventOption> EventOptions { get; }

    /// <summary>The exam the current time span ends at, as a proposal for a new exam.</summary>
    ExamSuggestion? ExamSuggestion { get; }

    /// <summary>Whether the signed-in user may do this (one of <see cref="Permission"/>).</summary>
    bool Can(string permission);
}

/// <param name="EventId">The event the time span ends at; <c>null</c> when dates define it.</param>
/// <param name="Date">The last day of the time span.</param>
internal sealed record ExamSuggestion(string? EventId, DateOnly Date);

/// <summary>What the user is asked to confirm before it happens: it overwrites or removes what the server holds.</summary>
public enum SavedFilterAction
{
    None,
    Update,
    DeleteFilter,
    DeleteExam,
}

/// <summary>
/// The filters the user saved on the server, each for one exam (one exam, many filters): choosing one puts it into the
/// filter card of the statistics page (and it can be put there again to take back later changes); the filter card as
/// it stands can be saved under a name for an existing exam or for a new one, a saved filter can be replaced by the
/// current one, and filters and exams can be deleted (replacing and deleting after a question). An exam is a Spond
/// event: a new exam is made by choosing the event that is the exam.
/// </summary>
public sealed partial class SavedFiltersViewModel : ViewModelBase
{
    private readonly IPlannerApi _api;
    private readonly IFilterHost _host;

    /// <summary>The "new exam" entry of <see cref="ExamChoices"/>; one instance, so that a selection of it stays valid.</summary>
    private readonly ExamOption _newExam = new();

    private bool _loaded;
    private bool _suppressApply;
    private string _suggestedName = "";

    internal SavedFiltersViewModel(IPlannerApi api, IFilterHost host)
    {
        _api = api;
        _host = host;
        ExamChoices = [_newExam];
        _selectedExam = _newExam;
    }

    // --- What the server holds ---------------------------------------------------------------------------

    /// <summary>All exams, the next one first, each with its filters.</summary>
    [ObservableProperty]
    private IReadOnlyList<ExamOption> _exams = [];

    /// <summary>Every saved filter of every exam, for a list that shows them one after the other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    private IReadOnlyList<SavedFilterOption> _filterChoices = [];

    /// <summary>The exams to save a filter for: the existing ones, then "new exam".</summary>
    [ObservableProperty]
    private IReadOnlyList<ExamOption> _examChoices = [];

    public bool HasFilters => FilterChoices.Count > 0;

    // --- What the user may do ----------------------------------------------------------------------------

    /// <summary>Whether the user may see the exams and their saved filters at all; without it the statistics page has no such part.</summary>
    public bool CanUse => _host.Can(Permission.ExamsView);

    /// <summary>Whether the user may make exams (a new exam is one of the choices to save a filter for).</summary>
    public bool CanMakeExams => _host.Can(Permission.ExamsEdit);

    /// <summary>Whether the user may save, replace and delete filters; saving needs an exam to save for, so also one that exists or the right to make one.</summary>
    public bool CanChangeFilters => _host.Can(Permission.ExamsFilters);

    /// <summary>The filter card can be saved: the user may, and there is an exam it can be saved for.</summary>
    public bool CanSaveFilter => CanUse && CanChangeFilters && (CanMakeExams || Exams.Count > 0);

    /// <summary>The permissions of the user changed: what depends on them is announced again and the exams are read again when they may be.</summary>
    internal void OnPermissionsChanged()
    {
        _loaded = false;
        OnPropertyChanged(string.Empty);
        AskUpdateCommand.NotifyCanExecuteChanged();
        AskDeleteFilterCommand.NotifyCanExecuteChanged();
        AskDeleteExamCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The events a new exam can be made of: those of the group the statistics page shows.</summary>
    public IReadOnlyList<EventOption> EventOptions => _host.EventOptions;

    // --- The filter in effect --------------------------------------------------------------------------

    /// <summary>The saved filter that was chosen last; choosing one puts it into the filter card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedFilterId), nameof(ConfirmText), nameof(CanReload), nameof(CanUpdate))]
    [NotifyCanExecuteChangedFor(nameof(ReloadSelectedCommand), nameof(AskUpdateCommand), nameof(AskDeleteFilterCommand))]
    private SavedFilterOption? _selectedFilter;

    public bool HasSelection => SelectedFilter is not null;

    /// <summary>A saved filter is chosen and the filter card no longer holds it: reloading it would take back changes.</summary>
    public bool CanReload => SelectedFilter is { } selected && !_host.HoldsFilter(selected.Settings);

    /// <summary>The filter card holds something else than the chosen saved filter, and it can be saved: replacing the filter would change it.</summary>
    public bool CanUpdate => CanChangeFilters && CanReload && _host.CaptureFilter() is not null;

    /// <summary>The filter card changed: whether it still holds the chosen saved filter must be asked again.</summary>
    internal void OnFilterCardChanged()
    {
        if (SelectedFilter is null) return; // nothing to compare with; choosing a filter asks again

        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(CanUpdate));
        ReloadSelectedCommand.NotifyCanExecuteChanged();
        AskUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The id of <see cref="SelectedFilter"/>, for lists that work with values instead of items; <c>null</c> = none.</summary>
    public int? SelectedFilterId
    {
        get => SelectedFilter?.Id;
        set => SelectedFilter = value is { } id ? FilterChoices.FirstOrDefault(f => f.Id == id) : null;
    }

    // --- Saving the filter card -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _newFilterName = "";

    /// <summary>The exam the filter is saved for; the "new exam" entry makes the event <see cref="NewExamEventId"/> an exam.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewExam), nameof(SelectedExamId), nameof(ConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(AskDeleteExamCommand))]
    private ExamOption? _selectedExam;

    public bool IsNewExam => SelectedExam is not { IsNew: false };

    /// <summary>The id of <see cref="SelectedExam"/>, for lists that work with values; 0 = a new exam.</summary>
    public int SelectedExamId
    {
        get => SelectedExam?.Id ?? 0;
        set => SelectedExam = ExamChoices.FirstOrDefault(e => e.Id == value) ?? _newExam;
    }

    /// <summary>The event that becomes the exam when the "new exam" entry is chosen; <see cref="EventOption.NoneId"/> = none chosen yet.</summary>
    [ObservableProperty]
    private string _newExamEventId = EventOption.NoneId;

    /// <summary>The entry of <see cref="EventOptions"/> behind <see cref="NewExamEventId"/>; <c>null</c> if none is chosen or it is not offered (any more).</summary>
    private EventOption? NewExamEvent => string.IsNullOrEmpty(NewExamEventId)
        ? null
        : EventOptions.FirstOrDefault(o => o.Id == NewExamEventId);

    // --- Replacing and deleting ----------------------------------------------------------------------

    /// <summary>What waits for the user's confirmation; nothing is replaced or deleted before it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(ConfirmText), nameof(ConfirmYesText))]
    private SavedFilterAction _pendingAction;

    public bool IsConfirming => PendingAction != SavedFilterAction.None;

    /// <summary>The question to confirm, e.g. <c>Delete the filter "Spring"?</c>.</summary>
    public string ConfirmText => PendingAction switch
    {
        SavedFilterAction.Update when SelectedFilter is { } filter =>
            string.Format(Strings.Saved_ConfirmUpdate, filter.Name),
        SavedFilterAction.DeleteFilter when SelectedFilter is { } filter =>
            string.Format(Strings.Saved_ConfirmDeleteFilter, filter.Name),
        SavedFilterAction.DeleteExam when SelectedExam is { IsNew: false } exam =>
            string.Format(Strings.Saved_ConfirmDeleteExam, exam.Name, exam.Filters.Count),
        _ => "",
    };

    /// <summary>The answer that confirms <see cref="ConfirmText"/>, e.g. <c>Yes, delete</c>.</summary>
    public string ConfirmYesText => PendingAction == SavedFilterAction.Update
        ? Strings.Saved_ConfirmYesUpdate
        : Strings.Saved_ConfirmYes;

    // --- Loading ---------------------------------------------------------------------------------------

    /// <summary>Loads the exams the first time (and again after <see cref="Invalidate"/>). Failures show in <see cref="ViewModelBase.ErrorMessage"/>.</summary>
    internal Task EnsureLoadedAsync() =>
        _loaded || !CanUse ? Task.CompletedTask : LoadAsync(ct => FetchAsync(ct, SelectedFilter?.Id));

    /// <summary>The next <see cref="EnsureLoadedAsync"/> asks the server again.</summary>
    internal void Invalidate() => _loaded = false;

    /// <summary>Forgets which filter was chosen, e.g. because the filter card was reset.</summary>
    internal void ClearSelection()
    {
        _suppressApply = true;
        try
        {
            SelectedFilter = null;
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>The page offers other events (another group, or fresh data).</summary>
    internal void OnEventOptionsChanged() => OnPropertyChanged(nameof(EventOptions));

    internal void OnLanguageChanged()
    {
        foreach (var exam in Exams) exam.Refresh();
        _newExam.Refresh();
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmYesText));
    }

    /// <summary>
    /// Asks the server for the exams and shows them. Lists are replaced wholesale (and a list control drops its
    /// selection when that happens), so what is chosen is chosen again afterwards, without applying it a second time.
    /// </summary>
    private async Task FetchAsync(CancellationToken cancellationToken, int? selectFilterId, int? selectExamId = null)
    {
        var data = await _api.GetExamsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var examId = selectExamId ?? (IsNewExam ? 0 : SelectedExamId);
        var exams = data.Select(e => new ExamOption(e)).ToList();

        _suppressApply = true;
        try
        {
            Exams = exams;
            FilterChoices = exams.SelectMany(e => e.Filters).ToList();
            ExamChoices = CanMakeExams ? [.. exams, _newExam] : exams;
            SelectedFilter = FilterChoices.FirstOrDefault(f => f.Id == selectFilterId);
            SelectedExam = ExamChoices.FirstOrDefault(e => !e.IsNew && e.Id == examId) ?? (CanMakeExams ? _newExam : ExamChoices.FirstOrDefault());
            OnPropertyChanged(nameof(CanSaveFilter));
            _loaded = true;
        }
        finally
        {
            _suppressApply = false;
        }
    }

    // --- Applying --------------------------------------------------------------------------------------

    partial void OnSelectedFilterChanged(SavedFilterOption? value)
    {
        PendingAction = SavedFilterAction.None;
        if (_suppressApply || value is null) return;

        Apply(value);
    }

    /// <summary>Puts the chosen saved filter into the filter card again, taking back what was changed since it was chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private void ReloadSelected()
    {
        PendingAction = SavedFilterAction.None;
        if (SelectedFilter is { } selected) Apply(selected);
    }

    private void Apply(SavedFilterOption filter)
    {
        if (!_host.KnowsGroup(filter.Settings.GroupId))
        {
            ErrorMessage = Strings.Saved_GroupGone;
            return;
        }

        ErrorMessage = null;
        _host.ApplyFilter(filter.Settings);
    }

    // --- Saving ----------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the form to save the filter card: for the exam the time span ends at (the exam of its last event, or, when
    /// dates define the span, the exam that is on its last day), otherwise for a new one, proposing that event.
    /// </summary>
    [RelayCommand]
    private void BeginSave()
    {
        if (!CanSaveFilter) return;

        ErrorMessage = null;
        PendingAction = SavedFilterAction.None;

        var suggestion = _host.ExamSuggestion;
        _suggestedName = NewFilterName = "";
        NewExamEventId = suggestion?.EventId ?? EventOption.NoneId;
        SelectedExam = (suggestion is null ? null
            : suggestion.EventId is { } eventId ? Exams.FirstOrDefault(e => e.EventId == eventId)
            : Exams.FirstOrDefault(e => e.Date == suggestion.Date)) ?? (CanMakeExams ? _newExam : Exams.FirstOrDefault());

        IsSaving = true;
        SuggestFilterName();
    }

    [RelayCommand]
    private void CancelSave()
    {
        IsSaving = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = NewFilterName.Trim();
        if (name.Length == 0)
        {
            ErrorMessage = Strings.Saved_NameRequired;
            return;
        }

        var newExamEvent = IsNewExam ? NewExamEvent : null;
        if (IsNewExam && newExamEvent is null)
        {
            ErrorMessage = Strings.Saved_ExamEventRequired;
            return;
        }

        if (_host.CaptureFilter() is not { } settings)
        {
            ErrorMessage = _host.FilterProblem ?? Strings.Saved_NoGroup;
            return;
        }

        // An event that already is an exam is used as it is instead of being made an exam a second time.
        var examId = newExamEvent is null ? SelectedExamId : Exams.FirstOrDefault(e => e.EventId == newExamEvent.Id)?.Id;

        await LoadAsync(async ct =>
        {
            if (examId is null)
            {
                // Only a new exam has no id yet, and its event was checked above.
                var exam = await _api.CreateExamAsync(new ExamInputDto(settings.GroupId, newExamEvent!.Id), ct);
                examId = exam.Id;

                // From here on the exam exists: show it, so that a second attempt does not try to create it again.
                await FetchAsync(ct, SelectedFilter?.Id, selectExamId: exam.Id);
            }

            var saved = await _api.CreateFilterAsync(examId.Value, new SavedFilterInputDto(name, settings), ct);
            await FetchAsync(ct, saved.Id, selectExamId: examId);
            IsSaving = false;
        });
    }

    /// <summary>Replaces the chosen saved filter (name and exam stay) with the filter card as it stands. Asked for first, see <see cref="AskUpdateCommand"/>.</summary>
    private async Task UpdateSelectedAsync()
    {
        if (SelectedFilter is not { } selected) return;

        if (_host.CaptureFilter() is not { } settings)
        {
            ErrorMessage = _host.FilterProblem ?? Strings.Saved_NoGroup;
            return;
        }

        await LoadAsync(async ct =>
        {
            await _api.UpdateFilterAsync(selected.ExamId, selected.Id, new SavedFilterInputDto(selected.Name, settings), ct);
            await FetchAsync(ct, selected.Id);
        });
    }

    // --- Suggesting a name -----------------------------------------------------------------------------

    partial void OnSelectedExamChanged(ExamOption? value)
    {
        PendingAction = SavedFilterAction.None;
        SuggestFilterName();
    }

    partial void OnNewExamEventIdChanged(string value) => SuggestFilterName();

    /// <summary>
    /// "Filter for Gürtelprüfung on 10 Oct 2026", following the exam chosen or the event chosen for a new one. A name
    /// the user changed is left alone: only a name that is empty or still the last suggestion is replaced.
    /// </summary>
    private void SuggestFilterName()
    {
        if (!IsSaving) return;

        var (name, date) = SelectedExam is { IsNew: false } exam
            ? (exam.Name, exam.Date)
            : (NewExamEvent?.EventName ?? "", NewExamEvent?.Day);

        var suggestion = date is { } d ? string.Format(Strings.Saved_DefaultFilterName, name, Display.Date(d)) : "";

        if (NewFilterName.Length == 0 || NewFilterName == _suggestedName) NewFilterName = suggestion;
        _suggestedName = suggestion;
    }

    // --- Replacing and deleting ----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private void AskUpdate() => PendingAction = SavedFilterAction.Update;

    private bool CanAskDeleteFilter() => CanChangeFilters && HasSelection;

    [RelayCommand(CanExecute = nameof(CanAskDeleteFilter))]
    private void AskDeleteFilter() => PendingAction = SavedFilterAction.DeleteFilter;

    [RelayCommand(CanExecute = nameof(CanAskDeleteExam))]
    private void AskDeleteExam() => PendingAction = SavedFilterAction.DeleteExam;

    private bool CanAskDeleteExam() => CanMakeExams && !IsNewExam;

    [RelayCommand]
    private void CancelPending() => PendingAction = SavedFilterAction.None;

    [RelayCommand]
    private async Task ConfirmPendingAsync()
    {
        var what = PendingAction;
        PendingAction = SavedFilterAction.None;

        if (what == SavedFilterAction.Update)
        {
            await UpdateSelectedAsync();
            return;
        }

        var filter = SelectedFilter;
        var exam = SelectedExam is { IsNew: false } chosen ? chosen : null;

        await LoadAsync(async ct =>
        {
            switch (what)
            {
                case SavedFilterAction.DeleteFilter when filter is not null:
                    await _api.DeleteFilterAsync(filter.ExamId, filter.Id, ct);
                    await FetchAsync(ct, selectFilterId: null);
                    break;

                // The exam takes its filters with it; if the chosen filter was one of them it is gone after the reload.
                case SavedFilterAction.DeleteExam when exam is not null:
                    await _api.DeleteExamAsync(exam.Id, ct);
                    await FetchAsync(ct, selectFilterId: filter?.ExamId == exam.Id ? null : filter?.Id);
                    break;
            }
        });
    }
}
