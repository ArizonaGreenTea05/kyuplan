using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Plugins;

/// <summary>
/// Puts plugins into the <see cref="PluginStore"/>: from a public GitHub repository (its latest release), or from a package file. One change at a
/// time. A plugin is loaded when the API starts, so whoever calls this restarts the API afterwards (<see cref="Kyuplan.Api.Infrastructure.IAppRestarter"/>).
/// </summary>
public sealed class PluginInstaller(PluginStore store, IPluginFetcher fetcher, TimeProvider time, ILogger<PluginInstaller> log)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Installs the plugin of the repository, or updates it when it is installed from there. Throws <see cref="InvalidInputException"/> with the reason.</summary>
    public async Task<(StoredPlugin Plugin, bool Changed)> InstallFromGitHubAsync(string? url, CancellationToken cancellationToken)
    {
        if (ThemeSourceRules.Parse(url) is not var (owner, repo))
            throw new InvalidInputException("url must be the address of a public GitHub repository, like https://github.com/owner/repo.");

        await _one.WaitAsync(cancellationToken);
        try
        {
            return await InstallCoreAsync(ThemeSourceRules.Url(owner, repo), owner, repo, null, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Fetches the latest release of the repository the plugin came from. <c>null</c> when it is not installed; throws <see cref="InvalidInputException"/> for a plugin that did not come from GitHub or cannot be fetched.</summary>
    public async Task<(StoredPlugin Plugin, bool Changed)?> UpdateAsync(string id, CancellationToken cancellationToken)
    {
        if (store.List().FirstOrDefault(p => p.Id == id) is not { } installed) return null;
        if (ThemeSourceRules.Parse(installed.Source) is not var (owner, repo))
            throw new InvalidInputException("This plugin was not installed from GitHub, so there is nothing to update it from. Install it again from its repository.");

        await _one.WaitAsync(cancellationToken);
        try
        {
            return await InstallCoreAsync(installed.Source, owner, repo, id, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<(StoredPlugin, bool)> InstallCoreAsync(string source, string owner, string repo, string? expectedId, CancellationToken cancellationToken)
    {
        PluginDownload download;
        try
        {
            download = await fetcher.FetchLatestAsync(owner, repo, cancellationToken);
        }
        catch (PluginFetchException ex)
        {
            throw new InvalidInputException(ex.Message);
        }

        var package = PluginPackage.Open(download.Bytes);
        // An update must not turn into the installation of another plugin (the repository now ships a plugin with a different id).
        if (expectedId is not null && package.Manifest.Id != expectedId)
            throw new InvalidInputException($"The latest release of the repository is the plugin '{package.Manifest.Id}', not '{expectedId}'. Remove '{expectedId}' and install the repository again to change over.");
        var before = store.List().FirstOrDefault(p => p.Id == package.Manifest.Id);
        var stored = store.Install(package, source, time);
        var changed = before is null || before.Sha256 != stored.Sha256;
        log.LogInformation("The plugin {Plugin} {Version} was {What} from {Source}.", stored.Id, stored.Version, before is null ? "installed" : changed ? "updated" : "checked (unchanged)", source);
        return (stored, changed);
    }

    /// <summary>Installs a package file (the plugins that come with the program).</summary>
    public StoredPlugin InstallFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var stored = store.Install(PluginPackage.Open(bytes), PluginStore.BundledSource, time);
        log.LogInformation("The plugin {Plugin} {Version} was installed from {File}.", stored.Id, stored.Version, path);
        return stored;
    }

    /// <summary>Removes the plugin; its files go when the API starts the next time. <c>false</c> when it is not installed.</summary>
    public bool Remove(string id)
    {
        var removed = store.Remove(id);
        if (removed) log.LogInformation("The plugin {Plugin} was removed.", id);
        return removed;
    }
}

/// <summary>
/// The plugins an installation starts with. When the plugin folder is new (nothing was ever installed there), the packages in
/// <see cref="PluginOptions.BundledDirectory"/> are installed (they come with the container image, so no network is needed); when that leaves it
/// empty, the repositories of <see cref="PluginOptions.Defaults"/> are asked for their latest release. A failure is logged and the folder stays new,
/// so the next start tries again. An administrator who removes the default plugins later does not get them back: the folder is not new any more.
/// </summary>
public static class PluginBootstrap
{
    public static readonly TimeSpan GitHubTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Installs the default plugins when there is no plugin yet. Returns the lines for the log (the logging is not set up yet at this point of the start).</summary>
    public static async Task<IReadOnlyList<(LogLevel Level, string Message)>> InstallDefaultsAsync(
        PluginStore store, PluginInstaller installer, PluginOptions options, string programDirectory, CancellationToken cancellationToken)
    {
        var lines = new List<(LogLevel, string)>();
        if (!options.InstallDefaults || !store.IsFresh) return lines;

        var bundled = Path.GetFullPath(options.BundledDirectory is { Length: > 0 } configured ? configured : "plugins", programDirectory);
        if (Directory.Exists(bundled))
        {
            foreach (var file in Directory.GetFiles(bundled, "*" + PluginRules.PackageExtension).Order(StringComparer.Ordinal))
            {
                try
                {
                    var plugin = installer.InstallFile(file);
                    lines.Add((LogLevel.Information, $"The plugin {plugin.Id} {plugin.Version} came with the program and is installed."));
                }
                catch (Exception ex) when (ex is InvalidInputException or IOException or UnauthorizedAccessException)
                {
                    lines.Add((LogLevel.Warning, $"The plugin package {file} could not be installed: {ex.Message}"));
                }
            }
        }

        if (!store.IsFresh) return lines;

        foreach (var url in options.Defaults.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitHubTimeout);
            try
            {
                var (plugin, _) = await installer.InstallFromGitHubAsync(url, timeout.Token);
                lines.Add((LogLevel.Information, $"The default plugin {plugin.Id} {plugin.Version} was installed from {url}."));
            }
            catch (Exception ex) when (ex is InvalidInputException or IOException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lines.Add((LogLevel.Warning, $"The default plugin {url} could not be installed: {ex.Message} It is tried again at the next start; administrators can also install it on the plugins page."));
            }
        }

        return lines;
    }
}

/// <summary>The settings of the plugins (the section <c>Plugins</c>).</summary>
public sealed class PluginOptions
{
    public const string Section = "Plugins";

    /// <summary>Where the plugins are kept; empty = the folder <c>plugins</c> next to the settings file.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Whether a new installation (no plugin was ever installed) gets the default plugins. Also off when <c>DefaultData:Enabled</c> is off: a new installation then starts with nothing.</summary>
    public bool InstallDefaults { get; set; } = true;

    /// <summary>The GitHub repositories of the plugins a new installation gets when it has none of the bundled packages.</summary>
    public string[] Defaults { get; set; } = [];

    /// <summary>A folder with <c>*.kyuplugin</c> files that a new installation installs first (relative to the program folder; empty = <c>plugins</c>). The container image puts the plugins that come with it there.</summary>
    public string BundledDirectory { get; set; } = "";
}
