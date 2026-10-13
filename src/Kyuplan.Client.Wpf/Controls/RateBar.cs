using System.Windows;
using System.Windows.Media;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// A thin percentage bar. The optional threshold is drawn as a line over the full height of the control, so rows of
/// bars that share a width form one line through the column: it is visible at a glance which side of the required
/// attendance a member is on. Drawn directly (no templates): cheap enough for hundreds of table rows.
/// </summary>
public sealed class RateBar : FrameworkElement
{
    private const double BarHeight = 6;

    public static readonly DependencyProperty FractionProperty = Register(nameof(Fraction), typeof(double), 0.0);
    public static readonly DependencyProperty ThresholdProperty = Register(nameof(Threshold), typeof(double), double.NaN);
    public static readonly DependencyProperty BarCenterProperty = Register(nameof(BarCenter), typeof(double), double.NaN);
    public static readonly DependencyProperty FillBrushProperty = Register(nameof(FillBrush), typeof(Brush), null);
    public static readonly DependencyProperty TrackBrushProperty = Register(nameof(TrackBrush), typeof(Brush), null);
    public static readonly DependencyProperty LineBrushProperty = Register(nameof(LineBrush), typeof(Brush), null);

    /// <summary>0–1.</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    /// <summary>Required share, 0–1; NaN draws no line.</summary>
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }

    /// <summary>Distance of the bar centre from the top; NaN centres it. Lets the line span a taller cell than the bar.</summary>
    public double BarCenter { get => (double)GetValue(BarCenterProperty); set => SetValue(BarCenterProperty, value); }

    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(RateBar),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width, 14);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0) return;

        var top = (double.IsNaN(BarCenter) ? ActualHeight / 2 : BarCenter) - BarHeight / 2;
        const double radius = BarHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, width, BarHeight), radius, radius);

        var fraction = Math.Clamp(double.IsNaN(Fraction) ? 0 : Fraction, 0, 1);
        if (fraction > 0)
        {
            // At least one bar height wide, so a tiny share still shows as a dot instead of a sliver.
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, top, Math.Max(width * fraction, BarHeight), BarHeight), radius, radius);
        }

        if (!double.IsNaN(Threshold))
        {
            var x = Math.Clamp(Threshold, 0, 1) * width;
            dc.PushOpacity(0.6);
            dc.DrawRectangle(LineBrush, null, new Rect(x - 1, 0, 2, ActualHeight));
            dc.Pop();
        }
    }
}
