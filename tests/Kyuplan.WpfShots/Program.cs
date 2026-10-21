using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;
using Kyuplan.Client.ViewModels;
using Kyuplan.Client.WpfApp;
using Kyuplan.Client.WpfApp.Services;
using Kyuplan.Client.WpfApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kyuplan.WpfShots;

/// <summary>
/// Opens the main window of the WPF client offscreen, signs in as the administrator of a running API, shows every page in turn and saves a
/// screenshot of each as PNG. Arguments: API address, user name, password, output folder. Exit code 1 if a page cannot be shown.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: Kyuplan.WpfShots <api address> <user> <password> <output folder>");
            return 2;
        }

        var app = new Application(); // not App: its OnStartup would sign in with the login of the real program
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var failures = 0;
        var done = new DispatcherFrame();
        var run = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { failures = await RunAsync(args[0], args[1], args[2], args[3]); }
            catch (Exception ex) { Console.Error.WriteLine(ex); failures = 1; }
            finally { done.Continue = false; }
        });
        Dispatcher.PushFrame(done);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync(string address, string user, string password, string output)
    {
        var preferences = new UserPreferences();
        var theme = new ThemeService(preferences);
        theme.Initialize();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPlannerClient(options =>
        {
            options.BaseAddress = address;
            options.ThemeClient = ThemeClients.Windows;
        });
        builder.Services.AddSingleton(preferences);
        builder.Services.AddSingleton<IFilterSettingsStore, FileFilterSettingsStore>();
        builder.Services.AddSingleton<ILoginStore, NoLoginStore>(); // nothing is remembered on this computer
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddSingleton(theme);
        builder.Services.AddSingleton<IClientUpdater, WindowsClientUpdater>();
        builder.Services.AddSingleton<ClientUpdateViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        using var host = builder.Build();
        await host.StartAsync();

        var login = host.Services.GetRequiredService<LoginViewModel>();
        login.UserName = user;
        login.Password = password;
        await login.SignInCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(login.ErrorMessage))
        {
            Console.Error.WriteLine($"Sign-in failed: {login.ErrorMessage}");
            return 1;
        }

        await host.Services.GetRequiredService<BrandingViewModel>().LoadAsync();
        await host.Services.GetRequiredService<ThemeViewModel>().LoadCatalogAsync();

        var main = host.Services.GetRequiredService<MainWindow>();
        var model = host.Services.GetRequiredService<MainViewModel>();
        main.Left = -20000; // shown (WPF renders nothing of a window that was never shown) but far outside every screen
        main.Top = 0;
        main.Width = 1440;
        main.Height = 900;
        main.ShowInTaskbar = false;
        main.Show();

        Directory.CreateDirectory(output);
        var failures = 0;
        var pages = model.Items.Concat(model.AdminItems).Concat(model.AccountItems).ToList();
        foreach (var item in pages)
        {
            model.SelectedItem = item;
            await Settle(item.Page);

            var name = item.Page.GetType().Name.Replace("ViewModel", "");
            if (item.Page.ErrorMessage is { Length: > 0 } error)
            {
                Console.Error.WriteLine($"{name}: {error}");
                failures++;
            }

            Save(main, Path.Combine(output, name + ".png"));
            Console.WriteLine($"{name}: ok");
        }

        main.Close();
        return failures;
    }

    /// <summary>Lets the page load: until it is no longer busy and WPF has drawn what the load changed.</summary>
    private static async Task Settle(PageViewModel page)
    {
        await Task.Delay(100);
        for (var i = 0; i < 300 && page.IsBusy; i++) await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(1500);
    }

    private static void Save(Window window, string file)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // The Windows 11 design draws the window on Mica, which RenderTargetBitmap does not see: the window's own background goes under it.
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle((Brush)(window.TryFindResource("SolidBackgroundFillColorBaseBrush") ?? window.FindResource(SystemColors.WindowBrushKey)), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(backdrop);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private sealed class NoLoginStore : ILoginStore
    {
        public Login? Load() => null;
        public void Save(Login login) { }
        public void Clear() { }
    }
}
