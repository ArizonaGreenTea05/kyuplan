using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// The installed theme packs (<see cref="ThemePackDto"/>): the ones that ship with the program (<see cref="BuiltInThemePacks"/>) and the ones
/// administrators uploaded, one <c>&lt;id&gt;.json</c> per pack in <c>Themes:Directory</c> (default: the folder <c>themes</c> next to the settings
/// file, so it lives on the data volume). A file put there by hand is a pack too; one that is no valid pack is skipped and logged. The folder
/// is looked at on every request (a few small files), so nothing needs a restart.
/// </summary>
public sealed class ThemePackStore(SettingsFile settings, IConfiguration configuration, ILogger<ThemePackStore> log)
{
    public const string DirectorySetting = "Themes:Directory";

    /// <summary>Packs an administrator can have installed at most; the catalog goes to every client.</summary>
    public const int MaxPacks = 50;

    public const int MaxBytes = ThemePackRules.MaxBytes;

    private static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions Write = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private string _seen = "\0"; // the signature of the folder the cache was made from
    private ThemeCatalogDto _catalog = new("", BuiltInThemePacks.All);

    private string Directory => configuration[DirectorySetting] is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Path))!, "themes");

    public ThemeCatalogDto Catalog()
    {
        lock (_lock)
        {
            var signature = Signature();
            if (signature != _seen)
            {
                _catalog = Build(signature);
                _seen = signature;
            }

            return _catalog;
        }
    }

    /// <summary>A problem with a theme that is to be saved as somebody's choice or an instance's default: a pack that is not installed. <c>null</c> for the
    /// themes the clients draw themselves and for installed packs (whether it is spelled right is <see cref="ThemeRules.Check"/>'s business).</summary>
    public string? CheckInstalled(string? theme, string field) =>
        theme is null || !ThemePackRules.IsPackId(theme) || Catalog().Packs.Any(p => p.Id == theme) ? null : $"{field}: there is no theme pack '{theme}'.";

    /// <summary>The pack as an administrator sent it: JSON text. Throws <see cref="InvalidInputException"/> when it cannot be installed.</summary>
    public ThemePackDto Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidInputException($"The theme pack is larger than {MaxBytes / 1024} KB.");

        ThemePackDto? pack;
        try
        {
            pack = JsonSerializer.Deserialize<ThemePackDto>(json, Read);
        }
        catch (JsonException ex)
        {
            throw new InvalidInputException($"The theme pack is no valid JSON: {ex.Message}");
        }

        if (ThemePackRules.Check(pack) is { } problem) throw new InvalidInputException(problem);
        if (BuiltInThemePacks.Contains(pack!.Id)) throw new InvalidInputException($"'{pack.Id}' is a built-in theme and cannot be replaced.");
        return pack with { BuiltIn = false };
    }

    /// <summary>Installs the pack, or replaces the one with the same id.</summary>
    public ThemeCatalogDto Save(ThemePackDto pack)
    {
        lock (_lock)
        {
            var directory = Directory;
            System.IO.Directory.CreateDirectory(directory);
            var path = FileOf(directory, pack.Id);
            if (!File.Exists(path) && System.IO.Directory.EnumerateFiles(directory, "*.json").Count() >= MaxPacks)
                throw new InvalidInputException($"At most {MaxPacks} theme packs can be installed; delete one first.");

            // Written beside and moved over, so a request that reads at the same time never sees half a file.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(pack with { BuiltIn = false }, Write), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
            _seen = "\0";
        }

        return Catalog();
    }

    /// <summary>Deletes an uploaded pack; <c>false</c> when there is none with this id (a built-in one cannot be deleted either).</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!ThemePackRules.IsPackId(id) || BuiltInThemePacks.Contains(id)) return false;

            var path = FileOf(Directory, id);
            if (!File.Exists(path)) return false;

            File.Delete(path);
            _seen = "\0";
            return true;
        }
    }

    private static string FileOf(string directory, string id) => Path.Combine(directory, id + ".json");

    private string Signature()
    {
        var directory = Directory;
        if (!System.IO.Directory.Exists(directory)) return "";

        var parts = System.IO.Directory.EnumerateFiles(directory, "*.json")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}");
        return string.Join('|', parts);
    }

    private ThemeCatalogDto Build(string signature)
    {
        var packs = new List<ThemePackDto>(BuiltInThemePacks.All);
        if (signature.Length > 0)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var pack = Parse(File.ReadAllText(file, Encoding.UTF8));
                    if (!string.Equals(pack.Id + ".json", Path.GetFileName(file), StringComparison.Ordinal))
                        throw new InvalidInputException($"The id '{pack.Id}' does not match the file name.");
                    if (packs.Any(p => p.Id == pack.Id)) continue;

                    packs.Add(pack);
                }
                catch (Exception ex) when (ex is InvalidInputException or IOException or UnauthorizedAccessException)
                {
                    log.LogWarning("The theme pack file {File} is skipped: {Problem}", Path.GetFileName(file), ex.Message);
                }
            }
        }

        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..12].ToLowerInvariant();
        return new ThemeCatalogDto(version, packs);
    }
}
