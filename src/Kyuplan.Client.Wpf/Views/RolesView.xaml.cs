using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class RolesView : UserControl
{
    public RolesView() => InitializeComponent();

    private RolesViewModel? ViewModel => DataContext as RolesViewModel;

    // The page is created anew every time it is shown, but its view model lives on: opening the form puts the cursor in its first field
    // (once the form is visible: it only becomes so after this change).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RolesViewModel.IsFormOpen) && ViewModel is { IsFormOpen: true })
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NameBox.Focus());
    }

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
