using System.Windows;
using System.Windows.Controls;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class HintedTextBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HintedTextBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(HintedTextBox), new PropertyMetadata(""));

    /// <summary>A Segoe Fluent Icons glyph shown in front of the text, e.g. the magnifier of a search box.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(HintedTextBox), new PropertyMetadata("", OnGlyphChanged));

    public HintedTextBox()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateInset();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Shown in grey while the box is empty (WinUI calls it PlaceholderText).</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HintedTextBox)d).UpdateInset();

    /// <summary>Text, hint and clear button keep clear of the icon; the box padding makes room for the buttons on the right.</summary>
    private void UpdateInset()
    {
        var left = string.IsNullOrEmpty(Glyph) ? 0 : 26;
        Box.Padding = new Thickness(11 + left, 5, 32, 6);
        HintBlock.Margin = new Thickness(11 + left + 1, 0, 32, 1);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Box.Clear();
        Box.Focus();
    }
}
