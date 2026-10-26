using System.Net;
using System.Text;
using System.Text.Json;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>One theme file of a repository: its path (<c>themes/name.json</c>) and its text.</summary>
public sealed record ThemeSourceFile(string Path, string Text);

/// <summary>What was found in a repository: the theme files, and the files that could not be taken (too large, too many).</summary>
public sealed record ThemeSourceFetch(IReadOnlyList<ThemeSourceFile> Files, IReadOnlyList<string> Problems);

/// <summary>A repository could not be read; the message says why, in words for an administrator.</summary>
public sealed class ThemeSourceException(string message) : Exception(message);

/// <summary>Reads the theme files of a theme pack. A seam for the tests, which must not reach GitHub.</summary>
public interface IThemeSourceFetcher
{
    /// <summary>The files of <see cref="ThemeSourceRules.Folder"/> in the default branch of the repository. Throws <see cref="ThemeSourceException"/> when it cannot be read.</summary>
    Task<ThemeSourceFetch> FetchAsync(string owner, string repo, CancellationToken cancellationToken);
}

/// <summary>
/// Reads public GitHub repositories: one request for the list of files (the Git tree API) and one per theme file from raw.githubusercontent.com,
/// so nothing is downloaded that is no theme. The hosts are fixed; the owner and the repository come from <see cref="ThemeSourceRules.Parse"/>.
/// </summary>
public sealed class GitHubThemeFetcher(HttpClient http) : IThemeSourceFetcher
{
    /// <summary>The list of files of a large repository can be long; more than this is no theme pack.</summary>
    private const int MaxTreeBytes = 10 * 1024 * 1024;

    public async Task<ThemeSourceFetch> FetchAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var escaped = $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        var tree = await GetAsync($"https://api.github.com/repos/{escaped}/git/trees/HEAD?recursive=1", MaxTreeBytes, cancellationToken)
            ?? throw new ThemeSourceException("The repository was not found. It has to be public.");

        var problems = new List<string>();
        var wanted = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(tree);
            // GitHub cuts a list of more than 100,000 files: the themes folder may be missing from it, so what it shows would be a wrong picture.
            if (document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                throw new ThemeSourceException("The repository is too large to be a theme pack.");
            if (document.RootElement.TryGetProperty("tree", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.GetProperty("type").GetString() != "blob" || entry.GetProperty("path").GetString() is not { } path || !IsThemeFile(path)) continue;

                    if (entry.TryGetProperty("size", out var size) && size.GetInt64() > ThemePackRules.MaxBytes)
                        problems.Add($"{Name(path)}: larger than {ThemePackRules.MaxBytes / 1024} KB.");
                    else
                        wanted.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new ThemeSourceException("GitHub's answer could not be read.");
        }

        wanted.Sort(StringComparer.Ordinal);
        foreach (var path in wanted.Skip(ThemeSourceRules.MaxThemes))
            problems.Add($"{Name(path)}: skipped, a theme pack offers at most {ThemeSourceRules.MaxThemes} themes.");

        var files = new List<ThemeSourceFile>();
        foreach (var path in wanted.Take(ThemeSourceRules.MaxThemes))
        {
            var url = $"https://raw.githubusercontent.com/{escaped}/HEAD/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
            if (await GetAsync(url, ThemePackRules.MaxBytes, cancellationToken) is { } text) files.Add(new ThemeSourceFile(path, text));
            else problems.Add($"{Name(path)}: not found.");
        }

        return new ThemeSourceFetch(files, problems);
    }

    /// <summary><c>themes/name.json</c>: directly in the folder, not below it.</summary>
    private static bool IsThemeFile(string path) =>
        path.StartsWith(ThemeSourceRules.Folder + "/", StringComparison.Ordinal) && path.IndexOf('/', ThemeSourceRules.Folder.Length + 1) < 0
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string Name(string path) => path[(ThemeSourceRules.Folder.Length + 1)..];

    /// <summary>GitHub answers 403 for a used-up quota (no requests left, or a pause to wait for) as well as for a repository it blocks; only the first is worth trying again.</summary>
    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0");

    /// <summary>The text at the address; <c>null</c> for 404. Throws <see cref="ThemeSourceException"/> for everything else that is not a plain answer.</summary>
    private async Task<string?> GetAsync(string url, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("kyuplan-themes");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response)))
                throw new ThemeSourceException("GitHub does not answer more requests for now (rate limit). It is tried again later.");
            if (response.StatusCode == HttpStatusCode.Conflict) throw new ThemeSourceException("The repository is empty."); // what the Git tree API says of a repository without a commit
            if (!response.IsSuccessStatusCode) throw new ThemeSourceException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit) throw new ThemeSourceException($"An answer of GitHub is larger than {limit / 1024} KB.");
            }

            // A file saved by a Windows editor often starts with a byte order mark, which the JSON parser does not take.
            var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
            return Encoding.UTF8.GetString(bytes);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ThemeSourceException("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ThemeSourceException($"GitHub could not be reached: {ex.Message}");
        }
    }
}
