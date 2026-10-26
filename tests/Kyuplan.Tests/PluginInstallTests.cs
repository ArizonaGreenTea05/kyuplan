using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Kyuplan.Tests;

/// <summary>A plugin of the repository named in the address, whatever it is asked for, or the failure a test wants.</summary>
internal sealed class FakePluginFetcher : IPluginFetcher
{
    public Func<string, string, byte[]> Package { get; set; } = (_, _) => PluginTestKit.AcmePackage();

    public string? Failure { get; set; }

    public List<string> Asked { get; } = [];

    public Task<PluginDownload> FetchLatestAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        Asked.Add($"{owner}/{repo}");
        return Failure is { } failure ? throw new PluginFetchException(failure) : Task.FromResult(new PluginDownload(Package(owner, repo), "v1"));
    }
}

/// <summary>Reading, checking and unpacking the package of a plugin: nothing in it is trusted.</summary>
public sealed class PluginPackageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    private static string ManifestJson(PluginManifest manifest) => JsonSerializer.Serialize(manifest, PluginPackage.Json);

    private static PluginManifest Simple => new("simple", "Simple", null, "1.0.0", null, null, null, null, "Simple.dll");

    private static void AssertRefused(byte[] bytes, string reason)
    {
        var ex = Assert.Throws<InvalidInputException>(() => PluginPackage.Open(bytes));
        Assert.Contains(reason, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_package_with_a_manifest_the_assembly_and_its_layouts_opens()
    {
        var package = PluginPackage.Open(PluginTestKit.AcmePackage());

        Assert.Equal("acme", package.Manifest.Id);
        Assert.Contains("ui/items.json", package.Files);
        Assert.Contains(PluginTestKit.AssemblyFile, package.Files);
    }

    [Fact]
    public void An_empty_or_oversized_or_broken_package_is_refused()
    {
        AssertRefused([], "empty");
        AssertRefused(new byte[PluginRules.MaxPackageBytes + 1], "larger than");
        AssertRefused(Encoding.UTF8.GetBytes("this is no zip file at all"), "not a valid .zip");
    }

    [Fact]
    public void A_package_without_a_manifest_or_with_a_bad_one_or_without_its_assembly_is_refused()
    {
        AssertRefused(Zip(("readme.txt", "x")), "plugin.json");
        AssertRefused(Zip(("plugin.json", "{ not json")), "plugin.json");
        AssertRefused(Zip(("plugin.json", ManifestJson(Simple with { Id = "Bad Id" }))), "id");
        AssertRefused(Zip(("plugin.json", ManifestJson(Simple))), "Simple.dll");
    }

    [Fact]
    public void A_layout_that_is_missing_or_wrong_is_refused_at_installation_not_later()
    {
        var page = Simple with { Pages = [new PluginPageDef("p", "P", null, null, null, "ui/p.json")] };
        var source = Simple with { DataSources = [new PluginDataSourceDef("src", "S", null, null, null, null, "ui/s.json")] };

        AssertRefused(Zip(("plugin.json", ManifestJson(page)), ("Simple.dll", "x")), "ui/p.json");
        AssertRefused(Zip(("plugin.json", ManifestJson(page)), ("Simple.dll", "x"), ("ui/p.json", """{ "elements": [ { "type": "nonsense" } ] }""")), "not known");
        AssertRefused(Zip(("plugin.json", ManifestJson(page)), ("Simple.dll", "x"), ("ui/p.json", "[1, 2")), "ui/p.json");
        AssertRefused(Zip(("plugin.json", ManifestJson(source)), ("Simple.dll", "x"), ("ui/s.json", """{ "elements": [ { "type": "table", "source": "x", "columns": [ { "key": "a", "label": "A" } ] } ] }""")), "only for pages");
    }

    [Fact]
    public void A_file_name_that_leaves_the_folder_or_is_hidden_or_twice_in_it_is_refused()
    {
        var manifest = ("plugin.json", ManifestJson(Simple));
        AssertRefused(Zip(manifest, ("Simple.dll", "x"), ("../evil.dll", "x")), "not allowed");
        AssertRefused(Zip(manifest, ("Simple.dll", "x"), ("/abs.dll", "x")), "not allowed");
        AssertRefused(Zip(manifest, ("Simple.dll", "x"), (".hidden/x", "x")), "not allowed");
        AssertRefused(Zip(manifest, ("Simple.dll", "x"), ("a b.txt", "x")), "not allowed");
    }

    [Fact]
    public void A_backslash_in_a_file_name_is_read_as_a_slash_because_windows_tools_write_it()
    {
        var page = Simple with { Pages = [new PluginPageDef("p", "P", null, null, null, "ui/p.json")] };
        var bytes = Zip(("plugin.json", ManifestJson(page)), ("Simple.dll", "x"), ("ui\\p.json", """{ "elements": [ { "type": "text", "text": "hi" } ] }"""));

        var package = PluginPackage.Open(bytes);
        package.ExtractTo(Path.Combine(_folder, "windows"));

        Assert.Contains("ui/p.json", package.Files);
        Assert.True(File.Exists(Path.Combine(_folder, "windows", "ui", "p.json")));
        AssertRefused(Zip(("plugin.json", ManifestJson(Simple)), ("Simple.dll", "x"), ("..\\evil.dll", "x")), "not allowed");
        AssertRefused(Zip(("plugin.json", ManifestJson(Simple)), ("Simple.dll", "x"), ("a/b.txt", "x"), ("a\\b.txt", "y")), "twice");
    }

    [Fact]
    public void A_package_with_too_many_files_or_a_large_manifest_is_refused()
    {
        var many = Enumerable.Range(0, PluginRules.MaxFiles + 1).Select(i => ($"f{i}.txt", "x")).Append(("plugin.json", ManifestJson(Simple))).ToArray();
        AssertRefused(Zip(many), "more than");

        AssertRefused(Zip(("plugin.json", ManifestJson(Simple) + new string(' ', PluginRules.MaxJsonBytes))), "larger than");
    }

    [Fact]
    public void The_package_is_unpacked_into_a_folder_and_a_failure_leaves_nothing_behind()
    {
        var package = PluginPackage.Open(PluginTestKit.AcmePackage());
        var target = Path.Combine(_folder, "acme", "1");

        package.ExtractTo(target);

        Assert.True(File.Exists(Path.Combine(target, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(target, "ui", "items.json")));
        Assert.True(File.Exists(Path.Combine(target, PluginTestKit.AssemblyFile)));
        Assert.False(Directory.Exists(target + ".partial"));
    }

    [Fact]
    public void A_stale_partial_folder_of_an_earlier_crash_does_not_get_in_the_way()
    {
        var target = Path.Combine(_folder, "acme", "1");
        Directory.CreateDirectory(target + ".partial");
        File.WriteAllText(Path.Combine(target + ".partial", "old.txt"), "old");

        PluginPackage.Open(PluginTestKit.AcmePackage()).ExtractTo(target);

        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
        Assert.True(File.Exists(Path.Combine(target, "plugin.json")));
    }

    [Fact]
    public void Reading_a_json_file_of_an_unpacked_plugin_gives_nothing_for_a_missing_broken_or_huge_file()
    {
        Directory.CreateDirectory(_folder);
        var good = Path.Combine(_folder, "good.json");
        File.WriteAllText(good, "﻿" + JsonSerializer.Serialize(new PluginUiLayout([new("text", Text: "hi")]), PluginPackage.Json)); // a byte order mark, as Windows editors write it
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(_folder, "huge.json"), new string(' ', PluginRules.MaxJsonBytes + 1));

        Assert.Equal("hi", PluginPackage.ReadFile<PluginUiLayout>(good)!.Elements[0].Text);
        Assert.Null(PluginPackage.ReadFile<PluginUiLayout>(Path.Combine(_folder, "broken.json")));
        Assert.Null(PluginPackage.ReadFile<PluginUiLayout>(Path.Combine(_folder, "huge.json")));
        Assert.Null(PluginPackage.ReadFile<PluginUiLayout>(Path.Combine(_folder, "missing.json")));
    }
}

/// <summary>The installed plugins on disk: the index, the folders, replacing, removing and sweeping.</summary>
public sealed class PluginStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private PluginStore Store(IDictionary<string, string?>? configuration = null) =>
        PluginTestKit.StoreOf(Path.Combine(_folder, "settings.json"), configuration);

    private StoredPlugin Install(PluginStore store, byte[] package, string source = "https://github.com/acme/plugin") =>
        store.Install(PluginPackage.Open(package), source, _time);

    [Fact]
    public void A_folder_that_never_had_a_plugin_is_fresh_and_stops_being_so_with_the_first()
    {
        var store = Store();
        Assert.True(store.IsFresh);
        Assert.Empty(store.List());

        Install(store, PluginTestKit.AcmePackage());

        Assert.False(store.IsFresh);
        var stored = Assert.Single(store.List());
        Assert.Equal(("acme", "2.1.0", "https://github.com/acme/plugin", _time.GetUtcNow()), (stored.Id, stored.Version, stored.Source, stored.InstalledAt));
        Assert.Equal(64, stored.Sha256.Length);
        Assert.Equal($"2.1.0-{stored.Sha256[..8]}", stored.Folder);
    }

    [Fact]
    public void The_plugins_folder_is_next_to_the_settings_file_unless_the_configuration_says_otherwise()
    {
        Assert.Equal(Path.Combine(_folder, "plugins"), Store().Directory);
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "elsewhere")), Store(new Dictionary<string, string?> { [PluginStore.DirectorySetting] = Path.Combine(_folder, "elsewhere") }).Directory);
    }

    [Fact]
    public void The_installed_plugins_are_read_with_their_manifests_and_layouts()
    {
        var store = Store();
        Install(store, PluginTestKit.AcmePackage());

        var installed = Assert.Single(store.ReadInstalled());

        Assert.Equal("acme", installed.Id);
        Assert.Equal("Acme", installed.Manifest.Name);
        Assert.Equal(["ui/about.json", "ui/items.json", "ui/settings.json"], installed.Layouts.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Plugins:acme", installed.SectionOf(installed.Manifest.DataSources![0]));
        Assert.NotNull(installed.FormOf(installed.Manifest.DataSources![0]));
        Assert.Null(installed.FormOf(new PluginDataSourceDef("x", "X", null, null, null, null, null)));
    }

    [Fact]
    public void A_data_source_with_a_section_of_its_own_keeps_it()
    {
        var store = Store();
        Install(store, PluginTestKit.SpondPackage());

        var installed = Assert.Single(store.ReadInstalled());

        Assert.Equal("Spond", installed.SectionOf(installed.Manifest.DataSources![0]));
    }

    [Fact]
    public void Installing_the_same_plugin_again_replaces_it_and_a_new_version_gets_a_new_folder()
    {
        var store = Store();
        var first = Install(store, PluginTestKit.AcmePackage("2.1.0"));
        var again = Install(store, PluginTestKit.AcmePackage("2.1.0"));
        var newer = Install(store, PluginTestKit.AcmePackage("2.2.0"));

        Assert.Equal(first.Folder, again.Folder);
        Assert.NotEqual(first.Folder, newer.Folder);
        Assert.Equal("2.2.0", Assert.Single(store.List()).Version);
        Assert.True(Directory.Exists(Path.Combine(store.Directory, "acme", first.Folder))); // the old version is only swept at the next start
    }

    [Fact]
    public void A_plugin_with_the_id_of_another_one_from_somewhere_else_is_refused()
    {
        var store = Store();
        Install(store, PluginTestKit.AcmePackage(), "https://github.com/acme/plugin");

        var ex = Assert.Throws<InvalidInputException>(() => Install(store, PluginTestKit.AcmePackage(), "https://github.com/someone-else/fork"));

        Assert.Contains("installed already", ex.Message);
        Assert.Contains("https://github.com/acme/plugin", ex.Message);
    }

    [Fact]
    public void The_source_may_differ_in_case_because_github_does_not_care()
    {
        var store = Store();
        Install(store, PluginTestKit.AcmePackage(), "https://github.com/Acme/Plugin");

        Install(store, PluginTestKit.AcmePackage("2.5.0"), "https://github.com/acme/plugin");

        Assert.Equal("2.5.0", Assert.Single(store.List()).Version);
    }

    [Fact]
    public void A_data_source_id_that_another_plugin_offers_is_refused()
    {
        var store = Store();
        Install(store, PluginTestKit.AcmePackage());
        var clash = PluginTestKit.Package(PluginTestKit.AcmeManifest with { Id = "other", Pages = null, Permissions = null },
            new Dictionary<string, string> { ["ui/settings.json"] = PluginTestKit.AcmeForm });

        var ex = Assert.Throws<InvalidInputException>(() => Install(store, clash, "https://github.com/x/other"));

        Assert.Contains("'acme'", ex.Message);
        Assert.Single(store.List());
    }

    [Fact]
    public void Not_more_plugins_than_the_limit_can_be_installed_but_one_of_them_can_be_replaced()
    {
        var store = Store();
        for (var i = 0; i < PluginRules.MaxPlugins; i++)
        {
            var manifest = new PluginManifest($"plug-{i}", "P", null, "1.0.0", null, null, null, null, PluginTestKit.AssemblyFile);
            Install(store, PluginTestKit.Package(manifest), $"https://github.com/o/r{i}");
        }

        var extra = new PluginManifest("plug-extra", "P", null, "1.0.0", null, null, null, null, PluginTestKit.AssemblyFile);
        Assert.Throws<InvalidInputException>(() => Install(store, PluginTestKit.Package(extra), "https://github.com/o/extra"));

        var replace = new PluginManifest("plug-3", "P", null, "1.0.1", null, null, null, null, PluginTestKit.AssemblyFile);
        Install(store, PluginTestKit.Package(replace), "https://github.com/o/r3");
        Assert.Equal(PluginRules.MaxPlugins, store.List().Count);
    }

    [Fact]
    public void A_removed_plugin_leaves_the_index_at_once_and_its_files_at_the_next_sweep()
    {
        var store = Store();
        var stored = Install(store, PluginTestKit.AcmePackage());

        Assert.True(store.Remove("acme"));
        Assert.False(store.Remove("acme"));
        Assert.Empty(store.List());
        Assert.False(store.IsFresh); // something was installed here once: the defaults do not come back
        Assert.True(Directory.Exists(Path.Combine(store.Directory, "acme", stored.Folder)));

        store.Sweep();

        Assert.False(Directory.Exists(Path.Combine(store.Directory, "acme")));
    }

    [Fact]
    public void The_sweep_keeps_the_version_in_use_and_takes_the_others_and_stray_folders()
    {
        var store = Store();
        var old = Install(store, PluginTestKit.AcmePackage("2.0.0"));
        var current = Install(store, PluginTestKit.AcmePackage("2.1.0"));
        Directory.CreateDirectory(Path.Combine(store.Directory, "stray", "1.0.0"));

        store.Sweep();

        Assert.False(Directory.Exists(Path.Combine(store.Directory, "acme", old.Folder)));
        Assert.True(Directory.Exists(Path.Combine(store.Directory, "acme", current.Folder)));
        Assert.False(Directory.Exists(Path.Combine(store.Directory, "stray")));
    }

    [Fact]
    public void Sweeping_a_folder_that_does_not_exist_is_nothing()
    {
        var store = Store();

        store.Sweep();

        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void An_index_that_cannot_be_read_is_set_aside_and_the_store_starts_empty()
    {
        var store = Store();
        Install(store, PluginTestKit.AcmePackage());
        File.WriteAllText(Path.Combine(store.Directory, PluginStore.IndexFile), "{ this is not an index");

        Assert.Empty(store.List());

        Assert.True(File.Exists(Path.Combine(store.Directory, PluginStore.IndexFile + ".bad")));
        Install(store, PluginTestKit.AcmePackage()); // and works again
        Assert.Single(store.List());
    }

    [Fact]
    public void An_entry_with_an_id_or_folder_that_is_none_is_ignored_so_the_index_cannot_point_out_of_the_folder()
    {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(Path.Combine(store.Directory, PluginStore.IndexFile), JsonSerializer.Serialize(new[]
        {
            new StoredPlugin("../evil", "1.0.0", "x", _time.GetUtcNow(), "h", "1.0.0"),
            new StoredPlugin("good", "1.0.0", "x", _time.GetUtcNow(), "h", "../escape"),
            new StoredPlugin("fine", "1.0.0", "x", _time.GetUtcNow(), "h", "1.0.0-abc"),
        }, PluginPackage.Json));

        Assert.Equal(["fine"], store.List().Select(p => p.Id));
    }

    [Fact]
    public void A_plugin_whose_files_are_gone_or_whose_manifest_changed_is_left_out_of_what_is_read()
    {
        var store = Store();
        var stored = Install(store, PluginTestKit.AcmePackage());
        var manifest = Path.Combine(store.Directory, "acme", stored.Folder, PluginRules.ManifestFile);

        File.WriteAllText(manifest, "{ broken");
        Assert.Empty(store.ReadInstalled());

        File.Delete(manifest);
        Assert.Empty(store.ReadInstalled());
        Assert.Single(store.List()); // still in the index: the page says it cannot be read
    }

    [Fact]
    public void A_layout_that_is_gone_is_left_out_but_the_plugin_stays()
    {
        var store = Store();
        var stored = Install(store, PluginTestKit.AcmePackage());
        File.Delete(Path.Combine(store.Directory, "acme", stored.Folder, "ui", "items.json"));

        var installed = Assert.Single(store.ReadInstalled());

        Assert.DoesNotContain("ui/items.json", installed.Layouts.Keys);
        Assert.Contains("ui/about.json", installed.Layouts.Keys);
    }
}

/// <summary>Getting a plugin from GitHub and putting it into the store, and the plugins a new installation starts with.</summary>
public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-installer-" + Guid.NewGuid().ToString("N"));
    private readonly FakePluginFetcher _fetcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
    private readonly PluginStore _store;
    private readonly PluginInstaller _installer;

    public PluginInstallerTests()
    {
        _store = PluginTestKit.StoreOf(Path.Combine(_folder, "settings.json"));
        _installer = new PluginInstaller(_store, _fetcher, _time, NullLogger<PluginInstaller>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_repository_is_installed_under_its_own_address_and_asked_once()
    {
        var (plugin, changed) = await _installer.InstallFromGitHubAsync("https://github.com/Acme/Kyuplan-Acme.git", default);

        Assert.True(changed);
        Assert.Equal("https://github.com/Acme/Kyuplan-Acme", plugin.Source);
        Assert.Equal(["Acme/Kyuplan-Acme"], _fetcher.Asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("http://github.com/a/b")]
    [InlineData("https://evil.example/a/b")]
    [InlineData("https://github.com/a")]
    public async Task Something_that_is_no_github_repository_is_refused_before_anything_is_fetched(string? url)
    {
        var ex = await Assert.ThrowsAsync<InvalidInputException>(() => _installer.InstallFromGitHubAsync(url, default));

        Assert.Contains("GitHub repository", ex.Message);
        Assert.Empty(_fetcher.Asked);
    }

    [Fact]
    public async Task A_failed_fetch_is_a_bad_request_with_the_reason_and_installs_nothing()
    {
        _fetcher.Failure = "The repository has no release.";

        var ex = await Assert.ThrowsAsync<InvalidInputException>(() => _installer.InstallFromGitHubAsync("https://github.com/a/b", default));

        Assert.Equal("The repository has no release.", ex.Message);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task A_package_that_is_no_plugin_is_refused_with_the_reason()
    {
        _fetcher.Package = (_, _) => Encoding.UTF8.GetBytes("no zip");

        var ex = await Assert.ThrowsAsync<InvalidInputException>(() => _installer.InstallFromGitHubAsync("https://github.com/a/b", default));

        Assert.Contains("not a valid .zip", ex.Message);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Fetching_the_same_package_again_changes_nothing_and_a_new_one_is_an_update()
    {
        await _installer.InstallFromGitHubAsync("https://github.com/a/b", default);

        var (_, same) = await _installer.InstallFromGitHubAsync("https://github.com/a/b", default);
        _fetcher.Package = (_, _) => PluginTestKit.AcmePackage("3.0.0");
        var (updated, changed) = await _installer.UpdateAsync("acme", default) ?? throw new Xunit.Sdk.XunitException("no update");

        Assert.False(same);
        Assert.True(changed);
        Assert.Equal("3.0.0", updated.Version);
    }

    [Fact]
    public async Task An_unknown_plugin_has_nothing_to_update_and_one_that_did_not_come_from_github_cannot_be()
    {
        Assert.Null(await _installer.UpdateAsync("nothing", default));

        _installer.InstallFile(WriteBundled(PluginTestKit.SpondPackage()));
        var ex = await Assert.ThrowsAsync<InvalidInputException>(() => _installer.UpdateAsync("spond", default));
        Assert.Contains("not installed from GitHub", ex.Message);
    }

    [Fact]
    public async Task An_update_that_would_be_another_plugin_is_refused_and_changes_nothing()
    {
        await _installer.InstallFromGitHubAsync("https://github.com/a/b", default);
        var before = Assert.Single(_store.List());
        _fetcher.Package = (_, _) => PluginTestKit.SpondPackage();

        var ex = await Assert.ThrowsAsync<InvalidInputException>(() => _installer.UpdateAsync("acme", default));

        Assert.Contains("'spond', not 'acme'", ex.Message);
        Assert.Equal(before, Assert.Single(_store.List()));
    }

    private string WriteBundled(byte[] package, string name = "spond.kyuplugin")
    {
        var directory = Path.Combine(_folder, "bundled");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, package);
        return path;
    }

    [Fact]
    public void A_package_file_is_installed_as_bundled()
    {
        var stored = _installer.InstallFile(WriteBundled(PluginTestKit.SpondPackage()));

        Assert.Equal(PluginStore.BundledSource, stored.Source);
        Assert.Equal("spond", Assert.Single(_store.List()).Id);
    }

    [Fact]
    public void A_plugin_is_removed_once()
    {
        _installer.InstallFile(WriteBundled(PluginTestKit.SpondPackage()));

        Assert.True(_installer.Remove("spond"));
        Assert.False(_installer.Remove("spond"));
    }

    // --- The plugins of a new installation --------------------------------------------------------------------

    private async Task<IReadOnlyList<(LogLevel Level, string Message)>> Defaults(PluginOptions options) =>
        await PluginBootstrap.InstallDefaultsAsync(_store, _installer, options, _folder, default);

    [Fact]
    public async Task A_new_installation_installs_the_bundled_packages_and_asks_nobody()
    {
        WriteBundled(PluginTestKit.SpondPackage());

        var lines = await Defaults(new PluginOptions { Defaults = ["https://github.com/a/b"], BundledDirectory = "bundled" });

        Assert.Equal("spond", Assert.Single(_store.List()).Id);
        Assert.Empty(_fetcher.Asked); // nothing is left to fetch
        Assert.Contains(lines, l => l.Level == LogLevel.Information && l.Message.Contains("spond"));
    }

    [Fact]
    public async Task Without_bundled_packages_the_repositories_of_the_defaults_are_asked()
    {
        var lines = await Defaults(new PluginOptions { Defaults = ["https://github.com/acme/plugin", ""] });

        Assert.Equal(["acme/plugin"], _fetcher.Asked);
        Assert.Equal("acme", Assert.Single(_store.List()).Id);
        Assert.Contains(lines, l => l.Message.Contains("default plugin acme"));
    }

    [Fact]
    public async Task A_default_that_cannot_be_fetched_is_logged_and_the_folder_stays_new_so_the_next_start_tries_again()
    {
        _fetcher.Failure = "GitHub could not be reached.";

        var lines = await Defaults(new PluginOptions { Defaults = ["https://github.com/acme/plugin"] });

        Assert.True(_store.IsFresh);
        var warning = Assert.Single(lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("GitHub could not be reached.", warning.Message);
        Assert.Contains("tried again at the next start", warning.Message);
    }

    [Fact]
    public async Task A_bundled_package_that_is_broken_is_logged_and_the_defaults_follow()
    {
        WriteBundled(Encoding.UTF8.GetBytes("no zip"), "broken.kyuplugin");

        var lines = await Defaults(new PluginOptions { Defaults = ["https://github.com/acme/plugin"], BundledDirectory = "bundled" });

        Assert.Contains(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("broken.kyuplugin"));
        Assert.Equal("acme", Assert.Single(_store.List()).Id);
    }

    [Fact]
    public async Task An_installation_that_has_had_a_plugin_gets_none_and_so_does_one_that_switches_it_off()
    {
        await Defaults(new PluginOptions { InstallDefaults = false, Defaults = ["https://github.com/acme/plugin"] });
        Assert.True(_store.IsFresh);
        Assert.Empty(_fetcher.Asked);

        _installer.InstallFile(WriteBundled(PluginTestKit.SpondPackage()));
        _installer.Remove("spond"); // an administrator removed the default plugin: it does not come back
        await Defaults(new PluginOptions { Defaults = ["https://github.com/acme/plugin"] });
        Assert.Empty(_fetcher.Asked);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task A_cancelled_start_is_not_swallowed_as_a_failed_default()
    {
        var slow = new SlowFetcher();
        var installer = new PluginInstaller(_store, slow, _time, NullLogger<PluginInstaller>.Instance);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PluginBootstrap.InstallDefaultsAsync(_store, installer, new PluginOptions { Defaults = ["https://github.com/a/b"] }, _folder, cancelled.Token));
    }

    private sealed class SlowFetcher : IPluginFetcher
    {
        public async Task<PluginDownload> FetchLatestAsync(string owner, string repo, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null!;
        }
    }
}

/// <summary>The GitHub reader: one request for the latest release, one for its package; the hosts are fixed and every answer is checked.</summary>
public sealed class GitHubPluginFetcherTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object Release(string tag, params (string Name, string Url, long Size)[] assets) =>
        new { tag_name = tag, assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url, size = a.Size }).ToArray() };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static async Task<PluginDownload> FetchAsync(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        await new GitHubPluginFetcher(new HttpClient(new Handler(answer))).FetchLatestAsync("acme", "plugin", default);

    private static async Task<string> FailureAsync(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        (await Assert.ThrowsAsync<PluginFetchException>(() => FetchAsync(answer))).Message;

    [Fact]
    public async Task The_package_of_the_latest_release_is_downloaded()
    {
        var handler = new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? Json(Release("v1.2.0", ("notes.txt", "https://github.com/acme/plugin/releases/download/v1.2.0/notes.txt", 5), ("acme.kyuplugin", "https://github.com/acme/plugin/releases/download/v1.2.0/acme.kyuplugin", 3)))
            : Bytes([1, 2, 3]));

        var download = await new GitHubPluginFetcher(new HttpClient(handler)).FetchLatestAsync("acme", "plugin", default);

        Assert.Equal("v1.2.0", download.Tag);
        Assert.Equal<byte>([1, 2, 3], download.Bytes);
        Assert.Equal(["https://api.github.com/repos/acme/plugin/releases/latest", "https://github.com/acme/plugin/releases/download/v1.2.0/acme.kyuplugin"], handler.Requests);
    }

    [Fact]
    public async Task A_repository_without_a_release_or_a_package_or_with_two_says_so()
    {
        Assert.Contains("no release", await FailureAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Contains("no .kyuplugin file", await FailureAsync(_ => Json(Release("v1", ("a.zip", "https://github.com/a.zip", 1)))));
        Assert.Contains("more than one", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "https://github.com/a", 1), ("b.kyuplugin", "https://github.com/b", 1)))));
    }

    [Fact]
    public async Task A_package_that_is_announced_too_large_or_is_missing_or_is_somewhere_else_is_refused()
    {
        Assert.Contains("larger than", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "https://github.com/a", PluginRules.MaxPackageBytes + 1)))));
        Assert.Contains("not found", await FailureAsync(r => r.RequestUri!.Host == "api.github.com" ? Json(Release("v1", ("a.kyuplugin", "https://github.com/a", 1))) : new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Contains("not on GitHub", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "https://evil.example/a.kyuplugin", 1)))));
        Assert.Contains("not on GitHub", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "http://github.com/a.kyuplugin", 1)))));
        Assert.Contains("not on GitHub", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "https://github.com:8443/a.kyuplugin", 1)))));
        Assert.Contains("not on GitHub", await FailureAsync(_ => Json(Release("v1", ("a.kyuplugin", "not a url", 1)))));
    }

    [Fact]
    public async Task The_download_hosts_of_github_are_fine_and_a_redirect_that_ends_elsewhere_is_not()
    {
        var fine = await FetchAsync(r => r.RequestUri!.Host == "api.github.com"
            ? Json(Release("v1", ("a.kyuplugin", "https://objects.githubusercontent.com/a", 1)))
            : Bytes([7]));
        Assert.Equal<byte>([7], fine.Bytes);

        var redirected = await FailureAsync(r =>
        {
            if (r.RequestUri!.Host == "api.github.com") return Json(Release("v1", ("a.kyuplugin", "https://github.com/a", 1)));
            var response = Bytes([7]);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/final"); // where the redirects ended
            return response;
        });
        Assert.Contains("another host", redirected);
    }

    [Fact]
    public async Task An_answer_that_is_not_what_github_sends_is_refused()
    {
        Assert.Contains("could not be read", await FailureAsync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }));
        Assert.Contains("could not be read", await FailureAsync(_ => Json(new { nothing = 1 })));
    }

    [Fact]
    public async Task A_rate_limit_and_other_failures_have_their_own_words()
    {
        Assert.Contains("rate limit", await FailureAsync(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Contains("rate limit", await FailureAsync(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return response;
        }));
        Assert.Contains("403", await FailureAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))); // blocked, not limited
        Assert.Contains("500", await FailureAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    }

    [Fact]
    public async Task A_package_that_turns_out_larger_than_it_said_or_that_cannot_be_reached_is_refused()
    {
        var huge = new byte[PluginRules.MaxPackageBytes + 10];
        Assert.Contains("larger than", await FailureAsync(r => r.RequestUri!.Host == "api.github.com" ? Json(Release("v1", ("a.kyuplugin", "https://github.com/a", 1))) : Bytes(huge)));

        var unreachable = new GitHubPluginFetcher(new HttpClient(new FailingHandler()));
        Assert.Contains("could not be reached", (await Assert.ThrowsAsync<PluginFetchException>(() => unreachable.FetchLatestAsync("a", "b", default))).Message);

        var slow = new GitHubPluginFetcher(new HttpClient(new TimeoutHandler()));
        Assert.Contains("did not answer in time", (await Assert.ThrowsAsync<PluginFetchException>(() => slow.FetchLatestAsync("a", "b", default))).Message);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("no route");
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new TaskCanceledException("timeout");
    }
}
