using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>An entry of a choice list (a source kind, an invitation) with its text in the current language.</summary>
public sealed class FeedChoice(string value, Func<string> text)
{
    public string Value => value;

    public string Text => text();
}

/// <summary>A subgroup or member a source can be about, ticked or not.</summary>
public sealed partial class FeedOption(string key, string name, bool isSelected = false, string label = "") : ObservableObject
{
    public string Key => key;

    public string Name => name;

    /// <summary>Subgroups only: the word put in front of the titles of what concerns the subgroup.</summary>
    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

/// <summary>One source of the feed being edited. Everything it offers (the subgroups, the members) belongs to its group and is read again when the group changes.</summary>
public sealed partial class FeedSourceEditor : ObservableObject
{
    private readonly IPlannerApi _api;
    private readonly IReadOnlyList<GroupDto> _groups;

    public FeedSourceEditor(IPlannerApi api, IReadOnlyList<GroupDto> groups, CalendarSourceDto? source, string defaultGroupId)
    {
        _api = api;
        _groups = groups;
        _kind = source?.Kind ?? CalendarSourceKinds.Events;
        _groupId = source?.GroupId ?? (groups.Any(g => g.Id == defaultGroupId) ? defaultGroupId : groups.FirstOrDefault()?.Id ?? "");
        _invitation = source?.Invitation ?? EventInvitations.Any;
        _nameContains = source?.NameContains ?? "";
        _nameExcludes = source?.NameExcludes ?? "";
        _onlyExams = source?.OnlyExams ?? false;
        _includeCancelled = source?.IncludeCancelled ?? false;
        _label = source?.Label ?? "";

        ApplyGroup(source);
        _ = LoadMembersAsync(source);
    }

    public static IReadOnlyList<FeedChoice> KindChoices { get; } =
    [
        new(CalendarSourceKinds.Events, () => Strings.CalendarFeeds_Kind_Events),
        new(CalendarSourceKinds.Birthdays, () => Strings.CalendarFeeds_Kind_Birthdays),
        new(CalendarSourceKinds.ClubAnniversaries, () => Strings.CalendarFeeds_Kind_ClubAnniversaries),
        new(CalendarSourceKinds.BeltAnniversaries, () => Strings.CalendarFeeds_Kind_BeltAnniversaries),
    ];

    public static IReadOnlyList<FeedChoice> InvitationChoices { get; } =
    [
        new(EventInvitations.Any, () => Strings.CalendarFeeds_Invitation_Any),
        new(EventInvitations.All, () => Strings.CalendarFeeds_Invitation_All),
        new(EventInvitations.Only, () => Strings.CalendarFeeds_Invitation_Only),
    ];

    public IReadOnlyList<GroupDto> Groups => _groups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEvents))]
    private string _kind;

    /// <summary>The invitation, the name words, the exam and cancelled switches only mean something for events.</summary>
    public bool IsEvents => Kind == CalendarSourceKinds.Events;

    [ObservableProperty]
    private string _groupId;

    [ObservableProperty]
    private string _invitation;

    [ObservableProperty]
    private string _nameContains;

    [ObservableProperty]
    private string _nameExcludes;

    [ObservableProperty]
    private bool _onlyExams;

    [ObservableProperty]
    private bool _includeCancelled;

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private IReadOnlyList<FeedOption> _subGroups = [];

    [ObservableProperty]
    private IReadOnlyList<FeedOption> _members = [];

    partial void OnGroupIdChanged(string value)
    {
        ApplyGroup(null);
        _ = LoadMembersAsync(null);
    }

    private void ApplyGroup(CalendarSourceDto? preset)
    {
        var picked = preset?.SubGroupIds ?? [];
        SubGroups = [.. (_groups.FirstOrDefault(g => g.Id == GroupId)?.SubGroups ?? []).Select(s => new FeedOption(s.Id, s.Name, picked.Contains(s.Id), preset?.SubGroupLabels?.GetValueOrDefault(s.Id) ?? ""))];
        Members = [];
    }

    private async Task LoadMembersAsync(CalendarSourceDto? preset)
    {
        var groupId = GroupId;
        if (groupId.Length == 0) return;

        try
        {
            var picked = preset?.MemberIds ?? [];
            var members = await _api.GetMembersAsync(groupId);
            if (GroupId != groupId) return; // the group changed while they came

            Members = [.. members.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => new FeedOption(m.Id.ToString(), m.Name, picked.Contains(m.Id)))];
        }
        catch (Exception)
        {
            // The members are only there to choose from; a source without them still works for subgroups and everybody.
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    public CalendarSourceDto ToDto()
    {
        var words = SubGroups.Where(s => s.IsSelected && s.Label.Trim().Length > 0).ToDictionary(s => s.Key, s => s.Label.Trim());
        return ToDto(words.Count > 0 ? words : null);
    }

    private CalendarSourceDto ToDto(IReadOnlyDictionary<string, string>? words) => new(
        Kind, GroupId,
        [.. SubGroups.Where(s => s.IsSelected).Select(s => s.Key)],
        [.. Members.Where(m => m.IsSelected).Select(m => int.Parse(m.Key))],
        IsEvents ? Invitation : null,
        IsEvents && NameContains.Trim().Length > 0 ? NameContains.Trim() : null,
        IsEvents && NameExcludes.Trim().Length > 0 ? NameExcludes.Trim() : null,
        IsEvents && OnlyExams,
        IsEvents && IncludeCancelled,
        Label.Trim().Length > 0 ? Label.Trim() : null,
        words);
}

