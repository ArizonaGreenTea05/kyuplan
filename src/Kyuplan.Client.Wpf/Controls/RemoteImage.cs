using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// <c>RemoteImage.Source="{Binding ...}"</c> on an <see cref="Image"/>: the image behind an address (what the API serves for a logo,
/// SVG or bitmap), downloaded without blocking the window. There is no image until it has arrived, and none when there is no address
/// or it cannot be drawn: branding is cosmetic, so that is never an error.
/// <c>RemoteImage.WindowIcon="{Binding ...}"</c> on a <see cref="Window"/> does the same for its icon (title bar, taskbar, Alt+Tab);
/// until the icon has arrived, and without one, the window has the icon of the program file.
/// </summary>
public static class RemoteImage
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(RemoteImage),
        new PropertyMetadata(null, (element, e) =>
        {
            if (element is Image image) Show(image, e.NewValue, ImageLoader.LoadAsync, source => image.Source = source);
        }));

    public static readonly DependencyProperty WindowIconProperty = DependencyProperty.RegisterAttached(
        "WindowIcon", typeof(string), typeof(RemoteImage),
        new PropertyMetadata(null, (element, e) =>
        {
            if (element is Window window) Show(window, e.NewValue, ImageLoader.LoadIconAsync, source => window.Icon = source);
        }));

    /// <summary>Tells the download that is still running whether the address it was started for is still the wanted one.</summary>
    private static readonly DependencyProperty LatestProperty = DependencyProperty.RegisterAttached(
        "Latest", typeof(object), typeof(RemoteImage));

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    public static string? GetWindowIcon(DependencyObject element) => (string?)element.GetValue(WindowIconProperty);

    public static void SetWindowIcon(DependencyObject element, string? value) => element.SetValue(WindowIconProperty, value);

    private static async void Show(
        DependencyObject element, object? address, Func<Uri, CancellationToken, Task<ImageSource>> load, Action<ImageSource?> show)
    {
        var token = new object();
        element.SetValue(LatestProperty, token);
        show(null);
        if (address is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        try
        {
            var source = await load(uri, CancellationToken.None);
            if (ReferenceEquals(element.GetValue(LatestProperty), token)) show(source);
        }
        catch (Exception)
        {
            // async void: nothing may escape. The image stays out, as for an instance without a logo.
        }
    }
}
