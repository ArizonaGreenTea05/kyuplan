using System.Globalization;
using System.Windows.Data;

namespace Kyuplan.Client.WpfApp.Converters;

/// <summary>True when the two bound values are the same object, e.g. to mark the row of a list that is the chosen one.</summary>
public sealed class SameInstanceConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
