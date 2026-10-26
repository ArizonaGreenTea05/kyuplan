using System.Text.RegularExpressions;

namespace Kyuplan.Contracts;

/// <summary>
/// A theme pack in the sense of a GitHub repository: it holds the files of one or more themes (<see cref="ThemePackDto"/>) in its folder
/// <see cref="ThemeSourceRules.Folder"/> and is kept up to date by the API (<c>GET /api/themes/sources</c>).
/// </summary>
/// <param name="Id">Short key of the repository; the themes of the pack have ids that start with it, so two packs can carry a theme of the same name.</param>
/// <param name="Url">The address of the repository, <c>https://github.com/owner/repo</c>.</param>
/// <param name="Name">"owner/repo".</param>
/// <param name="ThemeCount">How many themes the pack offers at the moment.</param>
/// <param name="LastSyncUtc">When the repository was last read; <c>null</c> = never.</param>
/// <param name="LastSucceeded">Whether that worked; <c>null</c> = never read.</param>
/// <param name="LastMessage">Why reading failed, or (after a read that worked) which theme files were skipped and why; <c>null</c> when there is nothing to say.</param>
public sealed record ThemeSourceDto(
    string Id, string Url, string Name, int ThemeCount, DateTimeOffset? LastSyncUtc, bool? LastSucceeded, string? LastMessage);

/// <summary>What an administrator sends to add a theme pack (<c>POST /api/themes/sources</c>).</summary>
/// <param name="Url">The address of a public GitHub repository.</param>
public sealed record ThemeSourceInputDto(string Url);

/// <summary>Where theme packs come from and how often they are read again.</summary>
public static partial class ThemeSourceRules
{
    /// <summary>The folder of a repository that holds the theme files (<c>themes/*.json</c>, one theme each).</summary>
    public const string Folder = "themes";

    /// <summary>Theme packs an administrator can have at most.</summary>
    public const int MaxSources = 10;

    /// <summary>Themes one pack can offer at most; further files are skipped.</summary>
    public const int MaxThemes = 50;

    /// <summary>A pack is read again after this long ...</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>... or after this long when reading it failed.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    /// <summary>
    /// The owner and the repository of an address like <c>https://github.com/owner/repo</c> (a trailing slash or <c>.git</c> is fine); <c>null</c> for
    /// everything else. Only github.com is read, nothing a user could point at an internal address.
    /// </summary>
    public static (string Owner, string Repo)? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("github.com" or "www.github.com")) return null;
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2) return null;

        var (owner, repo) = (parts[0], parts[1]);
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (!OwnerPattern().IsMatch(owner) || !RepoPattern().IsMatch(repo) || repo is "." or "..") return null;

        return (owner, repo);
    }

    public static bool IsValidUrl(string? url) => Parse(url) is not null;

    /// <summary>The address in its one spelling.</summary>
    public static string Url(string owner, string repo) => $"https://github.com/{owner}/{repo}";
}
