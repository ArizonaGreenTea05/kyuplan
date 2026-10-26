using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kyuplan.Tests;

/// <summary>Loading the plugins of a folder: which run, which do not and why, and what the running ones offer.</summary>
public sealed class PluginHostTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-host-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public PluginHostTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:acme:ApiKey"] = "the-key" }).Build());
        services.Configure<Api.Configuration.StatisticsOptions>(_ => { });
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private PluginStore Store() => PluginTestKit.StoreOf(Path.Combine(_folder, "settings.json"));

    private PluginHost Host(params byte[][] packages)
    {
        var store = Store();
        foreach (var package in packages) store.Install(PluginPackage.Open(package), "https://github.com/test/p" + Guid.NewGuid().ToString("N")[..6], TimeProvider.System);
        return new PluginHost(store, _services, NullLoggerFactory.Instance);
    }

    /// <summary>A package whose assembly is this file (any file: what is in it is what the test is about).</summary>
    private static byte[] WithAssemblyOf(PluginManifest manifest, string assemblyPath)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest, PluginPackage.Json));
            using var output = zip.CreateEntry(manifest.Assembly).Open();
            output.Write(File.ReadAllBytes(assemblyPath));
        }

        return stream.ToArray();
    }

    [Fact]
    public void A_plugin_that_loads_runs_and_offers_its_permissions_and_data_source()
    {
        using var host = Host(PluginTestKit.AcmePackage());

        var plugin = Assert.Single(host.Plugins);
        Assert.True(plugin.IsRunning);
        Assert.Null(plugin.Problem);
        Assert.Equal(["acme.view", "acme.edit"], host.PermissionIds);
        var found = host.FindDataSource("acme")!.Value;
        Assert.Equal(("acme", "acme"), (found.Plugin.Id, found.Source.Id));
        Assert.Null(host.FindDataSource("other"));
        Assert.Equal(plugin, Assert.Single(host.Running));
    }

    [Fact]
    public async Task The_data_source_is_built_from_its_section_of_the_configuration()
    {
        using var host = Host(PluginTestKit.AcmePackage());
        using var services = new ServiceCollection()
            .AddSingleton(host)
            .AddSingleton(_services.GetRequiredService<IConfiguration>())
            .AddSingleton(TimeProvider.System)
            .AddSingleton(_services.GetRequiredService<IOptions<Api.Configuration.StatisticsOptions>>())
            .AddLogging()
            .BuildServiceProvider();

        var source = PluginDataSources.Create("acme", services);

        Assert.Equal("acme", source.Kind);
        Assert.False(source.IsDemo);
        Assert.Equal("the-key", Assert.Single(await source.GetGroupsAsync(default)).Name);
        Assert.Empty(await source.GetEventsAsync("g1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default));
        source.InvalidateCache();
    }

    [Fact]
    public async Task A_data_source_that_no_running_plugin_offers_answers_every_question_with_the_reason()
    {
        using var host = Host();
        using var services = new ServiceCollection().AddSingleton(host).BuildServiceProvider();

        var source = PluginDataSources.Create("acme", services);

        Assert.Equal("acme", source.Kind);
        var groups = await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetGroupsAsync(default));
        Assert.Contains("not installed", groups.Message);
        await Assert.ThrowsAsync<DataSourceUnavailableException>(() => source.GetEventsAsync("g", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default));
        source.InvalidateCache(); // nothing is cached
    }

    [Fact]
    public void A_plugin_that_throws_is_listed_with_its_problem_and_adds_nothing()
    {
        using var host = Host(PluginTestKit.Package(PluginTestKit.BrokenManifest), PluginTestKit.AcmePackage());

        var broken = host.Plugins.Single(p => p.Id == "broken");
        Assert.False(broken.IsRunning);
        Assert.Contains("this plugin is broken", broken.Problem);
        Assert.Equal(["acme"], host.Running.Select(p => p.Id));
        Assert.Equal(["acme.view", "acme.edit"], host.PermissionIds);
    }

    [Fact]
    public void An_assembly_without_a_plugin_class_is_a_plugin_that_does_not_run()
    {
        var abstractions = typeof(IKyuplanPlugin).Assembly.Location;
        var manifest = new PluginManifest("empty", "Empty", null, "1.0.0", null, null, null, null, "Kyuplan.Plugins.Abstractions.dll");

        using var host = Host(WithAssemblyOf(manifest, abstractions));

        Assert.Contains("no public class that implements IKyuplanPlugin", Assert.Single(host.Plugins).Problem);
    }

    [Fact]
    public void A_data_source_the_manifest_declares_but_the_plugin_does_not_register_is_a_problem()
    {
        var manifest = PluginTestKit.AcmeManifest with
        {
            DataSources = [.. PluginTestKit.AcmeManifest.DataSources!, new PluginDataSourceDef("extra", "Extra", null, null, null, null, null)],
        };

        using var host = Host(PluginTestKit.Package(manifest, new Dictionary<string, string> { ["ui/settings.json"] = PluginTestKit.AcmeForm, ["ui/items.json"] = PluginTestKit.AcmePage, ["ui/about.json"] = PluginTestKit.AboutPage }));

        Assert.Contains("'extra'", Assert.Single(host.Plugins).Problem);
        Assert.Contains("did not register", Assert.Single(host.Plugins).Problem);
    }

    [Fact]
    public void A_plugin_that_registers_a_data_source_it_did_not_declare_does_not_run()
    {
        // The test plugin registers the data source "spond" for a plugin with that id; this manifest declares another one.
        var manifest = PluginTestKit.SpondManifest with { DataSources = [new PluginDataSourceDef("elsewhere", "Else", null, null, null, null, null)] };

        using var host = Host(PluginTestKit.Package(manifest));

        Assert.Contains("not declared", Assert.Single(host.Plugins).Problem);
    }

    [Fact]
    public void The_files_of_a_removed_plugin_are_swept_when_the_host_starts()
    {
        var store = Store();
        var stored = store.Install(PluginPackage.Open(PluginTestKit.AcmePackage()), "https://github.com/test/acme", TimeProvider.System);
        store.Remove("acme");

        using var host = new PluginHost(store, _services, NullLoggerFactory.Instance);

        Assert.Empty(host.Plugins);
        Assert.False(Directory.Exists(Path.Combine(store.Directory, "acme", stored.Folder)));
    }

    [Fact]
    public void Disposing_the_host_lets_go_of_the_plugins_and_twice_is_fine()
    {
        var host = Host(PluginTestKit.AcmePackage());

        host.Dispose();
        host.Dispose();

        Assert.Single(host.Plugins);
    }

    [Fact]
    public void The_plugins_are_loaded_from_memory_so_their_files_can_be_deleted_while_they_run()
    {
        using var host = Host(PluginTestKit.AcmePackage());
        var directory = host.Plugins[0].Installed.Directory;

        Directory.Delete(directory, recursive: true); // Windows would refuse if the assembly were held open

        Assert.True(host.Plugins[0].IsRunning);
    }
}
