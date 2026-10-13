using System.Windows;
using System.Windows.Media;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>Three segments (coming / declined / no answer yet) of one bar, as shares of the invited people.</summary>
public sealed class StackedBar : FrameworkElement
{
    private const double BarHeight = 8;

    public static readonly DependencyProperty YesProperty = Register(nameof(Yes), typeof(double), 0.0);
    public static readonly DependencyProperty NoProperty = Register(nameof(No), typeof(double), 0.0);
    public static readonly DependencyProperty OpenProperty = Register(nameof(Open), typeof(double), 0.0);
    public static readonly DependencyProperty YesBrushProperty = Register(nameof(YesBrush), typeof(Brush), null);
    public static readonly DependencyProperty NoBrushProperty = Register(nameof(NoBrush), typeof(Brush), null);
    public static readonly DependencyProperty OpenBrushProperty = Register(nameof(OpenBrush), typeof(Brush), null);
    public static readonly DependencyProperty TrackBrushProperty = Register(nameof(TrackBrush), typeof(Brush), null);

    public double Yes { get => (double)GetValue(YesProperty); set => SetValue(YesProperty, value); }
    public double No { get => (double)GetValue(NoProperty); set => SetValue(NoProperty, value); }
    public double Open { get => (double)GetValue(OpenProperty); set => SetValue(OpenProperty, value); }
    public Brush? YesBrush { get => (Brush?)GetValue(YesBrushProperty); set => SetValue(YesBrushProperty, value); }
    public Brush? NoBrush { get => (Brush?)GetValue(NoBrushProperty); set => SetValue(NoBrushProperty, value); }
    public Brush? OpenBrush { get => (Brush?)GetValue(OpenBrushProperty); set => SetValue(OpenBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(StackedBar),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0) return;

        // Everything is clipped to the rounded track, so the segments get rounded ends for free.
        var track = new Rect(0, 0, ActualWidth, BarHeight);
        dc.DrawRoundedRectangle(TrackBrush, null, track, BarHeight / 2, BarHeight / 2);
        dc.PushClip(new RectangleGeometry(track, BarHeight / 2, BarHeight / 2));

        var x = 0.0;
        foreach (var (share, brush) in new[] { (Yes, YesBrush), (No, NoBrush), (Open, OpenBrush) })
        {
            var width = Math.Clamp(share, 0, 1) * ActualWidth;
            if (width <= 0) continue;
            dc.DrawRectangle(brush, null, new Rect(x, 0, width, BarHeight));
            x += width;
        }

        dc.Pop();
    }
}
