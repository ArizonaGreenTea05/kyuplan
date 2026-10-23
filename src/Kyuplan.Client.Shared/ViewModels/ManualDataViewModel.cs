using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The page where users keep the groups, members and events by hand (data source "manual"): pick or add a group, then maintain its members,
/// its subgroups and its events (who is invited, what each answered). One form is open at a time. Every change is saved at once and the shell
/// reads the groups again, so the other pages show it. Used by both clients.
/// </summary>
public sealed partial class ManualDataViewModel : PageViewModel, IManualPageHost
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
        NameForm = new ManualNameFormViewModel(api, this);
        MemberEditor = new ManualMemberEditorViewModel(api, this);
        EventEditor = new ManualEventEditorViewModel(api, this);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Info)) OnPropertyChanged(nameof(IsWrongSource));
        };
    }

    /// <summary>The form to add or rename a group or subgroup.</summary>
    public ManualNameFormViewModel NameForm { get; }

    /// <summary>The form to add or change a member.</summary>
    public ManualMemberEditorViewModel MemberEditor { get; }

    /// <summary>The form to add or change an event.</summary>
    public ManualEventEditorViewModel EventEditor { get; }

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

    public bool IsAnyFormOpen => NameForm.IsOpen || MemberEditor.IsOpen || EventEditor.IsOpen;

    private void CloseForms()
    {
        CloseFormsOnly();
        Pending = null;
        NotifyStateChanged();
    }

    private void CloseFormsOnly()
    {
        NameForm.Close();
        MemberEditor.Close();
        EventEditor.Close();
    }

    private void Prepare()
    {
        ErrorMessage = null;
        SetNotice(null);
        CloseForms();
    }

    // --- What the forms see ------------------------------------------------------------------------------------

    TimeZoneInfo IManualPageHost.Zone => Zone;

    void IManualPageHost.Prepare() => Prepare();

    void IManualPageHost.StateChanged() => NotifyStateChanged();

    Task<bool> IManualPageHost.ChangeAsync(Func<CancellationToken, Task> change, Func<string> notice) => ChangeAsync(change, notice);

    void IManualPageHost.SelectGroup(string groupId) => SelectedGroup = Groups.FirstOrDefault(g => g.Id == groupId) ?? SelectedGroup;

    async Task<IReadOnlyList<ManualMemberDto>?> IManualPageHost.ReadMembersAsync(string groupId)
    {
        IReadOnlyList<ManualMemberDto>? members = null;
        await LoadAsync(async ct => members = await _api.GetManualMembersAsync(groupId, ct));
        return HasError ? null : members;
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
        CloseFormsOnly();
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
        NameForm.Refresh();
        MemberEditor.Refresh();
        EventEditor.Refresh();

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
        NotifyStateChanged(); // the problems and the form titles are worded in the language
    }
}
