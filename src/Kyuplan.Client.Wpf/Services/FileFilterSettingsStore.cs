using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// Remembers the filter of the statistics page next to the theme and the language, in the user's preferences folder
/// (%LocalAppData%\Kyuplan\last-filter.txt). A file that cannot be read or written is not worth an error.
/// </summary>
public sealed class FileFilterSettingsStore(UserPreferences preferences) : IFilterSettingsStore
{
    private const string Preference = "last-filter";

    public FilterSettingsDto? Load() => FilterSettingsJson.TryParse(preferences.Get(Preference));

    public void Save(FilterSettingsDto settings) => preferences.Set(Preference, FilterSettingsJson.Serialize(settings));
}
