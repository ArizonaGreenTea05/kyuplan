using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>What the page's name form is for.</summary>
public enum ManualNameForm
{
    None,
    NewGroup,
    RenameGroup,
    NewSubGroup,
    RenameSubGroup,
}

/// <summary>
/// The page where users keep the groups, members and events by hand (data source "manual"): pick or add a group, then maintain its members,
/// its subgroups and its events (who is invited, what each answered). One form is open at a time. Every change is saved at once and the shell
/// reads the groups again, so the other pages show it. Used by both clients.
/// </summary>
public sealed partial class ManualDataViewModel : PageViewModel
{
    public const int MembersSection = 0;
    public const int SubGroupsSection = 1;
    public const int EventsSection = 2;

    private readonly IPlannerApi _api;
    private Func<string>? _notice;
    private bool _selecting;
    private object? _pending;

    public ManualDataViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;
        _eventStartDate = DateTime.Today;
        _eventEndDate = DateTime.Today;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Info)) OnPropertyChanged(nameof(IsWrongSource));
        };
    }

    public override string Title => Strings.Nav_ManualData;

    protected override string? ViewPermission => Permission.ManualDataEdit;

    public bool CanEdit => CanView;

    /// <summary>Only offered while the data source is "manual" (nothing entered here is used otherwise).</summary>
    public override bool ShowInNavigation => CanView && Shell.Info?.DataSource == DataSourceKinds.Manual;

    /// <summary>The groups are the page's own (all of them, whatever the shell has selected).</summary>
    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => !IsAnyFormOpen && !IsConfirming;

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>What is entered here is only used while the data source is "manual"; otherwise the page says so.</summary>
    public bool IsWrongSource => Shell.Info is { } info && info.DataSource != DataSourceKinds.Manual;

    // --- The lists -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroups), nameof(HasNoGroups))]
    private IReadOnlyList<ManualGroupDto> _groups = [];

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroup), nameof(HasNoGroups), nameof(SelectedGroupId))]
    private ManualGroupDto? _selectedGroup;

    public string SelectedGroupId => SelectedGroup?.Id ?? "";

    public bool HasGroups => Groups.Count > 0;

    public bool HasGroup => SelectedGroup is not null;

    public bool HasNoGroups => HasLoaded && Groups.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MembersEmpty), nameof(HasMembers))]
    private IReadOnlyList<ManualMemberRow> _members = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubGroupsEmpty), nameof(HasSubGroupRows))]
    private IReadOnlyList<ManualSubGroupRow> _subGroups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EventsEmpty), nameof(HasEvents))]
    private IReadOnlyList<ManualEventRow> _events = [];

    public bool HasMembers => Members.Count > 0;

    public bool HasSubGroupRows => SubGroups.Count > 0;

    public bool HasEvents => Events.Count > 0;

    public bool MembersEmpty => HasGroup && Members.Count == 0;

    public bool SubGroupsEmpty => HasGroup && SubGroups.Count == 0;

    public bool EventsEmpty => HasGroup && Events.Count == 0;

    /// <summary>The tab that is shown: <see cref="MembersSection"/>, <see cref="SubGroupsSection"/> or <see cref="EventsSection"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMembersSection), nameof(IsSubGroupsSection), nameof(IsEventsSection))]
    private int _sectionIndex;

    public bool IsMembersSection => SectionIndex == MembersSection;

    public bool IsSubGroupsSection => SectionIndex == SubGroupsSection;

    public bool IsEventsSection => SectionIndex == EventsSection;

    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    private TimeZoneInfo Zone
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(Shell.Info?.TimeZone ?? "");
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }

    private string? _selectedId;

    partial void OnSelectedGroupChanged(ManualGroupDto? value)
    {
        if (value?.Id == _selectedId) return; // the same group, read again

        _selectedId = value?.Id;
        CloseForms();
        if (!_selecting) _ = LoadGroupAsync();
    }

    partial void OnSectionIndexChanged(int value) => CloseForms();

    // --- Loading ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            await ReadAsync(ct);
            HasLoaded = true;
            OnPropertyChanged(nameof(HasNoGroups));
        });
    }

    /// <summary>Reads the groups, keeps the one that was selected (or takes the first) and reads its members, subgroups and events.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var groups = await _api.GetManualGroupsAsync(cancellationToken);
        var keep = SelectedGroup?.Id;

        _selecting = true;
        try
        {
            Groups = groups;
            SelectedGroup = groups.FirstOrDefault(g => g.Id == keep) ?? groups.FirstOrDefault();
        }
        finally
        {
            _selecting = false;
        }

        await ReadGroupAsync(cancellationToken);
    }

    private async Task ReadGroupAsync(CancellationToken cancellationToken)
    {
        if (SelectedGroup is not { } group)
        {
            Members = [];
            SubGroups = [];
            Events = [];
            return;
        }

        var members = await _api.GetManualMembersAsync(group.Id, cancellationToken);
        var events = await _api.GetManualEventsAsync(group.Id, cancellationToken);

        var zone = Zone;
        Members = [.. members.Select(m => new ManualMemberRow(m, group.SubGroups))];
        SubGroups = [.. group.SubGroups.Select(s => new ManualSubGroupRow(s, members.Count(m => m.SubGroupIds.Contains(s.Id))))];
        Events = [.. events.Select(e => new ManualEventRow(e, zone))];
    }

    private Task LoadGroupAsync() => LoadAsync(ReadGroupAsync);

    /// <summary>Runs a change, then lets the shell and this page read what it changed. <paramref name="notice"/> is shown when it worked.</summary>
    private async Task<bool> ChangeAsync(Func<CancellationToken, Task> change, Func<string> notice)
    {
        var done = false;
        await LoadAsync(async ct =>
        {
            await change(ct);
            done = true;
            await ReadAsync(ct);
        });

        if (!done) return false;

        SetNotice(notice);
        await Shell.RefreshDataCommand.ExecuteAsync(null);
        return true;
    }

    // --- Forms: only one is open ------------------------------------------------------------------------------

    public bool IsAnyFormOpen => IsNameFormOpen || IsMemberFormOpen || IsEventFormOpen;

    private void CloseForms()
    {
        NameFormMode = ManualNameForm.None;
        IsMemberFormOpen = false;
        IsEventFormOpen = false;
        Pending = null;
        NotifyStateChanged();
    }

    private void Prepare()
    {
        ErrorMessage = null;
        SetNotice(null);
        CloseForms();
    }

    // --- Groups and subgroups: one text field ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameFormOpen), nameof(NameFormTitle))]
    private ManualNameForm _nameFormMode;

    [ObservableProperty]
    private string _nameFormText = "";

    private ManualSubGroupRow? _renamingSubGroup;

    public bool IsNameFormOpen => NameFormMode != ManualNameForm.None;

    public string NameFormTitle => NameFormMode switch
    {
        ManualNameForm.NewGroup => Strings.Manual_FormNewGroup,
        ManualNameForm.RenameGroup => string.Format(Strings.Manual_FormRenameGroup, SelectedGroup?.Name),
        ManualNameForm.NewSubGroup => Strings.Manual_FormNewSubGroup,
        ManualNameForm.RenameSubGroup => string.Format(Strings.Manual_FormRenameSubGroup, _renamingSubGroup?.Name),
        _ => "",
    };

    public string? NameFormProblem => IsNameFormOpen && NameFormText.Length > 0 && !ManualDataRules.IsName(NameFormText)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    /// <summary>The name is one the API accepts and, when renaming, another one.</summary>
    public bool CanSaveName =>
        CanEdit && IsNameFormOpen && !IsBusy && ManualDataRules.IsName(NameFormText) && NameFormText.Trim() != (NameFormMode switch
        {
            ManualNameForm.RenameGroup => SelectedGroup?.Name,
            ManualNameForm.RenameSubGroup => _renamingSubGroup?.Name,
            _ => null,
        });

    partial void OnNameFormTextChanged(string value)
    {
        if (IsNameFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    private bool CanBeginGroupForm() => CanEdit && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginGroupForm))]
    private void BeginNewGroup()
    {
        Prepare();
        NameFormText = "";
        NameFormMode = ManualNameForm.NewGroup;
        NotifyStateChanged();
    }

    private bool CanBeginRenameGroup() => CanEdit && !IsBusy && HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginRenameGroup))]
    private void BeginRenameGroup()
    {
        if (!CanBeginRenameGroup()) return;

        Prepare();
        NameFormText = SelectedGroup!.Name;
        NameFormMode = ManualNameForm.RenameGroup;
        NotifyStateChanged();
    }

    private bool CanBeginNewSubGroup() => CanEdit && !IsBusy && HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginNewSubGroup))]
    private void BeginNewSubGroup()
    {
        if (!CanBeginNewSubGroup()) return;

        Prepare();
        NameFormText = "";
        NameFormMode = ManualNameForm.NewSubGroup;
        NotifyStateChanged();
    }

    private bool CanBeginRenameSubGroup(ManualSubGroupRow? row) => CanEdit && !IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginRenameSubGroup))]
    private void BeginRenameSubGroup(ManualSubGroupRow row)
    {
        if (!CanBeginRenameSubGroup(row)) return;

        Prepare();
        _renamingSubGroup = row;
        NameFormText = row.Name;
        NameFormMode = ManualNameForm.RenameSubGroup;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void CancelName()
    {
        ErrorMessage = null;
        NameFormMode = ManualNameForm.None;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private async Task SaveNameAsync()
    {
        if (!CanSaveName) return;

        var input = new ManualNameInputDto(NameFormText.Trim());
        var mode = NameFormMode;
        var name = input.Name;
        var group = SelectedGroup;
        var subGroup = _renamingSubGroup;
        string? createdGroupId = null;

        var done = await ChangeAsync(async ct =>
        {
            switch (mode)
            {
                case ManualNameForm.NewGroup: createdGroupId = (await _api.CreateManualGroupAsync(input, ct)).Id; break;
                case ManualNameForm.RenameGroup: await _api.RenameManualGroupAsync(group!.Id, input, ct); break;
                case ManualNameForm.NewSubGroup: await _api.CreateManualSubGroupAsync(group!.Id, input, ct); break;
                default: await _api.RenameManualSubGroupAsync(subGroup!.Id, input, ct); break;
            }
        }, () => string.Format(mode is ManualNameForm.NewGroup or ManualNameForm.NewSubGroup ? Strings.Manual_Created : Strings.Manual_Saved, name));

        if (!done) return; // the reason is in ErrorMessage; the form stays open so the name can be corrected

        NameFormMode = ManualNameForm.None;
        if (createdGroupId is not null) SelectedGroup = Groups.FirstOrDefault(g => g.Id == createdGroupId) ?? SelectedGroup;
        NotifyStateChanged();
    }

    // --- Members -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemberFormTitle))]
    private bool _isMemberFormOpen;

    private ManualMemberRow? _editingMember;

    [ObservableProperty]
    private string _memberName = "";

    /// <summary>The day the member was born; <c>null</c> = not known. A date picker holds a <see cref="DateTime"/>, so this does too.</summary>
    [ObservableProperty]
    private DateTime? _memberBirthDate;

    [ObservableProperty]
    private IReadOnlyList<ManualChoiceRow> _memberSubGroups = [];

    public bool HasSubGroupChoices => MemberSubGroups.Count > 0;

    public string MemberFormTitle => _editingMember is { } member ? string.Format(Strings.Manual_FormEditMember, member.Name) : Strings.Manual_FormNewMember;

    public string? MemberNameProblem => IsMemberFormOpen && MemberName.Length > 0 && !ManualDataRules.IsName(MemberName)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    public string? MemberBirthProblem => IsMemberFormOpen && MemberBirthDate is { } day && MemberRules.CheckBirthDate(DateOnly.FromDateTime(day), DateOnly.FromDateTime(DateTime.Today)) is not null
        ? string.Format(Strings.MemberPage_BirthInvalid, Display.Date(MemberRules.EarliestBirth))
        : null;

    private ManualMemberInputDto MemberInput() => new(
        MemberName.Trim(),
        MemberBirthDate is { } day ? DateOnly.FromDateTime(day) : null,
        [.. MemberSubGroups.Where(s => s.IsSelected).Select(s => s.Id)]);

    public bool CanSaveMember =>
        CanEdit && IsMemberFormOpen && !IsBusy && ManualDataRules.IsName(MemberName) && MemberBirthProblem is null
        && (_editingMember is not { } member || !SameMember(MemberInput(), member.Member));

    private static bool SameMember(ManualMemberInputDto input, ManualMemberDto member) =>
        input.Name == member.Name && input.BirthDate == member.BirthDate
        && input.SubGroupIds!.Order().SequenceEqual(member.SubGroupIds.Order());

    private bool CanBeginNewMember() => CanEdit && !IsBusy && HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginNewMember))]
    private void BeginNewMember()
    {
        if (!CanBeginNewMember()) return;

        Prepare();
        OpenMemberForm(null);
    }

    private bool CanBeginEditMember(ManualMemberRow? row) => CanEdit && !IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginEditMember))]
    private void BeginEditMember(ManualMemberRow row)
    {
        if (!CanBeginEditMember(row)) return;

        Prepare();
        OpenMemberForm(row);
    }

    private void OpenMemberForm(ManualMemberRow? row)
    {
        _editingMember = row;
        MemberName = row?.Name ?? "";
        MemberBirthDate = row?.Member.BirthDate?.ToDateTime(TimeOnly.MinValue);
        MemberSubGroups = [.. (SelectedGroup?.SubGroups ?? []).Select(s => new ManualChoiceRow(s.Id, s.Name, row?.Member.SubGroupIds.Contains(s.Id) == true))];
        foreach (var choice in MemberSubGroups) choice.PropertyChanged += (_, _) => MemberFormEdited();
        OnPropertyChanged(nameof(HasSubGroupChoices));
        IsMemberFormOpen = true;
        OnPropertyChanged(nameof(MemberFormTitle));
        NotifyStateChanged();
    }

    partial void OnMemberNameChanged(string value) => MemberFormEdited();

    partial void OnMemberBirthDateChanged(DateTime? value) => MemberFormEdited();

    private void MemberFormEdited()
    {
        if (IsMemberFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    private bool CanClearMemberBirthDate() => MemberBirthDate is not null;

    [RelayCommand(CanExecute = nameof(CanClearMemberBirthDate))]
    private void ClearMemberBirthDate() => MemberBirthDate = null;

    [RelayCommand]
    private void CancelMember()
    {
        ErrorMessage = null;
        IsMemberFormOpen = false;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveMember))]
    private async Task SaveMemberAsync()
    {
        if (!CanSaveMember || SelectedGroup is not { } group) return;

        var input = MemberInput();
        var editing = _editingMember;
        var done = await ChangeAsync(
            ct => editing is null ? _api.CreateManualMemberAsync(group.Id, input, ct) : _api.UpdateManualMemberAsync(editing.Id, input, ct),
            () => string.Format(editing is null ? Strings.Manual_Created : Strings.Manual_Saved, input.Name));

        if (!done) return;

        IsMemberFormOpen = false;
        NotifyStateChanged();
    }

    // --- Events ------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EventFormTitle))]
    private bool _isEventFormOpen;

    private ManualEventRow? _editingEvent;

    [ObservableProperty]
    private string _eventName = "";

    [ObservableProperty]
    private DateTime? _eventStartDate;

    /// <summary>The time of day as typed, "18:00".</summary>
    [ObservableProperty]
    private string _eventStartTime = "18:00";

    [ObservableProperty]
    private DateTime? _eventEndDate;

    [ObservableProperty]
    private string _eventEndTime = "19:30";

    [ObservableProperty]
    private bool _eventCancelled;

    [ObservableProperty]
    private IReadOnlyList<ManualInviteRow> _invitees = [];

    public IReadOnlyList<ManualRsvpOption> RsvpOptions => ManualRsvpOption.All;

    public bool HasInvitees => Invitees.Count > 0;

    public string EventFormTitle => _editingEvent is { } e ? string.Format(Strings.Manual_FormEditEvent, e.Event.Name) : Strings.Manual_FormNewEvent;

    public string? EventNameProblem => IsEventFormOpen && EventName.Length > 0 && !ManualDataRules.IsName(EventName)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    /// <summary>The start and end typed, or <c>null</c> for the one that cannot be read.</summary>
    private DateTimeOffset? Combine(DateTime? date, string time)
    {
        if (date is null || !TimeOnly.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var clock)) return null;

        var zone = Zone;
        var local = date.Value.Date + clock.ToTimeSpan();
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // skipped by a change to summer time
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private DateTimeOffset? EventStart => Combine(EventStartDate, EventStartTime);

    private DateTimeOffset? EventEnd => Combine(EventEndDate, EventEndTime);

    public string? EventTimeProblem
    {
        get
        {
            if (!IsEventFormOpen) return null;
            if (EventStart is not { } start || EventEnd is not { } end) return Strings.Manual_TimeInvalid;
            return end < start ? Strings.Manual_EndBeforeStart : null;
        }
    }

    private ManualEventInputDto? EventInput() =>
        EventStart is { } start && EventEnd is { } end
            ? new ManualEventInputDto(
                EventName.Trim(), start, end, EventCancelled,
                [.. Invitees.Where(i => i.IsInvited).Select(i => new ManualInvitationDto(i.MemberId, i.Answer.Value))])
            : null;

    public bool CanSaveEvent =>
        CanEdit && IsEventFormOpen && !IsBusy && ManualDataRules.IsName(EventName) && EventTimeProblem is null && EventInput() is { } input
        && (_editingEvent is not { } e || !SameEvent(input, e.Event));

    private static bool SameEvent(ManualEventInputDto input, ManualEventDto e) =>
        input.Name == e.Name && input.Start == e.Start && input.End == e.End && input.Cancelled == e.Cancelled
        && input.Invitations!.OrderBy(i => i.MemberId).SequenceEqual(e.Invitations.OrderBy(i => i.MemberId));

    private bool CanBeginNewEvent() => CanEdit && !IsBusy && HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginNewEvent))]
    private async Task BeginNewEventAsync()
    {
        if (!CanBeginNewEvent()) return;

        Prepare();
        await OpenEventFormAsync(null);
    }

    private bool CanBeginEditEvent(ManualEventRow? row) => CanEdit && !IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginEditEvent))]
    private async Task BeginEditEventAsync(ManualEventRow row)
    {
        if (!CanBeginEditEvent(row)) return;

        Prepare();
        await OpenEventFormAsync(row);
    }

    private async Task OpenEventFormAsync(ManualEventRow? row)
    {
        // The people to invite are read again: the member list on screen may be older than a change made in another window.
        IReadOnlyList<ManualMemberDto> members = [.. Members.Select(m => m.Member)];
        if (SelectedGroup is { } group)
            await LoadAsync(async ct => members = await _api.GetManualMembersAsync(group.Id, ct));
        if (HasError) return;

        _editingEvent = row;
        var zone = Zone;
        var invitations = row?.Event.Invitations.ToDictionary(i => i.MemberId, i => i.Rsvp) ?? [];
        var unanswered = ManualRsvpOption.All.First(o => o.Value == ManualRsvp.Unanswered);

        Invitees = [.. members.Select(m =>
        {
            var invited = invitations.TryGetValue(m.Id, out var rsvp);
            return new ManualInviteRow(m.Id, m.Name, invited, invited ? ManualRsvpOption.All.First(o => o.Value == rsvp) : unanswered);
        })];
        foreach (var invitee in Invitees) invitee.PropertyChanged += (_, _) => EventFormEdited();

        if (row is { } existing)
        {
            var start = TimeZoneInfo.ConvertTime(existing.Event.Start, zone);
            var end = TimeZoneInfo.ConvertTime(existing.Event.End, zone);
            EventName = existing.Event.Name;
            EventStartDate = start.Date;
            EventStartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture);
            EventEndDate = end.Date;
            EventEndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture);
            EventCancelled = existing.Event.Cancelled;
        }
        else
        {
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
            EventName = "";
            EventStartDate = today;
            EventStartTime = "18:00";
            EventEndDate = today;
            EventEndTime = "19:30";
            EventCancelled = false;
        }

        OnPropertyChanged(nameof(HasInvitees));
        OnPropertyChanged(nameof(EventFormTitle));
        IsEventFormOpen = true;
        NotifyStateChanged();
    }

    partial void OnEventNameChanged(string value) => EventFormEdited();

    partial void OnEventStartDateChanged(DateTime? oldValue, DateTime? newValue)
    {
        // The end follows the start while it was on the same day or would lie before it.
        if (IsEventFormOpen && newValue is { } start && (EventEndDate is null || EventEndDate < start || EventEndDate == oldValue)) EventEndDate = start;
        EventFormEdited();
    }

    partial void OnEventStartTimeChanged(string value) => EventFormEdited();

    partial void OnEventEndDateChanged(DateTime? value) => EventFormEdited();

    partial void OnEventEndTimeChanged(string value) => EventFormEdited();

    partial void OnEventCancelledChanged(bool value) => EventFormEdited();

    private void EventFormEdited()
    {
        if (IsEventFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    private bool CanChangeInvitations() => IsEventFormOpen && Invitees.Count > 0;

    [RelayCommand(CanExecute = nameof(CanChangeInvitations))]
    private void InviteAll()
    {
        foreach (var invitee in Invitees) invitee.IsInvited = true;
    }

    [RelayCommand(CanExecute = nameof(CanChangeInvitations))]
    private void InviteNone()
    {
        foreach (var invitee in Invitees) invitee.IsInvited = false;
    }

    /// <summary>Everybody who is invited came: the quick way to record a training.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeInvitations))]
    private void AcceptAll()
    {
        var accepted = ManualRsvpOption.All.First(o => o.Value == ManualRsvp.Accepted);
        foreach (var invitee in Invitees.Where(i => i.IsInvited)) invitee.Answer = accepted;
    }

    [RelayCommand]
    private void CancelEvent()
    {
        ErrorMessage = null;
        IsEventFormOpen = false;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveEvent))]
    private async Task SaveEventAsync()
    {
        if (!CanSaveEvent || SelectedGroup is not { } group || EventInput() is not { } input) return;

        var editing = _editingEvent;
        var done = await ChangeAsync(
            ct => editing is null ? _api.CreateManualEventAsync(group.Id, input, ct) : _api.UpdateManualEventAsync(editing.Id, input, ct),
            () => string.Format(editing is null ? Strings.Manual_Created : Strings.Manual_Saved, input.Name));

        if (!done) return;

        IsEventFormOpen = false;
        NotifyStateChanged();
    }

    // --- Deleting asks first -----------------------------------------------------------------------------------

    public bool IsConfirming => _pending is not null;

    /// <summary>The question about what is about to be deleted; the answer is <see cref="ConfirmAsync"/> or <see cref="CancelConfirm"/>.</summary>
    public string ConfirmText => _pending switch
    {
        ManualGroupDto group => string.Format(Strings.Manual_ConfirmDeleteGroup, group.Name, group.MemberCount),
        ManualSubGroupRow subGroup => string.Format(Strings.Manual_ConfirmDeleteSubGroup, subGroup.Name),
        ManualMemberRow member => string.Format(Strings.Manual_ConfirmDeleteMember, member.Name),
        ManualEventRow e => string.Format(Strings.Manual_ConfirmDeleteEvent, e.Event.Name),
        _ => "",
    };

    private object? Pending
    {
        get => _pending;
        set
        {
            _pending = value;
            OnPropertyChanged(nameof(IsConfirming));
            OnPropertyChanged(nameof(ConfirmText));
            NotifyStateChanged();
        }
    }

    private bool CanAsk() => CanEdit && !IsBusy;

    private void Ask(object item)
    {
        if (!CanAsk()) return;

        ErrorMessage = null;
        SetNotice(null);
        NameFormMode = ManualNameForm.None;
        IsMemberFormOpen = false;
        IsEventFormOpen = false;
        Pending = item;
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskDeleteGroup()
    {
        if (SelectedGroup is { } group) Ask(group);
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskDeleteSubGroup(ManualSubGroupRow row) => Ask(row);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskDeleteMember(ManualMemberRow row) => Ask(row);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskDeleteEvent(ManualEventRow row) => Ask(row);

    [RelayCommand]
    private void CancelConfirm() => Pending = null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_pending is not { } item || !CanEdit) return;

        Pending = null;
        var (name, delete) = item switch
        {
            ManualGroupDto group => (group.Name, (Func<CancellationToken, Task>)(ct => _api.DeleteManualGroupAsync(group.Id, ct))),
            ManualSubGroupRow subGroup => (subGroup.Name, ct => _api.DeleteManualSubGroupAsync(subGroup.Id, ct)),
            ManualMemberRow member => (member.Name, ct => _api.DeleteManualMemberAsync(member.Id, ct)),
            ManualEventRow e => (e.Event.Name, ct => _api.DeleteManualEventAsync(e.Id, ct)),
            _ => ("", (Func<CancellationToken, Task>)(_ => Task.CompletedTask)),
        };

        await ChangeAsync(delete, () => string.Format(Strings.Manual_Deleted, name));
    }

    // --- State -------------------------------------------------------------------------------------------------

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyFormOpen));
        OnPropertyChanged(nameof(NameFormProblem));
        OnPropertyChanged(nameof(CanSaveName));
        OnPropertyChanged(nameof(MemberNameProblem));
        OnPropertyChanged(nameof(MemberBirthProblem));
        OnPropertyChanged(nameof(CanSaveMember));
        OnPropertyChanged(nameof(EventNameProblem));
        OnPropertyChanged(nameof(EventTimeProblem));
        OnPropertyChanged(nameof(CanSaveEvent));
        OnPropertyChanged(nameof(NameFormTitle));

        BeginNewGroupCommand.NotifyCanExecuteChanged();
        BeginRenameGroupCommand.NotifyCanExecuteChanged();
        BeginNewSubGroupCommand.NotifyCanExecuteChanged();
        BeginRenameSubGroupCommand.NotifyCanExecuteChanged();
        SaveNameCommand.NotifyCanExecuteChanged();
        BeginNewMemberCommand.NotifyCanExecuteChanged();
        BeginEditMemberCommand.NotifyCanExecuteChanged();
        SaveMemberCommand.NotifyCanExecuteChanged();
        ClearMemberBirthDateCommand.NotifyCanExecuteChanged();
        BeginNewEventCommand.NotifyCanExecuteChanged();
        BeginEditEventCommand.NotifyCanExecuteChanged();
        SaveEventCommand.NotifyCanExecuteChanged();
        InviteAllCommand.NotifyCanExecuteChanged();
        InviteNoneCommand.NotifyCanExecuteChanged();
        AcceptAllCommand.NotifyCanExecuteChanged();
        AskDeleteGroupCommand.NotifyCanExecuteChanged();
        AskDeleteSubGroupCommand.NotifyCanExecuteChanged();
        AskDeleteMemberCommand.NotifyCanExecuteChanged();
        AskDeleteEventCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var member in Members) member.Refresh();
        foreach (var subGroup in SubGroups) subGroup.Refresh();
        foreach (var e in Events) e.Refresh();
        foreach (var option in ManualRsvpOption.All) option.Refresh();
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(MemberFormTitle));
        OnPropertyChanged(nameof(EventFormTitle));
        NotifyStateChanged(); // the problems are worded in the language
    }
}
