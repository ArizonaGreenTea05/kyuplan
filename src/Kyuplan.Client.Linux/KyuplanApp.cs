using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.LinuxApp.Services;
using Kyuplan.Client.LinuxApp.Stages;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp;

/// <summary>
/// The program: one window that shows, one after the other, the question for the API's address (first start), the setup of a new API, the
/// login, and the application itself. Same order as the WPF client's windows; the work of each step is in the shared view models.
/// </summary>
internal sealed class KyuplanApp
{
    private const string ApiAddressPreference = "api-address";

    private readonly Adw.Application _application;
    private readonly string[] _args;
    private readonly UserPreferences _preferences = new();
    private Adw.ApplicationWindow? _window;
    private Gtk.Stack _stack = null!;
    private Adw.ToastOverlay _toasts = null!;
    private IHost? _host;
    private UiContext? _context;
    private Func<Gtk.Widget>? _currentStage;
    private string? _currentName;
    private bool _starting;

    public KyuplanApp(Adw.Application application, string[] args)
    {
        _application = application;
        _args = args;

        // The language the user picked last time, otherwise the one the system is displayed in.
        LanguageService.Apply(_preferences.Get("language") is { } saved
            ? LanguageService.Resolve(saved)
            : LanguageService.Resolve(CultureInfo.CurrentUICulture));
    }

    public void Activate()
    {
        if (_window is not null)
        {
            _window.Present();
            return;
        }

        _stack = Gtk.Stack.New();
        _stack.TransitionType = Gtk.StackTransitionType.Crossfade;
        _toasts = Adw.ToastOverlay.New();
        _toasts.SetChild(_stack);
        _window = Adw.ApplicationWindow.New(_application);
        _window.SetDefaultSize(1360, 860);
        _window.Title = "kyuplan";
        _window.SetContent(_toasts);
        _context = new UiContext(_window, _toasts, new ServiceCollection().BuildServiceProvider());
        _window.Present();

        if (_starting) return;
        _starting = true;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            if (ResolveApiAddress() is not { } address) return; // the address dialog is shown; the program starts again when it is answered
            await RunAsync(address);
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
    }

