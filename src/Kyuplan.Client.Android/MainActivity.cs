using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Screens;
using Kyuplan.Client.AndroidApp.Services;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Kyuplan.Client.AndroidApp;

/// <summary>
/// The one activity of the app. It decides what is on screen: the question for the server's address on the first start, the sign-in, and
/// then the app itself (<see cref="MainScreen"/>). A new address or a new sign-in starts the shared client over, so nothing of the old
/// server or the old user is left in the view models.
/// </summary>
[Activity(Label = "kyuplan", MainLauncher = true, Exported = true, Theme = "@style/AppTheme",
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard | ConfigChanges.Density | ConfigChanges.LayoutDirection)]
public class MainActivity : Activity, IAppActions
{
    private FrameLayout _frame = null!;
    private AppPreferences _preferences = null!;
    private Ui _ui = null!;
    private ServiceProvider? _services;
    private Screen? _screen;
    private Action? _cancelAddress;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _preferences = new AppPreferences(this);
        // The language the user picked last time, otherwise the one the phone is set to.
        LanguageService.Apply(_preferences.Language is { } saved ? LanguageService.Resolve(saved) : LanguageService.Resolve(CultureInfo.CurrentUICulture));
        _ui = new Ui(this, Palette.For(this, null));

        _frame = new FrameLayout(this);
        _frame.SetBackgroundColor(_ui.P.Background);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Window!.SetDecorFitsSystemWindows(false);
            _frame.SetOnApplyWindowInsetsListener(new InsetsListener());
        }

        SetContentView(_frame);
        _ = StartAsync();
    }

    protected override void OnDestroy()
    {
        _screen?.Dispose();
        _services?.Dispose();
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        if (_screen?.HandleBack() == true) return;
        if (_cancelAddress is { } cancel)
        {
            cancel();
            return;
        }

        base.OnBackPressed();
    }

    // --- What is on screen -----------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        if (ApiAddress.TryNormalize(_preferences.ApiAddress, out var address)) await SignInAsync(address);
        else ShowAddress(null);
    }

    private void Show(Screen screen)
    {
        _screen?.Dispose();
        _screen = screen;
        _frame.RemoveAllViews();
        _frame.AddView(screen.Root, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        screen.Start();
    }

    private void ShowAddress(string? current)
    {
        var vm = new ApiAddressViewModel(current);
        vm.Accepted += (_, _) =>
        {
            _cancelAddress = null;
            _preferences.ApiAddress = vm.Normalized;
            _ = SignInAsync(vm.Normalized!);
        };
        _cancelAddress = current is null ? null : () =>
        {
            _cancelAddress = null;
            _ = SignInAsync(current);
        };
        Show(new AddressScreen(_ui, vm, current is not null, () => _cancelAddress?.Invoke()));
    }

    /// <summary>Starts the shared client for <paramref name="address"/>: signs in with the remembered login, or asks for one.</summary>
    private async Task SignInAsync(string address)
    {
        _screen?.Dispose();
        _screen = null;
        _services?.Dispose();
        _frame.RemoveAllViews();
        _frame.AddView(new ProgressBar(this) { Indeterminate = true }, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));

        var services = _services = AppHost.Build(this, address);

        // The branding is public: asked for first, so that the sign-in already shows the name of the instance and its colour.
        var brand = services.GetRequiredService<BrandingViewModel>();
        await brand.LoadAsync();
        _ui = new Ui(this, Palette.For(this, brand.AccentColor));
        _frame.SetBackgroundColor(_ui.P.Background);

        var login = services.GetRequiredService<LoginViewModel>();
        if (await login.TrySignInWithSavedLoginAsync())
        {
            ShowMain(services);
            return;
        }

        login.SignedIn += (_, _) => ShowMain(services);
        Show(new LoginScreen(_ui, login, brand.ProductName, address, () => ShowAddress(address)));
    }

    private void ShowMain(ServiceProvider services)
    {
        // Picking another language is remembered, and every text reads itself again.
        var language = services.GetRequiredService<LanguageService>();
        language.Changed += (_, _) => _preferences.Language = language.Current.Code;

        Show(new MainScreen(_ui, services, this));
        services.GetRequiredService<ClientUpdateViewModel>().Watch(); // the server carries the APK of its own version
        services.GetRequiredService<EventBannerViewModel>().Watch(); // the banner of an event in progress
    }

    // --- IAppActions -----------------------------------------------------------------------------------------

    public void ChangeServer() => ShowAddress(_preferences.ApiAddress);

    public void SignOut()
    {
        if (_services is null || _preferences.ApiAddress is not { } address) return;

        _services.GetRequiredService<ILoginStore>().Clear();
        _ = SignInAsync(address);
    }

    public void Open(Uri address)
    {
        if (address.Scheme is not ("http" or "https")) return;

        StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(address.AbsoluteUri)));
    }

    /// <summary>
    /// The app draws behind the system bars (Android 15 makes every app do that), so the bars' size is the padding of the content, and the
    /// keyboard's, so that the field being typed in stays visible.
    /// </summary>
    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsets.Consumed;
        }
    }
}
