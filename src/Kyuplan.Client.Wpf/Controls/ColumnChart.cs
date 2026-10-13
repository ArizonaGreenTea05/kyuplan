using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// A plain column chart over <see cref="BarViewModel"/>s: value above each bar, label below a baseline, tooltip on hover.
/// Drawn directly so the shared view models need no chart library on either client.
/// </summary>
public sealed class ColumnChart : FrameworkElement
{
    private const double ValueHeight = 20;
    private const double LabelHeight = 24;
    private const double Gap = 6;
    private const double MaxBarWidth = 56;

    public static readonly DependencyProperty BarsProperty = Register(nameof(Bars), typeof(IReadOnlyList<BarViewModel>), null);
    public static readonly DependencyProperty BarBrushProperty = Register(nameof(BarBrush), typeof(Brush), null);
    public static readonly DependencyProperty HighlightBrushProperty = Register(nameof(HighlightBrush), typeof(Brush), null);
    public static readonly DependencyProperty BaselineBrushProperty = Register(nameof(BaselineBrush), typeof(Brush), null);
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), typeof(Brush), null);

    public IReadOnlyList<BarViewModel>? Bars { get => (IReadOnlyList<BarViewModel>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush? HighlightBrush { get => (Brush?)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public Brush? BaselineBrush { get => (Brush?)GetValue(BaselineBrushProperty); set => SetValue(BaselineBrushProperty, value); }
    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(ColumnChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent backdrop makes the whole area hit-testable for the tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (Bars is not { Count: > 0 } bars) return;

        var columnWidth = ActualWidth / bars.Count;
        var plotHeight = Math.Max(0, ActualHeight - ValueHeight - LabelHeight);
        var baseline = ValueHeight + plotHeight;

        // Bars are drawn with rounded ends top and bottom and clipped at the baseline, so only the top is rounded.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, baseline)));
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var barWidth = Math.Clamp(columnWidth - Gap * 2, 2, MaxBarWidth);
            var x = i * columnWidth + (columnWidth - barWidth) / 2;
            var height = Math.Max(3, plotHeight * Math.Clamp(bar.Fraction, 0, 1));

            dc.DrawRoundedRectangle(
                bar.IsHighlighted ? HighlightBrush : BarBrush, null,
                new Rect(x, baseline - height, barWidth, height + 4), 3, 3);
        }

        dc.Pop();

        dc.DrawRectangle(BaselineBrush, null, new Rect(0, baseline, ActualWidth, 1));

        for (var i = 0; i < bars.Count; i++)
        {
            var height = Math.Max(3, plotHeight * Math.Clamp(bars[i].Fraction, 0, 1));
            DrawCentered(dc, bars[i].Value, i * columnWidth, columnWidth, baseline - height - ValueHeight + 2, 12);
            DrawCentered(dc, bars[i].Label, i * columnWidth, columnWidth, baseline + 8, 12);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Bars is not { Count: > 0 } bars || ActualWidth <= 0) return;

        var index = (int)(e.GetPosition(this).X / (ActualWidth / bars.Count));
        ToolTip = index >= 0 && index < bars.Count ? bars[index].Tooltip : null;
    }

    private void DrawCentered(DrawingContext dc, string text, double columnLeft, double columnWidth, double top, double size)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, TextBrush ?? Brushes.Gray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = columnWidth,
            TextAlignment = TextAlignment.Center,
            MaxLineCount = 1,
            Trimming = TextTrimming.None,
        };

        dc.DrawText(formatted, new Point(columnLeft, top));
    }
}
