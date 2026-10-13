using System.Windows;
using Kyuplan.Client.ViewModels;
using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Client.WpfApp;

/// <summary>
/// Asks for the login before the main window opens. <see cref="Window.ShowDialog"/> returns <c>true</c> once the API accepted a
/// login, and <c>false</c> when the user gave up (Exit, or closing the window) or chose another server address
/// (<see cref="NewApiAddress"/>).
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    /// <param name="apiAddress">The API this signs in to, shown so that it is clear where the login goes; it can be changed from here.</param>
    public LoginWindow(LoginViewModel viewModel, ThemeService theme, BrandingViewModel brand, string apiAddress)
    {
        _viewModel = viewModel;
        Theme = theme;
        Brand = brand;
        ApiAddress = apiAddress;
        DataContext = viewModel;
        InitializeComponent();

        void OnSignedIn(object? sender, EventArgs e) => DialogResult = true;
        viewModel.SignedIn += OnSignedIn;
        Closed += (_, _) => viewModel.SignedIn -= OnSignedIn; // a sign-in that was still running when the window went away has nobody to tell

        // With a remembered user name only the password is missing.
        Loaded += (_, _) => (viewModel.UserName.Length == 0 ? UserNameBox : (UIElement)PasswordBox).Focus();
    }

    public ThemeService Theme { get; }

    /// <summary>The name and logos of the instance, as the API says; public there, so they are known before anybody has signed in.</summary>
    public BrandingViewModel Brand { get; }

    public string ApiAddress { get; }

    /// <summary>The address the user chose in the address dialog, in its canonical form; <c>null</c> as long as they did not change it. The window closes then.</summary>
    public string? NewApiAddress { get; private set; }

    private void OnChangeServer(object sender, RoutedEventArgs e)
    {
        var dialog = new ApiAddressWindow(ApiAddress, owner: this);
        if (dialog.ShowDialog() != true) return;

        NewApiAddress = dialog.Address;
        DialogResult = false;
    }

    /// <summary>PasswordBox.Password is deliberately not a dependency property, so it cannot be bound and is copied over by hand.</summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;
}
