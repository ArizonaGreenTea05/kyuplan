using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// Asks for the address of the API (WPF; the browser client is served together with its address). Accepting is only possible with
/// an address that is usable, and, when one is being changed, that differs from the current one.
/// </summary>
public sealed partial class ApiAddressViewModel : ObservableObject
{
    private readonly string? _current;

    /// <param name="current">The address in use, or <c>null</c> when none has been chosen yet (first start).</param>
    public ApiAddressViewModel(string? current = null)
    {
        _current = ApiAddress.TryNormalize(current, out var normalized) ? normalized : null;
        _address = current ?? "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Normalized), nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string _address;

    /// <summary>The address as it is kept (see <see cref="ApiAddress.TryNormalize"/>), or <c>null</c> while the text is not one.</summary>
    public string? Normalized => ApiAddress.TryNormalize(Address, out var normalized) ? normalized : null;

    /// <summary>The text is not an address. Nothing is said about an empty field: nobody has typed anything wrong yet.</summary>
    public bool HasProblem => Address.Trim().Length > 0 && Normalized is null;

    /// <summary>Raised when the user accepts; <see cref="Normalized"/> is the address then.</summary>
    public event EventHandler? Accepted;

    private bool CanAccept => Normalized is { } address && address != _current;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept() => Accepted?.Invoke(this, EventArgs.Empty);
}
