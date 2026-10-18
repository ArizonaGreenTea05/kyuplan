using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;
using Microsoft.Extensions.Options;

namespace Kyuplan.Client.ViewModels;

/// <summary>A link to a part of the documentation that the signed-in user may read.</summary>
public sealed record HelpLink(string Title, Uri Address);

/// <summary>
/// State that spans all pages: server info (demo banner, defaults), the group everything is evaluated for and who is signed in.
/// Pages react to <see cref="ContextChanged"/>.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IPlannerApi _api;
    private readonly Uri? _baseAddress;

    public ShellViewModel(IPlannerApi api, LanguageService? language = null, LoginSession? session = null, BrandingViewModel? brand = null,
        IOptions<PlannerClientOptions>? options = null)
    {
        _api = api;
        _baseAddress = options?.Value.BaseUri;
        Language = language ?? new LanguageService();
        Session = session ?? new LoginSession();
        Brand = brand ?? new BrandingViewModel(api);

        // Who is signed in changes when this client learns it (the browser asks the API once the shell loads) and when the user renames themselves.
        _knownPermissions = CurrentUser?.EffectivePermissions ?? [];
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LoginSession.User)) return;

            OnPropertyChanged(nameof(CurrentUser));

            // The language the user chose is theirs on every computer; without a choice the client keeps the language it has.
            if (LanguageRules.IsValid(CurrentUser?.Language)) Language.Select(CurrentUser!.Language);

            // Pages only need to know when what the user may do is different now, not for every rename.
            var permissions = CurrentUser?.EffectivePermissions ?? [];
            if (permissions.SequenceEqual(_knownPermissions)) return;

            _knownPermissions = permissions;
            OnPropertyChanged(nameof(UsesSpond));
            OnPropertyChanged(nameof(HelpLinks));
            PermissionsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private IReadOnlyList<string> _knownPermissions;

    /// <summary>Raised when the permissions of the signed-in user changed: somebody else signed in, or their roles were changed and the client learned it.</summary>
    public event EventHandler? PermissionsChanged;

    /// <summary>The language of the interface; pages rebuild their texts when it changes.</summary>
    public LanguageService Language { get; }

    /// <summary>Name, accent colour and logos of the instance. Public on the API, so a client loads it before the login (<see cref="BrandingViewModel.LoadAsync"/>).</summary>
    public BrandingViewModel Brand { get; }

    /// <summary>The login of this client and the user the API says it belongs to.</summary>
    public LoginSession Session { get; }

    /// <summary>The signed-in user, once known: WPF learns it when signing in, the browser when the shell has loaded.</summary>
    public UserDto? CurrentUser => Session.User;

    /// <summary>Whether the signed-in user may do this (one of <see cref="Permission"/>). Nobody may anything until the API said who they are.</summary>
    public bool Can(string permission) => CurrentUser?.Can(permission) == true;

    /// <summary>Whether the signed-in user may do at least one of these.</summary>
    public bool CanAny(IEnumerable<string> permissions) => permissions.Any(Can);

    /// <summary>Whether the signed-in user may read what Spond knows (groups, events): only then the group picker is of any use.</summary>
    public bool UsesSpond => CanAny(Permission.ReadsSpond);

    private bool _initialized;
    private Task? _initialization;

    /// <summary>The shell has read its data once (who is signed in, the server info, the groups). Before, the pages are still on their way to their first load.</summary>
    public bool IsInitialized => _initialized;

    private bool _refreshing;

    /// <summary>The shell is refreshing its data and will tell the pages to reload when it is done: they need no second reason to.</summary>
    public bool IsRefreshing => _refreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelpLinks))]
    [NotifyPropertyChangedFor(nameof(RepositoryUrl))]
    private ServerInfoDto? _info;

    /// <summary>The repository this server was published from (the "powered by" link), <c>null</c> when the build has none (from source).</summary>
    public Uri? RepositoryUrl => Uri.TryCreate(Info?.RepositoryUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;

    /// <summary>
    /// The parts of the documentation the signed-in user may read (<see cref="Documentation.For"/>): the user guide first, then the ones for administrators.
    /// Empty while the server has no documentation or has not said who is signed in.
    /// </summary>
    public IReadOnlyList<HelpLink> HelpLinks => Info?.HasDocumentation == true && CurrentUser is not null && _baseAddress is { } baseAddress
        ? [.. Documentation.For(Can).Select(a => new HelpLink(HelpTitle(a.Id), new Uri(baseAddress, Documentation.PathFor(a.Id))))]
        : [];

    private static string HelpTitle(string audience) => audience switch
    {
        Documentation.Admin => Strings.Help_Admin,
        Documentation.Deployment => Strings.Help_Deployment,
        _ => Strings.Help_User,
    };

    [ObservableProperty]
    private IReadOnlyList<GroupDto> _groups = [];

    private string _selectedGroupId = "";

    /// <summary>
    /// The id of the group everything is evaluated for, "" until there is one. A list control that gets another list loses its selection
    /// and writes "nothing chosen" (null) back (WPF, when the groups are refreshed): that is no choice of the user, so it is ignored.
    /// </summary>
    public string SelectedGroupId
    {
        get => _selectedGroupId;
        set
        {
            if (value is null || !SetProperty(ref _selectedGroupId, value)) return;

            OnPropertyChanged(nameof(SelectedGroup));
            ContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the selected group changed or the server data was refreshed.</summary>
    public event EventHandler? ContextChanged;

    public bool IsDemoData => Info?.IsDemoData == true;

    /// <summary>"API v1.0", or nothing until the server answered.</summary>
    public string ApiVersionText => Info is { } info ? string.Format(Strings.Shell_ApiVersion, info.Version) : "";

    public GroupDto? SelectedGroup => Groups.FirstOrDefault(g => g.Id == SelectedGroupId);

    public int DefaultMinAttendancePercent => Info?.DefaultMinAttendancePercent ?? 75;

    public int DefaultMinInvitedEvents => Info?.DefaultMinInvitedEvents ?? 3;

    public double DefaultExamMinAverageStars => Info?.DefaultExamMinAverageStars ?? RatingRules.StandardExamMinAverageStars;

    /// <summary>
    /// Loads server info and groups once. Concurrent callers share one load (the layout and the first page both
    /// ask on start-up); after a failed attempt the next call tries again.
    /// </summary>
    public Task InitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;

        // Join a load that is still running; a finished one (failed attempt) is replaced by a new attempt. Checking
        // IsCompleted instead of resetting a field in a finally block matters: a load that completes synchronously
        // would clear the field before the caller had stored the task in it.
        if (_initialization is { IsCompleted: false } running) return running;
        return _initialization = InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        await Brand.LoadAsync(); // a client that has not asked yet (or whose first try failed); never fails

        await LoadAsync(async ct =>
        {
            // Who the user is decides what they may ask for: somebody who may use no page that shows Spond's data is not shown its groups.
            var user = Session.User ?? await _api.GetAccountAsync(ct);
            Info = await _api.GetInfoAsync(ct);
            Groups = user.EffectivePermissions.Any(Permission.ReadsSpond.Contains) ? await _api.GetGroupsAsync(ct) : [];
            Session.User ??= user;
            _initialized = true;
        });

        OnPropertyChanged(nameof(IsDemoData));
        OnPropertyChanged(nameof(ApiVersionText));
        OnPropertyChanged(nameof(DefaultMinAttendancePercent));
        OnPropertyChanged(nameof(DefaultMinInvitedEvents));
        OnPropertyChanged(nameof(DefaultExamMinAverageStars));

        if (_initialized && SelectedGroup is null) SelectedGroupId = Groups.FirstOrDefault()?.Id ?? "";
    }

    /// <summary>Retry after a failed <see cref="InitializeAsync"/>, e.g. when the API was not up yet.</summary>
    [RelayCommand]
    private Task RetryAsync() => InitializeAsync();

    /// <summary>Drops the server's cached Spond data, learns again what the user may do (an administrator may have changed it) and tells the active page to reload.</summary>
    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        _refreshing = true;
        try
        {
            await LoadAsync(async ct =>
            {
                await _api.RefreshAsync(ct);
                var user = await _api.GetAccountAsync(ct);
                Groups = user.EffectivePermissions.Any(Permission.ReadsSpond.Contains) ? await _api.GetGroupsAsync(ct) : [];
                Session.User = user;
            });
        }
        finally
        {
            _refreshing = false;
        }

        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(SelectedGroupId)); // the group picker lost its choice with the old list: show it again
        if (!HasError) ContextChanged?.Invoke(this, EventArgs.Empty);
    }
}
