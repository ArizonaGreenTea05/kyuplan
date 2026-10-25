using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the feed list: the feed, its address and what its sources are about in one line.</summary>
public sealed class CalendarFeedRow(CalendarFeedDto feed, IReadOnlyList<GroupDto> groups) : ObservableObject
{
    public CalendarFeedDto Feed => feed;

    public int Id => feed.Id;

    public string Name => feed.Name;

    /// <summary>The address to give to calendar apps.</summary>
    public string Url => feed.Url;

    /// <summary>"Events (Dojo), Birthdays (Dojo)": the kind of each source and its group; a source whose group is gone gets a warning.</summary>
    public string SourcesText => string.Join(", ", feed.Sources.Select((s, i) =>
    {
        var text = $"{FeedSourceEditor.KindChoices.FirstOrDefault(k => k.Value == s.Kind)?.Text ?? s.Kind} ({groups.FirstOrDefault(g => g.Id == s.GroupId)?.Name ?? s.GroupId})";
        return feed.BrokenSources?.Contains(i) == true ? string.Format(Strings.CalendarFeeds_SourceBroken, text) : text;
    }));

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The calendar feeds page (administration): the calendars calendar apps subscribe to by an address, each with its sources (events of a group
/// narrowed by who is invited, birthdays, anniversaries). Making, changing, giving a new address, deleting. Used by both clients.
/// </summary>
public sealed partial class CalendarFeedsViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private Func<string>? _notice;

    public CalendarFeedsViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;

        Editor = new CalendarFeedEditorViewModel(shell, api, () => [.. Feeds.Select(f => f.Feed)]);
        Editor.Saved += OnSaved;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CalendarFeedEditorViewModel.IsOpen) or nameof(CalendarFeedEditorViewModel.Editing)) NotifyStateChanged();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_CalendarFeeds;

    protected override string? ViewPermission => Permission.CalendarFeeds;

    /// <summary>The page is for those who define feeds, so whoever sees it may change them.</summary>
    public bool CanEdit => CanView;

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    public CalendarFeedEditorViewModel Editor { get; }

    /// <summary>Feeds do not belong to the selected group (each source names its own).</summary>
    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => !Editor.IsOpen && !IsConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<CalendarFeedRow> _feeds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    public bool IsEmpty => HasLoaded && Feeds.Count == 0;

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
    private void BeginCreate()
    {
        if (!CanBeginCreate()) return;

        Prepare();
        Editor.BeginCreate();
    }

    private bool CanBeginEdit(CalendarFeedRow? feed) => CanEdit && feed is not null && !(Editor.IsOpen && Editor.Editing?.Id == feed.Id);

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(CalendarFeedRow feed)
    {
        if (!CanBeginEdit(feed)) return;

        Prepare();
        Editor.BeginEdit(feed.Feed);
    }

    private void Prepare()
    {
        ErrorMessage = null;
        SetNotice(null);
        Pending = null;
    }

    private async void OnSaved(object? sender, FeedSavedEventArgs e)
    {
        var name = e.Feed.Name;
        SetNotice(() => string.Format(e.Created ? Strings.CalendarFeeds_Created : Strings.CalendarFeeds_Saved, name));
        await LoadAsync(async ct => Feeds = await RowsAsync(ct));
    }

    // --- New address and deleting: both ask first -------------------------------------------------------------

    /// <summary>The feed waiting for the answer to the question what to do with it (<see cref="PendingDelete"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(ConfirmText))]
    private CalendarFeedRow? _pending;

    /// <summary>The question is about deleting; otherwise it is about a new address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    private bool _pendingDelete;

    public bool IsConfirming => Pending is not null;

    partial void OnPendingChanged(CalendarFeedRow? value) => NotifyStateChanged();

    public string ConfirmText => Pending is { } feed
        ? string.Format(PendingDelete ? Strings.CalendarFeeds_ConfirmDelete : Strings.CalendarFeeds_ConfirmRegenerate, feed.Name)
        : "";

    private bool CanAsk(CalendarFeedRow? feed) => CanEdit && feed is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskDelete(CalendarFeedRow feed) => Ask(feed, delete: true);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void AskRegenerate(CalendarFeedRow feed) => Ask(feed, delete: false);

    private void Ask(CalendarFeedRow feed, bool delete)
    {
        if (!CanAsk(feed)) return;

        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = delete;
        Pending = feed;
    }

    [RelayCommand]
    private void CancelConfirm() => Pending = null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Pending is not { } feed) return;

        var delete = PendingDelete;
        Pending = null;
        var done = false;
        await LoadAsync(async ct =>
        {
            if (delete) await _api.DeleteCalendarFeedAsync(feed.Id, ct);
            else await _api.RegenerateCalendarFeedAsync(feed.Id, ct);
            done = true;
            Feeds = await RowsAsync(ct);
        });

        if (!done) return;

        if (delete && Editor.Editing?.Id == feed.Id) Editor.CancelCommand.Execute(null);
        SetNotice(() => string.Format(delete ? Strings.CalendarFeeds_Deleted : Strings.CalendarFeeds_Regenerated, feed.Name));
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            Feeds = await RowsAsync(ct);
            HasLoaded = true;
        });
    }

    private async Task<IReadOnlyList<CalendarFeedRow>> RowsAsync(CancellationToken cancellationToken) =>
        [.. (await _api.GetCalendarFeedsAsync(cancellationToken)).Select(f => new CalendarFeedRow(f, Shell.Groups))];

    partial void OnFeedsChanged(IReadOnlyList<CalendarFeedRow> value)
    {
        Editor.KnownFeedsChanged();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
        AskRegenerateCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var feed in Feeds) feed.Refresh();
        Editor.Refresh();
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NoticeText));
    }
}
