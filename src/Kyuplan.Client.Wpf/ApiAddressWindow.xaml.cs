using System.Windows;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp;

/// <summary>
/// Asks for the address of the API. <see cref="Window.ShowDialog"/> returns <c>true</c> when the user saved an address (then
/// <see cref="Address"/> holds it in its canonical form), <c>false</c> when they cancelled.
/// </summary>
public partial class ApiAddressWindow : Window
{
    private readonly ApiAddressViewModel _viewModel;

    /// <param name="current">The address in use, shown as the start; <c>null</c> on the first start, when there is none.</param>
    /// <param name="owner">The window this one is opened from, if any (the login window); it is centred over it.</param>
    public ApiAddressWindow(string? current, Window? owner = null)
    {
        _viewModel = new ApiAddressViewModel(current);
        DataContext = _viewModel;
        InitializeComponent();

        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _viewModel.Accepted += (_, _) =>
        {
            Address = _viewModel.Normalized!;
            DialogResult = true;
        };

        Loaded += (_, _) =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        };
    }

    public string Address { get; private set; } = "";
}
