using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class TagForm : UserControl
{
    public TagForm() => InitializeComponent();

    /// <summary>Opening the form puts the cursor in its first field.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NameBox.Focus());
    }
}
