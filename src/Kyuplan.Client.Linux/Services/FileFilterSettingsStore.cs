using Kyuplan.Client.Storage;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Services;

/// <summary>Remembers the filter of the statistics page next to the language, in the user's config folder. A file that cannot be read or written is not worth an error.</summary>
public sealed class FileFilterSettingsStore(UserPreferences preferences) : IFilterSettingsStore
{
    private const string Preference = "last-filter";

    public FilterSettingsDto? Load() => FilterSettingsJson.TryParse(preferences.Get(Preference));

    public void Save(FilterSettingsDto settings) => preferences.Set(Preference, FilterSettingsJson.Serialize(settings));
}
