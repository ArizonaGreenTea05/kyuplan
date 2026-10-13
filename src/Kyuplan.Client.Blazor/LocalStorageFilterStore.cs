using Microsoft.JSInterop;
using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.Blazor;

/// <summary>
/// Remembers the filter of the statistics page in the browser's local storage, like the theme and the language
/// (see wwwroot/js). Blazor WebAssembly may call JavaScript synchronously, which is what lets the view model ask for the
/// filter while it is being created.
/// </summary>
internal sealed class LocalStorageFilterStore(IJSRuntime js) : IFilterSettingsStore
{
    private const string Key = "pp-filter";

    private IJSInProcessRuntime? Runtime => js as IJSInProcessRuntime;

    public FilterSettingsDto? Load()
    {
        try
        {
            return FilterSettingsJson.TryParse(Runtime?.Invoke<string?>("localStorage.getItem", Key));
        }
        catch (JSException)
        {
            return null; // storage blocked: start from the defaults
        }
    }

    public void Save(FilterSettingsDto settings)
    {
        try
        {
            Runtime?.InvokeVoid("localStorage.setItem", Key, FilterSettingsJson.Serialize(settings));
        }
        catch (JSException)
        {
            // Private mode or a full storage: the filter is not remembered, which is not worth an error.
        }
    }
}
