using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kyuplan.Plugins;

/// <summary>
/// The entry point of a plugin: the one public class of its assembly that implements this interface (the file is named by <c>assembly</c> in
/// <c>plugin.json</c>). The API creates it once when it starts and calls <see cref="Initialize"/>; what it registers there is what the plugin
/// adds. What the plugin looks like to the users (its pages, permissions and the forms of its data sources) is described in JSON, not here:
/// see <c>docs/plugin-development.md</c>.
/// </summary>
public interface IKyuplanPlugin
{
    void Initialize(IPluginBuilder builder);
}

/// <summary>What a plugin can add to the API. Given to <see cref="IKyuplanPlugin.Initialize"/>.</summary>
public interface IPluginBuilder
{
    /// <summary>The id of the plugin (<c>id</c> in <c>plugin.json</c>).</summary>
    string Id { get; }

    string Version { get; }

    /// <summary>The folder of the plugin's files (the assembly, <c>plugin.json</c> and the JSON of the pages and forms).</summary>
    string Directory { get; }

    ILoggerFactory Loggers { get; }

    /// <summary>
    /// Offers a data source. <paramref name="id"/> is the id of one of the <c>dataSources</c> of <c>plugin.json</c>, which also describes its
    /// settings form. <paramref name="create"/> runs when the API starts with this data source chosen.
    /// </summary>
    void AddDataSource(string id, Func<PluginDataSourceContext, IDataSource> create);

    /// <summary>
    /// Adds endpoints. They are below <c>/api/plugins/&lt;id&gt;</c> and need a login; limit them to the people who may use them with
    /// <see cref="PluginEndpointExtensions.RequirePermission"/>. The pages of the plugin read and write them.
    /// </summary>
    void MapEndpoints(Action<RouteGroupBuilder> map);

    /// <summary>The full id of one of the plugin's permissions: <c>Permission("view")</c> is <c>myplugin.view</c> for the plugin <c>myplugin</c>.</summary>
    string Permission(string name);
}

/// <summary>What a data source gets to build itself from.</summary>
/// <param name="Configuration">
/// The settings of the data source: the section named by <c>configSection</c> in <c>plugin.json</c>, filled from the form the administrator uses
/// (the field ids are the keys; a <c>password</c> field is stored encrypted and arrives decrypted). Read when the data source is created, so a
/// change of the settings takes effect with the restart that saving them causes.
/// </param>
/// <param name="Time">The clock; replace it in tests.</param>
/// <param name="ClubTimeZone">The time zone the club's days and weekdays are read in.</param>
/// <param name="Loggers">For the log of the plugin.</param>
/// <param name="Services">The services of the API; a data source rarely needs them.</param>
public sealed record PluginDataSourceContext(
    IConfigurationSection Configuration, TimeProvider Time, TimeZoneInfo ClubTimeZone, ILoggerFactory Loggers, IServiceProvider Services);
