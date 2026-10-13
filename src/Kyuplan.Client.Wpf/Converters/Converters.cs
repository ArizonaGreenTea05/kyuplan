using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Converters;

/// <summary>Visible when false — the built-in converter only does the opposite.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Convert(value, targetType, parameter, culture);
}

/// <summary>Visible when false — the built-in converter only does the opposite.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when at least one of the bound values is true, e.g. a column that holds two buttons which the user may use one of each.</summary>
public sealed class AnyToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Any(value => value is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed for null or empty text, so labels disappear instead of leaving a gap.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when the bound count is zero, for "nothing here" messages.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Binds a group of RadioButtons to one enum property: the parameter is the value a button stands for.</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>True for a number above zero, e.g. to show a clear button only while a text box has text.</summary>
public sealed class PositiveToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A colour written <c>#RRGGBB</c> as a brush, for the swatches of belts. Anything else (empty, or a colour that is still being
/// typed) is transparent, so a swatch simply shows nothing until the colour is complete.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || !BeltRules.IsColor(text.Trim())) return Brushes.Transparent;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Trim()));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Fills the placeholder of a text from the resources, e.g. "Use the colour {0}": the first value is the text (bound to a
/// <c>{loc:Loc}</c> source, so it follows the language), the others fill its placeholders.
/// </summary>
public sealed class FormatTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [string format, .. var arguments] ? string.Format(culture, format, arguments) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>How far a reply is moved in (a number of pixels) as the left margin of its comment; a little space under it separates the comments.</summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double indent ? Math.Max(0, indent) : 0, 0, 0, 10);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An attendance mark as the text of its tooltip, in the current language.</summary>
public sealed class MarkTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AttendanceMark mark ? Display.Mark(mark) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a share (0–1) into a left margin, to put a label where the required-attendance line is: the parameter is
/// "left,width" of the bars in device-independent pixels, e.g. "12,150".
/// </summary>
public sealed class FractionToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = ((string)parameter).Split(',');
        var left = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var width = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return new Thickness(left + width * Math.Clamp(value is double share ? share : 0, 0, 1), 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
