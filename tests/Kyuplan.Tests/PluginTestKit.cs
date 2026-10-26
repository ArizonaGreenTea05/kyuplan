using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kyuplan.Tests;

/// <summary>
/// Plugins for the tests: packages built in memory from the test plugin (<c>tests/Kyuplan.TestPlugin</c>) and installed into the plugin folder of a test's settings
/// file before its API starts. <see cref="Spond"/> plays the Spond plugin (same settings form, same section <c>Spond</c>); <see cref="Acme"/> has pages, permissions,
/// endpoints and a data source with a section of its own.
/// </summary>
internal static class PluginTestKit
{
    public static readonly string AssemblyFile = Path.GetFileName(typeof(Kyuplan.TestPlugin.TestPlugin).Assembly.Location);

    /// <summary>The settings form of the real Spond plugin (<c>ui/settings.json</c> of kyuplan.plugins.spond).</summary>
    public const string SpondForm = """
        {
          "elements": [
            { "type": "field", "id": "Email", "label": "Spond email", "labelDe": "Spond-E-Mail", "input": "text", "maxLength": 254 },
            { "type": "field", "id": "PhoneNumber", "label": "Spond phone number (instead of the email)", "input": "text", "maxLength": 50 },
            {
              "type": "field", "id": "Password", "label": "Spond password", "input": "password", "maxLength": 500,
              "together": { "groups": [["Email", "PhoneNumber"], ["Password"]], "message": "A Spond login needs the email (or phone number) and the password.", "messageDe": "Ein Spond-Zugang braucht die E-Mail (oder Telefonnummer) und das Passwort." }
            },
            {
              "type": "list", "id": "Accounts", "label": "Backup Spond accounts", "max": 10,
              "elements": [
                { "type": "field", "id": "Email", "label": "Spond email", "input": "text", "maxLength": 254 },
                { "type": "field", "id": "PhoneNumber", "label": "Spond phone number (instead of the email)", "input": "text", "maxLength": 50 },
                {
                  "type": "field", "id": "Password", "label": "Spond password", "input": "password", "maxLength": 500,
                  "together": { "groups": [["Email", "PhoneNumber"], ["Password"]], "message": "A backup Spond login needs the email (or phone number) and the password.", "messageDe": "Ein Ersatz-Spond-Zugang braucht die E-Mail (oder Telefonnummer) und das Passwort." }
                }
              ]
            }
          ]
        }
        """;

    public static readonly PluginManifest SpondManifest = new(
        "spond", "Spond", "Spond", "1.0.0", "Reads the club from Spond.", null, "tests", null, AssemblyFile,
        DataSources: [new PluginDataSourceDef("spond", "Spond", null, "Groups from Spond.", null, "Spond", "ui/settings.json")]);

    public const string AcmeForm = """
        {
          "elements": [
            { "type": "heading", "text": "Acme" },
            { "type": "field", "id": "ApiKey", "label": "API key", "input": "password", "required": true },
            { "type": "field", "id": "Region", "label": "Region", "input": "select", "required": true, "default": "eu",
              "options": [ { "value": "eu", "label": "Europe", "labelDe": "Europa" }, { "value": "us", "label": "America" } ] },
            { "type": "field", "id": "Retries", "label": "Retries", "input": "number", "min": 0, "max": 5, "default": "2" },
            { "type": "field", "id": "Verbose", "label": "Verbose", "input": "checkbox" },
            { "type": "field", "id": "Notes", "label": "Notes", "input": "multiline" },
            { "type": "list", "id": "Rows", "label": "Rows", "max": 3, "elements": [ { "type": "field", "id": "Name", "label": "Name", "input": "text" }, { "type": "field", "id": "Token", "label": "Token", "input": "password" } ] }
          ]
        }
        """;

    public const string AcmePage = """
        {
          "elements": [
            { "type": "heading", "text": "Items", "textDe": "Dinge" },
            { "type": "text", "text": "What Acme knows." },
            { "type": "table", "label": "Items", "source": "items", "text": "Nothing yet.", "columns": [ { "key": "name", "label": "Name", "labelDe": "Name" }, { "key": "count", "label": "Count" }, { "key": "done", "label": "Done" } ] },
            { "type": "form", "label": "Add", "action": { "method": "POST", "path": "items/add" },
              "elements": [ { "type": "field", "id": "name", "label": "Name", "input": "text", "required": true } ] },
            { "type": "button", "label": "Clear", "action": { "method": "DELETE", "path": "items", "confirm": "Really clear?", "confirmDe": "Wirklich leeren?" } }
          ]
        }
        """;

