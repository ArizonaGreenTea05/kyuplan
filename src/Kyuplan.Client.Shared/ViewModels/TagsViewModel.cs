using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the tag list: the tag plus ready-to-show texts, which follow the language after <see cref="Refresh"/>.</summary>
public sealed class TagRow(TagDto tag) : ObservableObject
{
    public TagDto Tag => tag;

    public int Id => tag.Id;

    public string Name => tag.Name;

    public bool HasEvent => tag.Event is not null;

    /// <summary>The name of the event the tag is about; empty for a tag without one.</summary>
    public string EventName => tag.Event?.Name ?? "";

    /// <summary>"Sat, 26 Sep 2026 · 10:00", when the event takes place; empty for a tag without an event.</summary>
    public string EventWhenText => tag.Event is { } e ? $"{Display.EventDay(e.Start)} · {Display.Time(e.Start)}" : "";

    /// <summary>"Gürtelprüfung · Sat, 26 Sep · 10:00", or "No event": the event in one line.</summary>
    public string EventText => tag.Event is { } e ? $"{e.Name} · {Display.EventStart(e.Start)}" : Strings.Tags_NoEvent;

    /// <summary>Sort key: tags without an event come last.</summary>
    public DateTimeOffset EventSort => tag.Event?.Start ?? DateTimeOffset.MaxValue;

    public int CommentCount => tag.CommentCount;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The tags page: the topics comments can be about, with creating, changing and deleting them and connecting each to a Spond event (or to
/// none; an event can have many tags). Tags can also be made and changed while a comment is written, see <see cref="MemberCommentsViewModel"/>.
/// Used by both clients.
/// </summary>
public sealed partial class TagsViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private Func<string>? _notice;

    public TagsViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;

        Editor = new TagEditorViewModel(shell, api, () => [.. Tags.Select(t => t.Tag)]);
        Editor.Saved += OnSaved;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TagEditorViewModel.IsOpen) or nameof(TagEditorViewModel.Editing)) NotifyStateChanged();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Tags;

    protected override string? ViewPermission => Permission.TagsView;

    /// <summary>Whether the user may create, change and delete tags; without it the page only shows them.</summary>
    public bool CanEdit => Shell.Can(Permission.TagsEdit);

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>The form that creates and changes tags.</summary>
    public TagEditorViewModel Editor { get; }

    /// <summary>Tags do not belong to a group (only the event a tag is about does).</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>The comment counts change when comments are written on the members page, and the events' names and days follow Spond: read again whenever the page is shown.</summary>
    protected override bool ReloadsOnActivation => !Editor.IsOpen && !IsConfirmingDelete;

    /// <summary>All tags by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<TagRow> _tags = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    /// <summary>The list was loaded and holds no tag: the page invites to create the first one.</summary>
    public bool IsEmpty => HasLoaded && Tags.Count == 0;

    /// <summary>What the page says when there is no tag: it invites to create one only somebody who may.</summary>
    public string EmptyText => CanEdit ? Strings.Tags_Empty : Strings.Tags_EmptyView;

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>Tag "Exam" created.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The form ------------------------------------------------------------------------------------------

    private bool CanBeginCreate() => CanEdit && !(Editor.IsOpen && Editor.Editing is null);

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private async Task BeginCreateAsync()
    {
        if (!CanBeginCreate()) return; // Execute does not ask CanExecute: what the user may not do is not done

        Prepare();
        await Editor.BeginCreateAsync();
    }

    private bool CanBeginEdit(TagRow? tag) => CanEdit && tag is not null && !(Editor.IsOpen && Editor.Editing?.Id == tag.Id);

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private async Task BeginEditAsync(TagRow tag)
    {
        if (!CanBeginEdit(tag)) return;

        Prepare();
        await Editor.BeginEditAsync(tag.Tag);
    }

    private void Prepare()
    {
        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = null;
    }

    private async void OnSaved(object? sender, TagSavedEventArgs e)
    {
        var name = e.Tag.Name;
        SetNotice(() => string.Format(e.Created ? Strings.Tags_Created : Strings.Tags_Saved, name));
        await LoadAsync(async ct => Tags = Rows(await _api.GetTagsAsync(ct)));
    }

    // --- Deleting --------------------------------------------------------------------------------------------

    /// <summary>The tag waiting for the confirmation that deletes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmText))]
    private TagRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    partial void OnPendingDeleteChanged(TagRow? value) => NotifyStateChanged();

    /// <summary>"Delete the tag …? It is taken off 3 comments; the comments stay."</summary>
    public string ConfirmText => PendingDelete is { } tag
        ? tag.CommentCount == 0
            ? string.Format(Strings.Tags_ConfirmDeleteUnused, tag.Name)
            : string.Format(Strings.Tags_ConfirmDelete, tag.Name, tag.CommentCount)
        : "";

    private bool CanAskDelete(TagRow? tag) => CanEdit && tag is not null && !ReferenceEquals(tag, PendingDelete) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAskDelete))]
    private void AskDelete(TagRow tag)
    {
        if (!CanAskDelete(tag)) return;

        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = tag;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } tag) return;

        PendingDelete = null;
        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteTagAsync(tag.Id, ct);
            deleted = true;
            Tags = Rows(await _api.GetTagsAsync(ct));
        });

        // A form for the deleted tag has nothing left to change.
        if (deleted && Editor.Editing?.Id == tag.Id) Editor.CancelCommand.Execute(null);
        if (deleted) SetNotice(() => string.Format(Strings.Tags_Deleted, tag.Name));
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            Tags = Rows(await _api.GetTagsAsync(ct));
            HasLoaded = true;
        });
    }

    private static IReadOnlyList<TagRow> Rows(IReadOnlyList<TagDto> tags) => [.. tags.Select(t => new TagRow(t))];

    partial void OnTagsChanged(IReadOnlyList<TagRow> value)
    {
        Editor.KnownTagsChanged();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var tag in Tags) tag.Refresh();
        Editor.Refresh();
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(EmptyText));
    }
}