    private async Task RunAsync(string apiAddress)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = _args, ContentRootPath = AppContext.BaseDirectory });

        // appsettings.json sits next to the executable; environment variables (PlannerApi__ApiKey) override it. The address of the API is not
        // configuration but the user's choice (see ResolveApiAddress): whatever the configuration says about it is ignored.
        builder.Services.AddPlannerClient(options =>
        {
            builder.Configuration.GetSection(PlannerClientOptions.Section).Bind(options);
            options.BaseAddress = apiAddress;
            options.ThemeClient = ThemeClients.Web;
        });
        builder.Services.AddSingleton(_preferences);
        builder.Services.AddSingleton<IFilterSettingsStore, FileFilterSettingsStore>();
        builder.Services.AddSingleton<ILoginStore, SecretServiceLoginStore>();
        builder.Services.AddTransient<LoginViewModel>(); // one per sign-in: a fresh form every time
        builder.Services.AddSingleton<IClientUpdater>(_ => new LinuxClientUpdater(() => _application.Quit()));
        builder.Services.AddSingleton<ClientUpdateViewModel>();
        builder.Services.AddSingleton<NavigationViewModel>();
        builder.Services.AddSingleton<Theming>();

        _host = builder.Build();
        await _host.StartAsync();
        var services = _host.Services;
        _context = new UiContext(_window!, _toasts, services);

        RememberLanguageChanges(services);

        // The accent colour is the instance's: taken over as soon as the API has said it, and again whenever an administrator changes it.
        var theming = services.GetRequiredService<Theming>();
        theming.Apply();
        var brand = services.GetRequiredService<BrandingViewModel>();
        brand.Watch();
        brand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrandingViewModel.WindowTitle)) _window!.Title = brand.WindowTitle;
        };

        // The branding is public: asked for first, so that every screen already shows the name and logo of the instance.
        await brand.LoadAsync();
        _window!.Title = brand.WindowTitle;

        var setup = services.GetRequiredService<SetupViewModel>();
        await setup.CheckAsync();
        if (setup.NeedsSetup) await SetUpAsync(setup);

        if (!await SignInAsync(apiAddress)) return;
        ShowShell();
    }

    /// <summary>
    /// The address of the API: the one this user chose before, otherwise asked for (first start). It is the user's own setting, kept with the
    /// language in <c>~/.config/kyuplan</c>, and can be changed from the login screen. <c>null</c> while the user is being asked.
    /// </summary>
    private string? ResolveApiAddress()
    {
        if (ApiAddress.TryNormalize(_preferences.Get(ApiAddressPreference), out var saved)) return saved;

        var model = new ApiAddressViewModel(current: null);
        model.Accepted += (_, _) =>
        {
            _preferences.Set(ApiAddressPreference, model.Normalized!);
            Restart();
        };
        Show("address", () => AddressStage.Build(_context!, model, onCancel: null));
        return null;
    }

    /// <summary>
    /// Starts this program again, which then reads the address that was just saved. Everything that was set up for the old address
    /// (HTTP clients, view models and the data they show) is of no use for another server, and a restart is the one way to be sure none of it is left over.
    /// </summary>
    private void Restart()
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false };

            // Started with `dotnet kyuplan.dll`: the host program needs the library again.
            if (Path.GetFileNameWithoutExtension(path) == "dotnet" && Environment.GetCommandLineArgs() is [var library, ..])
                start.ArgumentList.Add(library);
            Process.Start(start);
        }

        _application.Quit();
    }

    /// <summary>Shows the setup screen until the API is set up (the API restarts to apply the settings, which the screen waits for).</summary>
    private Task SetUpAsync(SetupViewModel setup)
    {
        var done = new TaskCompletionSource();
        void Finished(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SetupViewModel.IsDone) || !setup.IsDone) return;

            setup.PropertyChanged -= Finished;
            done.TrySetResult();
        }

        setup.PropertyChanged += Finished;
        Show("setup", () => SetupStage.Build(_context!, setup));
        return done.Task.ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Signs in with the login remembered in the keyring, and asks for one when there is none or the API refuses it. <c>false</c> when the user
    /// gave up or chose another address in the login screen: the program starts again then.
    /// </summary>
    private async Task<bool> SignInAsync(string apiAddress)
    {
        var services = _host!.Services;
        var login = services.GetRequiredService<LoginViewModel>();
        var themes = services.GetRequiredService<ThemeViewModel>();
        if (await login.TrySignInWithSavedLoginAsync())
        {
            await themes.LoadCatalogAsync(); // a theme pack is only known once the catalog is: else its user would be shown the default
            return true;
        }

        await themes.LoadCatalogAsync();
        var done = new TaskCompletionSource<bool>();
        login.SignedIn += (_, _) => done.TrySetResult(true);
        Show("login", () => LoginStage.Build(_context!, login, services.GetRequiredService<BrandingViewModel>(), apiAddress, ChangeAddress));
        return await done.Task;
    }

    private void ChangeAddress(string current)
    {
        var model = new ApiAddressViewModel(current);
        model.Accepted += (_, _) =>
        {
            _preferences.Set(ApiAddressPreference, model.Normalized!);
            Restart();
        };
        Show("address", () => AddressStage.Build(_context!, model, onCancel: () => _ = BackToLoginAsync()));
    }

    private async Task BackToLoginAsync()
    {
        var services = _host!.Services;
        var done = await SignInAsync(services.GetRequiredService<IOptions<PlannerClientOptions>>().Value.BaseAddress);
        if (done) ShowShell();
    }

    private void ShowShell()
    {
        var services = _host!.Services;
        var navigation = services.GetRequiredService<NavigationViewModel>();
        var context = _context!;
        Show("shell", () => ShellStage.Build(context, navigation, SignOutAsync));
        navigation.Start();

        // The server carries the installer of its own version; a client that is older offers it in the sidebar.
        navigation.Update.Watch();
        navigation.Banner.Watch(); // the banner of an event in progress
    }

    /// <summary>Forgets the remembered login and asks for a login again. The data on the pages is the same for every user, so they keep what they show.</summary>
    private async Task SignOutAsync()
    {
        var services = _host!.Services;
        services.GetRequiredService<ILoginStore>().Clear();
        var session = services.GetRequiredService<LoginSession>();
        session.Login = null;
        session.User = null;

        var signedIn = await SignInAsync(services.GetRequiredService<IOptions<PlannerClientOptions>>().Value.BaseAddress);
        if (signedIn) ShowShell();
    }

    /// <summary>Picking another language is remembered, and every screen reads its texts again.</summary>
    private void RememberLanguageChanges(IServiceProvider services)
    {
        var language = services.GetRequiredService<LanguageService>();
        language.Changed += (_, _) =>
        {
            _preferences.Set("language", language.Current.Code);
            if (_currentStage is { } stage && _currentName is { } name) Show(name, stage);
        };
    }

    /// <summary>Puts a screen in the window in place of the one shown; <paramref name="build"/> is kept to draw it again when the language changes.</summary>
    private void Show(string name, Func<Gtk.Widget> build)
    {
        _currentStage = build;
        _currentName = name;
        var widget = build();
        if (_stack.GetChildByName(name) is { } old) _stack.Remove(old);
        _stack.AddNamed(widget, name);
        _stack.SetVisibleChildName(name);
    }

    private void ShowFatal(Exception exception)
    {
        var page = Adw.StatusPage.New();
        page.IconName = "dialog-error-symbolic";
        page.Title = "kyuplan";
        page.Description = exception.Message;
        var close = Gtk.Button.NewWithLabel(Strings.Login_Exit);
        close.Halign = Gtk.Align.Center;
        close.OnClicked += (_, _) => _application.Quit();
        page.SetChild(close);
        if (_stack.GetChildByName("fatal") is { } old) _stack.Remove(old);
        _stack.AddNamed(page, "fatal");
        _stack.SetVisibleChildName("fatal");
    }
}
