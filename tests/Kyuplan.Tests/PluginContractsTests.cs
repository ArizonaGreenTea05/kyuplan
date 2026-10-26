using System.Globalization;
using System.Text.Json;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The contract a plugin is written against (<c>PluginDtos</c>, <see cref="PluginRules"/> in <c>Kyuplan.Contracts</c>): the shapes of ids, paths and versions, the manifest, the layouts of pages and settings
/// forms, and the rules for the values such a form sends. A package that breaks one of them is refused, so the plugins (Spond, ...) are built and tested against exactly these rules.
/// </summary>
public class PluginContractsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static IReadOnlyDictionary<string, JsonElement> Values(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => Json(v.Value));

    private static PluginUiElement Field(string id, string input = "text", string? label = null) => new PluginUiElement("field", Id: id, Label: label ?? id, Input: input);

    private static PluginUiLayout Layout(params PluginUiElement[] elements) => new(elements);

    private static PluginUiTogether Login(string message = "Needs both.") => new([["Email", "PhoneNumber"], ["Password"]], message);

    private static readonly PluginUiAction Post = new("POST", "jobs/refresh");

    // ----- the shapes of ids, names, paths and versions -----

    [Theory]
    [InlineData("spond", true)]
    [InlineData("my-plugin", true)]
    [InlineData("a1", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdef", true)] // 32
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", false)] // 33
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Spond", false)]
    [InlineData("1abc", false)]
    [InlineData("-abc", false)]
    [InlineData("my_plugin", false)]
    [InlineData("my plugin", false)]
    [InlineData("members", false)] // the name of a domain of the permissions
    [InlineData("settings", false)]
    [InlineData("api", false)]
    [InlineData("plugins", false)]
    public void A_plugin_id_is_lower_case_letters_digits_and_hyphens_and_no_domain_of_the_program(string? id, bool fine) => Assert.Equal(fine, PluginRules.IsPluginId(id));

    [Fact]
    public void No_domain_of_the_permissions_of_the_program_is_a_plugin_id()
    {
        Assert.All(Permission.Groups, group => Assert.False(PluginRules.IsPluginId(group.Name), group.Name));
    }

    [Theory]
    [InlineData("view", true)]
    [InlineData("editJobs", true)]
    [InlineData("a", true)]
    [InlineData("View", false)]
    [InlineData("1a", false)]
    [InlineData("edit-jobs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_name_is_letters_and_digits_starting_with_a_lower_case_letter(string? name, bool fine) => Assert.Equal(fine, PluginRules.IsName(name));

    [Theory]
    [InlineData("Email", true)]
    [InlineData("phone_2", true)]
    [InlineData("1a", false)]
    [InlineData("_a", false)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_id_of_a_field_is_a_key_of_the_configuration(string? key, bool fine) => Assert.Equal(fine, PluginRules.IsKey(key));

    [Fact]
    public void The_length_of_a_name_and_of_a_key_is_limited()
    {
        Assert.True(PluginRules.IsName("a" + new string('b', 31)));
        Assert.False(PluginRules.IsName("a" + new string('b', 32)));
        Assert.True(PluginRules.IsKey("A" + new string('b', 49)));
        Assert.False(PluginRules.IsKey("A" + new string('b', 50)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.2.0", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("12345.12345", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("123456", false)]
    [InlineData("v1.0", false)]
    [InlineData("1.", false)]
    [InlineData("1..2", false)]
    [InlineData("1.0-beta", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_version_is_dotted_numbers(string? version, bool fine) => Assert.Equal(fine, PluginRules.IsVersion(version));

    [Theory]
    [InlineData("spond", true)]
    [InlineData("demo", false)]
    [InlineData("custom", false)]
    [InlineData("manual", false)]
    [InlineData("Spond", false)]
    [InlineData(null, false)]
    public void A_data_source_id_is_not_one_of_the_built_in_sources(string? id, bool fine) => Assert.Equal(fine, PluginRules.IsSourceId(id));

    [Fact]
    public void The_built_in_sources_are_reserved()
    {
        Assert.Equal([DataSourceKinds.Demo, DataSourceKinds.Custom, DataSourceKinds.Manual], PluginRules.ReservedSourceIds);
    }

    [Fact]
    public void The_permission_of_a_plugin_starts_with_its_id()
    {
        Assert.Equal("spond.view", PluginRules.PermissionId("spond", "view"));
        Assert.Equal("Plugins:spond", PluginRules.DefaultSection("spond"));
    }

    [Theory]
    [InlineData("spond.view", true)]
    [InlineData("my-plugin.editJobs", true)]
    [InlineData("members.view", false)] // the program's own
    [InlineData("spond", false)]
    [InlineData(".view", false)]
    [InlineData("spond.", false)]
    [InlineData("spond.View", false)]
    [InlineData("spond.view.more", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_permission_of_a_plugin_is_its_id_and_a_name(string? permission, bool fine) => Assert.Equal(fine, PluginRules.IsPluginPermission(permission));

    [Fact]
    public void A_permission_of_a_plugin_is_at_most_70_characters()
    {
        var name = new string('b', 31);
        var fits = "spond." + "a" + name; // 38

        Assert.True(PluginRules.IsPluginPermission(fits));
        Assert.False(PluginRules.IsPluginPermission(fits + new string('x', 40)));
    }

    [Theory]
    [InlineData("plugin.json", true)]
    [InlineData("ui/settings.json", true)]
    [InlineData("a-b/c_d.dll", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../x", false)]
    [InlineData("a/../b", false)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a//b", false)]
    [InlineData("./a", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/.hidden", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    [InlineData("jobs/refresh?x=1", false)]
    [InlineData("ümlaut.json", false)]
    public void A_path_in_a_package_is_relative_and_stays_inside(string? path, bool fine)
    {
        Assert.Equal(fine, PluginRules.IsPackagePath(path));
        Assert.Equal(fine, PluginRules.IsEndpointPath(path));
    }

    [Fact]
    public void A_path_in_a_package_is_at_most_200_characters()
    {
        Assert.True(PluginRules.IsPackagePath(new string('a', 200)));
        Assert.False(PluginRules.IsPackagePath(new string('a', 201)));
    }

    [Theory]
    [InlineData("Spond", true)] // the settings that were there before Spond was a plugin
    [InlineData("Plugins:myplugin", true)]
    [InlineData("Plugins:my:deep", true)]
    [InlineData("Plugins", false)]
    [InlineData("Database", false)]
    [InlineData("Accounts", false)]
    [InlineData("Kestrel", false)]
    [InlineData("Accounts:Spond", false)]
    [InlineData("1a", false)]
    [InlineData("a:", false)]
    [InlineData("a::b", false)]
    [InlineData("a:b:c:d:e", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_section_of_the_configuration_is_not_one_of_the_program(string? section, bool fine) => Assert.Equal(fine, PluginRules.IsSection(section));

    [Theory]
    [InlineData("Hello", "Hallo", true, "Hallo")]
    [InlineData("Hello", "Hallo", false, "Hello")]
    [InlineData("Hello", null, true, "Hello")]
    [InlineData("Hello", "  ", true, "Hello")]
    [InlineData(null, null, true, "")]
    public void A_text_falls_back_to_english_without_a_german_one(string? english, string? german, bool isGerman, string expected) =>
        Assert.Equal(expected, PluginRules.Localized(english, german, isGerman));

    // ----- the manifest -----

    private static PluginManifest Manifest() => new(
        "spond", "Spond", "Spond", "1.2.0", "Reads the data from Spond.", null, "Someone", "https://github.com/someone/spond", "Kyuplan.Plugins.Spond.dll",
        [new PluginPermissionDef("view", "See it")],
        [new PluginPageDef("jobs", "Jobs", null, "chart", "view", "ui/jobs.json")],
        [new PluginDataSourceDef("spond", "Spond", null, null, null, "Spond", "ui/settings.json")]);

    [Fact]
    public void A_complete_manifest_is_fine()
    {
        Assert.Null(PluginRules.CheckManifest(Manifest()));
        Assert.Null(PluginRules.CheckManifest(new("acme", "Acme", null, "1", null, null, null, null, "Acme.dll")));
    }

    [Fact]
    public void A_manifest_is_refused_with_the_reason()
    {
        var page = Manifest().Pages![0];
        var source = Manifest().DataSources![0];
        var tooLong = new string('x', PluginRules.MaxLabelLength + 1);
        var cases = new (PluginManifest? Manifest, string Problem)[]
        {
            (null, "plugin.json is empty"),
            (Manifest() with { Id = "Spond" }, "id must be"),
            (Manifest() with { Id = "members" }, "id must be"),
            (Manifest() with { Name = " " }, "name is required"),
            (Manifest() with { Name = tooLong }, "name is required"),
            (Manifest() with { NameDe = tooLong }, "name is required"),
            (Manifest() with { Version = "v1" }, "version must be"),
            (Manifest() with { Description = new string('x', PluginRules.MaxTextLength + 1) }, "description is too long"),
            (Manifest() with { DescriptionDe = new string('x', PluginRules.MaxTextLength + 1) }, "description is too long"),
            (Manifest() with { Author = tooLong }, "author is too long"),
            (Manifest() with { Homepage = "http://github.com/someone/spond" }, "homepage must be an https"),
            (Manifest() with { Homepage = "not an address" }, "homepage must be an https"),
            (Manifest() with { Homepage = "https://example.org/" + new string('x', 300) }, "homepage must be an https"),
            (Manifest() with { Assembly = "Spond.exe" }, "assembly must be"),
            (Manifest() with { Assembly = "bin/Spond.dll" }, "assembly must be"),
            (Manifest() with { Assembly = "../Spond.dll" }, "assembly must be"),
            (Manifest() with { Permissions = [new("View", "x")] }, "a permission name must be"),
            (Manifest() with { Permissions = [new("view", " ")] }, "permission view: label is required"),
            (Manifest() with { Permissions = [new("view", "x"), new("view", "y")] }, "a permission is listed twice"),
            (Manifest() with { Permissions = [.. Enumerable.Range(0, PluginRules.MaxPermissions + 1).Select(i => new PluginPermissionDef("p" + i, "x"))] }, "at most 20 permissions"),
            (Manifest() with { Pages = [page with { Id = "Jobs" }] }, "a page id must be"),
            (Manifest() with { Pages = [page with { Title = "" }] }, "title is required"),
            (Manifest() with { Pages = [page with { Icon = "Not An Icon" }] }, "icon must be"),
            (Manifest() with { Pages = [page with { Layout = "ui/jobs.txt" }] }, "layout must be the path of a .json"),
            (Manifest() with { Pages = [page with { Layout = "../jobs.json" }] }, "layout must be the path of a .json"),
            (Manifest() with { Pages = [page with { Permission = "edit" }] }, "the permission 'edit' is not one of the plugin's"),
            (Manifest() with { Pages = [page, page] }, "a page id is used twice"),
            (Manifest() with { Pages = [.. Enumerable.Range(0, PluginRules.MaxPages + 1).Select(i => page with { Id = "p" + i })] }, "at most 20 pages"),
            (Manifest() with { DataSources = [source with { Id = "demo" }] }, "a data source id must be"),
            (Manifest() with { DataSources = [source with { Name = "" }] }, "name is required"),
            (Manifest() with { DataSources = [source with { Description = new string('x', PluginRules.MaxTextLength + 1) }] }, "description is too long"),
            (Manifest() with { DataSources = [source with { ConfigSection = "Database" }] }, "configSection must be"),
            (Manifest() with { DataSources = [source with { Settings = "ui/settings.txt" }] }, "settings must be the path of a .json"),
            (Manifest() with { DataSources = [source, source] }, "a data source id is used twice"),
            (Manifest() with { DataSources = [.. Enumerable.Range(0, PluginRules.MaxDataSources + 1).Select(i => source with { Id = "src" + i })] }, "at most 5 data sources"),
        };

        Assert.All(cases, c => Assert.Contains(c.Problem, PluginRules.CheckManifest(c.Manifest) ?? "(fine)"));
    }

    [Fact]
    public void A_page_without_a_permission_is_open_to_every_signed_in_user_and_a_data_source_needs_no_settings()
    {
        var manifest = Manifest() with
        {
            Pages = [new("jobs", "Jobs", null, null, null, "ui/jobs.json"), new("more", "More", null, "", "", "ui/more.json")],
            DataSources = [new("spond", "Spond", null, null, null, null, null)],
        };

        Assert.Null(PluginRules.CheckManifest(manifest));
    }

    [Fact]
    public void The_limits_are_the_ones_the_documentation_names()
    {
        Assert.Equal(".kyuplugin", PluginRules.PackageExtension);
        Assert.Equal("plugin.json", PluginRules.ManifestFile);
        Assert.Equal(["text", "password", "number", "checkbox", "select", "multiline"], PluginRules.Inputs);
        Assert.Equal(["POST", "PUT", "DELETE"], PluginRules.Methods);
    }

    [Fact]
    public void A_manifest_and_a_layout_are_read_from_the_json_of_a_package()
    {
        const string manifestJson = """
            {"id":"spond","name":"Spond","version":"1.0.0","assembly":"Kyuplan.Plugins.Spond.dll",
             "dataSources":[{"id":"spond","name":"Spond","configSection":"Spond","settings":"ui/settings.json"}]}
            """;
        const string layoutJson = """
            {"elements":[{"type":"field","id":"Email","label":"Email","labelDe":"E-Mail","input":"text","maxLength":254},
                         {"type":"field","id":"Password","label":"Password","input":"password",
                          "together":{"groups":[["Email"],["Password"]],"message":"Both.","messageDe":"Beides."}}]}
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, Web)!;
        var layout = JsonSerializer.Deserialize<PluginUiLayout>(layoutJson, Web)!;

        Assert.Null(PluginRules.CheckManifest(manifest));
        Assert.Equal("Spond", manifest.DataSources!.Single().ConfigSection);
        Assert.Null(PluginRules.CheckLayout(layout, PluginUiUse.DataSourceSettings));
        Assert.Equal("E-Mail", layout.Elements[0].LabelDe);
        Assert.Equal(254, layout.Elements[0].MaxLength);
        Assert.Equal("Beides.", layout.Elements[1].Together!.MessageDe);
        Assert.Equal(["Password"], PluginRules.SecretPaths(layout));
    }

    // ----- layouts -----

    [Fact]
    public void A_settings_form_with_fields_and_a_list_and_a_page_with_a_table_a_button_and_a_form_are_fine()
    {
        var settings = Layout(
            new PluginUiElement("heading", Text: "Login"),
            new PluginUiElement("text", Text: "Explains it."),
            Field("Email") with { Required = true, MaxLength = 254 },
            Field("PhoneNumber"),
            Field("Password", "password") with { Together = Login() },
            Field("Port", "number") with { Min = 1, Max = 65535, Default = "80" },
            Field("Mode", "select") with { Options = [new("a", "A"), new("b", "B", "B")], Default = "a" },
            Field("Enabled", "checkbox") with { Default = "true" },
            Field("Notes", "multiline"),
            new PluginUiElement("list", Id: "Accounts", Label: "Accounts", Max: 5, Elements: [Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = Login() }]));
        var page = Layout(
            new PluginUiElement("heading", Text: "Jobs"),
            new PluginUiElement("table", Label: "Jobs", Source: "jobs/list", Columns: [new("name", "Name"), new("state", "State", "Zustand")], Text: "None yet."),
            new PluginUiElement("button", Label: "Refresh", Action: Post with { Confirm = "Sure?", ConfirmDe = "Sicher?" }),
            new PluginUiElement("form", Label: "Add", Action: new("PUT", "jobs/add"), Elements: [new PluginUiElement("text", Text: "Name it."), Field("name") with { Required = true }]));

        Assert.Null(PluginRules.CheckLayout(settings, PluginUiUse.DataSourceSettings));
        Assert.Null(PluginRules.CheckLayout(page, PluginUiUse.Page));
        Assert.Null(PluginRules.CheckLayout(Layout(), PluginUiUse.Page)); // nothing to show is allowed
    }

    [Fact]
    public void A_layout_is_refused_with_the_reason()
    {
        var tooLong = new string('x', PluginRules.MaxTextLength + 1);
        var cases = new (PluginUiUse Use, PluginUiLayout? Layout, string Problem)[]
        {
            (PluginUiUse.Page, null, "elements is required"),
            (PluginUiUse.Page, Layout(Field("name")), "a field on a page must be inside a form"),
            (PluginUiUse.DataSourceSettings, Layout(Field("1x")), "id must be letters"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A"), Field("a")), "the id is used twice"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("field", Id: "A", Input: "text")), "label is required"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("field", Id: "A", Label: "A")), "input must be one of"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "color")), "input must be one of"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "select")), "a select needs 1 to 100 options"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "select") with { Options = [new("a", "A"), new("a", "B")] }), "an option value is used twice"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "select") with { Options = [new("", "A")] }), "an option needs a value and a label"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "select") with { Options = [new("a", " ")] }), "an option needs a value and a label"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A") with { Options = [new("a", "A")] }), "only a select has options"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "number") with { Min = 5, Max = 1 }), "min is larger than max"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A") with { MaxLength = 0 }), "maxLength must be between 1 and 2000"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A") with { MaxLength = 2001 }), "maxLength must be between 1 and 2000"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "number") with { Default = "abc" }), "default does not fit the field"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "checkbox") with { Default = "yes" }), "default does not fit the field"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", "select") with { Options = [new("a", "A")], Default = "z" }), "default does not fit the field"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A") with { MaxLength = 3, Default = "abcd" }), "default does not fit the field"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("heading")), "text is required"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("text", Text: "  ")), "text is required"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("text", Text: tooLong)), "a text is too long"),
            (PluginUiUse.DataSourceSettings, Layout(Field("A", label: new string('x', PluginRules.MaxLabelLength + 1))), "a text is too long"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("image")), "type 'image' is not known"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("heading", Text: "x", Together: Login())), "together is only for fields"),
            (PluginUiUse.Page, Layout(new PluginUiElement("list", Id: "L", Label: "L", Elements: [Field("A")])), "a list is only for the settings form"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Elements: [new PluginUiElement("list", Id: "M", Label: "M", Elements: [Field("A")])])), "a row of a list only holds fields"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L")), "a list needs the fields of a row"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Elements: [])), "a list needs the fields of a row"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Max: 51, Elements: [Field("A")])), "max must be between 1 and 50"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Max: 0, Elements: [Field("A")])), "max must be between 1 and 50"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Label: "L", Elements: [Field("A")])), "id must be letters"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Elements: [Field("A")])), "label is required"),
            (PluginUiUse.DataSourceSettings, Layout(Field("L"), new PluginUiElement("list", Id: "l", Label: "L", Elements: [Field("A")])), "the id is used twice"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Elements: [Field("A", "multiline")])), "cannot hold a multiline field"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("list", Id: "L", Label: "L", Elements: [Field("A"), Field("a")])), "the id is used twice"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("table", Source: "jobs/list", Columns: [new("a", "A")])), "a table is only for pages"),
            (PluginUiUse.Page, Layout(new PluginUiElement("table", Source: "../jobs", Columns: [new("a", "A")])), "source must be the path of an endpoint"),
            (PluginUiUse.Page, Layout(new PluginUiElement("table", Source: "jobs/list")), "a table needs 1 to 12 columns"),
            (PluginUiUse.Page, Layout(new PluginUiElement("table", Source: "jobs/list", Columns: [.. Enumerable.Range(0, 13).Select(i => new PluginUiColumn("c" + i, "C"))])), "a table needs 1 to 12 columns"),
            (PluginUiUse.Page, Layout(new PluginUiElement("table", Source: "jobs/list", Columns: [new("1a", "A")])), "a column needs a key and a label"),
            (PluginUiUse.Page, Layout(new PluginUiElement("table", Source: "jobs/list", Columns: [new("a", " ")])), "a column needs a key and a label"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("button", Label: "Go", Action: Post)), "a button is only for pages"),
            (PluginUiUse.Page, Layout(new PluginUiElement("button", Action: Post)), "label is required"),
            (PluginUiUse.Page, Layout(new PluginUiElement("button", Label: "Go")), "action is required"),
            (PluginUiUse.Page, Layout(new PluginUiElement("button", Label: "Go", Action: new("GET", "jobs/refresh"))), "action.method must be one of"),
            (PluginUiUse.Page, Layout(new PluginUiElement("button", Label: "Go", Action: new("POST", "a/../b"))), "action.path must be the path of an endpoint"),
            (PluginUiUse.Page, Layout(new PluginUiElement("button", Label: "Go", Action: Post with { Confirm = new string('x', PluginRules.MaxLabelLength + 1) })), "action.confirm is too long"),
            (PluginUiUse.DataSourceSettings, Layout(new PluginUiElement("form", Label: "Go", Action: Post, Elements: [Field("A")])), "a form is only for pages"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Action: Post, Elements: [Field("A")])), "label is required"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Label: "Go", Action: Post)), "a form needs fields"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Label: "Go", Action: Post, Elements: [new PluginUiElement("heading", Text: "x")])), "a form only holds fields and texts"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Label: "Go", Elements: [Field("A")])), "action is required"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Label: "Go", Action: Post, Elements: [Field("A"), Field("a")])), "the id is used twice"),
            (PluginUiUse.Page, Layout(new PluginUiElement("form", Label: "Go", Action: Post, Elements: [Field("A", "multiline")])), null!), // a form may hold a multiline field
            (PluginUiUse.Page, Layout([.. Enumerable.Range(0, PluginRules.MaxElements + 1).Select(i => new PluginUiElement("text", Text: "t"))]), "at most 200 elements"),
        };

        Assert.All(cases, c =>
        {
            var problem = PluginRules.CheckLayout(c.Layout, c.Use);
            if (c.Problem is null) Assert.Null(problem);
            else Assert.Contains(c.Problem, problem ?? "(fine)");
        });
    }

    [Fact]
    public void The_problem_names_the_element_it_is_about()
    {
        Assert.StartsWith("Port: ", PluginRules.CheckLayout(Layout(Field("Name"), Field("Port", "number") with { Min = 2, Max = 1 }), PluginUiUse.DataSourceSettings));
        Assert.StartsWith("heading: ", PluginRules.CheckLayout(Layout(new PluginUiElement("heading")), PluginUiUse.Page));
    }

    [Fact]
    public void The_rows_of_two_lists_can_use_the_same_field_ids()
    {
        var row = new PluginUiElement[] { Field("Email") };

        Assert.Null(PluginRules.CheckLayout(Layout(new PluginUiElement("list", Id: "A", Label: "A", Elements: row), new PluginUiElement("list", Id: "B", Label: "B", Elements: row)), PluginUiUse.DataSourceSettings));
    }

    [Fact]
    public void A_rule_between_fields_has_to_name_fields_next_to_it()
    {
        var cases = new (PluginUiElement[] Elements, string Problem)[]
        {
            ([Field("Email"), Field("Password", "password") with { Together = Login() }], "together names 'PhoneNumber', which is no field next to it"),
            ([Field("Email"), Field("PhoneNumber", "checkbox"), Field("Password", "password") with { Together = Login() }], "which is no field next to it (a checkbox does not count)"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = new([["Email"]], "x") }], "together needs 2 to 5 groups"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = new([["Email"], []], "x") }], "a group of together needs 1 to 5 field ids"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = new([["Email"], ["bad id"]], "x") }], "a group of together needs 1 to 5 field ids"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = new([["Email"], ["EMAIL", "PhoneNumber"]], "x") }], "a field is in two groups of together"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = Login(" ") }], "together needs a message"),
            ([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = Login(new string('x', PluginRules.MaxLabelLength + 1)) }], "together needs a message"),
            ([Field("A"), Field("B"), Field("C"), Field("D"), Field("E"), Field("F"), Field("G") with { Together = new([["A"], ["B"], ["C"], ["D"], ["E"], ["F"]], "x") }], "together needs 2 to 5 groups"),
        };

        Assert.All(cases, c => Assert.Contains(c.Problem, PluginRules.CheckLayout(Layout(c.Elements), PluginUiUse.DataSourceSettings) ?? "(fine)"));

        // The fields may come after the rule that names them.
        Assert.Null(PluginRules.CheckLayout(Layout(Field("Password", "password") with { Together = Login() }, Field("Email"), Field("PhoneNumber")), PluginUiUse.DataSourceSettings));
    }

    // ----- passwords and the identity of a row -----

    [Fact]
    public void The_passwords_of_a_form_are_its_password_fields_and_those_of_the_rows_of_its_lists()
    {
        var form = Layout(
            Field("Email"),
            Field("Password", "password"),
            new PluginUiElement("list", Id: "Accounts", Label: "A", Elements: [Field("Email"), Field("Password", "password"), Field("Token", "password")]),
            new PluginUiElement("list", Id: "Other", Label: "O", Elements: [Field("Name")]));

        Assert.Equal(["Password", "Accounts/*/Password", "Accounts/*/Token"], PluginRules.SecretPaths(form));
        Assert.Empty(PluginRules.SecretPaths(Layout()));
        Assert.Empty(PluginRules.SecretPaths(Layout(new PluginUiElement("heading", Text: "x"), Field("Email"))));
    }

    [Fact]
    public void The_identity_of_a_row_is_the_login_its_password_is_tied_to()
    {
        Assert.Equal(["Email", "PhoneNumber"], PluginRules.RowIdentity([Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = Login() }]));
        Assert.Empty(PluginRules.RowIdentity([Field("Email"), Field("Password", "password")])); // no rule: the password keeps its place
        Assert.Empty(PluginRules.RowIdentity([Field("Email"), Field("Notes") with { Together = Login() }])); // only a password has an identity
        Assert.Empty(PluginRules.RowIdentity([]));
        Assert.Equal(["Email"], PluginRules.RowIdentity([Field("Password", "password") with { Together = new([["Email"], ["Password"]], "x") }]));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_rule_between_fields_holds_when_every_group_or_none_has_a_value(bool first, bool second, bool holds)
    {
        var rule = new PluginUiTogether([["a"], ["b"]], "x");

        Assert.Equal(holds, PluginRules.TogetherHolds(rule, id => id == "a" ? first : second));
    }

    [Fact]
    public void A_group_has_a_value_when_any_of_its_fields_has_one()
    {
        var rule = new PluginUiTogether([["a", "b"], ["c"]], "x");

        Assert.True(PluginRules.TogetherHolds(rule, id => id is "b" or "c"));
        Assert.False(PluginRules.TogetherHolds(rule, id => id == "a"));
    }

    // ----- the values of a form -----

    [Theory]
    [InlineData("text", "\"hello\"", null)]
    [InlineData("text", "\"\"", null)]
    [InlineData("text", "null", null)]
    [InlineData("text", "5", "must be text.")]
    [InlineData("text", "true", "must be text.")]
    [InlineData("text", "\"two\\nlines\"", "must be one line of text.")]
    [InlineData("text", "\"tab\\there\"", "must be one line of text.")]
    [InlineData("multiline", "\"two\\nlines\"", null)]
    [InlineData("password", "\"secret\"", null)]
    [InlineData("checkbox", "true", null)]
    [InlineData("checkbox", "false", null)]
    [InlineData("checkbox", "null", null)]
    [InlineData("checkbox", "\"true\"", "must be true or false.")]
    [InlineData("number", "5", null)]
    [InlineData("number", "\"5\"", null)]
    [InlineData("number", "\"\"", null)]
    [InlineData("number", "\"1.5\"", null)]
    [InlineData("number", "\"abc\"", "must be a number.")]
    [InlineData("number", "true", "must be a number.")]
    [InlineData("number", "0", "must be at least 1.")]
    [InlineData("number", "100", null)]
    [InlineData("number", "101", "must be at most 100.")]
    [InlineData("select", "\"a\"", null)]
    [InlineData("select", "\"\"", null)]
    [InlineData("select", "\"z\"", "is not one of the options.")]
    [InlineData("select", "\"A\"", "is not one of the options.")] // the value is compared as it is
    [InlineData("select", "1", "is not one of the options.")]
    public void The_value_of_a_field_has_to_fit_its_input(string input, string json, string? problem)
    {
        var field = Field("F", input) with { Min = 1, Max = 100, Options = input == "select" ? [new("a", "A"), new("b", "B")] : null };

        Assert.Equal(problem, PluginRules.CheckValue(field, JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void A_missing_value_fits_every_input()
    {
        Assert.All(PluginRules.Inputs, input => Assert.Null(PluginRules.CheckValue(Field("F", input) with { Options = [new("a", "A")] }, default)));
    }

    [Theory]
    [InlineData("text", 200, true)]
    [InlineData("text", 201, false)]
    [InlineData("multiline", 2000, true)]
    [InlineData("multiline", 2001, false)]
    public void Text_is_limited_unless_the_field_says_otherwise(string input, int length, bool fits)
    {
        var problem = PluginRules.CheckValue(Field("F", input), Json(new string('x', length)));

        Assert.Equal(fits, problem is null);
    }

    [Fact]
    public void The_limit_of_a_field_wins_and_counts_characters_not_bytes()
    {
        var field = Field("F") with { MaxLength = 5 };

        Assert.Null(PluginRules.CheckValue(field, Json("äöüßé"))); // five characters, ten bytes
        Assert.Equal("must be at most 5 characters.", PluginRules.CheckValue(field, Json("äöüßéx")));
        Assert.Null(PluginRules.CheckValue(Field("F") with { MaxLength = 500 }, Json(new string('x', 500))));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("-2.5", -2.5)]
    [InlineData("\"1.5\"", 1.5)]
    [InlineData("\" 3 \"", 3)]
    [InlineData("\"1e3\"", 1000)]
    public void A_number_is_a_json_number_or_text_in_the_invariant_culture(string json, double expected)
    {
        Assert.True(PluginRules.TryNumber(JsonDocument.Parse(json).RootElement, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("\"1,5\"")] // a German decimal comma is not a number of the form
    [InlineData("\"1,000\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[1]")]
    public void Something_else_is_no_number(string json)
    {
        Assert.False(PluginRules.TryNumber(JsonDocument.Parse(json).RootElement, out _));
    }

    [Fact]
    public void A_number_does_not_depend_on_the_culture_of_the_computer()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.True(PluginRules.TryNumber(Json("1.5"), out var number));
            Assert.Equal(1.5, number);
            Assert.False(PluginRules.TryNumber(Json("1,5"), out _));
            Assert.Null(PluginRules.CheckValue(Field("N", "number") with { Min = 1, Max = 2 }, Json("1.5")));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_required_field_needs_a_value_but_a_stored_password_is_one()
    {
        var required = Field("F") with { Required = true };
        var password = Field("P", "password") with { Required = true };

        Assert.Equal("is required.", PluginRules.CheckField(required, default, false));
        Assert.Equal("is required.", PluginRules.CheckField(required, Json(""), false));
        Assert.Equal("is required.", PluginRules.CheckField(required, Json("   "), false));
        Assert.Equal("is required.", PluginRules.CheckField(required, Json(null), false));
        Assert.Null(PluginRules.CheckField(required, Json("x"), false));
        Assert.Equal("is required.", PluginRules.CheckField(required, default, true)); // only a password can be kept
        Assert.Null(PluginRules.CheckField(password, Json(""), true));
        Assert.Equal("is required.", PluginRules.CheckField(password, Json(""), false));
        Assert.Null(PluginRules.CheckField(Field("C", "checkbox") with { Required = true }, Json(false), false)); // a checkbox is never missing
    }

    [Fact]
    public void The_values_of_a_form_are_checked_field_by_field_and_the_first_problem_names_the_field()
    {
        var form = Layout(
            Field("Name") with { Required = true },
            Field("Port", "number") with { Min = 1, Max = 65535 },
            Field("Mode", "select") with { Options = [new("a", "A")] });

        Assert.Null(PluginRules.CheckValues(form, Values(("Name", "x"), ("Port", 80), ("Mode", "a")), _ => false));
        Assert.Null(PluginRules.CheckValues(form, Values(("Name", "x"), ("Unknown", "ignored")), _ => false));
        Assert.Equal("Name: is required.", PluginRules.CheckValues(form, Values(), _ => false));
        Assert.Equal("Port: must be at most 65535.", PluginRules.CheckValues(form, Values(("Name", "x"), ("Port", 70000)), _ => false));
        Assert.Equal("Mode: is not one of the options.", PluginRules.CheckValues(form, Values(("Name", "x"), ("Mode", "b")), _ => false));
    }

    private static readonly PluginUiLayout Accounts = Layout(
        new PluginUiElement("list", Id: "Accounts", Label: "Accounts", Max: 2, Elements:
        [
            Field("Email") with { MaxLength = 20 },
            Field("PhoneNumber"),
            Field("Password", "password") with { Together = Login("Row needs both.") },
        ]));

    [Fact]
    public void The_rows_of_a_list_are_checked_and_the_problem_names_the_row()
    {
        Assert.Null(PluginRules.CheckValues(Accounts, Values(), _ => false));
        Assert.Null(PluginRules.CheckValues(Accounts, Values(("Accounts", null)), _ => false));
        Assert.Null(PluginRules.CheckValues(Accounts, Values(("Accounts", Array.Empty<object>())), _ => false));
        Assert.Null(PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Email = "a@b.c", Password = "pw" }, new { PhoneNumber = "1", Password = "pw" } })), _ => false));

        Assert.Equal("Accounts: must be a list.", PluginRules.CheckValues(Accounts, Values(("Accounts", "x")), _ => false));
        Assert.Equal("Accounts: at most 2 rows.", PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { }, new { }, new { } })), _ => false));
        Assert.Equal("Accounts: a row must be an object.", PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { "x" })), _ => false));
        Assert.Equal("Accounts[2].Email: must be at most 20 characters.",
            PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Email = "a@b.c", Password = "pw" }, new { Email = new string('x', 21), Password = "pw" } })), _ => false));
        Assert.Equal("Accounts[1].Password: Row needs both.", PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Email = "a@b.c" } })), _ => false));
        Assert.Equal("Accounts[1].Password: Row needs both.", PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Password = "pw" } })), _ => false));
    }

    [Fact]
    public void A_row_that_is_kept_uses_the_stored_password_of_its_old_place()
    {
        var kept = Values(("Accounts", new object[] { new { Email = "a@b.c", Password = "", _keep = 1 } }));

        Assert.Null(PluginRules.CheckValues(Accounts, kept, path => path == "Accounts/1/Password"));
        Assert.NotNull(PluginRules.CheckValues(Accounts, kept, path => path == "Accounts/0/Password")); // another row's password does not count
        Assert.NotNull(PluginRules.CheckValues(Accounts, kept, _ => false));
        Assert.NotNull(PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Email = "a@b.c", Password = "", _keep = -1 } })), _ => true)); // no place
        Assert.NotNull(PluginRules.CheckValues(Accounts, Values(("Accounts", new object[] { new { Email = "a@b.c", Password = "", _keep = "1" } })), _ => true)); // not a number
        Assert.Equal("_keep", PluginRules.KeepProperty);
    }

    [Fact]
    public void A_rule_between_fields_of_the_form_counts_a_stored_password_and_ignores_blank_text()
    {
        var form = Layout(Field("Email"), Field("PhoneNumber"), Field("Password", "password") with { Together = Login("Both.") });

        Assert.Null(PluginRules.CheckValues(form, Values(), _ => false));
        Assert.Null(PluginRules.CheckValues(form, Values(("Email", "  "), ("Password", "")), _ => false));
        Assert.Null(PluginRules.CheckValues(form, Values(("Email", "a@b.c"), ("Password", "")), path => path == "Password"));
        Assert.Equal("Password: Both.", PluginRules.CheckValues(form, Values(("Email", "a@b.c")), _ => false));
        Assert.Equal("Password: Both.", PluginRules.CheckValues(form, Values(("Password", "pw")), _ => false));
        Assert.Equal("Password: Both.", PluginRules.CheckValues(form, Values(("Email", "  "), ("Password", "pw")), _ => false)); // blank is no login
        Assert.Null(PluginRules.CheckValues(form, Values(("PhoneNumber", "+491701234567"), ("Password", "pw")), _ => false));
    }
}
