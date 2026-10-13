using System.Text.Json;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Client.Storage;

/// <summary>
/// Where a client keeps the filter the user worked with last, so that the statistics page opens with it the next
/// time: a file in the user's profile in WPF, local storage in the browser. Both read and write failures are the
/// store's to swallow; a filter that was not remembered is not worth an error.
/// </summary>
public interface IFilterSettingsStore
{
    /// <summary>The remembered filter, or <c>null</c> when there is none (or it can no longer be read).</summary>
    FilterSettingsDto? Load();

    void Save(FilterSettingsDto settings);
}

/// <summary>The text the stores keep: one JSON document, written with the same source-generated metadata as the API.</summary>
public static class FilterSettingsJson
{
    public static string Serialize(FilterSettingsDto settings) =>
        JsonSerializer.Serialize(settings, PlannerJsonContext.Default.FilterSettingsDto);

    /// <summary>
    /// The settings in <paramref name="text"/>, or <c>null</c> when it is empty, damaged or from a version that
    /// stored something else: a remembered filter must never stop the page from opening.
    /// </summary>
    public static FilterSettingsDto? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var settings = JsonSerializer.Deserialize(text, PlannerJsonContext.Default.FilterSettingsDto);
            return settings is { GroupId.Length: > 0 } ? settings with { SubGroupIds = settings.SubGroupIds ?? [] } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Keeps the filter for the lifetime of the process. The default until a client registers a persistent store.</summary>
public sealed class InMemoryFilterSettingsStore : IFilterSettingsStore
{
    private FilterSettingsDto? _settings;

    public FilterSettingsDto? Load() => _settings;

    public void Save(FilterSettingsDto settings) => _settings = settings;
}
