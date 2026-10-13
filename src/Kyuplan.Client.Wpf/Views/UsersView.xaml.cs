using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class UsersView : UserControl
{
    private bool _copying;

    public UsersView() => InitializeComponent();

    private UserAdminViewModel? ViewModel => DataContext as UserAdminViewModel;

    // The page is created anew every time it is shown, but its view model lives on: the box starts from it, and follows it while
    // the page is visible (the view model empties it when a form opens and after a save).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.PropertyChanged += OnViewModelChanged;
        ShowPassword(viewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        if (e.PropertyName == nameof(UserAdminViewModel.FormPassword)) ShowPassword(viewModel);

        // Opening the form puts the cursor in its first field (once the form is visible: it only becomes so after this change).
        if (e.PropertyName == nameof(UserAdminViewModel.IsFormOpen) && viewModel.IsFormOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UserNameBox.Focus());
    }

    private void ShowPassword(UserAdminViewModel viewModel)
    {
        _copying = true;
        try
        {
            if (FormPasswordBox.Password != viewModel.FormPassword) FormPasswordBox.Password = viewModel.FormPassword;
        }
        finally
        {
            _copying = false;
        }
    }

    /// <summary>PasswordBox.Password is deliberately not a dependency property, so it cannot be bound and is copied over by hand.</summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_copying && ViewModel is { } viewModel) viewModel.FormPassword = FormPasswordBox.Password;
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
