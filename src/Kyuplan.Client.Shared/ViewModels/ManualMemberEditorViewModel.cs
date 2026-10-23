using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>The form to add a member to the selected group or to change one: name, birth date and subgroups.</summary>
public sealed partial class ManualMemberEditorViewModel : ObservableObject
{
    private readonly IPlannerApi _api;
    private readonly IManualPageHost _host;
    private ManualMemberRow? _editing;

    internal ManualMemberEditorViewModel(IPlannerApi api, IManualPageHost host)
    {
        _api = api;
        _host = host;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isOpen;

    [ObservableProperty]
    private string _name = "";

    /// <summary>The day the member was born; <c>null</c> = not known. A date picker holds a <see cref="DateTime"/>, so this does too.</summary>
    [ObservableProperty]
    private DateTime? _birthDate;

    [ObservableProperty]
    private IReadOnlyList<ManualChoiceRow> _subGroups = [];

    public bool HasSubGroupChoices => SubGroups.Count > 0;

    public string Title => _editing is { } member ? string.Format(Strings.Manual_FormEditMember, member.Name) : Strings.Manual_FormNewMember;

    public string? NameProblem => IsOpen && Name.Length > 0 && !ManualDataRules.IsName(Name)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    public string? BirthProblem => IsOpen && BirthDate is { } day && MemberRules.CheckBirthDate(DateOnly.FromDateTime(day), DateOnly.FromDateTime(DateTime.Today)) is not null
        ? string.Format(Strings.MemberPage_BirthInvalid, Display.Date(MemberRules.EarliestBirth))
        : null;

    public bool CanSave =>
        _host.CanEdit && IsOpen && !_host.IsBusy && ManualDataRules.IsName(Name) && BirthProblem is null
        && (_editing is not { } member || !SameMember(Input(), member.Member));

    private ManualMemberInputDto Input() => new(
        Name.Trim(),
        BirthDate is { } day ? DateOnly.FromDateTime(day) : null,
        [.. SubGroups.Where(s => s.IsSelected).Select(s => s.Id)]);

    private static bool SameMember(ManualMemberInputDto input, ManualMemberDto member) =>
        input.Name == member.Name && input.BirthDate == member.BirthDate
        && input.SubGroupIds!.Order().SequenceEqual(member.SubGroupIds.Order());

    /// <summary>Closes the form without asking anything.</summary>
    internal void Close() => IsOpen = false;

    /// <summary>Asks the commands and derived values again; the page calls it whenever its state changed.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(BirthProblem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Title));
        BeginNewCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ClearBirthDateCommand.NotifyCanExecuteChanged();
    }

    private bool CanBeginNew() => _host.CanEdit && !_host.IsBusy && _host.HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginNew))]
    private void BeginNew()
    {
        if (!CanBeginNew()) return;

        _host.Prepare();
        Open(null);
    }

    private bool CanBeginEdit(ManualMemberRow? row) => _host.CanEdit && !_host.IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(ManualMemberRow row)
    {
        if (!CanBeginEdit(row)) return;

        _host.Prepare();
        Open(row);
    }

    private void Open(ManualMemberRow? row)
    {
        _editing = row;
        Name = row?.Name ?? "";
        BirthDate = row?.Member.BirthDate?.ToDateTime(TimeOnly.MinValue);
        SubGroups = [.. (_host.SelectedGroup?.SubGroups ?? []).Select(s => new ManualChoiceRow(s.Id, s.Name, row?.Member.SubGroupIds.Contains(s.Id) == true))];
        foreach (var choice in SubGroups) choice.PropertyChanged += (_, _) => Edited();
        OnPropertyChanged(nameof(HasSubGroupChoices));
        IsOpen = true;
        OnPropertyChanged(nameof(Title));
        _host.StateChanged();
    }

    partial void OnNameChanged(string value) => Edited();

    partial void OnBirthDateChanged(DateTime? value) => Edited();

    private void Edited()
    {
        if (IsOpen) _host.ErrorMessage = null;
        _host.StateChanged();
    }

    private bool CanClearBirthDate() => BirthDate is not null;

    [RelayCommand(CanExecute = nameof(CanClearBirthDate))]
    private void ClearBirthDate() => BirthDate = null;

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
        if (!CanSave || _host.SelectedGroup is not { } group) return;

        var input = Input();
        var editing = _editing;
        var done = await _host.ChangeAsync(
            ct => editing is null ? _api.CreateManualMemberAsync(group.Id, input, ct) : _api.UpdateManualMemberAsync(editing.Id, input, ct),
            () => string.Format(editing is null ? Strings.Manual_Created : Strings.Manual_Saved, input.Name));

        if (!done) return;

        IsOpen = false;
        _host.StateChanged();
    }
}
