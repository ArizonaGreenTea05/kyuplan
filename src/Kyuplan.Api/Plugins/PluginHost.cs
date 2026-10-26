using System.Reflection;
using System.Runtime.Loader;
using Kyuplan.Contracts;
using Kyuplan.Plugins;

namespace Kyuplan.Api.Plugins;

/// <summary>
/// The assemblies of one plugin in a context of their own, so that two plugins (or a plugin and the API) can use different versions of the same
/// library. What the API provides (the framework, <c>Kyuplan.Plugins.Abstractions</c>, logging, ...) is taken from the API, not from the plugin's folder:
/// the types a plugin and the API share have to be the same types. The context can be unloaded when the API stops (it is rebuilt on every restart).
/// </summary>
internal sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The API's own copy wins when it has one; a library only the plugin brings is loaded from the plugin's folder.
        if (assemblyName.Name is { } name && Default.Assemblies.Any(a => a.GetName().Name == name)) return null;

        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromFile(path) : null;
    }

    /// <summary>
    /// Loads an assembly from a copy in memory: the file is not held open, so a plugin can be updated or removed (its folder deleted) while the API still runs
    /// (Windows would refuse otherwise).
    /// </summary>
    public Assembly LoadFromFile(string path)
    {
        using var file = File.OpenRead(path);
        return LoadFromStream(file);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}

/// <summary>What a plugin registered while it was initialised.</summary>
public sealed class PluginRegistrations
{
    public Dictionary<string, Func<PluginDataSourceContext, IDataSource>> DataSources { get; } = new(StringComparer.Ordinal);

    public List<Action<Microsoft.AspNetCore.Routing.RouteGroupBuilder>> Endpoints { get; } = [];
}

/// <summary>One plugin that is installed, and whether it runs.</summary>
/// <param name="Problem">Why it does not run; <c>null</c> when it does.</param>
public sealed record LoadedPlugin(InstalledPlugin Installed, PluginRegistrations? Registrations, string? Problem)
{
    public string Id => Installed.Id;

    public PluginManifest Manifest => Installed.Manifest;

    public bool IsRunning => Problem is null && Registrations is not null;
}

