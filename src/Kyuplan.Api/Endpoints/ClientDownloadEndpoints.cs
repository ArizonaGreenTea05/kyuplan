using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>Finds the installers of the desktop clients that the server carries: per platform the newest one in the folder.</summary>
public sealed class ClientInstaller(IOptions<ClientDownloadOptions> options, IHostEnvironment environment)
{
    /// <summary>Full path and version of the newest installer of each platform that has one. The folder is looked at every time, so an installer that is put there later is offered.</summary>
    public IReadOnlyDictionary<string, (string Path, string Version)> FindAll()
    {
        var found = new Dictionary<string, (string Path, string Version)>();
        var folder = string.IsNullOrWhiteSpace(options.Value.Directory)
            ? Path.Combine(AppContext.BaseDirectory, "downloads")
            : Path.GetFullPath(options.Value.Directory, environment.ContentRootPath);
        if (!Directory.Exists(folder)) return found;

        foreach (var file in Directory.EnumerateFiles(folder, "Kyuplan-*"))
        {
            if (ClientDownload.Parse(Path.GetFileName(file)) is not var (platform, version)) continue;
            if (!found.TryGetValue(platform, out var known) || ClientDownload.IsNewer(version, known.Version)) found[platform] = (file, version);
        }

        return found;
    }

    public (string Path, string Version)? Find(string platform) => FindAll().TryGetValue(platform, out var found) ? found : null;
}

/// <summary>The installers of the desktop clients. Like everything except the web client's files they need a login (an installed client downloads its update from here).</summary>
public static class ClientDownloadEndpoints
{
    public static IEndpointRouteBuilder MapClientDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/client/{platform}", (string platform, ClientInstaller installer) =>
            {
                if (!ClientPlatform.IsKnown(platform) || installer.Find(platform) is not { } found) return Results.NotFound();

                return Results.File(found.Path, "application/octet-stream", Path.GetFileName(found.Path));
            })
            .WithTags("Client")
            .WithSummary("The installer of the desktop client for a platform (windows, macos, linux); 404 when this server has none.");

        app.MapGet("/api/client/{platform}/signature", (string platform, ClientInstaller installer) =>
            {
                if (!ClientPlatform.IsKnown(platform) || installer.Find(platform) is not { } found) return Results.NotFound();

                var signature = found.Path + ClientDownload.SignatureExtension;
                return File.Exists(signature) ? Results.File(signature, "application/octet-stream") : Results.NotFound();
            })
            .WithTags("Client")
            .WithSummary("The signature of that installer (made with the release's update key); 404 when the release was not signed.");

        return app;
    }
}
