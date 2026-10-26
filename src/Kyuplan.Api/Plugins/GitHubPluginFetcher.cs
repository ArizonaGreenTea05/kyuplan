using System.Net;
using System.Text.Json;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Plugins;

/// <summary>The package of the latest release of a repository.</summary>
/// <param name="Tag">The tag of the release (<c>v1.2.0</c>).</param>
public sealed record PluginDownload(byte[] Bytes, string Tag);

/// <summary>A plugin could not be fetched; the message says why, in words for an administrator.</summary>
public sealed class PluginFetchException(string message) : Exception(message);

/// <summary>Gets the package of a plugin from its repository. A seam for the tests, which must not reach GitHub.</summary>
public interface IPluginFetcher
{
    /// <summary>The package of the latest release of the repository. Throws <see cref="PluginFetchException"/> when there is none or it cannot be read.</summary>
    Task<PluginDownload> FetchLatestAsync(string owner, string repo, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a public GitHub repository's latest release (<c>api.github.com</c>) and downloads the one asset that ends in <c>.kyuplugin</c>. The hosts
/// are fixed: the asset must be on github.com or on GitHub's download hosts, over https, so a repository cannot make the API fetch from anywhere else.
/// </summary>
public sealed class GitHubPluginFetcher(HttpClient http) : IPluginFetcher
{
    private const int MaxReleaseBytes = 1024 * 1024;

    /// <summary>The hosts GitHub serves release assets from (github.com redirects to one of the others).</summary>
    private static bool IsGitHubHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && (uri.Host is "github.com" or "api.github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));

    public async Task<PluginDownload> FetchLatestAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var escaped = $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        var release = await GetAsync($"https://api.github.com/repos/{escaped}/releases/latest", MaxReleaseBytes, cancellationToken)
            ?? throw new PluginFetchException("The repository has no release (it has to be public and have a release with a .kyuplugin file).");

        string tag;
        string? url = null;
        try
        {
            using var document = JsonDocument.Parse(release);
            tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(PluginRules.PackageExtension, StringComparison.OrdinalIgnoreCase)) continue;

                if (url is not null) throw new PluginFetchException($"The latest release has more than one {PluginRules.PackageExtension} file.");
                if (asset.TryGetProperty("size", out var size) && size.GetInt64() > PluginRules.MaxPackageBytes)
                    throw new PluginFetchException($"The package is larger than {PluginRules.MaxPackageBytes / 1024 / 1024} MB.");

                url = asset.GetProperty("browser_download_url").GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new PluginFetchException("GitHub's answer could not be read.");
        }

        if (url is null) throw new PluginFetchException($"The latest release ({tag}) has no {PluginRules.PackageExtension} file.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !IsGitHubHost(address)) throw new PluginFetchException("The package is not on GitHub.");

        var bytes = await GetBytesAsync(address, PluginRules.MaxPackageBytes, cancellationToken)
            ?? throw new PluginFetchException("The package of the release was not found.");
        return new PluginDownload(bytes, tag);
    }

    private async Task<string?> GetAsync(string url, int limit, CancellationToken cancellationToken) =>
        await GetBytesAsync(new Uri(url), limit, cancellationToken) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The body of the address; <c>null</c> for 404. Throws <see cref="PluginFetchException"/> for everything else that is not a plain answer.</summary>
    private async Task<byte[]?> GetBytesAsync(Uri address, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("kyuplan-plugins");
        request.Headers.Accept.ParseAdd(address.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response)))
                throw new PluginFetchException("GitHub does not answer more requests for now (rate limit). Try again later.");
            if (!response.IsSuccessStatusCode) throw new PluginFetchException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
            // A redirect is followed by the HttpClient: where it ended must still be a GitHub host.
            if (response.RequestMessage?.RequestUri is { } final && !IsGitHubHost(final)) throw new PluginFetchException("GitHub sent the download to another host.");
            if (response.Content.Headers.ContentLength > limit) throw new PluginFetchException($"An answer of GitHub is larger than {limit / 1024} KB.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit) throw new PluginFetchException($"An answer of GitHub is larger than {limit / 1024} KB.");
            }

            return buffer.ToArray();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PluginFetchException("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new PluginFetchException($"GitHub could not be reached: {ex.Message}");
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0");
}
