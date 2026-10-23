using Kyuplan.Api.Persistence;

namespace Kyuplan.Api.Backup;

/// <summary>
/// Where a SQLite backup database may live. A backup database holds a full copy of both databases (password hashes, personal data, feed tokens), so
/// whoever may define one (<c>backups.edit</c>) must not be able to make the API write it anywhere its user may write: it has to be below the folder of the
/// settings file (in a container: on the data volume), and never in a folder the web server hands out. Links are followed first, so a link inside the data
/// folder that points elsewhere does not lead out of it.
/// </summary>
internal static class BackupFolder
{
    private const int MaxLinkDepth = 32;

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The folder a SQLite backup database at <paramref name="location"/> uses (links resolved); <see cref="InvalidInputException"/> when it may not be used.</summary>
    /// <param name="location">Absolute, or relative to <paramref name="contentRoot"/>.</param>
    /// <param name="settingsFilePath">The settings file: its folder is the only place below which backup databases may be put.</param>
    /// <param name="webRootPath">The folder the web server serves files from (besides <c>wwwroot</c> of the content root); nothing in or below it is allowed.</param>
    public static string Resolve(string location, string settingsFilePath, string contentRoot, string? webRootPath)
    {
        var data = RealPath(Path.GetDirectoryName(Path.GetFullPath(settingsFilePath, contentRoot))!);
        var folder = RealPath(Path.GetFullPath(location, contentRoot));

        if (!IsBelow(folder, data, orSame: false))
            throw new InvalidInputException($"The folder must be below the folder of the settings file ({data}).");

        foreach (var web in new[] { Path.Combine(contentRoot, "wwwroot"), webRootPath }.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            if (IsBelow(folder, RealPath(Path.GetFullPath(web!, contentRoot)), orSame: true))
                throw new InvalidInputException("The folder must not be one the web server serves files from.");
        }

        return folder;
    }

    /// <summary>A full path with every link on the way replaced by its target, to compare it with a folder from <see cref="Resolve"/>.</summary>
    public static string Real(string full) => RealPath(full);

    private static bool IsBelow(string path, string root, bool orSame)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return orSame;

        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, Comparison)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, Comparison);
    }

    /// <summary>
    /// The path with every link on the way replaced by its target (a link that points nowhere too); what does not exist yet is kept as it is.
    /// A target is resolved again itself, because its own parents may be links (a link to a path below another link).
    /// </summary>
    private static string RealPath(string full, int depth = 0)
    {
        if (depth > MaxLinkDepth) throw new InvalidInputException("The folder is behind too many links.");

        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;

            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null) continue;

            try
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target) current = RealPath(Path.GetFullPath(target.FullName), depth + 1);
            }
            catch (IOException)
            {
                throw new InvalidInputException($"The link {current} cannot be followed.");
            }
        }

        return current;
    }
}
