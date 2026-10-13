using System.Windows;
using System.Windows.Controls;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// Lays children out in equally wide columns, as many as fit given <see cref="MinItemWidth"/> — the WPF
/// counterpart of CSS <c>repeat(auto-fit, minmax(min, 1fr))</c> used by the Blazor client for KPI rows and card grids.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Fixed column count (0 = automatic); used where a balanced grid reads better than "as many as fit".</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        if (double.IsInfinity(width)) width = MinItemWidth;
        var columns = Columns > 0 ? Columns : Math.Max(1, (int)Math.Floor((width + Gap) / (MinItemWidth + Gap)));
        columns = Math.Min(columns, Math.Max(1, InternalChildren.Count));
        return (columns, Math.Max(0, (width - (columns - 1) * Gap) / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Layout(availableSize.Width);
        double totalHeight = 0, rowHeight = 0;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);

            if ((i + 1) % columns == 0 || i == InternalChildren.Count - 1)
            {
                totalHeight += rowHeight + (totalHeight > 0 ? Gap : 0);
                rowHeight = 0;
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Layout(finalSize.Width);
        double y = 0, rowHeight = 0;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var column = i % columns;
            if (column == 0 && i > 0) y += rowHeight + Gap;
            if (column == 0) rowHeight = RowHeight(i, columns);

            InternalChildren[i].Arrange(new Rect(column * (itemWidth + Gap), y, itemWidth, rowHeight));
        }

        return finalSize;
    }

    private double RowHeight(int firstIndex, int columns)
    {
        var height = 0.0;
        for (var i = firstIndex; i < Math.Min(firstIndex + columns, InternalChildren.Count); i++)
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        return height;
    }
}
