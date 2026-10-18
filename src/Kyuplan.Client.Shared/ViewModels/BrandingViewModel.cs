using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// How the instance presents itself (name, accent colour, logos): what the API's <c>Branding</c> settings say (or an administrator
/// saved), so the clients carry nothing of their own. Until <see cref="LoadAsync"/> has succeeded, and when the API cannot be reached,
/// it holds neutral values: the product name and no logo. Branding is cosmetic, so a failure here is never reported as an error.
/// It follows changes by itself: <see cref="Watch"/> asks the API again from time to time, and whatever is bound to it (or listens
/// to <see cref="Changed"/>) is updated when the answer differs.
/// </summary>
public sealed partial class BrandingViewModel : ObservableObject, IDisposable
{
    /// <summary>How often <see cref="Watch"/> asks the API. A change is rare and the answer is small; half a minute is "by itself" enough.</summary>
    public static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(30);

    private readonly IPlannerApi _api;
    private readonly Uri? _baseAddress;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _watching;
    private BrandingDto? _current;
    private Task? _loading;

    /// <param name="options">Holds the API's address; the urls of the images are made absolute against it. Without it they stay relative.</param>
    public BrandingViewModel(IPlannerApi api, IOptions<PlannerClientOptions>? options = null)
    {
        _api = api;
        _baseAddress = options?.Value.BaseUri;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(LogoDescription))]
    private string _productName = "kyuplan";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(LogoDescription))]
    private string _organizationName = "";

    /// <summary><c>#RGB</c> or <c>#RRGGBB</c>. Both clients draw selection, focus, buttons and links in it; the desktop client keeps the accent colour of Windows until <see cref="Changed"/> was raised for the first time.</summary>
    [ObservableProperty]
    private string _accentColor = "#2563eb";

    /// <summary>Logo for light surfaces (an SVG or a bitmap; both clients draw either), or <c>null</c>. The address changes with the image.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    private string? _logoUrl;

    /// <summary>Logo for dark surfaces; the API already answers with the light one when the instance has no other.</summary>
    [ObservableProperty]
    private string? _logoOnDarkUrl;

    [ObservableProperty]
    private string? _iconUrl;

    /// <summary>The theme of the web client for users who chose none (see <see cref="ThemeViewModel"/>).</summary>
    [ObservableProperty]
    private string _defaultWebTheme = ThemeIds.Default;

    /// <summary>The same for the Windows client.</summary>
    [ObservableProperty]
    private string _defaultWindowsTheme = ThemeIds.Default;

    /// <summary>Changes whenever a theme pack is installed, replaced or deleted (<see cref="ThemeViewModel"/> then asks for the catalog again).</summary>
    [ObservableProperty]
    private string _themesVersion = "";

    /// <summary>"kyuplan · Club name", or just the product name when the instance has no organisation.</summary>
    public string WindowTitle => OrganizationName.Length == 0 ? ProductName : $"{ProductName} · {OrganizationName}";

    /// <summary>What the logo shows, for screen readers: the organisation, or the product when there is none.</summary>
    public string LogoDescription => OrganizationName.Length == 0 ? ProductName : OrganizationName;

    public bool HasLogo => LogoUrl is not null;

    /// <summary>
    /// Raised when the API's answer has been taken over: the first time, and whenever a later answer differs from what is shown. The web
    /// client passes accent colour and icon on to the page then, the desktop client the accent colour to its theme; everything else is bound.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Asks the API until it has answered once. Concurrent callers share one request; once it has succeeded further calls do nothing (use
    /// <see cref="RefreshAsync"/> for a newer answer), and after a failure the next call tries again. Never throws.
    /// </summary>
    public Task LoadAsync()
    {
        if (_current is not null) return Task.CompletedTask;
        if (_loading is { IsCompleted: false } running) return running;
        return _loading = RefreshAsync();
    }

    /// <summary>
    /// Asks the API and takes over what is different from what is shown. Every call asks anew (a caller that has just changed the
    /// branding wants an answer from after that); calls are served one after the other, so answers are applied in order. Nothing happens
    /// when the API cannot be reached: the last branding stays. Never throws.
    /// </summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            BrandingDto branding;
            try
            {
                branding = Absolute(await _api.GetBrandingAsync());
            }
            catch (PlannerApiException)
            {
                return; // keeps what it had; the shell reports an API that cannot be reached, with its own retry
            }

            if (branding == _current) return;

            _current = branding;
            ProductName = branding.ProductName;
            OrganizationName = branding.OrganizationName;
            AccentColor = branding.AccentColor;
            LogoUrl = branding.LogoUrl;
            LogoOnDarkUrl = branding.LogoOnDarkUrl;
            IconUrl = branding.IconUrl;
            DefaultWebTheme = branding.DefaultWebTheme;
            DefaultWindowsTheme = branding.DefaultWindowsTheme;
            ThemesVersion = branding.ThemesVersion;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts asking the API every <paramref name="every"/> (<see cref="DefaultWatchInterval"/>) for as long as this lives, so that a
    /// change an administrator makes reaches this client without a reload or a restart. Does nothing when it is already watching. Call
    /// it on the thread that owns the bound controls: <see cref="Changed"/> is raised from there.
    /// </summary>
    public void Watch(TimeSpan? every = null)
    {
        if (_watching is not null) return;

        _watching = new CancellationTokenSource();
        _ = WatchAsync(every ?? DefaultWatchInterval, _watching.Token);
    }

    private async Task WatchAsync(TimeSpan every, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Never throws by itself, but a listener of Changed might; one faulty tick must not end the watching.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    public void Dispose()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;
    }

    private BrandingDto Absolute(BrandingDto branding) => branding with
    {
        LogoUrl = Absolute(branding.LogoUrl),
        LogoOnDarkUrl = Absolute(branding.LogoOnDarkUrl),
        IconUrl = Absolute(branding.IconUrl),
    };

    private string? Absolute(string? url) =>
        url is null || _baseAddress is null ? url : new Uri(_baseAddress, url).AbsoluteUri;
}
