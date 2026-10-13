using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;
using Kyuplan.Client.ViewModels;
using Kyuplan.Client.WpfApp.Localization;
using Kyuplan.Client.WpfApp.Services;
using Kyuplan.Client.WpfApp.ViewModels;

namespace Kyuplan.Client.WpfApp;

public partial class App : Application
{
    private const string ApiAddressPreference = "api-address";

    private readonly UserPreferences _preferences = new();
    private IHost? _host;

    public App()
    {
        // The language the user picked last time, otherwise the one Windows is displayed in. It is applied here, in the
        // constructor, on purpose: a culture set later, in OnStartup's async continuation, is undone when that returns
        // (CultureInfo.CurrentCulture flows with the execution context) and the UI would format with the system's regional settings.
        LanguageService.Apply(_preferences.Get("language") is { } saved
            ? LanguageService.Resolve(saved)
            : LanguageService.Resolve(CultureInfo.CurrentUICulture));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            var product = _host?.Services.GetService<BrandingViewModel>()?.ProductName ?? "kyuplan";
            MessageBox.Show(args.Exception.Message, product, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TraceBindingErrorsIfRequested();

        // The windows that open and close again before the main window exists (the address dialog, the login window) must not
        // end the app with them. From the main window on, it ends with it.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            // Up before any window: the address dialog is drawn with it, and there is no host yet.
            var theme = new ThemeService(_preferences);
            theme.Initialize();

            if (ResolveApiAddress() is not { } apiAddress)
            {
                Shutdown();
                return;
            }

            // appsettings.json sits next to the executable; environment variables (PlannerApi__ApiKey) override it. The address of the
            // API is not configuration but the user's choice (see ResolveApiAddress): whatever the configuration says about it is ignored.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory,
            });

            builder.Services.AddPlannerClient(options =>
            {
                builder.Configuration.GetSection(PlannerClientOptions.Section).Bind(options);
                options.BaseAddress = apiAddress;
                options.ThemeClient = ThemeClients.Windows;
            });
            builder.Services.AddSingleton(_preferences);
            builder.Services.AddSingleton<IFilterSettingsStore, FileFilterSettingsStore>();
            builder.Services.AddSingleton<ILoginStore, DpapiLoginStore>();
            builder.Services.AddTransient<LoginViewModel>(); // one per sign-in: a fresh form every time
            builder.Services.AddSingleton(theme);
            builder.Services.AddSingleton<IClientUpdater, WindowsClientUpdater>();
            builder.Services.AddSingleton<ClientUpdateViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            await _host.StartAsync();

            RememberLanguageChanges(_host.Services);

            // The accent colour is the instance's, not Windows': taken over as soon as the API has said it (on the UI thread, where LoadAsync
            // was called), and again whenever an administrator changes it. The name and the logos are bound and follow by themselves.
            var brand = _host.Services.GetRequiredService<BrandingViewModel>();
            brand.Changed += (_, _) => theme.UseAccent(brand.AccentColor);
            brand.Watch(); // also while the login window is open: it shows the branding, too

            if (!await SignInAsync())
            {
                Shutdown();
                return;
            }

            if (AdoptTheme())
            {
                RestartNow(); // the theme of this user is of another design family than the one this run was started with
                return;
            }

            var main = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();

