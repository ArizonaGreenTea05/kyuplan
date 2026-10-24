using System.Text.RegularExpressions;

namespace Kyuplan.Contracts;

/// <summary>
/// The installers of the desktop clients are part of the server (the container image carries them): the API offers each for download and
/// says which version it is, so that the web client can offer the one for the visitor's system and an installed client can tell when it is
/// out of date. One installer per <see cref="ClientPlatform"/>.
/// </summary>
public static partial class ClientDownload
{
    /// <summary>Address of the installer of a platform, relative to the API. Needs a login like everything else.</summary>
    public static string PathFor(string platform) => $"api/client/{Uri.EscapeDataString(platform)}";

    /// <summary>Address of the signature of that installer (see <c>UpdateSignature</c>); 404 when the release was not signed.</summary>
    public static string SignaturePathFor(string platform) => $"{PathFor(platform)}/signature";

    /// <summary>The signature of an installer file is the file beside it with this added to its name (<c>Kyuplan-windows-1.2.3.exe.sig</c>).</summary>
    public const string SignatureExtension = ".sig";

    /// <summary>Name of the installer file of a platform and version, e.g. <c>Kyuplan-windows-1.2.3.exe</c>; the release workflow and the installer scripts build the same name.</summary>
    public static string FileName(string platform, string version, string extension) => $"Kyuplan-{platform}-{version}.{extension}";

    /// <summary>The platform and version in the name of an installer file, <c>null</c> when it is not one.</summary>
    public static (string Platform, string Version)? Parse(string fileName)
    {
        var match = InstallerName().Match(fileName);
        return match.Success ? (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"^Kyuplan-(windows|macos|linux|android)-(\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)\.(?:exe|msi|dmg|pkg|deb|rpm|appimage|zip|apk)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();

    /// <summary>
    /// Whether <paramref name="available"/> is a later version than <paramref name="current"/>. Versions are <c>1.2</c>, <c>1.2.3</c> or
    /// <c>1.2.3-beta.1</c> (a build number after a plus sign is ignored); a version with a suffix comes before the one without. A version
    /// that cannot be read is never newer.
    /// </summary>
    public static bool IsNewer(string? available, string? current)
    {
        if (!TryParse(available, out var a) || !TryParse(current, out var c)) return false;

        var core = a.Core.CompareTo(c.Core);
        if (core != 0) return core > 0;

        if (a.Suffix.Length == 0 || c.Suffix.Length == 0) return a.Suffix.Length == 0 && c.Suffix.Length != 0;
        return CompareSuffix(a.Suffix, c.Suffix) > 0;
    }

    private static bool TryParse(string? text, out (Version Core, string Suffix) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var withoutBuild = text.Trim().Split('+')[0];
        var dash = withoutBuild.IndexOf('-');
        var numbers = dash < 0 ? withoutBuild : withoutBuild[..dash];
        if (!Version.TryParse(numbers, out var core)) return false;

        // 1.2 and 1.2.0 are the same version (Version leaves the missing parts at -1).
        version = (new Version(core.Major, core.Minor, Math.Max(core.Build, 0), Math.Max(core.Revision, 0)), dash < 0 ? "" : withoutBuild[(dash + 1)..]);
        return true;
    }

    /// <summary>Semantic-versioning order of the dot-separated parts of a suffix: numbers by value and before words, words by letters.</summary>
    private static int CompareSuffix(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');
        for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var lNumber = long.TryParse(l[i], out var ln);
            var rNumber = long.TryParse(r[i], out var rn);
            var order = (lNumber, rNumber) switch
            {
                (true, true) => ln.CompareTo(rn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(l[i], r[i]),
            };
            if (order != 0) return order;
        }

        return l.Length.CompareTo(r.Length);
    }
}
