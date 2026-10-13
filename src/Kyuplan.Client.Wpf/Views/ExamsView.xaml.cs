using System.Windows.Controls;
using System.Windows.Input;

namespace Kyuplan.Client.WpfApp.Views;

public partial class ExamsView : UserControl
{
    public ExamsView() => InitializeComponent();

    /// <summary>
    /// The tables and the lists that sit in this page have scroll viewers of their own (they scroll sideways when the window is narrow,
    /// and a list box has one by nature). A scroll viewer takes every turn of the mouse wheel for itself even when it has nothing to
    /// scroll up or down, so the wheel would stop working while the pointer is over them. They hand it on to the page instead.
    /// </summary>
    private void OnInnerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        e.Handled = true;
        PageScroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }
}
