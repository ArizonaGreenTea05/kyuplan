using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Plugins;

/// <summary>An installed plugin as <c>index.json</c> keeps it. <see cref="Folder"/> is where its files are, below the plugin's own folder.</summary>
public sealed record StoredPlugin(string Id, string Version, string Source, DateTimeOffset InstalledAt, string Sha256, string Folder);

/// <summary>An installed plugin with what its files say: the manifest and the layouts of its pages and data source forms (read, not loaded).</summary>
public sealed record InstalledPlugin(StoredPlugin Stored, string Directory, PluginManifest Manifest, IReadOnlyDictionary<string, PluginUiLayout> Layouts)
{
    public string Id => Stored.Id;

    /// <summary>The section of the configuration the settings of a data source are kept in.</summary>
    public string SectionOf(PluginDataSourceDef source) =>
        string.IsNullOrEmpty(source.ConfigSection) ? PluginRules.DefaultSection(Id) : source.ConfigSection;

    public PluginUiLayout? FormOf(PluginDataSourceDef source) => source.Settings is { Length: > 0 } file ? Layouts.GetValueOrDefault(file) : null;
}

/// <summary>
/// The installed plugins: <c>Plugins:Directory</c> (default: the folder <c>plugins</c> next to the settings file, so it lives on the data volume) holds
/// <c>index.json</c> and, for each plugin, a folder <c>&lt;id&gt;/&lt;version&gt;-&lt;hash&gt;/</c> with its unpacked files. Installing writes a new
/// folder and then the index, so a crash leaves the plugin as it was; folders the index does not name any more (an older version, a removed plugin)
/// are deleted when the API starts, because the files of a plugin that runs cannot be deleted on every system.
/// </summary>
public sealed class PluginStore(SettingsFile settings, IConfiguration configuration, ILogger<PluginStore> log)
{
    public const string DirectorySetting = "Plugins:Directory";

    public const string IndexFile = "index.json";

    /// <summary>The source of a plugin that was installed from a file of the program folder (<see cref="PluginBootstrap"/>), not from GitHub.</summary>
    public const string BundledSource = PluginRules.BundledSource;

    private readonly Lock _lock = new();

