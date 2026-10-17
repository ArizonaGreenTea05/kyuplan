using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

public enum MemberStatusFilter
{
    All,
    Qualified,
    BelowThreshold,
    InsufficientData,
}

/// <summary>Columns of the member table the user can sort by.</summary>
public enum MemberSortKey
{
    Name,
    SubGroups,
    Invited,
    Attended,
    AttendanceRate,
    ResponseRate,
    CurrentStreak,
    DaysSinceLastAttended,
    SessionsPerWeek,
    Stars,
    EngagementScore,
}

/// <summary>
/// Who attended at least X % of the events they were invited to in a time span, plus the figures around it.
/// <para>
/// The span is defined by two events the user picks (by default the previous and the next exam, as the server
/// suggests) or, under the advanced filters, by two dates; whichever was changed last is in effect.
/// </para>
/// Changing a server-side filter reloads after a short pause; search, status filter and sorting work on the
/// loaded result and apply instantly.
/// <para>
/// The filter in effect is remembered by the client (<see cref="IFilterSettingsStore"/>) and comes back the next time
/// the page opens. <see cref="SavedFilters"/> keeps named filters on the server, one set per exam.
/// </para>
/// </summary>
public sealed partial class AttendanceStatisticsViewModel : PageViewModel, IFilterHost
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

    private readonly IPlannerApi _api;
    private readonly TimeProvider _time;
    private readonly IFilterSettingsStore _store;
    private CancellationTokenSource? _pendingReload;
    private bool _suppressReload;
    private bool _serverDefaultsApplied;
    private IReadOnlyList<MemberRowViewModel> _allMembers = [];
    private AttendanceStatisticsDto? _last;

    // The events to choose from and the span the server suggests. Fetched once per group and again after the
    // server data was refreshed; changing a filter reuses them.
    private EventChoicesDto? _choices;
    private string? _choicesGroupId;
    private bool _choicesStale = true;

    // A filter waiting to be put into the filter card: the one remembered from last time, or a saved one the user chose.
    // It is applied during the next load, when the subgroups and events of its group are known.
    private FilterSettingsDto? _pendingFilter;
    private bool _storedFilterTaken;

    public AttendanceStatisticsViewModel(
        ShellViewModel shell, IPlannerApi api, TimeProvider time, IFilterSettingsStore? store = null) : base(shell)
    {
        _api = api;
        _time = time;
        _store = store ?? new InMemoryFilterSettingsStore();
        SavedFilters = new SavedFiltersViewModel(api, this);
        SavedFilters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SavedFiltersViewModel.HasSelection)) NotifyResetChanged();
        };
        ResetFilters();
    }

    /// <summary>The filters saved on the server for exams.</summary>
    public SavedFiltersViewModel SavedFilters { get; }

    public override string Title => Strings.Nav_Statistics;

    protected override string? ViewPermission => Permission.StatisticsView;

    bool IFilterHost.Can(string permission) => Shell.Can(permission);

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        SavedFilters.OnPermissionsChanged();
    }

    // --- Time span: two events (default) or two dates (advanced) ---------------------------------------

    /// <summary>The events to define the time span with, led by the empty entry.</summary>
    [ObservableProperty]
    private IReadOnlyList<EventOption> _eventOptions = [EventOption.None];

    /// <summary>The event whose day starts the time span; <see cref="EventOption.NoneId"/> = none chosen.</summary>
    [ObservableProperty]
    private string _fromEventId = EventOption.NoneId;

    /// <summary>The event whose day ends the time span; <see cref="EventOption.NoneId"/> = none chosen.</summary>
    [ObservableProperty]
    private string _toEventId = EventOption.NoneId;

    /// <summary>True while the two events define the span, false while the two dates do. The one changed last wins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpanHint))]
    private bool _spanByEvents;

    /// <summary>First day when the dates define the span; while the events do, the day their first event resolved to.</summary>
    [ObservableProperty]
    private DateTime? _from;

    /// <summary>Last day, see <see cref="From"/>.</summary>
    [ObservableProperty]
    private DateTime? _to;

    /// <summary>Tells which of the two ways to define the span is in effect.</summary>
    public string SpanHint => SpanByEvents ? Strings.Stats_SpanHintEvents : Strings.Stats_SpanHintDates;

    // --- Other server-side filters -------------------------------------------------------------------

    /// <summary>Required share of attended invitations, 0–100.</summary>
    [ObservableProperty]
    private double _minAttendancePercent = 75;

    /// <summary>Members with fewer invitations get no verdict (too little data to judge).</summary>
    [ObservableProperty]
    private int _minInvitedEvents = 3;

    /// <summary>The subgroups of the group to choose from. Tick as many as you like; none ticked = everyone in the group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSubGroupIds), nameof(IsEveryoneSelected), nameof(SubGroupSummary))]
    private IReadOnlyList<SubGroupOption> _subGroupOptions = [];

    /// <summary>Ids of the ticked subgroups; empty = everyone in the group.</summary>
    public IReadOnlyList<string> SelectedSubGroupIds => SubGroupOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();

    /// <summary>True while no subgroup is ticked, i.e. the report covers everyone in the group.</summary>
    public bool IsEveryoneSelected => !SubGroupOptions.Any(o => o.IsSelected);

    /// <summary>What the member filter shows when closed: the ticked subgroups by name, or "Everyone".</summary>
    public string SubGroupSummary => SubGroupOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList() is { Count: > 0 } names
        ? string.Join(", ", names)
        : Strings.Stats_Everyone;

    /// <summary>Advanced: only count events whose name contains one of these terms (comma separated).</summary>
    [ObservableProperty]
    private string _eventNameContains = "";

    /// <summary>Advanced: ignore events whose name contains one of these terms (comma separated).</summary>
    [ObservableProperty]
    private string _eventNameExcludes = "";

    /// <summary>Whether the advanced filters (dates, quick ranges, event names) are shown.</summary>
    [ObservableProperty]
    private bool _showAdvanced;

    // --- Local view of the loaded result ---------------------------------------------------------------

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private MemberStatusFilter _statusFilter = MemberStatusFilter.All;

    [ObservableProperty]
    private MemberSortKey _sortKey = MemberSortKey.AttendanceRate;

    [ObservableProperty]
    private bool _sortDescending = true;

    // --- Results -------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _hasResults;

    [ObservableProperty]
    private string? _filterError;

    [ObservableProperty]
    private IReadOnlyList<KpiViewModel> _kpis = [];

    [ObservableProperty]
    private IReadOnlyList<MemberRowViewModel> _visibleMembers = [];

    [ObservableProperty]
    private string _membersCaption = "";

    [ObservableProperty]
    private IReadOnlyList<BarViewModel> _distribution = [];

    [ObservableProperty]
    private IReadOnlyList<BarViewModel> _trend = [];

    [ObservableProperty]
    private IReadOnlyList<EventRowViewModel> _events = [];

    [ObservableProperty]
    private string _resultTitle = "";

    /// <summary>True when a load finished without any held event in the selected span and filters.</summary>
    public bool HasNoResults => !HasResults && !IsBusy && !HasError && Shell.SelectedGroup is not null && _loadedOnce;

    private bool _loadedOnce;

    /// <summary>The required attendance as a share (0–1), e.g. for the line through the attendance bars.</summary>
    public double ThresholdFraction => Math.Clamp(MinAttendancePercent, 0, 100) / 100.0;

    /// <summary>The required attendance as text, e.g. "75 %".</summary>
    public string ThresholdText => Display.Percent(Math.Round(MinAttendancePercent));

    /// <summary>Names the required-attendance line in the table header, e.g. "75 % required".</summary>
    public string RequiredText => string.Format(Strings.Rate_RequiredShort, ThresholdText);

    /// <summary>The question the current filters ask, in a sentence — shown above the filters and updated as they change.</summary>
    public string FilterSummary
    {
        get
        {
            var period = SpanByEvents && Chosen(FromEventId) is { Day: { } firstDay } fromEvent && Chosen(ToEventId) is { Day: { } lastDay } toEvent
                ? string.Format(Strings.Stats_PeriodEvents,
                    fromEvent.EventName, Display.Date(firstDay), toEvent.EventName, Display.Date(lastDay))
                : Display.Period(FirstDay, LastDay);
            var text = string.Format(Strings.Stats_Summary,
                Display.Percent(Math.Round(MinAttendancePercent)), period, Math.Max(1, MinInvitedEvents));
            if (!string.IsNullOrWhiteSpace(EventNameContains)) text += " " + string.Format(Strings.Stats_SummaryOnly, EventNameContains.Trim());
            if (!string.IsNullOrWhiteSpace(EventNameExcludes)) text += " " + string.Format(Strings.Stats_SummaryIgnoring, EventNameExcludes.Trim());
            return text;
        }
    }

    private DateOnly FirstDay => DateOnly.FromDateTime(From ?? _time.GetLocalNow().Date.AddMonths(-6));

    private DateOnly LastDay => DateOnly.FromDateTime(To ?? _time.GetLocalNow().Date);

    private bool BothEventsChosen => !string.IsNullOrEmpty(FromEventId) && !string.IsNullOrEmpty(ToEventId);

    /// <summary>The entry of the event lists with this id; <c>null</c> for the empty entry and for events that are no longer offered.</summary>
    private EventOption? Chosen(string? id) =>
        string.IsNullOrEmpty(id) ? null : EventOptions.FirstOrDefault(o => o.Id == id);

    // --- Commands ------------------------------------------------------------------------------------

    /// <summary>Defines the span by dates: a quick range ending today.</summary>
    [RelayCommand]
    private void SetRange(string preset)
    {
        var today = _time.GetLocalNow().Date;
        var (from, to) = preset switch
        {
            "3m" => (today.AddMonths(-3).AddDays(1), today),
            "12m" => (today.AddMonths(-12).AddDays(1), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            _ => (today.AddMonths(-6).AddDays(1), today),
        };

        _suppressReload = true;
        try
        {
            From = from;
            To = to;
            SpanByEvents = false;
        }
        finally
        {
            _suppressReload = false;
        }

        ScheduleReload(immediate: true);
    }

    /// <summary>Unticks every subgroup: back to everyone in the group.</summary>
    [RelayCommand]
    private void ClearSubGroups()
    {
        foreach (var option in SubGroupOptions) option.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void ResetFilters()
    {
        _suppressReload = true;
        try
        {
            MinAttendancePercent = Shell.DefaultMinAttendancePercent;
            MinInvitedEvents = Shell.DefaultMinInvitedEvents;
            ClearSubGroups();
            EventNameContains = "";
            EventNameExcludes = "";
            SearchText = "";
            StatusFilter = MemberStatusFilter.All;
            ApplyDefaultSpan();
            SavedFilters.ClearSelection();
        }
        finally
        {
            _suppressReload = false;
        }

        if (_loadedOnce) ScheduleReload(immediate: true);
    }

    /// <summary>
    /// The span the page starts with: the exams the server suggests (the previous one to the next one), or, when it
    /// has none to suggest, the last six months. Call with reloads suppressed.
    /// </summary>
    private void ApplyDefaultSpan()
    {
        From = DefaultFrom;
        To = DefaultTo;

        if (SuggestedEvents is (var first, var last))
        {
            FromEventId = first;
            ToEventId = last;
            SpanByEvents = true;
        }
        else
        {
            FromEventId = ToEventId = EventOption.NoneId;
            SpanByEvents = false;
        }
    }

    private DateTime DefaultFrom => _time.GetLocalNow().Date.AddMonths(-6).AddDays(1);

    private DateTime DefaultTo => _time.GetLocalNow().Date;

    /// <summary>The previous and the next exam as the server suggests them; <c>null</c> when it has none to suggest.</summary>
    private (string From, string To)? SuggestedEvents =>
        _choices is { SuggestedFromEventId: { } first, SuggestedToEventId: { } last } ? (first, last) : null;

    /// <summary>Whether the filter card holds the span <see cref="ApplyDefaultSpan"/> sets.</summary>
    private bool HasDefaultSpan => SuggestedEvents is (var first, var last)
        ? SpanByEvents && FromEventId == first && ToEventId == last
        : !SpanByEvents && FromEventId == EventOption.NoneId && ToEventId == EventOption.NoneId
            && From == DefaultFrom && To == DefaultTo;

    /// <summary>
    /// Whether resetting would change anything: something in the filter card or the member view differs from what
    /// <see cref="ResetFilters"/> sets, or a saved filter is chosen (resetting forgets it).
    /// </summary>
    public bool CanReset =>
        SavedFilters.HasSelection
        || MinAttendancePercent != Shell.DefaultMinAttendancePercent
        || MinInvitedEvents != Shell.DefaultMinInvitedEvents
        || !IsEveryoneSelected
        || EventNameContains.Length > 0
        || EventNameExcludes.Length > 0
        || SearchText.Length > 0
        || StatusFilter != MemberStatusFilter.All
        || !HasDefaultSpan;

    /// <summary>Sorts by <paramref name="key"/>; choosing the active column again flips the direction.</summary>
    [RelayCommand]
    private void SortBy(MemberSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = key is not MemberSortKey.Name and not MemberSortKey.SubGroups and not MemberSortKey.DaysSinceLastAttended;
        }
    }

    // --- Reacting to changes -------------------------------------------------------------------------

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(FromEventId) or nameof(ToEventId):
                OnPropertyChanged(nameof(FilterSummary));
                if (!_suppressReload)
                {
                    // One event alone says nothing yet: wait for the other, unless the events already define the span.
                    if (BothEventsChosen) SpanByEvents = true;
                    if (SpanByEvents) ScheduleReload();
                }
                break;
            case nameof(From) or nameof(To):
                OnPropertyChanged(nameof(FilterSummary));
                if (!_suppressReload)
                {
                    SpanByEvents = false;
                    ScheduleReload();
                }
                break;
            case nameof(SpanByEvents):
                OnPropertyChanged(nameof(FilterSummary));
                break;
            case nameof(EventOptions):
                SavedFilters.OnEventOptionsChanged();
                break;
            case nameof(MinAttendancePercent) or nameof(MinInvitedEvents)
                or nameof(EventNameContains) or nameof(EventNameExcludes):
                OnPropertyChanged(nameof(FilterSummary));
                if (e.PropertyName == nameof(MinAttendancePercent))
                {
                    OnPropertyChanged(nameof(ThresholdFraction));
                    OnPropertyChanged(nameof(ThresholdText));
                    OnPropertyChanged(nameof(RequiredText));
                }
                if (!_suppressReload) ScheduleReload();
                break;
            case nameof(SearchText) or nameof(StatusFilter) or nameof(SortKey) or nameof(SortDescending):
                RebuildVisibleMembers();
                break;
            case nameof(IsBusy) or nameof(ErrorMessage):
                OnPropertyChanged(nameof(HasNoResults));
                break;
        }

        if (e.PropertyName is nameof(FromEventId) or nameof(ToEventId) or nameof(SpanByEvents) or nameof(From) or nameof(To)
            or nameof(MinAttendancePercent) or nameof(MinInvitedEvents) or nameof(EventNameContains) or nameof(EventNameExcludes)
            or nameof(SubGroupOptions) or nameof(EventOptions) or nameof(SearchText) or nameof(StatusFilter))
        {
            OnFilterCardChanged();
        }
    }

    /// <summary>
    /// The filter card (or the member view next to it) changed, so the buttons that only make sense while it differs
    /// from something must be asked again: the saved filter that is chosen, and the defaults.
    /// </summary>
    private void OnFilterCardChanged()
    {
        NotifyResetChanged();
        SavedFilters.OnFilterCardChanged();
    }

    private void NotifyResetChanged()
    {
        OnPropertyChanged(nameof(CanReset));
        ResetFiltersCommand.NotifyCanExecuteChanged();
    }

    protected override void OnContextChanged()
    {
        _choicesStale = true;
        SavedFilters.Invalidate();
        OnFilterCardChanged(); // another group: the filter card and the filters it is compared with are no longer the same thing
    }

    /// <summary>Ticking or unticking a subgroup reloads after the usual pause, so several ticks in a row load once.</summary>
    private void OnSubGroupOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubGroupOption.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedSubGroupIds));
        OnPropertyChanged(nameof(IsEveryoneSelected));
        OnPropertyChanged(nameof(SubGroupSummary));
        OnFilterCardChanged();
        if (!_suppressReload) ScheduleReload();
    }

    private async void ScheduleReload(bool immediate = false)
    {
        _pendingReload?.Cancel();
        var pending = _pendingReload = new CancellationTokenSource();

        try
        {
            if (!immediate) await Task.Delay(Debounce, _time, pending.Token);
            await InvalidateAsync();
        }
        catch (OperationCanceledException)
        {
            // Another change arrived within the debounce window; that one will load.
        }
    }

    protected override Task ReloadAsync()
    {
        _ = SavedFilters.EnsureLoadedAsync();

        // The first load of the session starts from the filter the client remembered.
        if (!_storedFilterTaken)
        {
            _storedFilterTaken = true;
            _pendingFilter ??= _store.Load();
        }

        if (_pendingFilter is { } pending && Shell.Groups.Count > 0 && pending.GroupId != Shell.SelectedGroupId)
        {
            if (Shell.Groups.Any(g => g.Id == pending.GroupId))
            {
                // The filter belongs to another group. Choosing that group reloads the page, and that load applies it.
                Shell.SelectedGroupId = pending.GroupId;
                return Task.CompletedTask;
            }

            _pendingFilter = null; // its group does not exist any more
        }

        return LoadAsync(async ct =>
        {
            if (Shell.SelectedGroup is not { } group)
            {
                HasResults = false;
                return;
            }

            ApplyServerDefaultsOnce();
            SyncSubGroups(group);
            await SyncEventChoicesAsync(group.Id, ct);
            ApplyPendingFilter(group);
            OnFilterCardChanged(); // the group, the events on offer and the defaults are settled now

            FilterError = ValidateSpan();
            if (FilterError is not null) return;

            _store.Save(CaptureFilter(group.Id));
            var result = await _api.GetAttendanceAsync(BuildQuery(group.Id), ct);
            ct.ThrowIfCancellationRequested(); // a newer load took over while this response was arriving
            Apply(result);
        });
    }

    /// <summary>Puts the waiting filter into the filter card, if it is meant for this group. Needs the subgroups and events of the group.</summary>
    private void ApplyPendingFilter(GroupDto group)
    {
        if (_pendingFilter is not { } filter || filter.GroupId != group.Id) return;
        _pendingFilter = null;

        _suppressReload = true;
        try
        {
            MinAttendancePercent = Math.Clamp(filter.MinAttendancePercent, 0, 100);
            MinInvitedEvents = Math.Max(1, filter.MinInvitedEvents);
            EventNameContains = filter.EventNameContains ?? "";
            EventNameExcludes = filter.EventNameExcludes ?? "";

            var ticked = filter.SubGroupIds.ToHashSet();
            foreach (var option in SubGroupOptions) option.IsSelected = ticked.Contains(option.Id);

            // The events define the span as long as both are still on offer; otherwise the days they stood for do.
            if (filter.SpanByEvents && Chosen(filter.FromEventId) is not null && Chosen(filter.ToEventId) is not null)
            {
                FromEventId = filter.FromEventId!;
                ToEventId = filter.ToEventId!;
                SpanByEvents = true;
            }
            else
            {
                FromEventId = ToEventId = EventOption.NoneId;
                From = filter.From.ToDateTime(TimeOnly.MinValue);
                To = filter.To.ToDateTime(TimeOnly.MinValue);
                SpanByEvents = false;
            }
        }
        finally
        {
            _suppressReload = false;
        }
    }

    /// <summary>The filter card as a value. Expects a valid time span (<see cref="ValidateSpan"/>).</summary>
    private FilterSettingsDto CaptureFilter(string groupId)
    {
        // The days of the two events as the server offers them; the date fields only show what the last load resolved.
        var (first, last, byEvents) = SpanByEvents && Chosen(FromEventId)?.Day is { } firstDay && Chosen(ToEventId)?.Day is { } lastDay
            ? (firstDay, lastDay, true)
            : (FirstDay, LastDay, false);

        return new FilterSettingsDto(
            groupId, first, last,
            byEvents ? FromEventId : null,
            byEvents ? ToEventId : null,
            SelectedSubGroupIds.ToList(),
            (int)Math.Round(Math.Clamp(MinAttendancePercent, 0, 100)),
            Math.Max(1, MinInvitedEvents),
            Normalize(EventNameContains),
            Normalize(EventNameExcludes));
    }

    // --- What the saved filters see ------------------------------------------------------------------

    FilterSettingsDto? IFilterHost.CaptureFilter() =>
        Shell.SelectedGroup is { } group && ValidateSpan() is null ? CaptureFilter(group.Id) : null;

    string? IFilterHost.FilterProblem => Shell.SelectedGroup is null ? Strings.Saved_NoGroup : ValidateSpan();

    bool IFilterHost.HoldsFilter(FilterSettingsDto filter)
    {
        if (Shell.SelectedGroup is not { } group || filter.GroupId != group.Id || ValidateSpan() is not null) return false;

        // Compared the way applying it would set the card: a span of events that are not offered any more is the days it
        // stood for, and the days of events are not compared (they are the server's, and may have moved since it was saved).
        var byEvents = filter.SpanByEvents && Chosen(filter.FromEventId) is { Day: not null } && Chosen(filter.ToEventId) is { Day: not null };
        var current = CaptureFilter(group.Id);

        return current.SpanByEvents == byEvents
            && (byEvents
                ? current.FromEventId == filter.FromEventId && current.ToEventId == filter.ToEventId
                : current.From == filter.From && current.To == filter.To)
            && current.SubGroupIds.ToHashSet().SetEquals(filter.SubGroupIds)
            && current.MinAttendancePercent == Math.Clamp(filter.MinAttendancePercent, 0, 100)
            && current.MinInvitedEvents == Math.Max(1, filter.MinInvitedEvents)
            && current.EventNameContains == Normalize(filter.EventNameContains ?? "")
            && current.EventNameExcludes == Normalize(filter.EventNameExcludes ?? "");
    }

    bool IFilterHost.KnowsGroup(string groupId) => Shell.Groups.Any(g => g.Id == groupId);

    void IFilterHost.ApplyFilter(FilterSettingsDto filter)
    {
        _pendingFilter = filter;
        ScheduleReload(immediate: true);
    }

    /// <summary>A belt exam ends the span: the event the span ends at (or the last day, when dates define it) is what a new exam is proposed for.</summary>
    ExamSuggestion? IFilterHost.ExamSuggestion => SpanByEvents && Chosen(ToEventId) is { Day: { } day } toEvent
        ? new ExamSuggestion(toEvent.Id, day)
        : new ExamSuggestion(null, LastDay);

    /// <summary>The constructor runs before the server info is known, so the server's thresholds are adopted on first load.</summary>
    private void ApplyServerDefaultsOnce()
    {
        if (_serverDefaultsApplied || Shell.Info is null) return;
        _serverDefaultsApplied = true;

        _suppressReload = true;
        MinAttendancePercent = Shell.DefaultMinAttendancePercent;
        MinInvitedEvents = Shell.DefaultMinInvitedEvents;
        _suppressReload = false;
    }

    /// <summary>
    /// Fetches the events to choose from when the group changed or the server data was refreshed. A new group starts
    /// from the server's suggested span; after a refresh the user's choice stays as long as its events still exist.
    /// </summary>
    private async Task SyncEventChoicesAsync(string groupId, CancellationToken cancellationToken)
    {
        if (_choices is not null && !_choicesStale && _choicesGroupId == groupId) return;

        var choices = await _api.GetEventChoicesAsync(groupId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var newGroup = _choicesGroupId != groupId;
        var (fromId, toId) = (FromEventId, ToEventId);

        _suppressReload = true;
        try
        {
            _choices = choices;
            _choicesGroupId = groupId;
            _choicesStale = false;
            SyncEventOptions(choices);

            // A combo box drops its selection when its list is replaced, so the choice is set again.
            FromEventId = Chosen(fromId) is null ? EventOption.NoneId : fromId;
            ToEventId = Chosen(toId) is null ? EventOption.NoneId : toId;
            if (newGroup || (SpanByEvents && !BothEventsChosen)) ApplyDefaultSpan();
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void SyncEventOptions(EventChoicesDto choices)
    {
        var options = new List<EventOption> { EventOption.None };
        options.AddRange(choices.Events.Select(e => new EventOption(e)));

        if (EventOptions.Select(o => o.Id).SequenceEqual(options.Select(o => o.Id))) return;
        EventOptions = options;
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(SpanHint));
        OnPropertyChanged(nameof(SubGroupSummary));
        OnPropertyChanged(nameof(ThresholdText));
        OnPropertyChanged(nameof(RequiredText));
        if (FilterError is not null) FilterError = ValidateSpan();
        foreach (var option in EventOptions) option.Refresh();
        SavedFilters.OnLanguageChanged();
        if (_last is { } result) Apply(result);
    }

    private void SyncSubGroups(GroupDto group)
    {
        if (SubGroupOptions.Select(o => o.Id).SequenceEqual(group.SubGroups.Select(s => s.Id))) return;

        // What was ticked stays ticked as long as its subgroup is still offered. The ticks are set before the view
        // model listens, so this does not schedule a reload: the one that is under way sends the result.
        var ticked = SelectedSubGroupIds.ToHashSet();
        var options = group.SubGroups
            .Select(s => new SubGroupOption(s.Id, s.Name) { IsSelected = ticked.Contains(s.Id) })
            .ToList();

        foreach (var option in SubGroupOptions) option.PropertyChanged -= OnSubGroupOptionChanged;
        foreach (var option in options) option.PropertyChanged += OnSubGroupOptionChanged;
        SubGroupOptions = options;
    }

    private AttendanceQuery BuildQuery(string groupId)
    {
        var query = new AttendanceQuery(
            groupId,
            SubGroupIds: SelectedSubGroupIds is { Count: > 0 } subGroupIds ? subGroupIds.ToArray() : null,
            MinAttendancePercent: (int)Math.Round(Math.Clamp(MinAttendancePercent, 0, 100)),
            MinInvitedEvents: Math.Max(1, MinInvitedEvents),
            EventNameContains: Normalize(EventNameContains),
            EventNameExcludes: Normalize(EventNameExcludes));

        // The server resolves events to days itself; only one of the two ways is sent.
        return SpanByEvents
            ? query with { FromEventId = FromEventId, ToEventId = ToEventId }
            : query with { From = FirstDay, To = LastDay };
    }

    private static string? Normalize(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>What is wrong with the time span as it stands, or <c>null</c> when it can be sent.</summary>
    private string? ValidateSpan()
    {
        if (!SpanByEvents) return FirstDay > LastDay ? Strings.Stats_FilterError : null;

        if (Chosen(FromEventId) is not { Day: { } firstDay } || Chosen(ToEventId) is not { Day: { } lastDay })
            return Strings.Stats_SelectBothEvents;

        return firstDay > lastDay ? Strings.Stats_EventOrderError : null;
    }

    private void Apply(AttendanceStatisticsDto result)
    {
        var filter = result.Filter;
        var s = result.Summary;
        _last = result;
        _loadedOnce = true;
        ResultTitle = $"{result.GroupName} · {Display.Period(filter.From, filter.To)}";

        // The date fields of the advanced filters show the days the chosen events resolved to.
        if (SpanByEvents) ShowResolvedDays(filter);

        var assessed = s.MemberCount - s.InsufficientDataCount;
        Kpis =
        [
            new(string.Format(Strings.Kpi_Meeting, filter.MinAttendancePercent),
                string.Format(Strings.Kpi_OfAssessed, s.QualifiedCount, assessed),
                string.Format(Strings.Kpi_MeetingStatsHint, filter.MinAttendancePercent),
                KpiTone.Success),
            new(Strings.Kpi_BelowTarget, s.BelowThresholdCount.ToString(), Strings.Kpi_BelowTargetHint),
            new(Strings.Kpi_TooFew, s.InsufficientDataCount.ToString(), string.Format(Strings.Kpi_TooFewHint, filter.MinInvitedEvents)),
            new(Strings.Kpi_EventsAnalysed, s.EventCount.ToString(), Strings.Kpi_EventsAnalysedHint),
            new(Strings.Kpi_Attendance, Display.Percent(s.OverallAttendanceRate), Strings.Kpi_AttendanceOverallHint),
            new(Strings.Kpi_ResponseRate, Display.Percent(s.AverageResponseRate), Strings.Kpi_ResponseRateHint),
            new(Strings.Kpi_AverageAttendees, Display.Number(s.AverageAttendeesPerEvent), Strings.Kpi_AverageAttendeesHint),
            new(Strings.Kpi_Stars, s.AverageStars is { } stars ? Display.Stars(stars) : Display.None, Strings.Kpi_StarsHint),
            new(Strings.Kpi_AtRisk, s.AtRiskCount.ToString(), Strings.Kpi_AtRiskHint, s.AtRiskCount > 0 ? KpiTone.Warning : KpiTone.Neutral),
        ];

        _allMembers = result.Members.Select(m => new MemberRowViewModel(m)).ToList();
        Events = result.Events.Select(e => new EventRowViewModel(e)).ToList();
        Distribution = ChartData.Distribution(result.Distribution, filter.MinAttendancePercent);
        Trend = ChartData.MonthlyTrend(result.MonthlyTrend);
        HasResults = s.EventCount > 0;
        RebuildVisibleMembers();
    }

    private void ShowResolvedDays(AttendanceFilter filter)
    {
        _suppressReload = true;
        try
        {
            From = filter.From.ToDateTime(TimeOnly.MinValue);
            To = filter.To.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void RebuildVisibleMembers()
    {
        IEnumerable<MemberRowViewModel> rows = _allMembers;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            rows = rows.Where(m => m.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        rows = StatusFilter switch
        {
            MemberStatusFilter.Qualified => rows.Where(m => m.Status == QualificationStatus.Qualified),
            MemberStatusFilter.BelowThreshold => rows.Where(m => m.Status == QualificationStatus.BelowThreshold),
            MemberStatusFilter.InsufficientData => rows.Where(m => m.Status == QualificationStatus.InsufficientData),
            _ => rows,
        };

        VisibleMembers = Sort(rows).ToList();
        MembersCaption = VisibleMembers.Count == _allMembers.Count
            ? string.Format(Strings.Stats_MembersCount, _allMembers.Count)
            : string.Format(Strings.Stats_MembersCountOf, VisibleMembers.Count, _allMembers.Count);
    }

    private IEnumerable<MemberRowViewModel> Sort(IEnumerable<MemberRowViewModel> rows)
    {
        Func<MemberRowViewModel, IComparable> key = SortKey switch
        {
            MemberSortKey.Name => m => m.Name.ToUpperInvariant(),
            MemberSortKey.SubGroups => m => m.SubGroupsText.ToUpperInvariant(),
            MemberSortKey.Invited => m => m.Invited,
            MemberSortKey.Attended => m => m.Attended,
            MemberSortKey.ResponseRate => m => m.ResponseRate,
            MemberSortKey.CurrentStreak => m => m.CurrentStreak,
            MemberSortKey.DaysSinceLastAttended => m => m.DaysSinceLastAttended,
            MemberSortKey.SessionsPerWeek => m => m.SessionsPerWeek,
            MemberSortKey.Stars => m => m.Stars,
            MemberSortKey.EngagementScore => m => m.EngagementScore,
            _ => m => m.AttendanceRate,
        };

        // Ties keep a stable, readable order: by name.
        var ordered = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
    }
}
