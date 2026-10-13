using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class AccountView : UserControl
{
    private bool _copying;

    public AccountView() => InitializeComponent();

    private AccountViewModel? ViewModel => DataContext as AccountViewModel;

    // The page is created anew every time it is shown, but its view model lives on: the boxes start from it, and follow it
    // while the page is visible (after a password was changed the view model empties them).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.PropertyChanged += OnViewModelChanged;
        ShowPasswords(viewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.CurrentPassword) or nameof(AccountViewModel.NewPassword) or nameof(AccountViewModel.ConfirmPassword)
            && ViewModel is { } viewModel)
            ShowPasswords(viewModel);
    }

    private void ShowPasswords(AccountViewModel viewModel)
    {
        _copying = true;
        try
        {
            Show(CurrentBox, viewModel.CurrentPassword);
            Show(NewBox, viewModel.NewPassword);
            Show(ConfirmBox, viewModel.ConfirmPassword);
        }
        finally
        {
            _copying = false;
        }

        static void Show(Controls.RevealPasswordBox box, string password)
        {
            if (box.Password != password) box.Password = password;
        }
    }

    /// <summary>PasswordBox.Password is deliberately not a dependency property, so it cannot be bound and is copied over by hand.</summary>
    private void OnCurrentChanged(object sender, RoutedEventArgs e)
    {
        if (!_copying && ViewModel is { } viewModel) viewModel.CurrentPassword = CurrentBox.Password;
    }

    private void OnNewChanged(object sender, RoutedEventArgs e)
    {
        if (!_copying && ViewModel is { } viewModel) viewModel.NewPassword = NewBox.Password;
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (!_copying && ViewModel is { } viewModel) viewModel.ConfirmPassword = ConfirmBox.Password;
    }
}