            // The server carries the installer of its own version; a client that is older offers it in the navigation pane.
            _host.Services.GetRequiredService<ClientUpdateViewModel>().Watch();
        }
        catch
        {
            Shutdown(); // with no window to keep it alive and nobody to end it, the app would linger as an invisible process
            throw;
        }
    }

    /// <summary>
    /// The address of the API: the one this Windows user chose before, otherwise asked for (first start). It is the user's own
    /// setting, kept with the language and the theme in <c>%LocalAppData%\Kyuplan</c>, and can be changed from the login window.
    /// <c>null</c> when the user gave up.
    /// </summary>
    private string? ResolveApiAddress()
    {
        if (ApiAddress.TryNormalize(_preferences.Get(ApiAddressPreference), out var saved)) return saved;

        var dialog = new ApiAddressWindow(current: null);
        if (dialog.ShowDialog() != true) return null;

        _preferences.Set(ApiAddressPreference, dialog.Address);
        return dialog.Address;
    }

    /// <summary>
    /// Starts this program again, which then reads the address that was just saved. Everything that was set up for the old address
    /// (HTTP clients, view models and the data they show) is of no use for another server, and a restart is the one way to be
    /// sure none of it is left over. The caller ends this instance.
    /// </summary>
    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Another design is loaded once, at start: ends this instance and starts the program again, which reads the new choice. The
    /// login is remembered, so the new instance signs in by itself.</summary>
    public void RestartNow()
    {
        Restart();
        Shutdown();
    }

    /// <summary>
    /// Signs in with the login remembered on this computer, and asks for one when there is none or the API refuses it.
    /// <c>false</c> when the user gave up, or chose another address in the login window: the program starts again then.
    /// </summary>
    private async Task<bool> SignInAsync()
    {
        var login = _host!.Services.GetRequiredService<LoginViewModel>();
        var brand = _host.Services.GetRequiredService<BrandingViewModel>();

        // The branding is public: asked for first, so that the login window already shows the name and logo of the instance.
        // It does not fail; an API that cannot be reached is reported by the login itself.
        var branding = brand.LoadAsync();
        if (await login.TrySignInWithSavedLoginAsync())
        {
            await branding; // the main window opens in the instance's accent colour, not first in the one of Windows
            return true;
        }

        await branding;
        var apiAddress = _host.Services.GetRequiredService<IOptions<PlannerClientOptions>>().Value.BaseAddress;
        var window = new LoginWindow(login, _host.Services.GetRequiredService<ThemeService>(), brand, apiAddress);
        var signedIn = window.ShowDialog() == true;

        if (window.NewApiAddress is { } changed)
        {
            _preferences.Set(ApiAddressPreference, changed);
            Restart();
        }

        return signedIn;
    }

    /// <summary>
    /// Takes over the theme of the signed-in user (their own, else the administrators' default) and starts to follow it: a change of it
    /// that is of the design family in use is drawn at once. <c>true</c> when the family differs, which only a restart can load.
    /// </summary>
    private bool AdoptTheme()
    {
        var services = _host!.Services;
        var themes = services.GetRequiredService<ThemeViewModel>();
        var theme = services.GetRequiredService<ThemeService>();

        if (!_followingTheme)
        {
            _followingTheme = true;
            // A default the administrators change while somebody works is drawn if the family allows it, otherwise it is remembered for
            // the next start: nobody is asked to restart for something they did not do. (A user's own choice is handled by the main window.)
            themes.Changed += (_, _) =>
            {
                if (services.GetRequiredService<LoginSession>().User is not null) theme.Adopt(themes.Effective);
            };
        }

        return theme.Adopt(themes.Effective);
    }

    private bool _followingTheme;

    /// <summary>
    /// Forgets the remembered login and asks for a login again, with the main window out of sight meanwhile. The data on the
    /// pages is the same for every user, so they keep what they show.
    /// </summary>
    public async Task SignOutAsync()
    {
        var services = _host!.Services;
        services.GetRequiredService<ILoginStore>().Clear();
        var session = services.GetRequiredService<LoginSession>();
        session.Login = null;
        session.User = null;

        MainWindow.Hide();
        var signedIn = false;
        try
        {
            signedIn = await SignInAsync();
        }
        finally
        {
            if (signedIn && AdoptTheme()) RestartNow(); // another user, another theme family
            else if (signedIn) MainWindow.Show();
            else Shutdown(); // gave up, or failed: a hidden window must not keep the app alive
        }
    }

    /// <summary>Picking another language is remembered, and every <c>{loc:Loc ...}</c> in the windows reads its text again.</summary>
    private void RememberLanguageChanges(IServiceProvider services)
    {
        var language = services.GetRequiredService<LanguageService>();
        language.Changed += (_, _) =>
        {
            _preferences.Set("language", language.Current.Code);
            LocalizationSource.Instance.Refresh();
        };
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// WPF swallows binding errors silently. With PP_BINDING_LOG=path they are written there, which is how
    /// typos in bindings get caught without a debugger attached.
    /// </summary>
    private static void TraceBindingErrorsIfRequested()
    {
        if (Environment.GetEnvironmentVariable("PP_BINDING_LOG") is not { Length: > 0 } path) return;

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(path));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        Trace.AutoFlush = true;
    }
}
