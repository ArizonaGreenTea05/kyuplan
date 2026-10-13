using CommunityToolkit.Mvvm.Input;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// A page bound to the shell's selected group. It loads when shown for the first time and whenever the
/// group or the server data changed — but only while it is the active page, so hidden pages do no work.
/// </summary>
public abstract partial class PageViewModel : ViewModelBase
{
    private bool _isActive;
    private bool _isStale = true;
    private bool _loadedSinceActivation;
    private bool _loadSkipped;

    protected PageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        shell.ContextChanged += (_, _) =>
        {
            if (!ReloadsWithContext) return;

            OnContextChanged();
            _ = InvalidateAsync();
        };
        shell.Language.Changed += (_, _) => OnLanguageChanged();

        // What the user may do changed (the shell learned who is signed in, or their roles changed): show and hide what they may use, and
        // read the page again, because what it could not read before it may read now (and the other way round). While the shell is still
        // starting up there is nothing to read again unless the page already tried and was not allowed: otherwise it loads when it is shown,
        // and the shell's start is what tells it who is signed in.
        shell.PermissionsChanged += (_, _) =>
        {
            OnPermissionsChanged();
            if ((shell.IsInitialized || _loadSkipped) && !(shell.IsRefreshing && ReloadsWithContext)) _ = InvalidateAsync(); // a refresh reloads the pages that follow the context itself, once it is done
        };
    }

    public ShellViewModel Shell { get; }

    /// <summary>The permission the page needs to be shown at all; <c>null</c> for a page every signed-in user may use (their own account).</summary>
    protected abstract string? ViewPermission { get; }

    /// <summary>Whether the signed-in user may use the page. A page that may not be used shows nothing and loads nothing.</summary>
    public bool CanView => ViewPermission is null || Shell.Can(ViewPermission);

    /// <summary>
    /// Whether the page shows data that depends on the selected group or on the Spond data (the default). Pages that do not, like
    /// the account and user pages, keep what they show and what the user typed when either changes.
    /// </summary>
    protected virtual bool ReloadsWithContext => true;

    /// <summary>
    /// Whether the page also reads its data again every time it is shown, not only when it is stale. For pages whose data another page
    /// changes (belts and members: handing out belts changes how many members hold each, creating one changes what can be handed out).
    /// A page that is in the middle of something (an open form) should answer <c>false</c>: its lists must not be replaced under it.
    /// </summary>
    protected virtual bool ReloadsOnActivation => false;

    /// <summary>The page's name in the current language.</summary>
    public abstract string Title { get; }

    /// <summary>Call when the page becomes visible. Loads if the page has no data yet or the context changed meanwhile.</summary>
    public async Task ActivateAsync()
    {
        _isActive = true;
        _loadedSinceActivation = false;
        await Shell.InitializeAsync();

        // A load that the shell's start-up caused meanwhile (the group became known) already is the fresh read.
        if (_isStale || (ReloadsOnActivation && !_loadedSinceActivation)) await ReloadCoreAsync();
    }

    public void Deactivate() => _isActive = false;

    [RelayCommand]
    private Task RefreshAsync() => Shell.RefreshDataCommand.ExecuteAsync(null);

    /// <summary>Marks the data outdated and reloads right away if the page is visible.</summary>
    protected async Task InvalidateAsync()
    {
        _isStale = true;
        if (_isActive) await ReloadCoreAsync();
    }

    /// <summary>
    /// The group changed or the server data was refreshed, so what the page keeps besides its main data (lists to
    /// choose from, for example) is outdated too. Called right away, even when the page is hidden and does not reload yet.
    /// </summary>
    protected virtual void OnContextChanged()
    {
    }

    /// <summary>
    /// The permissions of the signed-in user changed. Everything that depends on them (<see cref="CanView"/>, what can be edited, the commands
    /// that do it) is announced again; a page with commands overrides this to tell them too.
    /// </summary>
    protected virtual void OnPermissionsChanged() => OnPropertyChanged(string.Empty);

    /// <summary>Called whenever the page is stale and visible and may be used. Implementations load through <c>LoadAsync</c>.</summary>
    protected abstract Task ReloadAsync();

    /// <summary>
    /// The language changed. Texts that are built when data arrives (key figures, chart labels) must be built again
    /// from the data already loaded; the title is announced here.
    /// </summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(nameof(Title));

    private async Task ReloadCoreAsync()
    {
        _isStale = false;
        _loadedSinceActivation = true;
        _loadSkipped = !CanView;
        if (CanView) await ReloadAsync(); // the API would refuse what the user may not read
        if (HasError) _isStale = true; // try again next time the page is shown
    }
}