    public string Directory => configuration[DirectorySetting] is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Path))!, "plugins");

    private string IndexPath => Path.Combine(Directory, IndexFile);

    /// <summary>Whether anything was ever installed here (the index exists). A folder that never had a plugin gets the default plugins (<see cref="PluginBootstrap"/>).</summary>
    public bool IsFresh => !File.Exists(IndexPath);

    public IReadOnlyList<StoredPlugin> List()
    {
        lock (_lock) return [.. Load()];
    }

    /// <summary>The installed plugins with their manifests and layouts. A plugin whose files cannot be read any more is left out and logged.</summary>
    public IReadOnlyList<InstalledPlugin> ReadInstalled()
    {
        var installed = new List<InstalledPlugin>();
        foreach (var stored in List())
        {
            var directory = Path.Combine(Directory, stored.Id, stored.Folder);
            var manifest = PluginPackage.ReadFile<PluginManifest>(Path.Combine(directory, PluginRules.ManifestFile));
            if (manifest is null || PluginRules.CheckManifest(manifest) is not null || manifest.Id != stored.Id)
            {
                log.LogWarning("The plugin {Plugin} is installed, but its {File} in {Directory} cannot be read: it is not loaded.", stored.Id, PluginRules.ManifestFile, directory);
                continue;
            }

            var layouts = new Dictionary<string, PluginUiLayout>();
            foreach (var file in (manifest.Pages ?? []).Select(p => p.Layout).Concat((manifest.DataSources ?? []).Select(s => s.Settings).OfType<string>().Where(s => s.Length > 0)))
            {
                if (PluginPackage.ReadFile<PluginUiLayout>(Path.Combine(directory, file)) is { } layout) layouts[file] = layout;
                else log.LogWarning("The plugin {Plugin}: the layout {File} cannot be read.", stored.Id, file);
            }

            installed.Add(new InstalledPlugin(stored, directory, manifest, layouts));
        }

        return installed;
    }

    /// <summary>
    /// Installs the package (or replaces the installed plugin with the same id). Throws <see cref="InvalidInputException"/> when another plugin
    /// with the id is installed from somewhere else, when a data source id is taken by another plugin, or when too many plugins are installed.
    /// A plugin that came with the program (<see cref="BundledSource"/>) is replaced by the one of its repository: that is how it gets updated, since it has no repository to update from.
    /// </summary>
    public StoredPlugin Install(PluginPackage package, string source, TimeProvider time)
    {
        var manifest = package.Manifest;
        lock (_lock)
        {
            var all = Load();
            var existing = all.FirstOrDefault(p => p.Id == manifest.Id);
            if (existing is not null && existing.Source != BundledSource && !string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidInputException($"A plugin with the id '{manifest.Id}' is installed already, from {existing.Source}. Remove it first to install this one.");
            if (existing is null && all.Count >= PluginRules.MaxPlugins) throw new InvalidInputException($"At most {PluginRules.MaxPlugins} plugins can be installed; remove one first.");

            var others = ReadInstalled().Where(p => p.Id != manifest.Id).ToList();
            foreach (var wanted in manifest.DataSources ?? [])
            {
                if (others.FirstOrDefault(o => (o.Manifest.DataSources ?? []).Any(s => s.Id == wanted.Id)) is { } taken)
                    throw new InvalidInputException($"The data source '{wanted.Id}' is offered by the plugin '{taken.Id}' already.");
            }

            var hash = Convert.ToHexString(SHA256.HashData(package.Bytes)).ToLowerInvariant();
            var folder = $"{manifest.Version}-{hash[..8]}";
            var directory = Path.Combine(Directory, manifest.Id, folder);
            if (!System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                package.ExtractTo(directory);
            }

            var stored = new StoredPlugin(manifest.Id, manifest.Version, source, time.GetUtcNow(), hash, folder);
            Save([.. all.Where(p => p.Id != manifest.Id), stored]);
            return stored;
        }
    }

    /// <summary>Takes the plugin out of the index; its files go when the API starts the next time. <c>false</c> when it is not installed.</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.All(p => p.Id != id)) return false;

            Save([.. all.Where(p => p.Id != id)]);
            return true;
        }
    }

    /// <summary>Deletes the files no plugin of the index uses (older versions, removed plugins). A folder that cannot be deleted now is tried again at the next start.</summary>
    public void Sweep()
    {
        lock (_lock)
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var wanted = Load().ToDictionary(p => p.Id, p => p.Folder);
            foreach (var plugin in System.IO.Directory.GetDirectories(Directory))
            {
                // Plugins:Directory may be a folder that holds other things: only what this store makes (<id>/<version>-<hash>) is touched.
                var id = Path.GetFileName(plugin);
                var versions = System.IO.Directory.GetFileSystemEntries(plugin);
                if (!PluginRules.IsPluginId(id) || !versions.All(v => System.IO.Directory.Exists(v) && IsStoreFolder(Path.GetFileName(v)))) continue;

                foreach (var version in versions.Where(v => !wanted.TryGetValue(id, out var folder) || Path.GetFileName(v) != folder))
                    TryDelete(version);

                if (!wanted.ContainsKey(id)) TryDelete(plugin);
            }
        }
    }

    /// <summary>A folder of an installed version (<c>1.2.0-1a2b3c4d</c>), or what is left of one that was being unpacked (<c>.partial</c>).</summary>
    private static bool IsStoreFolder(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"-[0-9a-f]{8}(\.partial)?$");

    private void TryDelete(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("The old plugin files {Directory} could not be deleted: {Problem}", directory, ex.Message);
        }
    }

    private List<StoredPlugin> Load()
    {
        var path = IndexPath;
        if (!File.Exists(path)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<StoredPlugin>>(File.ReadAllText(path, Encoding.UTF8), PluginPackage.Json)?
                .Where(p => p is not null && PluginRules.IsPluginId(p.Id) && PluginRules.IsPackagePath(p.Folder) && !p.Folder.Contains('/')).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("The plugin index {File} cannot be read and is set aside as .bad: {Problem}", path, ex.Message);
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { log.LogWarning("The plugin index could not be set aside: {Problem}", move.Message); }
            return [];
        }
    }

    private void Save(List<StoredPlugin> all)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = IndexPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, PluginPackage.Json), Encoding.UTF8);
            File.Move(temporary, IndexPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
