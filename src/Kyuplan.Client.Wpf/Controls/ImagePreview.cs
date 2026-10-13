using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// Shows the image behind an address (what the API serves for a logo or icon), or says why there is none: <see cref="EmptyText"/>
/// when there is no address, <see cref="FailedText"/> when what the address serves cannot be drawn. The text takes the foreground of its surroundings, so a page can put the control on a dark or a light background.
/// </summary>
public sealed class ImagePreview : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(ImagePreview), new PropertyMetadata(null, (d, _) => ((ImagePreview)d).Load()));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(ImagePreview), new PropertyMetadata("", (d, _) => ((ImagePreview)d).ShowText()));

    public static readonly DependencyProperty FailedTextProperty = DependencyProperty.Register(
        nameof(FailedText), typeof(string), typeof(ImagePreview), new PropertyMetadata("", (d, _) => ((ImagePreview)d).ShowText()));

    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
    };

    private bool _failed;
    private int _loads;

    public ImagePreview()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);
        Children.Add(_text);
        Load();
    }

    /// <summary>The absolute address of the image, or <c>null</c> when there is none.</summary>
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>What to say while there is no image.</summary>
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    /// <summary>What to say when there is an image that cannot be drawn.</summary>
    public string FailedText { get => (string)GetValue(FailedTextProperty); set => SetValue(FailedTextProperty, value); }

    private async void Load()
    {
        var load = ++_loads; // a download that is still running for an earlier address must not show its result
        _failed = false;
        _image.Source = null;
        ShowText();

        if (Source is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return;

        try
        {
            var image = await ImageLoader.LoadAsync(address);
            if (load == _loads) _image.Source = image;
        }
        catch (Exception)
        {
            // async void: nothing may escape. Whatever went wrong (no download, no image, a format that cannot be drawn), say so.
            if (load != _loads) return;
            _failed = true;
            ShowText();
        }
    }

    private void ShowText()
    {
        if (Source is null) _text.Text = EmptyText;
        else _text.Text = _failed ? FailedText : "";
    }
}
