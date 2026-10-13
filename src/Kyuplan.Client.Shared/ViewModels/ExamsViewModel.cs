using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The exams page, the heart of the planner: the exams of the selected group (the ones to come first, then the past ones, newest first)
/// and, for the one that is chosen, <see cref="Detail"/>: who is fit for it, or who attended and which belt each received. An exam is
/// a Spond event, so a new one is made by choosing the event. Used by both clients.
/// </summary>
public sealed partial class ExamsViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly TimeProvider _time;
    private Func<string>? _notice;
    private bool _quietSelection;
    private int? _selectAfterLoad;

    public ExamsViewModel(ShellViewModel shell, IPlannerApi api, TimeProvider time) : base(shell)
    {
        _api = api;
        _time = time;
        _eventOptions = [EventOption.None];

        Detail = new ExamDetailViewModel(shell, api, time);
        Detail.Changed += (_, _) => _ = RefreshRowsAsync();

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Exams;

    protected override string? ViewPermission => Permission.ExamsView;

    /// <summary>Whether the user may make events exams and delete exams; without it the page only shows them.</summary>
    public bool CanEdit => Shell.Can(Permission.ExamsEdit);

    /// <summary>Whether the user may use the statistics page, where the filters for the suggestions are defined.</summary>
    public bool CanOpenStatistics => Shell.Can(Permission.StatisticsView);

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        Detail.OnPermissionsChanged();
        if (!CanEdit) CloseAdd();
        NotifyStateChanged();
    }

    /// <summary>The exam on show. One instance for every exam, so that a page can keep listening to it.</summary>
    public ExamDetailViewModel Detail { get; }

    /// <summary>
    /// Exams change on other pages (belts, what members hold, saved filters) and members answer on Spond: read again whenever the page
    /// is shown, unless the user is in the middle of something (a form, a question, ticks that are not saved yet).
    /// </summary>
    protected override bool ReloadsOnActivation => !IsAdding && !IsConfirmingDelete && !Detail.HasChanges;

    /// <summary>The exams of another group are another list: the old one goes away, and with it what was started on it.</summary>
    protected override void OnContextChanged()
    {
        CloseAdd();
        IsConfirmingDelete = false;
        _selectAfterLoad = null;
        SelectQuietly(null);
        Exams = [];
        HasLoaded = false;
        Detail.Clear();
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().Date);

    // --- The list --------------------------------------------------------------------------------------------

    /// <summary>The exams of the selected group: the ones to come (the next first), then the ones that took place (the latest first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<ExamRow> _exams = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    /// <summary>The group has no exam yet.</summary>
    public bool IsEmpty => HasLoaded && Exams.Count == 0;

    /// <summary>The exam on show in <see cref="Detail"/>. Choosing another one reads what the detail needs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteQuestion))]
    private ExamRow? _selectedExam;

    partial void OnSelectedExamChanged(ExamRow? value)
    {
        IsConfirmingDelete = false; // the question was about another exam
        NotifyStateChanged();
        if (!_quietSelection) _ = Detail.ShowAsync(value);
    }

    private void SelectQuietly(ExamRow? exam)
    {
        _quietSelection = true;
        try
        {
            SelectedExam = exam;
        }
        finally
        {
            _quietSelection = false;
        }
    }

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>Gürtelprüfung was added.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- Adding an exam ----------------------------------------------------------------------------------

    /// <summary>The form to make an event an exam is open.</summary>
    [ObservableProperty]
    private bool _isAdding;

    /// <summary>The events of the group that are not exams yet, led by the empty entry.</summary>
    [ObservableProperty]
    private IReadOnlyList<EventOption> _eventOptions;

    /// <summary>The event that becomes the exam; <see cref="EventOption.NoneId"/> = none chosen.</summary>
    [ObservableProperty]
    private string _newEventId = EventOption.NoneId;

    /// <summary>
    /// The subgroups of the group to choose from for the default filter of the new exam: the suggestions then only judge their members.
    /// Tick as many as you like; none ticked = everyone in the group.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubGroups), nameof(SelectedSubGroupIds), nameof(IsEveryoneSelected), nameof(SubGroupSummary))]
    private IReadOnlyList<SubGroupOption> _subGroupOptions = [];

    /// <summary>The group has subgroups: only then is there anything to choose.</summary>
    public bool HasSubGroups => SubGroupOptions.Count > 0;

    /// <summary>Ids of the ticked subgroups; empty = everyone in the group.</summary>
    public IReadOnlyList<string> SelectedSubGroupIds => SubGroupOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();

    /// <summary>True while no subgroup is ticked, i.e. the suggestions judge everyone in the group.</summary>
    public bool IsEveryoneSelected => !SubGroupOptions.Any(o => o.IsSelected);

    /// <summary>What the subgroup field shows when closed: the ticked subgroups by name, or "Everyone".</summary>
    public string SubGroupSummary => SubGroupOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList() is { Count: > 0 } names
        ? string.Join(", ", names)
        : Strings.Stats_Everyone;

    /// <summary>Unticks every subgroup: back to everyone in the group.</summary>
    [RelayCommand]
    private void ClearSubGroups()
    {
        foreach (var option in SubGroupOptions) option.IsSelected = false;
    }

    private void SetSubGroupOptions(IReadOnlyList<SubGroupOption> options)
    {
        foreach (var option in SubGroupOptions) option.PropertyChanged -= OnSubGroupOptionChanged;
        foreach (var option in options) option.PropertyChanged += OnSubGroupOptionChanged;
        SubGroupOptions = options;
    }

    private void OnSubGroupOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubGroupOption.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedSubGroupIds));
        OnPropertyChanged(nameof(IsEveryoneSelected));
        OnPropertyChanged(nameof(SubGroupSummary));
    }

    partial void OnIsAddingChanged(bool value) => NotifyStateChanged();

    partial void OnNewEventIdChanged(string value) => NotifyStateChanged();

    /// <summary>Opening the form again would change nothing.</summary>
    private bool CanBeginAdd() => CanEdit && !IsAdding && !IsBusy;

    /// <summary>The form holds an event of the list, and nothing else is going on.</summary>
    public bool CanAdd => CanEdit && IsAdding && !IsBusy && !string.IsNullOrEmpty(NewEventId) && EventOptions.Any(o => o.Id == NewEventId);

    [RelayCommand(CanExecute = nameof(CanBeginAdd))]
    private async Task BeginAddAsync()
    {
        var groupId = Shell.SelectedGroupId;
        if (groupId.Length == 0 || !CanBeginAdd()) return; // Execute does not ask CanExecute: what the user may not do is not done

        ErrorMessage = null;
        SetNotice(null);

        await LoadAsync(async ct =>
        {
            var choices = await _api.GetEventChoicesAsync(groupId, ct);
            ct.ThrowIfCancellationRequested();

            // An event is at most one exam: the ones that already are one are not offered again.
            var taken = Exams.Select(e => e.Exam.EventId).ToHashSet();
            EventOptions = [EventOption.None, .. choices.Events.Where(e => !taken.Contains(e.Id)).Select(e => new EventOption(e))];
            NewEventId = EventOption.NoneId;
            SetSubGroupOptions([.. (Shell.SelectedGroup?.SubGroups ?? []).Select(s => new SubGroupOption(s.Id, s.Name))]);
            IsAdding = true;
        });
    }

    [RelayCommand]
    private void CancelAdd()
    {
        ErrorMessage = null;
        CloseAdd();
    }

    private void CloseAdd()
    {
        IsAdding = false;
        NewEventId = EventOption.NoneId;
        SetSubGroupOptions([]);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (!CanAdd) return; // a form can also be submitted with Enter

        var groupId = Shell.SelectedGroupId;
        var input = new ExamInputDto(groupId, NewEventId, SelectedSubGroupIds);
        ExamDto? created = null;
        await LoadAsync(async ct => created = await _api.CreateExamAsync(input, ct));
        if (created is null) return; // the reason is in ErrorMessage; the form stays open so that another event can be chosen

        CloseAdd();
        var name = created.Name;
        SetNotice(() => string.Format(Strings.Exams_Added, name));

        _selectAfterLoad = created.Id;
        await InvalidateAsync();
    }

    // --- Deleting an exam ----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteQuestion))]
    private bool _isConfirmingDelete;

    partial void OnIsConfirmingDeleteChanged(bool value) => NotifyStateChanged();

    /// <summary>"Delete the exam … and what belongs to it?" for the exam on show.</summary>
    public string DeleteQuestion => SelectedExam is { } exam ? string.Format(Strings.Exams_DeleteAsk, exam.Label) : "";

    private bool CanAskDelete() => CanEdit && SelectedExam is not null && !IsConfirmingDelete && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAskDelete))]
    private void AskDelete()
    {
        SetNotice(null);
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var exam = SelectedExam;
        IsConfirmingDelete = false;
        if (exam is null) return;

        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteExamAsync(exam.Id, ct);
            deleted = true;
        });
        if (!deleted) return;

        var name = exam.Label;
        SetNotice(() => string.Format(Strings.Exams_Deleted, name));
        _selectAfterLoad = null;
        SelectQuietly(null);
        await InvalidateAsync();
    }

    // --- Defining a filter ---------------------------------------------------------------------------------

    /// <summary>
    /// The user wants to define a filter for the suggestions, which is done on the statistics page. A client that navigates by itself
    /// (WPF) shows that page when this is raised; Blazor links to it by URL instead.
    /// </summary>
    public event EventHandler? OpenStatisticsRequested;

    [RelayCommand]
    private void OpenStatistics()
    {
        if (CanOpenStatistics) OpenStatisticsRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        var groupId = Shell.SelectedGroupId;
        if (groupId.Length == 0) return; // the shell has no group (yet): there is nothing to ask for

        await LoadAsync(async ct =>
        {
            var exams = await _api.GetExamsAsync(ct);
            var belts = await _api.GetBeltsAsync(ct);
            ct.ThrowIfCancellationRequested();

            Show(exams.Where(e => e.GroupId == groupId).ToList(), keep: _selectAfterLoad ?? SelectedExam?.Id);
            _selectAfterLoad = null;
            Detail.SetData(belts, exams);
            HasLoaded = true;
        });

        if (HasError) return;
        await Detail.ShowAsync(SelectedExam);
    }

    /// <summary>Puts the exams of the group in order and chooses one: the one to keep, else the next one to come, else the latest that took place.</summary>
    private void Show(IReadOnlyList<ExamDto> exams, int? keep)
    {
        var today = Today;
        var rows = exams.Select(e => new ExamRow(e, today)).ToList();

        IEnumerable<ExamRow> Order(IEnumerable<ExamRow> list, bool newestFirst) =>
            (newestFirst ? list.OrderByDescending(r => r.Date) : list.OrderBy(r => r.Date))
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id);

        var ordered = Order(rows.Where(r => r.IsUpcoming), newestFirst: false).Concat(Order(rows.Where(r => r.IsPast), newestFirst: true)).ToList();

        // A list control that gets another list loses its selection and writes "nothing chosen" back (WPF): that is no choice of the user
        // and must not reach the detail, so the replacing of the list is quiet as well.
        // A list control that gets another list loses its selection and writes "nothing chosen" back (WPF): that is no choice of the user
        // and must not reach the detail, so the replacing of the list is quiet as well.
        _quietSelection = true;
        try
        {
            Exams = ordered;
            SelectedExam = ordered.FirstOrDefault(r => r.Id == keep) ?? ordered.FirstOrDefault();
        }
        finally
        {
            _quietSelection = false;
        }
    }

    /// <summary>The detail changed something the list counts (saved members, belts): reads the counts again, without touching the detail.</summary>
    private async Task RefreshRowsAsync()
    {
        try
        {
            var groupId = Shell.SelectedGroupId;
            var exams = await _api.GetExamsAsync();
            if (groupId == Shell.SelectedGroupId) Show(exams.Where(e => e.GroupId == groupId).ToList(), SelectedExam?.Id);
        }
        catch (PlannerApiException)
        {
            // The counts stay as they were; the next load reads them again.
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanAdd));
        BeginAddCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var exam in Exams) exam.Refresh();
        foreach (var option in EventOptions) option.Refresh();
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(DeleteQuestion));
        OnPropertyChanged(nameof(SubGroupSummary));
    }
}