/// <summary>A calendar feed was created or saved.</summary>
public sealed class FeedSavedEventArgs(CalendarFeedDto feed, bool created) : EventArgs
{
    public CalendarFeedDto Feed { get; } = feed;

    public bool Created { get; } = created;
}

/// <summary>The form that makes a calendar feed or changes one: its name and its sources.</summary>
public sealed partial class CalendarFeedEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private readonly Func<IReadOnlyList<CalendarFeedDto>> _knownFeeds;

    public CalendarFeedEditorViewModel(ShellViewModel shell, IPlannerApi api, Func<IReadOnlyList<CalendarFeedDto>> knownFeeds)
    {
        _shell = shell;
        _api = api;
        _knownFeeds = knownFeeds;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public event EventHandler<FeedSavedEventArgs>? Saved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(Title))]
    private bool _isOpen;

    /// <summary>The feed the form changes; <c>null</c> when it makes one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(Title))]
    private CalendarFeedDto? _editing;

    [ObservableProperty]
    private string _name = "";

    public ObservableCollection<FeedSourceEditor> Sources { get; } = [];

    public bool IsCreating => IsOpen && Editing is null;

    public string Title => Editing is { } feed ? string.Format(Strings.CalendarFeeds_FormEdit, feed.Name) : Strings.CalendarFeeds_FormNew;

    public string? NameProblem
    {
        get
        {
            if (!IsOpen) return null;

            var name = Name.Trim();
            if (name.Length == 0) return Editing is not null ? string.Format(Strings.CalendarFeeds_NameInvalid, CalendarFeedRules.MaxNameLength) : null;
            if (name.Length > CalendarFeedRules.MaxNameLength) return string.Format(Strings.CalendarFeeds_NameInvalid, CalendarFeedRules.MaxNameLength);

            return _knownFeeds().Any(f => f.Id != Editing?.Id && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ? Strings.CalendarFeeds_NameTaken
                : null;
        }
    }

    public bool CanAddSource => IsOpen && Sources.Count < CalendarFeedRules.MaxSources && _shell.Groups.Count > 0;

    public bool CanSave =>
        IsOpen && !IsBusy && NameProblem is null && Name.Trim().Length > 0 && Sources.Count > 0 && Sources.All(s => s.GroupId.Length > 0);

    public void BeginCreate() => Open(null);

    public void BeginEdit(CalendarFeedDto feed) => Open(feed);

    private void Open(CalendarFeedDto? feed)
    {
        Editing = feed;
        Name = feed?.Name ?? "";
        ErrorMessage = null;

        Sources.Clear();
        foreach (var source in feed?.Sources ?? []) Sources.Add(NewSource(source));
        if (feed is null && _shell.Groups.Count > 0) Sources.Add(NewSource(null));

        IsOpen = true;
        NotifyStateChanged();
    }

    /// <summary>A source the form shows; what changes in it (its kind decides which fields there are) re-renders whoever watches the form.</summary>
    private FeedSourceEditor NewSource(CalendarSourceDto? dto)
    {
        var source = new FeedSourceEditor(_api, _shell.Groups, dto, _shell.SelectedGroupId);
        source.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Sources));
        return source;
    }

    private bool CanAdd() => CanAddSource;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSource()
    {
        if (!CanAdd()) return;

        Sources.Add(NewSource(null));
        FormEdited();
    }

    [RelayCommand]
    private void RemoveSource(FeedSourceEditor source)
    {
        Sources.Remove(source);
        FormEdited();
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
        Sources.Clear();
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var editing = Editing;
        var input = new CalendarFeedInputDto(Name.Trim(), [.. Sources.Select(s => s.ToDto())]);

        CalendarFeedDto? saved = null;
        await LoadAsync(async ct =>
        {
            try
            {
                saved = editing is null ? await _api.CreateCalendarFeedAsync(input, ct) : await _api.UpdateCalendarFeedAsync(editing.Id, input, ct);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new PlannerApiException(Strings.CalendarFeeds_NameTaken, ex.StatusCode, ex);
            }
        });

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open

        Close();
        Saved?.Invoke(this, new FeedSavedEventArgs(saved, created: editing is null));
    }

    partial void OnNameChanged(string value) => FormEdited();

    private void FormEdited()
    {
        if (IsOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    public void KnownFeedsChanged() => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanAddSource));
        OnPropertyChanged(nameof(NameProblem));
        SaveCommand.NotifyCanExecuteChanged();
        AddSourceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The language changed: the title and the choices are worded in it.</summary>
    public void Refresh()
    {
        foreach (var source in Sources) source.Refresh();
        OnPropertyChanged(nameof(Title));
        NotifyStateChanged();
    }
}
