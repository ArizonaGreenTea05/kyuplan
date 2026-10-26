using System.Windows;
using System.Windows.Controls;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>Draws the elements of a layout a plugin described in JSON (see the templates in the XAML); the plugin brings no WPF code.</summary>
public partial class PluginItemsView : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<PluginItemViewModel>), typeof(PluginItemsView),
        new PropertyMetadata(null, (d, e) => ((PluginItemsView)d).List.ItemsSource = e.NewValue as IEnumerable<PluginItemViewModel>));

    public PluginItemsView() => InitializeComponent();

    public IEnumerable<PluginItemViewModel>? Items
    {
        get => (IEnumerable<PluginItemViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    // The password box has no property to bind: what is typed goes into the field's view model, and a field that is shown starts empty (a stored password is never sent here).
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RevealPasswordBox { DataContext: PluginFieldViewModel field } box && field.Text != box.Password) field.Text = box.Password;
    }

    private void OnPasswordLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RevealPasswordBox { DataContext: PluginFieldViewModel field } box && box.Password != field.Text) box.Password = field.Text;
    }
}
