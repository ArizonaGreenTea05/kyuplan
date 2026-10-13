using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>A tag was created or saved.</summary>
public sealed class TagSavedEventArgs(TagDto tag, bool created) : EventArgs
{
    public TagDto Tag { get; } = tag;

    public bool Created { get; } = created;
}

/// <summary>
/// The form that creates a tag or changes one: its name and the event it is about (none, or one event of the selected group). The tags page has
/// one, and so does the comment being written, which is why a tag can be made and changed there as well as on the tags page. It tells the
/// owner through <see cref="Saved"/>; what is shown of the tags afterwards is the owner's business.
/// </summary>
public sealed partial class TagEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private readonly Func<IReadOnlyList<TagDto>> _knownTags;

    /// <summary>The group of each event the picker offers: the events of the selected group, and the event the edited tag is about already.</summary>
    private readonly Dictionary<string, string> _groupOfEvent = [];

    /// <param name="knownTags">The tags there are: a name cannot be used twice.</param>
    public TagEditorViewModel(ShellViewModel shell, IPlannerApi api, Func<IReadOnlyList<TagDto>> knownTags)
    {
        _shell = shell;
        _api = api;
        _knownTags = knownTags;
        _eventOptions = [EventOption.None];

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    /// <summary>Raised when a tag was saved on the API.</summary>
    public event EventHandler<TagSavedEventArgs>? Saved;

    /// <summary>The form is open, for a new tag (<see cref="Editing"/> is <c>null</c>) or for <see cref="Editing"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(Title))]
    private bool _isOpen;

    /// <summary>The tag the form changes; <c>null</c> when it creates one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(Title))]
    private TagDto? _editing;

    [ObservableProperty]
    private string _name = "";

    /// <summary>The events to choose from: those of the selected group, led by the empty entry.</summary>
    [ObservableProperty]
    private IReadOnlyList<EventOption> _eventOptions;

    /// <summary>The event the tag is about; <see cref="EventOption.NoneId"/> = none.</summary>
    [ObservableProperty]
    private string _eventId = EventOption.NoneId;

    public bool IsCreating => IsOpen && Editing is null;

    public string Title => Editing is { } tag ? string.Format(Strings.Tags_FormEdit, tag.Name) : Strings.Tags_FormNew;

    public string? NameProblem
    {
        get
        {
            if (!IsOpen) return null;

            var name = Name.Trim();
            if (TagRules.CheckName(name) is not null)
                return Name.Length > 0 || Editing is not null ? string.Format(Strings.Tags_NameInvalid, TagRules.MaxNameLength) : null;

            return _knownTags().Any(t => t.Id != Editing?.Id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ? Strings.Tags_NameTaken
                : null;
        }
    }

    /// <summary>The form holds a tag the API accepts and, for an existing tag, one that differs from what is there.</summary>
    public bool CanSave =>
        IsOpen && !IsBusy && NameProblem is null && TagRules.CheckName(Name) is null && HasKnownEvent
        && (Editing is null || Name.Trim() != Editing.Name || EventId != (Editing.Event?.EventId ?? EventOption.NoneId));

    /// <summary>The chosen event is none, or one the picker offers (it has a group).</summary>
    private bool HasKnownEvent => EventId.Length == 0 || _groupOfEvent.ContainsKey(EventId);

    private bool CanClearEvent() => IsOpen && EventId.Length > 0;

    [RelayCommand(CanExecute = nameof(CanClearEvent))]
    private void ClearEvent() => EventId = EventOption.NoneId;

    /// <summary>Opens the form for a new tag. The events of the selected group are read for the picker; if that fails the form still works for the name.</summary>
    public Task BeginCreateAsync(string? name = null) => OpenAsync(null, name);

    /// <summary>Opens the form for an existing tag.</summary>
    public Task BeginEditAsync(TagDto tag) => OpenAsync(tag, tag.Name);

    private async Task OpenAsync(TagDto? tag, string? name)
    {
        Editing = tag;
        Name = name ?? "";
        EventId = tag?.Event?.EventId ?? EventOption.NoneId;
        SetEvents([], tag);
        ErrorMessage = null;
        IsOpen = true;
        NotifyStateChanged();

        var groupId = _shell.SelectedGroupId;
        if (groupId.Length == 0) return;

        await LoadAsync(async ct =>
        {
            var choices = await _api.GetEventChoicesAsync(groupId, ct);
            ct.ThrowIfCancellationRequested();

            if (!ReferenceEquals(Editing, tag) || !IsOpen) return; // the form was closed or opened for another tag while the events came
            SetEvents(choices.Events, tag, groupId);
        });
    }

    /// <summary>
    /// Fills the picker. The event the edited tag is about is always in it, also when it is not in the selected group's list (another group, or
    /// out of the search window), so that the picker can show it.
    /// </summary>
    private void SetEvents(IReadOnlyList<EventChoiceDto> events, TagDto? tag, string? groupId = null)
    {
        _groupOfEvent.Clear();
        List<EventChoiceDto> options = [.. events];
        foreach (var choice in events) _groupOfEvent[choice.Id] = groupId ?? "";

        if (tag?.Event is { } current && options.All(e => e.Id != current.EventId))
        {
            options.Add(new EventChoiceDto(current.EventId, current.Name, current.Start));
            options.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        if (tag?.Event is { } own) _groupOfEvent[own.EventId] = own.GroupId;

        var selected = EventId;
        EventOptions = [EventOption.None, .. options.Select(e => new EventOption(e))];
        EventId = selected;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        Close();
        ErrorMessage = null;
    }

    private void Close()
    {
        IsOpen = false;
        Editing = null;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return; // a command is only guarded by its button; a form can also be submitted with Enter

        var editing = Editing;
        var groupId = EventId.Length == 0 ? null : _groupOfEvent[EventId];
        var input = new TagInputDto(Name.Trim(), groupId, EventId.Length == 0 ? null : EventId);

        TagDto? saved = null;
        await LoadAsync(async ct =>
        {
            try
            {
                saved = editing is null ? await _api.CreateTagAsync(input, ct) : await _api.UpdateTagAsync(editing.Id, input, ct);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new PlannerApiException(Strings.Tags_NameTaken, ex.StatusCode, ex);
            }
        });

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open so the user can correct it

        Close();
        Saved?.Invoke(this, new TagSavedEventArgs(saved, created: editing is null));
    }

    partial void OnNameChanged(string value) => FormEdited();

    partial void OnEventIdChanged(string value) => FormEdited();

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    /// <summary>The owner's list of tags changed (a name may have become taken or free).</summary>
    public void KnownTagsChanged() => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NameProblem));
        SaveCommand.NotifyCanExecuteChanged();
        ClearEventCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The language changed: the title, the problem and the events' texts are worded in it.</summary>
    public void Refresh()
    {
        foreach (var option in EventOptions) option.Refresh();
        OnPropertyChanged(nameof(Title));
        NotifyStateChanged();
    }
}
