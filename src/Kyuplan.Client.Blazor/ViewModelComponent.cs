using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace Kyuplan.Client.Blazor;

/// <summary>
/// Base class for components bound to a shared view model: injects it and re-renders whenever it (or any other
/// observed source) raises <c>PropertyChanged</c>. View models are singletons, so the subscription is removed on dispose.
/// </summary>
public abstract class ViewModelComponent<TViewModel> : ComponentBase, IDisposable
    where TViewModel : INotifyPropertyChanged
{
    [Inject] protected TViewModel Vm { get; set; } = default!;

    /// <summary>What this component re-renders for. Override to add e.g. the shell view model.</summary>
    protected virtual IEnumerable<INotifyPropertyChanged> ObservedSources => [Vm];

    protected override void OnInitialized()
    {
        foreach (var source in ObservedSources) source.PropertyChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        foreach (var source in ObservedSources) source.PropertyChanged -= OnSourceChanged;
    }
}
