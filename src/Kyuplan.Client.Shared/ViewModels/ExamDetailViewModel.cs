using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The exam chosen on the exams page. While it is to come (or today): who is fit for it, judged with a filter saved on the statistics
/// page (or the default one), and the members the user wants to save for it. Once it has taken place (or today): who attended and
/// which belt each received. One instance serves every exam; <see cref="ShowAsync"/> points it at another.
/// </summary>
public sealed partial class ExamDetailViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private readonly TimeProvider _time;
    private readonly BeltOption _noBelt = new();
    private readonly Dictionary<int, int?> _basisByExam = [];
    private readonly SemaphoreSlim _results = new(1, 1); // belts are recorded one after the other, so that a quick second choice does not cancel the first
    private IReadOnlyList<ExamDto> _exams = [];
    private IReadOnlyList<ExamMemberDto> _members = [];
    private ExamSuggestionsDto? _suggested;
    private SuggestionRow[] _watched = [];
    private bool _quietBasis;
    private bool _resultsLoaded;
    private bool _paymentsLoaded;
    private Func<string>? _notice;
    private Task _loaded = Task.CompletedTask;

    public ExamDetailViewModel(ShellViewModel shell, IPlannerApi api, TimeProvider time)
    {
        _shell = shell;
        _api = api;
        _time = time;

        _beltOptions = [_noBelt];
        _basisOptions = [new BasisOption()];
        _selectedBasis = _basisOptions[0];
        FilterOptions = [.. Enum.GetValues<SuggestionFilter>().Select(kind => new SuggestionFilterOption(kind))];
        _selectedFilter = FilterOptions[0];

        shell.Language.Changed += (_, _) => OnLanguageChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifySelectionChanged();
        };
    }

    /// <summary>Raised when the user changed something other pages show: members saved for the exam, belts handed out.</summary>
    public event EventHandler? Changed;

    /// <summary>The API sent a spreadsheet of the exam on show; the client saves it where the user says.</summary>
    public event EventHandler<BackupFile>? ExportReady;

    /// <summary>Whether the user may save members for an exam (tick them); without it the list of who is fit is only to look at.</summary>
    public bool CanPlan => _shell.Can(Permission.ExamsCandidates);

    /// <summary>Whether the user may record the belts handed out at an exam; without it who received which is only to look at.</summary>
    public bool CanRecordResults => _shell.Can(Permission.ExamsResults);

    /// <summary>Whether the user may see who paid for an exam; the section is hidden without it.</summary>
    public bool CanViewPayments => _shell.Can(Permission.PaymentsView);

    /// <summary>Whether the user may record what members paid; without it the payments are only to look at.</summary>
    public bool CanEditPayments => _shell.Can(Permission.PaymentsEdit);

    /// <summary>The permissions of the user changed: what depends on them is announced again.</summary>
    internal void OnPermissionsChanged()
    {
        OnPropertyChanged(string.Empty);
        NotifySelectionChanged();
        foreach (var row in PaymentRows) row.PermissionsChanged();
    }

    /// <summary>The load that <see cref="ShowAsync"/> started last; it completes when the exam is on show (or the load failed).</summary>
    public Task Loaded => _loaded;

    // --- Which exam ----------------------------------------------------------------------------------------

    /// <summary>The exam on show; <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExam), nameof(ShowsPlan), nameof(ShowsResults), nameof(Heading))]
    private ExamRow? _exam;

    public bool HasExam => Exam is not null;

    /// <summary>Who is fit is only asked about an exam that is today or still to come.</summary>
    public bool ShowsPlan => Exam is { IsUpcoming: true };

    /// <summary>Belts are only recorded for an exam that is today or has taken place.</summary>
    public bool ShowsResults => Exam is { } exam && exam.Date <= Today;

    /// <summary>"Gürtelprüfung · 26 Sep 2026".</summary>
    public string Heading => Exam?.Label ?? "";

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().Date);

    /// <summary>The page hands over what the detail needs besides the exam: the belts, and every exam (their saved filters are what suggestions can be based on).</summary>
    internal void SetData(IReadOnlyList<BeltDto> belts, IReadOnlyList<ExamDto> exams)
    {
        _exams = exams;
        BeltOptions = [_noBelt, .. belts.Select(b => new BeltOption(b))];
        OnPropertyChanged(nameof(HasNoBelts));
    }

    /// <summary>The group changed, or the data was refreshed: whatever is on show belongs to the old state.</summary>
    internal void Clear()
    {
        _basisByExam.Clear();
        _ = ShowAsync(null);
    }

    /// <summary>Shows an exam (or nothing): reads what it needs from the API.</summary>
    internal Task ShowAsync(ExamRow? exam) => _loaded = ShowCoreAsync(exam);

    private async Task ShowCoreAsync(ExamRow? exam)
    {
        var sameExam = exam is not null && Exam?.Id == exam.Id;
        Exam = exam;
        ErrorMessage = null;
        if (!sameExam) SetNotice(null);

        // What the user ticked and has not saved survives a reload of the same exam (the page reads again when it is shown).
        var kept = sameExam ? _watched.Where(r => r.HasChange).ToDictionary(r => r.MemberId, r => r.IsSelected) : new Dictionary<int, bool>();
        _members = [];
        _resultsLoaded = false;
        var keptPayments = sameExam ? PaymentRows.Where(r => r.HasChange).ToDictionary(r => r.MemberId, r => (r.Paid.Text, r.Note, r.OwnBelt)) : [];
        ShowPayments(null, keptPayments);
        ShowSuggestions(null, new Dictionary<int, bool>());
        ShowAttendees();
        BuildBasisOptions();

        if (exam is null) return;

        await LoadAsync(async ct =>
        {
            if (ShowsPlan) await LoadPlanAsync(exam, kept, ct);
            if (ShowsResults) await LoadResultsAsync(exam, ct);
            if (CanViewPayments) await LoadPaymentsAsync(exam, keptPayments, ct);
        });
    }

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>5 members saved for the exam.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- Belts -------------------------------------------------------------------------------------------

    /// <summary>The belts, led by the empty entry. One list for all the selects, so that a selection of it stays valid.</summary>
    [ObservableProperty]
    private IReadOnlyList<BeltOption> _beltOptions;

    /// <summary>There are no belts: nobody can be judged for the next one, and none can be recorded.</summary>
    public bool HasNoBelts => BeltOptions.Count <= 1;

    private BeltOption BeltOf(int? beltId) => BeltOptions.FirstOrDefault(o => o.Id is not null && o.Id == beltId) ?? _noBelt;

    // --- Who is fit: what it is based on ------------------------------------------------------------------------

    /// <summary>The default basis, then the saved filters of the exam's group: those of this exam first, newest first, then those of the other exams.</summary>
    [ObservableProperty]
    private IReadOnlyList<BasisOption> _basisOptions;

    /// <summary>What the attendance is measured with. Choosing another one asks the API again.</summary>
    [ObservableProperty]
    private BasisOption _selectedBasis;

    private void BuildBasisOptions()
    {
        var options = new List<BasisOption> { new() };
        if (Exam is { } exam)
        {
            var own = _exams.FirstOrDefault(e => e.Id == exam.Id)?.Filters ?? [];
            options.AddRange(own.Where(f => f.Settings.GroupId == exam.Exam.GroupId)
                .OrderByDescending(f => f.UpdatedAt).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new BasisOption(f, exam.Exam, ofThisExam: true)));

            foreach (var other in _exams.Where(e => e.Id != exam.Id && e.GroupId == exam.Exam.GroupId).OrderByDescending(e => e.Date))
                options.AddRange(other.Filters.Where(f => f.Settings.GroupId == exam.Exam.GroupId)
                    .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(f => new BasisOption(f, other, ofThisExam: false)));
        }

        // The choice made for this exam before is chosen again; otherwise the newest filter saved for it, since that is what the user
        // saved the filter for; without one (a new exam has none, and the filters of other exams are only offered), the default.
        var wanted = Exam is { } e && _basisByExam.TryGetValue(e.Id, out var chosen) ? chosen : options.FirstOrDefault(o => o.IsOfThisExam)?.FilterId;

        _quietBasis = true;
        try
        {
            BasisOptions = options;
            SelectedBasis = options.FirstOrDefault(o => o.FilterId == wanted) ?? options[0];
        }
        finally
        {
            _quietBasis = false;
        }
    }

    partial void OnSelectedBasisChanged(BasisOption value)
    {
        if (_quietBasis || Exam is not { } exam || value is null) return;

        _basisByExam[exam.Id] = value.FilterId;
        _loaded = ReloadPlanAsync(exam);
    }

    /// <summary>The suggestions again, with the other basis. What was ticked stays ticked.</summary>
    private async Task ReloadPlanAsync(ExamRow exam)
    {
        var kept = _watched.Where(r => r.HasChange).ToDictionary(r => r.MemberId, r => r.IsSelected);
        await LoadAsync(ct => LoadPlanAsync(exam, kept, ct));
    }

    /// <summary>Says what the suggestions were measured with, in a sentence.</summary>
    public string BasisSummary
    {
        get
        {
            if (_suggested is not { } result) return "";

            var f = result.Filter;
            var text = string.Format(Strings.Plan_Summary,
                Display.Date(f.From), Display.Date(f.To), Display.Percent(f.MinAttendancePercent), Math.Max(1, f.MinInvitedEvents));

            if (f.SubGroupIds is { Count: > 0 } ids)
            {
                var names = ids.Select(id => _shell.SelectedGroup?.SubGroups.FirstOrDefault(s => s.Id == id)?.Name).OfType<string>().ToList();
                if (names.Count > 0) text += " " + string.Format(Strings.Plan_SummarySubGroups, string.Join(", ", names));
            }

            if (!string.IsNullOrWhiteSpace(f.EventNameContains)) text += " " + string.Format(Strings.Stats_SummaryOnly, f.EventNameContains.Trim());
            if (!string.IsNullOrWhiteSpace(f.EventNameExcludes)) text += " " + string.Format(Strings.Stats_SummaryIgnoring, f.EventNameExcludes.Trim());
            return text;
        }
    }

    // --- Who is fit: the list -----------------------------------------------------------------------------------

    /// <summary>Every member of the group, as the API ranked them (the fit first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    private IReadOnlyList<SuggestionRow> _suggestions = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The members the filter and the search let through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoneMatch), nameof(EmptyText))]
    private IReadOnlyList<SuggestionRow> _visibleSuggestions = [];

    /// <summary>The choices of the list filter, each with how many members it shows. Never replaced, so that a selection of the list stays valid.</summary>
    public IReadOnlyList<SuggestionFilterOption> FilterOptions { get; }

    /// <summary>Which members the list shows. Starts with the fit ones: they are what the page is for.</summary>
    [ObservableProperty]
    private SuggestionFilterOption _selectedFilter;

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSelectedFilterChanged(SuggestionFilterOption value)
    {
        if (value is not null) RebuildVisible();
    }

    partial void OnSearchTextChanged(string value) => RebuildVisible();

    /// <summary>The list is empty because of the filter or the search, not because the group has nobody.</summary>
    public bool NoneMatch => HasSuggestions && VisibleSuggestions.Count == 0;

    /// <summary>Why the list is empty: nobody is fit yet (and who else there is), or nobody matches the search.</summary>
    public string EmptyText
    {
        get
        {
            if (!NoneMatch) return "";
            if (!string.IsNullOrWhiteSpace(SearchText) || SelectedFilter?.Kind != SuggestionFilter.Fit) return Strings.Plan_NoneMatch;

            var others = Suggestions.Count - Count(SuggestionVerdict.Fit);
            return string.Format(Strings.Plan_NobodyFit, others);
        }
    }

    private int Count(SuggestionVerdict verdict) => Suggestions.Count(r => r.Verdict == verdict);

    private void ShowSuggestions(ExamSuggestionsDto? result, IReadOnlyDictionary<int, bool> kept)
    {
        foreach (var old in _watched) old.PropertyChanged -= OnRowChanged;

        _suggested = result;
        _watched = result is null
            ? []
            : [.. result.Members.Select(m => new SuggestionRow(
                m, BeltOf(m.BeltId), m.NextBeltId is { } next ? BeltOf(next) : null, result.Filter))];

        foreach (var row in _watched)
        {
            if (kept.TryGetValue(row.MemberId, out var ticked)) row.IsSelected = ticked;
            row.PropertyChanged += OnRowChanged;
        }

        Suggestions = _watched;
        foreach (var option in FilterOptions) option.Count = CountOf(option.Kind);
        RebuildVisible();
        OnPropertyChanged(nameof(BasisSummary));
        NotifySelectionChanged();
    }

    private int CountOf(SuggestionFilter kind) => kind switch
    {
        SuggestionFilter.Fit => Count(SuggestionVerdict.Fit),
        SuggestionFilter.NeedsData => Count(SuggestionVerdict.NeedsData),
        SuggestionFilter.NotYet => Count(SuggestionVerdict.NotYet),
        SuggestionFilter.NoNextBelt => Count(SuggestionVerdict.NoNextBelt),
        _ => Suggestions.Count,
    };

    private void RebuildVisible()
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var words = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Every word has to occur in the name; case and accents do not matter. The order is the API's.
        VisibleSuggestions =
        [
            .. Suggestions.Where(row => Shows(row.Verdict, SelectedFilter?.Kind ?? SuggestionFilter.Fit)
                && words.All(word => compare.IndexOf(row.Name, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)),
        ];
        OnPropertyChanged(nameof(EmptyText));
    }

    private static bool Shows(SuggestionVerdict verdict, SuggestionFilter filter) => filter switch
    {
        SuggestionFilter.Fit => verdict == SuggestionVerdict.Fit,
        SuggestionFilter.NeedsData => verdict == SuggestionVerdict.NeedsData,
        SuggestionFilter.NotYet => verdict == SuggestionVerdict.NotYet,
        SuggestionFilter.NoNextBelt => verdict == SuggestionVerdict.NoNextBelt,
        _ => true,
    };

    private async Task LoadPlanAsync(ExamRow exam, IReadOnlyDictionary<int, bool> kept, CancellationToken cancellationToken)
    {
        var result = await _api.GetExamSuggestionsAsync(exam.Id, SelectedBasis?.FilterId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); // another exam took over while this response was arriving

        ShowSuggestions(result, kept);
    }

    // --- Saving members for the exam -----------------------------------------------------------------------

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SuggestionRow.IsSelected) or nameof(SuggestionRow.IsSaved)) NotifySelectionChanged();
    }

    /// <summary>How many members are ticked.</summary>
    public int SelectedCount => _watched.Count(r => r.IsSelected);

    /// <summary>"3 selected".</summary>
    public string SelectionText => string.Format(Strings.Plan_SelectedCount, SelectedCount);

    /// <summary>How many of the saved members are not invited on Spond yet: those are the ones left to invite.</summary>
    public int SavedNotInvitedCount => _watched.Count(r => r.IsSaved && !r.IsInvited);

    public bool HasSavedNotInvited => SavedNotInvitedCount > 0;

    /// <summary>"2 of the saved members are not invited on Spond yet."</summary>
    public string SavedNotInvitedText => string.Format(Strings.Plan_SavedNotInvited, SavedNotInvitedCount);

    /// <summary>Somebody who is fit is not ticked yet: ticking them all would change something.</summary>
    public bool CanSelectFit => CanPlan && _watched.Any(r => r.IsFit && !r.IsSelected);

    /// <summary>Somebody is ticked: clearing would change something.</summary>
    public bool CanClearSelection => CanPlan && _watched.Any(r => r.IsSelected);

    /// <summary>A tick differs from what the server holds: saving or discarding would change something.</summary>
    public bool HasChanges => _watched.Any(r => r.HasChange);

    public bool CanSave => CanPlan && HasChanges && !IsBusy;

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SavedNotInvitedCount));
        OnPropertyChanged(nameof(HasSavedNotInvited));
        OnPropertyChanged(nameof(SavedNotInvitedText));
        OnPropertyChanged(nameof(CanSelectFit));
        OnPropertyChanged(nameof(CanClearSelection));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
        SelectFitCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ExportPaymentsCsvCommand.NotifyCanExecuteChanged();
        ExportPaymentsExcelCommand.NotifyCanExecuteChanged();
        ExportResultsCsvCommand.NotifyCanExecuteChanged();
        ExportResultsExcelCommand.NotifyCanExecuteChanged();
        DiscardChangesCommand.NotifyCanExecuteChanged();
        SaveSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ticks every member who is fit.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectFit))]
    private void SelectFit()
    {
        foreach (var row in _watched.Where(r => r.IsFit)) row.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        foreach (var row in _watched) row.IsSelected = false;
    }

    /// <summary>Puts the ticks back to what the server holds.</summary>
    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void DiscardChanges()
    {
        foreach (var row in _watched) row.IsSelected = row.IsSaved;
    }

    /// <summary>
    /// Saves the ticks for the exam: who is ticked and not saved is added, who is saved and not ticked is taken off. Members the list does
    /// not show (a filter for some subgroups leaves the others out) are not touched. Nobody is invited on Spond.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveSelectionAsync()
    {
        if (!CanSave || Exam is not { } exam) return;

        var add = _watched.Where(r => r.IsSelected && !r.IsSaved).Select(r => r.MemberId).ToList();
        var remove = _watched.Where(r => !r.IsSelected && r.IsSaved).Select(r => r.MemberId).ToList();

        IReadOnlyList<ExamMemberDto>? saved = null;
        await LoadAsync(async ct => saved = await _api.SetExamCandidatesAsync(exam.Id, new ExamCandidatesInputDto(add, remove), ct));
        if (saved is null || !ReferenceEquals(Exam, exam)) return; // refused (the reason is in ErrorMessage), or another exam is on show now

        var savedIds = saved.Where(m => m.IsPreSaved).Select(m => m.MemberId).ToHashSet();
        foreach (var row in _watched)
        {
            row.IsSaved = savedIds.Contains(row.MemberId);
            row.IsSelected = row.IsSaved;
        }

        var name = exam.Name;
        var count = savedIds.Count;
        SetNotice(() => string.Format(Strings.Plan_Saved, count, name));
        Changed?.Invoke(this, EventArgs.Empty);
        NotifySelectionChanged();
        await ReloadPaymentsAsync(exam);
    }

    // --- Who attended and which belt they received -----------------------------------------------------------

    /// <summary>Everybody who accepted the exam, and anybody who got a belt at it, by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttendees), nameof(NobodyAttended))]
    private IReadOnlyList<AttendeeRow> _attendees = [];

    public bool HasAttendees => Attendees.Count > 0;

    /// <summary>The exam on show has taken place, its people were read, and nobody accepted it.</summary>
    public bool NobodyAttended => ShowsResults && _resultsLoaded && !HasAttendees;

    /// <summary>Invited, accepted, declined, no answer, and the belts handed out.</summary>
    [ObservableProperty]
    private IReadOnlyList<KpiViewModel> _resultKpis = [];

    private async Task LoadResultsAsync(ExamRow exam, CancellationToken cancellationToken)
    {
        var members = await _api.GetExamMembersAsync(exam.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _members = members;
        _resultsLoaded = true;
        ShowAttendees();
    }

    private void ShowAttendees()
    {
        Attendees =
        [
            .. _members
                .Where(m => m.Invitation == AttendanceMark.Attended || m.ReceivedBeltId is not null)
                .Select(m => new AttendeeRow(m, BeltOf(m.ReceivedBeltId), OnBeltChosenAsync)),
        ];
        BuildResultKpis();
        OnPropertyChanged(nameof(NobodyAttended));
    }

    private void BuildResultKpis()
    {
        var invited = _members.Count(m => m.Invitation is not null);
        var attended = _members.Count(m => m.Invitation == AttendanceMark.Attended);
        var declined = _members.Count(m => m.Invitation == AttendanceMark.Declined);
        var unanswered = _members.Count(m => m.Invitation == AttendanceMark.Unanswered);
        var awarded = _members.Count(m => m.ReceivedBeltId is not null);

        ResultKpis =
        [
            new(Strings.Res_Invited, invited.ToString()),
            new(Strings.Res_Attended, attended.ToString(), Strings.Res_AttendedHint, KpiTone.Success),
            new(Strings.Res_Declined, declined.ToString()),
            new(Strings.Res_NoAnswer, unanswered.ToString()),
            new(Strings.Res_Awarded, awarded.ToString(), Strings.Res_AwardedHint, awarded > 0 ? KpiTone.Success : KpiTone.Neutral),
        ];
    }

    /// <summary>The user chose a belt for an attendee: record it. If the API refuses, the row shows what it holds again.</summary>
    private async Task OnBeltChosenAsync(AttendeeRow row, BeltOption? belt)
    {
        if (!CanRecordResults || Exam is not { } exam || belt is null) return;

        await _results.WaitAsync();
        try
        {
            if (!ReferenceEquals(Exam, exam)) return;
            if (belt.Id == row.Member.ReceivedBeltId) return; // nothing to record

            ExamMemberDto? saved = null;
            await LoadAsync(async ct => saved = await _api.SetExamResultAsync(exam.Id, row.MemberId, new ExamResultInputDto(belt.Id), ct));

            if (saved is null)
            {
                row.Reset(row.Member, BeltOf(row.Member.ReceivedBeltId)); // refused: show what the server holds
                return;
            }

            row.Reset(saved, BeltOf(saved.ReceivedBeltId));
            _members = [.. _members.Where(m => m.MemberId != saved.MemberId), saved];
            BuildResultKpis();

            var name = saved.Name;
            var beltName = belt.Label;
            SetNotice(belt.IsNone
                ? () => string.Format(Strings.Res_Cleared, name)
                : () => string.Format(Strings.Res_Recorded, name, beltName));
            Changed?.Invoke(this, EventArgs.Empty);
            await ReloadPaymentsAsync(exam);
        }
        finally
        {
            _results.Release();
        }
    }

    // --- Payments ------------------------------------------------------------------------------------------

    /// <summary>Everybody who pays for the exam, by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPayments), nameof(NobodyPays))]
    private IReadOnlyList<PaymentRow> _paymentRows = [];

    public bool HasPayments => PaymentRows.Count > 0;

    /// <summary>The payments were read and nobody owes anything yet.</summary>
    public bool NobodyPays => _paymentsLoaded && !HasPayments;

    /// <summary>"Paid 120.00 € of 200.00 €".</summary>
    public string PaymentTotalsText => string.Format(Strings.Pay_Totals,
        Display.Money(PaymentRows.Sum(r => r.Payment.Paid)), Display.Money(PaymentRows.Sum(r => r.Payment.Due)));

    /// <summary>"3 paid · 1 partly paid · 2 not paid".</summary>
    public string PaymentCountsText => string.Format(Strings.Pay_Counts,
        PaymentRows.Count(r => r.Status == PaymentStatus.Paid),
        PaymentRows.Count(r => r.Status == PaymentStatus.Partial),
        PaymentRows.Count(r => r.Status == PaymentStatus.Open));

    private async Task LoadPaymentsAsync(ExamRow exam, IReadOnlyDictionary<int, (string Paid, string Note, bool OwnBelt)> kept, CancellationToken cancellationToken)
    {
        var payments = await _api.GetExamPaymentsAsync(exam.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _paymentsLoaded = true;
        ShowPayments(payments, kept);
    }

    private void ShowPayments(ExamPaymentsDto? payments, IReadOnlyDictionary<int, (string Paid, string Note, bool OwnBelt)> kept)
    {
        if (payments is null) _paymentsLoaded = false;

        PaymentRows =
        [
            .. (payments?.Members ?? []).Select(m =>
            {
                var row = new PaymentRow(m, BeltOf(m.BeltId), () => CanEditPayments, OnPaymentSavedAsync);
                if (kept.TryGetValue(m.MemberId, out var typed))
                {
                    row.Paid.Text = typed.Paid;
                    row.Note = typed.Note;
                    row.OwnBelt = typed.OwnBelt;
                }

                return row;
            }),
        ];

        OnPropertyChanged(nameof(NobodyPays));
        OnPropertyChanged(nameof(PaymentTotalsText));
        OnPropertyChanged(nameof(PaymentCountsText));
    }

    /// <summary>Who owes something changes with who is saved for the exam and who received a belt: read the payments again, keeping what was typed.</summary>
    private async Task ReloadPaymentsAsync(ExamRow exam)
    {
        if (!CanViewPayments) return;

        var kept = PaymentRows.Where(r => r.HasChange).ToDictionary(r => r.MemberId, r => (r.Paid.Text, r.Note, r.OwnBelt));
        await LoadAsync(ct => LoadPaymentsAsync(exam, kept, ct));
    }

    /// <summary>The user saved a payment row: record it. If the API refuses, what was typed stays and the reason is shown.</summary>
    private async Task OnPaymentSavedAsync(PaymentRow row)
    {
        if (!CanEditPayments || Exam is not { } exam || !row.CanSave) return;

        await _results.WaitAsync();
        try
        {
            if (!ReferenceEquals(Exam, exam)) return;

            ExamPaymentDto? saved = null;
            var input = new ExamPaymentInputDto(row.Paid.Amount, null, string.IsNullOrWhiteSpace(row.Note) ? null : row.Note.Trim(), row.OwnBelt);
            await LoadAsync(async ct => saved = await _api.SetExamPaymentAsync(exam.Id, row.MemberId, input, ct));
            if (saved is null) return;

            row.Reset(saved);
            OnPropertyChanged(nameof(PaymentTotalsText));
            OnPropertyChanged(nameof(PaymentCountsText));

            var name = saved.Name;
            var paid = Display.Money(saved.Paid);
            SetNotice(() => string.Format(Strings.Pay_Saved, name, paid));
        }
        finally
        {
            _results.Release();
        }
    }

    // --- Export ----------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPaymentsCsvAsync() => ExportAsync(ExamExportKind.Payments, excel: false);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPaymentsExcelAsync() => ExportAsync(ExamExportKind.Payments, excel: true);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportResultsCsvAsync() => ExportAsync(ExamExportKind.Results, excel: false);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportResultsExcelAsync() => ExportAsync(ExamExportKind.Results, excel: true);

    private bool CanExport() => Exam is not null && !IsBusy;

    private async Task ExportAsync(ExamExportKind kind, bool excel)
    {
        if (Exam is not { } exam) return;

        BackupFile? file = null;
        await LoadAsync(async ct => file = await _api.DownloadExamExportAsync(exam.Id, kind, excel, ct));
        if (file is not null) ExportReady?.Invoke(this, file);
    }

    // --- Language --------------------------------------------------------------------------------------------

    private void OnLanguageChanged()
    {
        Exam?.Refresh();
        foreach (var belt in BeltOptions) belt.Refresh();
        foreach (var option in BasisOptions) option.Refresh();
        foreach (var option in FilterOptions) option.Refresh();
        foreach (var row in _watched) row.Refresh();
        foreach (var row in Attendees) row.Refresh();
        foreach (var row in PaymentRows) row.Refresh();
        OnPropertyChanged(nameof(PaymentTotalsText));
        OnPropertyChanged(nameof(PaymentCountsText));
        if (_resultsLoaded) BuildResultKpis();

        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(BasisSummary));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SavedNotInvitedText));
    }
}