    public static readonly PluginManifest AcmeManifest = new(
        "acme", "Acme", "Acme DE", "2.1.0", "Things of Acme.", null, "tests", null, AssemblyFile,
        Permissions: [new PluginPermissionDef("view", "See the Acme items", "Acme-Dinge sehen"), new PluginPermissionDef("edit", "Change the Acme items")],
        Pages:
        [
            new PluginPageDef("items", "Acme items", "Acme-Dinge", "chart", "view", "ui/items.json"),
            new PluginPageDef("about", "About Acme", null, null, null, "ui/about.json"),
        ],
        DataSources: [new PluginDataSourceDef("acme", "Acme", "Acme DE", "Acme's data.", null, null, "ui/settings.json")]);

    public const string AboutPage = """{ "elements": [ { "type": "text", "text": "Acme is a test." } ] }""";

    public static PluginManifest BrokenManifest => new("broken", "Broken", null, "1.0.0", null, null, null, null, AssemblyFile);

    /// <summary>The package of a plugin: the manifest, the test plugin's assembly and the given files (path → text).</summary>
    public static byte[] Package(PluginManifest manifest, IReadOnlyDictionary<string, string>? files = null, bool withAssembly = true)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, PluginRules.ManifestFile, JsonSerializer.Serialize(manifest, PluginPackage.Json));
            foreach (var (path, text) in files ?? new Dictionary<string, string>()) Add(zip, path, text);
            if (withAssembly)
            {
                var entry = zip.CreateEntry(manifest.Assembly);
                entry.LastWriteTime = Stamp;
                using var output = entry.Open();
                output.Write(File.ReadAllBytes(typeof(Kyuplan.TestPlugin.TestPlugin).Assembly.Location));
            }
        }

        return stream.ToArray();
    }

    /// <summary>Fixed, so that the same plugin built twice is the same package (the folder of an installed plugin is named after its hash).</summary>
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void Add(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        entry.LastWriteTime = Stamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    public static byte[] SpondPackage() => Package(SpondManifest, new Dictionary<string, string> { ["ui/settings.json"] = SpondForm });

    public static byte[] AcmePackage(string version = "2.1.0") => Package(AcmeManifest with { Version = version }, new Dictionary<string, string>
    {
        ["ui/settings.json"] = AcmeForm, ["ui/items.json"] = AcmePage, ["ui/about.json"] = AboutPage,
    });

    public static PluginUiLayout Layout(string json) => JsonSerializer.Deserialize<PluginUiLayout>(json, PluginPackage.Json)!;

    /// <summary>The values of a settings form as the clients send them: field id → value.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    /// <summary>The settings of one data source, as <see cref="SettingsInputDto.PluginSettings"/> carries them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Settings(string source, params (string Key, object? Value)[] pairs) =>
        new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { [source] = Values(pairs) };

    /// <summary>What the catalog of the API says when the Spond plugin runs, with the settings form for those who may see it.</summary>
    public static PluginCatalogDto SpondCatalog() => new([new PluginDto(
        "spond", "Spond", "Spond", "1.0.0", null, null, [], [],
        [new PluginDataSourceDto("spond", "Spond", null, "Groups from Spond.", null, Layout(SpondForm))])]);

    /// <summary>What the catalog says when the Acme plugin runs: pages, permissions and a data source with a form.</summary>
    public static PluginCatalogDto AcmeCatalog() => new([new PluginDto(
        "acme", "Acme", "Acme DE", "2.1.0", "Things of Acme.", null,
        [new PluginPermissionDto("acme.view", "See the Acme items", "Acme-Dinge sehen"), new PluginPermissionDto("acme.edit", "Change the Acme items", null)],
        [new PluginPageDto("items", "Acme items", "Acme-Dinge", "chart", "acme.view", Layout(AcmePage)), new PluginPageDto("about", "About Acme", null, null, null, Layout(AboutPage))],
        [new PluginDataSourceDto("acme", "Acme", "Acme DE", "Acme's data.", null, Layout(AcmeForm))])]);

    /// <summary>The store of the plugin folder that belongs to a settings file (what an API started with that settings file looks at).</summary>
    public static PluginStore StoreOf(string settingsFile, IDictionary<string, string?>? configuration = null) =>
        new(new SettingsFile(settingsFile), new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), NullLogger<PluginStore>.Instance);

    /// <summary>Installs the package for the API that will be started with this settings file.</summary>
    public static StoredPlugin Install(string settingsFile, byte[] package, string source = "https://github.com/test/plugin") =>
        StoreOf(settingsFile).Install(PluginPackage.Open(package), source, TimeProvider.System);
}
