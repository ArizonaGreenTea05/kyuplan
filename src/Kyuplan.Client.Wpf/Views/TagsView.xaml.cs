using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kyuplan.Client.WpfApp.Views;

public partial class TagsView : UserControl
{
    public TagsView() => InitializeComponent();

    /// <summary>
    /// A DataGrid keeps the mouse wheel for itself even when it has nothing to scroll. The list is as tall as its rows, so the wheel
    /// is handed on to the page, which is what scrolls.
    /// </summary>
    private void OnGridMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || VisualTreeHelper.GetParent((DependencyObject)sender) is not UIElement parent) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
