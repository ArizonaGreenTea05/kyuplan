using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;
using Kyuplan.Plugins;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Plugins;

/// <summary>Builds the data source of a plugin, or says that there is none.</summary>
public static class PluginDataSources
{
    /// <summary>
    /// The data source <paramref name="kind"/> of the running plugin that offers it, built from its section of the configuration. When no plugin offers it (the
    /// plugin is not installed, or it did not load) the data source is one that says so to everybody who asks for data, so the API still starts and the settings page
    /// can be used to put it right.
    /// </summary>
    public static IDataSource Create(string kind, IServiceProvider services)
    {
        var host = services.GetRequiredService<PluginHost>();
        if (host.FindDataSource(kind) is not var (plugin, source, create))
        {
            var problem = host.Plugins.FirstOrDefault(p => !p.IsRunning && (p.Manifest.DataSources ?? []).Any(s => s.Id == kind))?.Problem;
            return new MissingDataSource(kind, problem);
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var context = new PluginDataSourceContext(
            configuration.GetSection(plugin.Installed.SectionOf(source)),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IOptions<StatisticsOptions>>().Value.ResolveTimeZone(),
            services.GetRequiredService<ILoggerFactory>(),
            services);
        var dataSource = create(context);
        if (dataSource.Kind != kind) throw new InvalidOperationException($"The plugin {plugin.Id} registered the data source '{kind}', but it says it is '{dataSource.Kind}'.");

        return dataSource;
    }
}

/// <summary>Stands in for a data source whose plugin is missing: every question is answered with the reason.</summary>
internal sealed class MissingDataSource(string kind, string? problem) : IDataSource
{
    public string Kind => kind;

    private DataSourceUnavailableException Unavailable() => new(problem is null
        ? $"The data source '{kind}' is offered by a plugin that is not installed. Install the plugin on the plugins page, or choose another data source on the settings page."
        : $"The plugin that offers the data source '{kind}' does not run: {problem}");

    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken) => throw Unavailable();

    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => throw Unavailable();

    public void InvalidateCache()
    {
    }
}

/// <summary>The secret fields of the settings forms of a plugin, as places in the settings file (see <see cref="SettingsFile.AddSecretPaths"/>).</summary>
public static class PluginSecrets
{
    public static IEnumerable<string> Of(InstalledPlugin plugin) =>
        (plugin.Manifest.DataSources ?? [])
            .Select(source => (Section: plugin.SectionOf(source), Form: plugin.FormOf(source)))
            .Where(s => s.Form is not null)
            .SelectMany(s => PluginRules.SecretPaths(s.Form!).Select(path => $"{s.Section}:{path.Replace('/', ':')}"));
}
