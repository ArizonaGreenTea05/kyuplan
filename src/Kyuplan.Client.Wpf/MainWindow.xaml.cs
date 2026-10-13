using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Kyuplan.Client.WpfApp.Services;
using Kyuplan.Client.WpfApp.ViewModels;

namespace Kyuplan.Client.WpfApp;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, ThemeService theme)
    {
        Theme = theme;
        DataContext = viewModel;
        InitializeComponent();

        // The default size is made for a wide screen; on a smaller one the window shrinks to fit instead of running off it.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

        ApplyLanguage();
        viewModel.Shell.Language.Changed += (_, _) => ApplyLanguage();
    }

    public ThemeService Theme { get; }

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Uri address) Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnOpenRepository(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private DateTime _flyoutClosedAt = DateTime.MinValue;

    // A click on the entry while its menu is open first closes the menu (a click outside it) and then lands here.
    private void OnFlyoutClick(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(300)) return;

        ((System.Windows.Controls.Primitives.Popup)((FrameworkElement)sender).Tag).IsOpen = true;
    }

    private void OnFlyoutClosed(object? sender, EventArgs e) => _flyoutClosedAt = DateTime.UtcNow;

    // Choosing a page closes the menu; the selection itself is made by the list.
    private void OnFlyoutPicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AdminPop.IsOpen = false;
        AccountPop.IsOpen = false;
    }

    private async void OnSignOut(object sender, RoutedEventArgs e) => await ((App)Application.Current).SignOutAsync();

    /// <summary>
    /// Bindings with a StringFormat and the date picker format with the element's Language, which is en-US unless it
    /// is set. Everything in the window inherits it, so setting it here is enough.
    /// </summary>
    private void ApplyLanguage() => Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
}
