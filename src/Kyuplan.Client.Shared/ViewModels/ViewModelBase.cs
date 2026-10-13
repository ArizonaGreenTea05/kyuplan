using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;

namespace Kyuplan.Client.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    private CancellationTokenSource? _currentLoad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Runs <paramref name="work"/> as the current load: sets <see cref="IsBusy"/>, reports failures through
    /// <see cref="ErrorMessage"/> and cancels a previous load that is still running. A load that was
    /// superseded by a newer one never touches the state, so stale results cannot overwrite fresh ones.
    /// </summary>
    protected async Task LoadAsync(Func<CancellationToken, Task> work)
    {
        _currentLoad?.Cancel();
        var load = _currentLoad = new CancellationTokenSource();

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work(load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // Superseded by a newer load.
        }
        catch (PlannerApiException ex) when (!load.IsCancellationRequested)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (!load.IsCancellationRequested)
        {
            ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_currentLoad, load))
            {
                _currentLoad = null;
                IsBusy = false;
            }
        }
    }
}
