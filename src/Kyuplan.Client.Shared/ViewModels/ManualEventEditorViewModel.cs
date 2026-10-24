using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>The form to add an event to the selected group or to change one: name, start and end, and who is invited with what each answered.</summary>
public sealed partial class ManualEventEditorViewModel : ObservableObject
{
    private readonly IPlannerApi _api;
    private readonly IManualPageHost _host;
    private ManualEventRow? _editing;

    internal ManualEventEditorViewModel(IPlannerApi api, IManualPageHost host)
    {
        _api = api;
        _host = host;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isOpen;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private DateTime? _startDate = DateTime.Today;

    /// <summary>The time of day as typed, "18:00".</summary>
    [ObservableProperty]
    private string _startTime = "18:00";

    [ObservableProperty]
    private DateTime? _endDate = DateTime.Today;

    [ObservableProperty]
    private string _endTime = "19:30";

    [ObservableProperty]
    private bool _cancelled;

    [ObservableProperty]
    private IReadOnlyList<ManualInviteRow> _invitees = [];

    public IReadOnlyList<ManualRsvpOption> RsvpOptions => ManualRsvpOption.All;

    public bool HasInvitees => Invitees.Count > 0;

    public string Title => _editing is { } e ? string.Format(Strings.Manual_FormEditEvent, e.Event.Name) : Strings.Manual_FormNewEvent;

    public string? NameProblem => IsOpen && Name.Length > 0 && !ManualDataRules.IsName(Name)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    /// <summary>The start and end typed, or <c>null</c> for the one that cannot be read.</summary>
    private DateTimeOffset? Combine(DateTime? date, string time)
    {
        if (date is null || !TimeOnly.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var clock)) return null;

        var zone = _host.Zone;
        var local = date.Value.Date + clock.ToTimeSpan();
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // skipped by a change to summer time
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private DateTimeOffset? Start => Combine(StartDate, StartTime);

    private DateTimeOffset? End => Combine(EndDate, EndTime);

    public string? TimeProblem
    {
        get
        {
            if (!IsOpen) return null;
            if (Start is not { } start || End is not { } end) return Strings.Manual_TimeInvalid;
            return end < start ? Strings.Manual_EndBeforeStart : null;
        }
    }

    private ManualEventInputDto? Input() =>
        Start is { } start && End is { } end
            ? new ManualEventInputDto(
                Name.Trim(), start, end, Cancelled,
                [.. Invitees.Where(i => i.IsInvited).Select(i => new ManualInvitationDto(i.MemberId, i.Answer.Value))],
                [.. Invitees.Where(i => i.IsTrainer).Select(i => i.MemberId)])
            : null;

    public bool CanSave =>
        _host.CanEdit && IsOpen && !_host.IsBusy && ManualDataRules.IsName(Name) && TimeProblem is null && Input() is { } input
        && (_editing is not { } e || !SameEvent(input, e.Event));

    private static bool SameEvent(ManualEventInputDto input, ManualEventDto e) =>
        input.Name == e.Name && input.Start == e.Start && input.End == e.End && input.Cancelled == e.Cancelled
        && input.Invitations!.OrderBy(i => i.MemberId).SequenceEqual(e.Invitations.OrderBy(i => i.MemberId))
        && input.TrainerIds!.Order().SequenceEqual((e.TrainerIds ?? []).Order());

    /// <summary>Closes the form without asking anything.</summary>
    internal void Close() => IsOpen = false;

    /// <summary>Asks the commands and derived values again; the page calls it whenever its state changed.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(TimeProblem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Title));
        BeginNewCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        InviteAllCommand.NotifyCanExecuteChanged();
        InviteNoneCommand.NotifyCanExecuteChanged();
        AcceptAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanBeginNew() => _host.CanEdit && !_host.IsBusy && _host.HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginNew))]
    private async Task BeginNewAsync()
    {
        if (!CanBeginNew()) return;

        _host.Prepare();
        await OpenAsync(null);
    }

    private bool CanBeginEdit(ManualEventRow? row) => _host.CanEdit && !_host.IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private async Task BeginEditAsync(ManualEventRow row)
    {
        if (!CanBeginEdit(row)) return;

        _host.Prepare();
        await OpenAsync(row);
    }

    private async Task OpenAsync(ManualEventRow? row)
    {
        // The people to invite are read again: the member list on screen may be older than a change made in another window.
        IReadOnlyList<ManualMemberDto> members = [.. _host.Members.Select(m => m.Member)];
        if (_host.SelectedGroup is { } group)
        {
            if (await _host.ReadMembersAsync(group.Id) is not { } fresh) return;
            members = fresh;
        }

        _editing = row;
        var zone = _host.Zone;
        var invitations = row?.Event.Invitations.ToDictionary(i => i.MemberId, i => i.Rsvp) ?? [];
        var trainers = row?.Event.TrainerIds?.ToHashSet() ?? [];
        var unanswered = ManualRsvpOption.All.First(o => o.Value == ManualRsvp.Unanswered);

        Invitees = [.. members.Select(m =>
        {
            var invited = invitations.TryGetValue(m.Id, out var rsvp);
            return new ManualInviteRow(m.Id, m.Name, invited, invited ? ManualRsvpOption.All.First(o => o.Value == rsvp) : unanswered, trainers.Contains(m.Id));
        })];
        foreach (var invitee in Invitees) invitee.PropertyChanged += (_, _) => Edited();

        if (row is { } existing)
        {
            var start = TimeZoneInfo.ConvertTime(existing.Event.Start, zone);
            var end = TimeZoneInfo.ConvertTime(existing.Event.End, zone);
            Name = existing.Event.Name;
            StartDate = start.Date;
            StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndDate = end.Date;
            EndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture);
            Cancelled = existing.Event.Cancelled;
        }
        else
        {
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
            Name = "";
            StartDate = today;
            StartTime = "18:00";
            EndDate = today;
            EndTime = "19:30";
            Cancelled = false;
        }

        OnPropertyChanged(nameof(HasInvitees));
        OnPropertyChanged(nameof(Title));
        IsOpen = true;
        _host.StateChanged();
    }

    partial void OnNameChanged(string value) => Edited();

    partial void OnStartDateChanged(DateTime? oldValue, DateTime? newValue)
    {
        // The end follows the start while it was on the same day or would lie before it.
        if (IsOpen && newValue is { } start && (EndDate is null || EndDate < start || EndDate == oldValue)) EndDate = start;
        Edited();
    }

    partial void OnStartTimeChanged(string value) => Edited();

    partial void OnEndDateChanged(DateTime? value) => Edited();

    partial void OnEndTimeChanged(string value) => Edited();

    partial void OnCancelledChanged(bool value) => Edited();

    private void Edited()
    {
        if (IsOpen) _host.ErrorMessage = null;
        _host.StateChanged();
    }

    private bool CanChangeInvitations() => IsOpen && Invitees.Count > 0;

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
    private void Cancel()
    {
        _host.ErrorMessage = null;
        IsOpen = false;
        _host.StateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || _host.SelectedGroup is not { } group || Input() is not { } input) return;

        var editing = _editing;
        var done = await _host.ChangeAsync(
            ct => editing is null ? _api.CreateManualEventAsync(group.Id, input, ct) : _api.UpdateManualEventAsync(editing.Id, input, ct),
            () => string.Format(editing is null ? Strings.Manual_Created : Strings.Manual_Saved, input.Name));

        if (!done) return;

        IsOpen = false;
        _host.StateChanged();
    }
}