/// <summary>
/// The plugins that run in this API: loaded once when the API starts (a change of the plugins restarts it), each in a load context of its own. A plugin
/// that cannot be loaded (its assembly is broken, it throws when it initialises, it wants an id that is taken) does not run and says why
/// (<see cref="LoadedPlugin.Problem"/>), and the API starts all the same. This is where what a plugin adds is found: its data sources, its endpoints, its pages and permissions.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginHost> _log;

    public PluginHost(PluginStore store, IServiceProvider services, ILoggerFactory loggers)
    {
        _services = services;
        _log = loggers.CreateLogger<PluginHost>();
        store.Sweep();
        foreach (var installed in store.ReadInstalled()) _plugins.Add(Load(installed, loggers));
    }

    /// <summary>Every installed plugin, the running ones and those that could not be loaded.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public IEnumerable<LoadedPlugin> Running => _plugins.Where(p => p.IsRunning);

    /// <summary>The full ids of the permissions the running plugins add.</summary>
    public IReadOnlyList<string> PermissionIds =>
        [.. Running.SelectMany(p => (p.Manifest.Permissions ?? []).Select(permission => PluginRules.PermissionId(p.Id, permission.Name)))];

    /// <summary>The running plugin that offers the data source <paramref name="kind"/>, with the definition and the factory; <c>null</c> when none does.</summary>
    public (LoadedPlugin Plugin, PluginDataSourceDef Source, Func<PluginDataSourceContext, IDataSource> Create)? FindDataSource(string kind)
    {
        foreach (var plugin in Running)
        {
            var source = (plugin.Manifest.DataSources ?? []).FirstOrDefault(s => s.Id == kind);
            if (source is not null && plugin.Registrations!.DataSources.TryGetValue(kind, out var create)) return (plugin, source, create);
        }

        return null;
    }

    private LoadedPlugin Load(InstalledPlugin installed, ILoggerFactory loggers)
    {
        var manifest = installed.Manifest;
        if (_plugins.FirstOrDefault(p => p.IsRunning && (p.Manifest.DataSources ?? []).Any(s => (manifest.DataSources ?? []).Any(m => m.Id == s.Id))) is { } taken)
            return Fail(installed, $"a data source id is offered by the plugin '{taken.Id}' already.");

        PluginLoadContext? context = null;
        try
        {
            var assemblyPath = Path.Combine(installed.Directory, manifest.Assembly);
            context = new PluginLoadContext(assemblyPath);
            var assembly = context.LoadFromFile(assemblyPath);
            var types = assembly.GetExportedTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IKyuplanPlugin).IsAssignableFrom(t)).ToList();
            if (types.Count != 1) return Unload(context, Fail(installed, types.Count == 0 ? $"{manifest.Assembly} has no public class that implements IKyuplanPlugin." : $"{manifest.Assembly} has more than one class that implements IKyuplanPlugin."));

            var plugin = (IKyuplanPlugin)Activator.CreateInstance(types[0])!;
            var builder = new PluginBuilder(installed, loggers);
            plugin.Initialize(builder);

            if ((manifest.DataSources ?? []).Select(s => s.Id).Except(builder.Registrations.DataSources.Keys).FirstOrDefault() is { } missing)
                return Unload(context, Fail(installed, $"the data source '{missing}' is declared in {PluginRules.ManifestFile}, but the plugin did not register it."));

            _contexts.Add(context);
            _log.LogInformation("The plugin {Plugin} {Version} is running.", installed.Id, manifest.Version);
            return new LoadedPlugin(installed, builder.Registrations, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "The plugin {Plugin} could not be loaded.", installed.Id);
            return Unload(context, Fail(installed, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private LoadedPlugin Fail(InstalledPlugin installed, string problem)
    {
        _log.LogWarning("The plugin {Plugin} does not run: {Problem}", installed.Id, problem);
        return new LoadedPlugin(installed, null, problem);
    }

    private static LoadedPlugin Unload(PluginLoadContext? context, LoadedPlugin result)
    {
        context?.Unload();
        return result;
    }

    /// <summary>Maps the endpoints of the running plugins below <c>/api/plugins/&lt;id&gt;</c>. A plugin that throws while it maps does not take the API down.</summary>
    public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
    {
        foreach (var plugin in Running.Where(p => p.Registrations!.Endpoints.Count > 0))
        {
            try
            {
                var group = app.MapGroup($"/api/plugins/{plugin.Id}").WithTags($"Plugin {plugin.Manifest.Name}").RequireAuthorization();
                foreach (var map in plugin.Registrations!.Endpoints) map(group);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.LogError(ex, "The endpoints of the plugin {Plugin} could not be mapped.", plugin.Id);
            }
        }
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Unload();
        _contexts.Clear();
    }

    private sealed class PluginBuilder(InstalledPlugin installed, ILoggerFactory loggers) : IPluginBuilder
    {
        public PluginRegistrations Registrations { get; } = new();

        public string Id => installed.Id;

        public string Version => installed.Manifest.Version;

        public string Directory => installed.Directory;

        public ILoggerFactory Loggers => loggers;

        public void AddDataSource(string id, Func<PluginDataSourceContext, IDataSource> create)
        {
            ArgumentNullException.ThrowIfNull(create);
            if ((installed.Manifest.DataSources ?? []).All(s => s.Id != id)) throw new InvalidOperationException($"The data source '{id}' is not declared in {PluginRules.ManifestFile}.");
            if (!Registrations.DataSources.TryAdd(id, create)) throw new InvalidOperationException($"The data source '{id}' is registered twice.");
        }

        public void MapEndpoints(Action<Microsoft.AspNetCore.Routing.RouteGroupBuilder> map)
        {
            ArgumentNullException.ThrowIfNull(map);
            Registrations.Endpoints.Add(map);
        }

        public string Permission(string name) =>
            (installed.Manifest.Permissions ?? []).Any(p => p.Name == name) ? PluginRules.PermissionId(installed.Id, name) : throw new InvalidOperationException($"The permission '{name}' is not declared in {PluginRules.ManifestFile}.");
    }
}
