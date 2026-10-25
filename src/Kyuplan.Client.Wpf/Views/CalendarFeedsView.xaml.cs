using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class CalendarFeedsView : UserControl
{
    public CalendarFeedsView() => InitializeComponent();

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CalendarFeedsViewModel { HasAddress: true } page) return;

        try
        {
            Clipboard.SetText(page.Address);
        }
        catch (COMException)
        {
            // Another program holds the clipboard: the address is in the field above, to select and copy by hand.
        }
    }
}
