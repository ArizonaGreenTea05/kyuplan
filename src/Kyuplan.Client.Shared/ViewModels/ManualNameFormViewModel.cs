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

/// <summary>The form for groups and subgroups: one text field, to add a group or subgroup or to rename one.</summary>
public sealed partial class ManualNameFormViewModel : ObservableObject
{
    private readonly IPlannerApi _api;
    private readonly IManualPageHost _host;
    private ManualSubGroupRow? _renamingSubGroup;

    internal ManualNameFormViewModel(IPlannerApi api, IManualPageHost host)
    {
        _api = api;
        _host = host;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(Title))]
    private ManualNameForm _mode;

    [ObservableProperty]
    private string _text = "";

    public bool IsOpen => Mode != ManualNameForm.None;

    public string Title => Mode switch
    {
        ManualNameForm.NewGroup => Strings.Manual_FormNewGroup,
        ManualNameForm.RenameGroup => string.Format(Strings.Manual_FormRenameGroup, _host.SelectedGroup?.Name),
        ManualNameForm.NewSubGroup => Strings.Manual_FormNewSubGroup,
        ManualNameForm.RenameSubGroup => string.Format(Strings.Manual_FormRenameSubGroup, _renamingSubGroup?.Name),
        _ => "",
    };

    public string? Problem => IsOpen && Text.Length > 0 && !ManualDataRules.IsName(Text)
        ? string.Format(Strings.Manual_NameInvalid, ManualDataRules.MaxNameLength)
        : null;

    /// <summary>The name is one the API accepts and, when renaming, another one.</summary>
    public bool CanSave =>
        _host.CanEdit && IsOpen && !_host.IsBusy && ManualDataRules.IsName(Text) && Text.Trim() != (Mode switch
        {
            ManualNameForm.RenameGroup => _host.SelectedGroup?.Name,
            ManualNameForm.RenameSubGroup => _renamingSubGroup?.Name,
            _ => null,
        });

    /// <summary>Closes the form without asking anything.</summary>
    internal void Close() => Mode = ManualNameForm.None;

    /// <summary>Asks the commands and derived values again; the page calls it whenever its state changed.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Title));
        BeginNewGroupCommand.NotifyCanExecuteChanged();
        BeginRenameGroupCommand.NotifyCanExecuteChanged();
        BeginNewSubGroupCommand.NotifyCanExecuteChanged();
        BeginRenameSubGroupCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnTextChanged(string value)
    {
        if (IsOpen) _host.ErrorMessage = null;
        _host.StateChanged();
    }

    private bool CanBeginNewGroup() => _host.CanEdit && !_host.IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginNewGroup))]
    private void BeginNewGroup() => Open(ManualNameForm.NewGroup, "");

    private bool CanBeginRenameGroup() => _host.CanEdit && !_host.IsBusy && _host.HasGroup;

    [RelayCommand(CanExecute = nameof(CanBeginRenameGroup))]
    private void BeginRenameGroup()
    {
        if (CanBeginRenameGroup()) Open(ManualNameForm.RenameGroup, _host.SelectedGroup!.Name);
    }

    [RelayCommand(CanExecute = nameof(CanBeginRenameGroup))]
    private void BeginNewSubGroup()
    {
        if (CanBeginRenameGroup()) Open(ManualNameForm.NewSubGroup, "");
    }

    private bool CanBeginRenameSubGroup(ManualSubGroupRow? row) => _host.CanEdit && !_host.IsBusy && row is not null;

    [RelayCommand(CanExecute = nameof(CanBeginRenameSubGroup))]
    private void BeginRenameSubGroup(ManualSubGroupRow row)
    {
        if (CanBeginRenameSubGroup(row)) Open(ManualNameForm.RenameSubGroup, row.Name, row);
    }

    private void Open(ManualNameForm mode, string text, ManualSubGroupRow? subGroup = null)
    {
        _host.Prepare();
        _renamingSubGroup = subGroup;
        Text = text;
        Mode = mode;
        _host.StateChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        _host.ErrorMessage = null;
        Mode = ManualNameForm.None;
        _host.StateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var input = new ManualNameInputDto(Text.Trim());
        var mode = Mode;
        var name = input.Name;
        var group = _host.SelectedGroup;
        var subGroup = _renamingSubGroup;
        string? createdGroupId = null;

        var done = await _host.ChangeAsync(async ct =>
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

        Mode = ManualNameForm.None;
        if (createdGroupId is not null) _host.SelectGroup(createdGroupId);
        _host.StateChanged();
    }
}
