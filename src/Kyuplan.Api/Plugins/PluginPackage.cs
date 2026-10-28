using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Plugins;

/// <summary>
/// The package of a plugin (<c>*.kyuplugin</c>, a .zip): read, checked and unpacked. Nothing in it is trusted: the size, the number of files, the
/// names (no way out of the folder) and the JSON files are checked before anything is written, and the manifest has to be what
/// <see cref="PluginRules.CheckManifest"/> asks for. The code in it is not checked (nothing can be): that is why only administrators install plugins.
/// </summary>
public sealed class PluginPackage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private PluginPackage(PluginManifest manifest, byte[] bytes, IReadOnlyList<string> files)
    {
        Manifest = manifest;
        Bytes = bytes;
        Files = files;
    }

    public PluginManifest Manifest { get; }

    public byte[] Bytes { get; }

    /// <summary>The paths of the files in the package.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Reads and checks the package. Throws <see cref="InvalidInputException"/> with the reason when it cannot be a plugin.</summary>
    public static PluginPackage Open(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidInputException("The package is empty.");
        if (bytes.Length > PluginRules.MaxPackageBytes) throw new InvalidInputException($"The package is larger than {PluginRules.MaxPackageBytes / 1024 / 1024} MB.");

        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var files = Entries(archive);
            var manifest = ReadManifest(archive, files);
            CheckFiles(manifest, archive, files);
            return new PluginPackage(manifest, bytes, [.. files.Keys]);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidInputException($"The package is not a valid .zip file ({ex.Message}).");
        }
    }

    /// <summary>Unpacks the package into <paramref name="directory"/> (which must not exist yet). Nothing is left behind when it fails.</summary>
    public void ExtractTo(string directory)
    {
        var partial = directory + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);

        try
        {
            using var archive = new ZipArchive(new MemoryStream(Bytes), ZipArchiveMode.Read);
            long total = 0;
            foreach (var (path, entry) in Entries(archive))
            {
                var target = Path.GetFullPath(Path.Combine(partial, path));
                if (!target.StartsWith(Path.GetFullPath(partial) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidInputException($"{path}: outside the plugin's folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = File.Create(target);
                total += Copy(input, output, PluginRules.MaxUnpackedBytes - total, path);
            }

            Directory.Move(partial, directory);
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes: what an entry says about its size is not believed.</summary>
    private static long Copy(Stream input, Stream output, long limit, string name)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > limit) throw new InvalidInputException($"The package is larger than {PluginRules.MaxUnpackedBytes / 1024 / 1024} MB when unpacked ({name}).");

            output.Write(buffer, 0, read);
        }

        return copied;
    }

    /// <summary>
    /// The files of the archive by their path. A backslash counts as a slash: Windows PowerShell's <c>Compress-Archive</c> and some other tools write
    /// <c>ui\settings.json</c> for what the zip format wants as <c>ui/settings.json</c>, and a plugin made on Windows should not fail for that.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith('/') && entry.Length == 0) continue; // a folder

            if (files.Count >= PluginRules.MaxFiles) throw new InvalidInputException($"The package has more than {PluginRules.MaxFiles} files.");
            if (!PluginRules.IsPackagePath(path)) throw new InvalidInputException($"The file name '{Printable(entry.FullName)}' is not allowed in a package (relative path with /, no hidden parts, no ..).");

            declared += entry.Length;
            if (declared > PluginRules.MaxUnpackedBytes) throw new InvalidInputException($"The package is larger than {PluginRules.MaxUnpackedBytes / 1024 / 1024} MB when unpacked.");
            if (!files.TryAdd(path, entry)) throw new InvalidInputException($"The file {path} is in the package twice.");
        }

        return files;
    }

    private static string Printable(string name) => new(name.Take(60).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>
    /// The entry the manifest (or the package) names. The map is case-insensitive (only to find duplicates), but the plugin is loaded from a file system
    /// that may not be (Linux): a name that differs from the file's spelling only in case is refused now, not after the restart.
    /// </summary>
    private static ZipArchiveEntry? Find(Dictionary<string, ZipArchiveEntry> files, string path, string owner)
    {
        if (!files.TryGetValue(path, out var entry)) return null;

        var actual = files.Keys.First(key => string.Equals(key, path, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(actual, path, StringComparison.Ordinal)) throw new InvalidInputException($"{path} ({owner}) is spelled {actual} in the package: the case of file names has to match.");

        return entry;
    }

    private static PluginManifest ReadManifest(ZipArchive archive, Dictionary<string, ZipArchiveEntry> files)
    {
        var entry = Find(files, PluginRules.ManifestFile, "the manifest") ?? throw new InvalidInputException($"The package has no {PluginRules.ManifestFile} in its root.");

        var manifest = Parse<PluginManifest>(entry, PluginRules.ManifestFile);
        if (PluginRules.CheckManifest(manifest) is { } problem) throw new InvalidInputException($"{PluginRules.ManifestFile}: {problem}");

        return manifest!;
    }

    private static void CheckFiles(PluginManifest manifest, ZipArchive archive, Dictionary<string, ZipArchiveEntry> files)
    {
        if (Find(files, manifest.Assembly, $"assembly in {PluginRules.ManifestFile}") is null) throw new InvalidInputException($"The assembly {manifest.Assembly} (assembly in {PluginRules.ManifestFile}) is not in the package.");

        foreach (var page in manifest.Pages ?? [])
        {
            var layout = LayoutOf(files, page.Layout, $"page {page.Id}");
            if (PluginRules.CheckLayout(layout, PluginUiUse.Page) is { } problem) throw new InvalidInputException($"{page.Layout}: {problem}");
        }

        foreach (var source in manifest.DataSources ?? [])
        {
            if (string.IsNullOrEmpty(source.Settings)) continue;

            var layout = LayoutOf(files, source.Settings, $"data source {source.Id}");
            if (PluginRules.CheckLayout(layout, PluginUiUse.DataSourceSettings) is { } problem) throw new InvalidInputException($"{source.Settings}: {problem}");
        }
    }

    private static PluginUiLayout LayoutOf(Dictionary<string, ZipArchiveEntry> files, string path, string owner)
    {
        var entry = Find(files, path, owner) ?? throw new InvalidInputException($"{path} ({owner}) is not in the package.");

        return Parse<PluginUiLayout>(entry, path) ?? throw new InvalidInputException($"{path} is empty.");
    }

    private static T? Parse<T>(ZipArchiveEntry entry, string name)
    {
        if (entry.Length > PluginRules.MaxJsonBytes) throw new InvalidInputException($"{name} is larger than {PluginRules.MaxJsonBytes / 1024} KB.");

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        Copy(stream, buffer, PluginRules.MaxJsonBytes, name);

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidInputException($"{name} is not valid: {ex.Message}");
        }
    }

    /// <summary>Reads a JSON file of an unpacked plugin (the manifest or a layout); <c>null</c> when it is not there or not valid. Used when the plugins are loaded: they were checked when installed.</summary>
    public static T? ReadFile<T>(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > PluginRules.MaxJsonBytes) return default;

            var bytes = File.ReadAllBytes(path).AsSpan();
            if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
