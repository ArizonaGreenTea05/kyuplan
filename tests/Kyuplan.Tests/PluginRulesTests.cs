using System.Text.Json;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>What a plugin has to look like: ids, the manifest, layouts, the values typed into a form, and how a permission of a plugin is told from the program's own.</summary>
public class PluginRulesTests
{
    private static PluginManifest Manifest(Func<PluginManifest, PluginManifest>? change = null)
    {
        var manifest = PluginTestKit.AcmeManifest;
        return change?.Invoke(manifest) ?? manifest;
    }

    // --- Ids -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("acme", true)]
    [InlineData("my-plugin2", true)]
    [InlineData("ab", true)]
    [InlineData("a", false)]
    [InlineData("1abc", false)]
    [InlineData("Acme", false)]
    [InlineData("has space", false)]
    [InlineData("under_score", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("members", false)] // a permission domain of the program: members.view must stay the program's own
    [InlineData("settings", false)]
    [InlineData("plugins", false)]
    [InlineData("api", false)]
    public void A_plugin_id_is_lower_case_letters_digits_and_hyphens_and_is_no_domain_of_the_program(string? id, bool valid) =>
        Assert.Equal(valid, PluginRules.IsPluginId(id));

    [Fact]
    public void A_plugin_id_of_more_than_32_characters_is_refused() =>
        Assert.False(PluginRules.IsPluginId(new string('a', 33)));

    [Theory]
    [InlineData("spond", true)]
    [InlineData("demo", false)]
    [InlineData("custom", false)]
    [InlineData("manual", false)]
    [InlineData("Spond", false)]
    public void A_data_source_id_is_none_of_the_programs_own(string id, bool valid) => Assert.Equal(valid, PluginRules.IsSourceId(id));

    [Theory]
    [InlineData("acme.view", true)]
    [InlineData("my-plugin.editJobs", true)]
    [InlineData("members.view", false)] // the program's own
    [InlineData("acme", false)]
    [InlineData("acme.", false)]
    [InlineData("acme.View", false)]
    [InlineData(".view", false)]
    [InlineData("acme.view.more", false)]
    [InlineData(null, false)]
    public void A_permission_of_a_plugin_has_the_shape_of_its_id_and_a_name(string? permission, bool valid) =>
        Assert.Equal(valid, PluginRules.IsPluginPermission(permission));

    [Fact]
    public void The_permissions_of_plugins_are_known_to_roles_whether_or_not_the_plugin_is_installed()
    {
        Assert.True(Permission.IsKnown("acme.view"));
        Assert.True(Permission.IsKnown(Permission.MembersView));
        Assert.False(Permission.IsKnown("nonsense"));
        Assert.Null(RoleRules.CheckPermissions(["acme.view", Permission.ExamsView]));
        Assert.NotNull(RoleRules.CheckPermissions(["not a permission"]));
    }

    [Fact]
    public void The_catalogue_order_keeps_the_programs_permissions_first_and_the_plugins_after_them_and_drops_the_rest()
    {
        var ordered = Permission.InCatalogueOrder(["zeta.view", Permission.MembersView, "alpha.edit", "rubbish", Permission.DashboardView]);

        Assert.Equal([Permission.DashboardView, Permission.MembersView, "alpha.edit", "zeta.view"], ordered);
    }

    [Theory]
    [InlineData("Spond", true)]
    [InlineData("Plugins:acme", true)]
    [InlineData("Plugins:acme:Sub", true)]
    [InlineData("Plugins", false)]
    [InlineData("Database", false)]
    [InlineData("Accounts:X", false)]
    [InlineData("DataSource", false)]
    [InlineData("Setup", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void A_data_source_keeps_its_settings_in_a_section_of_its_own_and_none_of_the_programs(string section, bool valid) =>
        Assert.Equal(valid, PluginRules.IsSection(section));

    [Theory]
    [InlineData("ui/settings.json", true)]
    [InlineData("plugin.json", true)]
    [InlineData("a/b/c.json", true)]
    [InlineData("../x.json", false)]
    [InlineData("/x.json", false)]
    [InlineData("a/../x.json", false)]
    [InlineData(".hidden/x.json", false)]
    [InlineData("a//b.json", false)]
    [InlineData("a\\b.json", false)]
    [InlineData("a b.json", false)]
    [InlineData("", false)]
    public void A_path_in_a_package_is_relative_and_does_not_leave_the_folder(string path, bool valid) =>
        Assert.Equal(valid, PluginRules.IsPackagePath(path));

    // --- The manifest ------------------------------------------------------------------------------------------

    [Fact]
    public void The_manifests_of_the_test_plugins_are_fine()
    {
        Assert.Null(PluginRules.CheckManifest(PluginTestKit.AcmeManifest));
        Assert.Null(PluginRules.CheckManifest(PluginTestKit.SpondManifest));
        Assert.Null(PluginRules.CheckManifest(PluginTestKit.BrokenManifest));
    }

    private static object[] Case(Func<PluginManifest, PluginManifest> change, string reason) => [change, reason];

    public static IEnumerable<object[]> BadManifests() =>
    [
        Case(m => m with { Id = "Bad Id" }, "id"),
        Case(m => m with { Id = "members" }, "id"),
        Case(m => m with { Name = " " }, "name"),
        Case(m => m with { Name = new string('x', 201) }, "name"),
        Case(m => m with { Version = "one" }, "version"),
        Case(m => m with { Version = "1.2.3.4.5" }, "version"),
        Case(m => m with { Description = new string('x', 2001) }, "description"),
        Case(m => m with { Homepage = "http://insecure.example.org" }, "homepage"),
        Case(m => m with { Assembly = "sub/Acme.dll" }, "assembly"),
        Case(m => m with { Assembly = "Acme.exe" }, "assembly"),
        Case(m => m with { Permissions = [new PluginPermissionDef("View", "x")] }, "permission"),
        Case(m => m with { Permissions = [new PluginPermissionDef("view", "")] }, "label"),
        Case(m => m with { Permissions = [new PluginPermissionDef("view", "x"), new PluginPermissionDef("view", "y")] }, "twice"),
        Case(m => m with { Permissions = Enumerable.Range(0, 21).Select(i => new PluginPermissionDef($"p{i}", "x")).ToList() }, "at most"),
        Case(m => m with { Pages = [new PluginPageDef("items", "T", null, null, "missing", "ui/a.json")] }, "not one of the plugin's permissions"),
        Case(m => m with { Pages = [new PluginPageDef("Bad", "T", null, null, null, "ui/a.json")] }, "page id"),
        Case(m => m with { Pages = [new PluginPageDef("a", "", null, null, null, "ui/a.json")] }, "title"),
        Case(m => m with { Pages = [new PluginPageDef("a", "T", null, "Bad Icon", null, "ui/a.json")] }, "icon"),
        Case(m => m with { Pages = [new PluginPageDef("a", "T", null, null, null, "ui/a.txt")] }, "layout"),
        Case(m => m with { Pages = [new PluginPageDef("a", "T", null, null, null, "../a.json")] }, "layout"),
        Case(m => m with { Pages = [new PluginPageDef("a", "T", null, null, null, "ui/a.json"), new PluginPageDef("a", "U", null, null, null, "ui/b.json")] }, "twice"),
        Case(m => m with { DataSources = [new PluginDataSourceDef("demo", "x", null, null, null, null, null)] }, "data source id"),
        Case(m => m with { DataSources = [new PluginDataSourceDef("ok", "", null, null, null, null, null)] }, "name"),
        Case(m => m with { DataSources = [new PluginDataSourceDef("ok", "x", null, null, null, "Database", null)] }, "configSection"),
        Case(m => m with { DataSources = [new PluginDataSourceDef("ok", "x", null, null, null, null, "ui/s.txt")] }, "settings"),
        Case(m => m with { DataSources = [new PluginDataSourceDef("ok", "x", null, null, null, null, null), new PluginDataSourceDef("ok", "y", null, null, null, null, null)] }, "twice"),
    ];

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void A_manifest_that_breaks_a_rule_is_refused_with_the_reason(Func<PluginManifest, PluginManifest> change, string reason)
    {
        var problem = PluginRules.CheckManifest(change(Manifest()));

        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_manifest_at_all_is_a_problem() => Assert.NotNull(PluginRules.CheckManifest(null));

    // --- Layouts ----------------------------------------------------------------------------------------------

    private static string? Check(string json, PluginUiUse use) => PluginRules.CheckLayout(PluginTestKit.Layout(json), use);

    [Fact]
    public void The_layouts_of_the_test_plugins_are_fine()
    {
        Assert.Null(Check(PluginTestKit.AcmeForm, PluginUiUse.DataSourceSettings));
        Assert.Null(Check(PluginTestKit.SpondForm, PluginUiUse.DataSourceSettings));
        Assert.Null(Check(PluginTestKit.AcmePage, PluginUiUse.Page));
        Assert.Null(Check(PluginTestKit.AboutPage, PluginUiUse.Page));
    }

    [Theory]
    [InlineData("""{ "elements": [ { "type": "nonsense" } ] }""", PluginUiUse.Page, "not known")]
    [InlineData("""{ "elements": [ { "type": "text" } ] }""", PluginUiUse.Page, "text is required")]
    [InlineData("""{ "elements": [ { "type": "heading", "text": " " } ] }""", PluginUiUse.Page, "text is required")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text" } ] }""", PluginUiUse.Page, "inside a form")]
    [InlineData("""{ "elements": [ { "type": "field", "label": "A", "input": "text" } ] }""", PluginUiUse.DataSourceSettings, "id must be")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "1A", "label": "A", "input": "text" } ] }""", PluginUiUse.DataSourceSettings, "id must be")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text" }, { "type": "field", "id": "a", "label": "B", "input": "text" } ] }""", PluginUiUse.DataSourceSettings, "twice")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "input": "text" } ] }""", PluginUiUse.DataSourceSettings, "label is required")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "colour" } ] }""", PluginUiUse.DataSourceSettings, "input must be")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "select" } ] }""", PluginUiUse.DataSourceSettings, "options")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "select", "options": [ { "value": "x", "label": "X" }, { "value": "x", "label": "Y" } ] } ] }""", PluginUiUse.DataSourceSettings, "twice")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "select", "options": [ { "value": "", "label": "X" } ] } ] }""", PluginUiUse.DataSourceSettings, "option needs")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text", "options": [ { "value": "x", "label": "X" } ] } ] }""", PluginUiUse.DataSourceSettings, "only a select")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "number", "min": 5, "max": 1 } ] }""", PluginUiUse.DataSourceSettings, "min is larger")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text", "maxLength": 0 } ] }""", PluginUiUse.DataSourceSettings, "maxLength")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "number", "default": "abc" } ] }""", PluginUiUse.DataSourceSettings, "default")]
    [InlineData("""{ "elements": [ { "type": "field", "id": "A", "label": "A", "input": "checkbox", "default": "maybe" } ] }""", PluginUiUse.DataSourceSettings, "default")]
    [InlineData("""{ "elements": [ { "type": "list", "id": "L", "label": "L", "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text" } ] } ] }""", PluginUiUse.Page, "only for the settings form")]
    [InlineData("""{ "elements": [ { "type": "list", "id": "L", "label": "L", "max": 100, "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text" } ] } ] }""", PluginUiUse.DataSourceSettings, "max must be")]
    [InlineData("""{ "elements": [ { "type": "list", "id": "L", "label": "L" } ] }""", PluginUiUse.DataSourceSettings, "fields of a row")]
    [InlineData("""{ "elements": [ { "type": "list", "id": "L", "label": "L", "elements": [ { "type": "text", "text": "x" } ] } ] }""", PluginUiUse.DataSourceSettings, "only holds fields")]
    [InlineData("""{ "elements": [ { "type": "list", "id": "L", "label": "L", "elements": [ { "type": "field", "id": "A", "label": "A", "input": "multiline" } ] } ] }""", PluginUiUse.DataSourceSettings, "multiline")]
    [InlineData("""{ "elements": [ { "type": "table", "source": "x", "columns": [ { "key": "a", "label": "A" } ] } ] }""", PluginUiUse.DataSourceSettings, "only for pages")]
    [InlineData("""{ "elements": [ { "type": "table", "source": "../x", "columns": [ { "key": "a", "label": "A" } ] } ] }""", PluginUiUse.Page, "source must be")]
    [InlineData("""{ "elements": [ { "type": "table", "source": "x" } ] }""", PluginUiUse.Page, "columns")]
    [InlineData("""{ "elements": [ { "type": "table", "source": "x", "columns": [ { "key": "1", "label": "A" } ] } ] }""", PluginUiUse.Page, "column needs")]
    [InlineData("""{ "elements": [ { "type": "button", "label": "B" } ] }""", PluginUiUse.Page, "action is required")]
    [InlineData("""{ "elements": [ { "type": "button", "label": "B", "action": { "method": "GET", "path": "x" } } ] }""", PluginUiUse.Page, "method")]
    [InlineData("""{ "elements": [ { "type": "button", "label": "B", "action": { "method": "POST", "path": "https://evil.example" } } ] }""", PluginUiUse.Page, "path")]
    [InlineData("""{ "elements": [ { "type": "button", "action": { "method": "POST", "path": "x" } } ] }""", PluginUiUse.Page, "label is required")]
    [InlineData("""{ "elements": [ { "type": "button", "label": "B", "action": { "method": "POST", "path": "x" } } ] }""", PluginUiUse.DataSourceSettings, "only for pages")]
    [InlineData("""{ "elements": [ { "type": "form", "label": "F", "action": { "method": "POST", "path": "x" } } ] }""", PluginUiUse.Page, "needs fields")]
    [InlineData("""{ "elements": [ { "type": "form", "label": "F", "action": { "method": "POST", "path": "x" }, "elements": [ { "type": "button", "label": "B", "action": { "method": "POST", "path": "x" } } ] } ] }""", PluginUiUse.Page, "only holds fields")]
    [InlineData("""{ "elements": [ { "type": "form", "label": "F", "action": { "method": "POST", "path": "x" }, "elements": [ { "type": "form", "label": "G", "action": { "method": "POST", "path": "x" }, "elements": [ { "type": "text", "text": "x" } ] } ] } ] }""", PluginUiUse.Page, "only holds fields")]
    public void A_layout_that_breaks_a_rule_is_refused_with_the_reason(string json, PluginUiUse use, string reason)
    {
        var problem = Check(json, use);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_layout_without_elements_is_a_problem() => Assert.NotNull(PluginRules.CheckLayout(new PluginUiLayout(null!), PluginUiUse.Page));

    [Fact]
    public void A_layout_with_too_many_elements_or_nested_too_deeply_is_refused()
    {
        var many = new PluginUiLayout([.. Enumerable.Range(0, PluginRules.MaxElements + 1).Select(_ => new PluginUiElement("text", Text: "x"))]);
        Assert.Contains("at most", PluginRules.CheckLayout(many, PluginUiUse.Page));

        var field = new PluginUiElement("field", "A", Label: "A", Input: "text");
        var deep = new PluginUiLayout([new("list", "L", Label: "L", Elements: [new("list", "M", Label: "M", Elements: [field])])]);
        Assert.Contains("list", PluginRules.CheckLayout(deep, PluginUiUse.DataSourceSettings));
    }

    [Fact]
    public void A_text_that_is_too_long_is_refused()
    {
        var layout = new PluginUiLayout([new PluginUiElement("text", Text: new string('x', PluginRules.MaxTextLength + 1))]);

        Assert.Contains("too long", PluginRules.CheckLayout(layout, PluginUiUse.Page));
    }

    // --- The password fields of a form -----------------------------------------------------------------------

    [Fact]
    public void The_secret_places_of_a_form_are_its_password_fields_and_those_in_its_lists()
    {
        Assert.Equal(["Password", "Accounts/*/Password"], PluginRules.SecretPaths(PluginTestKit.Layout(PluginTestKit.SpondForm)));
        Assert.Equal(["ApiKey", "Rows/*/Token"], PluginRules.SecretPaths(PluginTestKit.Layout(PluginTestKit.AcmeForm)));
    }

    // --- Values ------------------------------------------------------------------------------------------------

    private static PluginUiElement Field(string input, bool required = false, int? min = null, int? max = null, int? maxLength = null, params string[] options) =>
        new("field", "F", Label: "F", Input: input, Required: required, Min: min, Max: max, MaxLength: maxLength,
            Options: options.Length == 0 ? null : [.. options.Select(o => new PluginUiOption(o, o))]);

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("text", "hello", true)]
    [InlineData("text", "", true)] // empty is allowed unless the field is required
    [InlineData("text", 5, false)]
    [InlineData("text", "two\nlines", false)]
    [InlineData("multiline", "two\nlines", true)]
    [InlineData("password", "secret", true)]
    [InlineData("number", 3, true)]
    [InlineData("number", "3.5", true)]
    [InlineData("number", "abc", false)]
    [InlineData("number", true, false)]
    [InlineData("checkbox", true, true)]
    [InlineData("checkbox", false, true)]
    [InlineData("checkbox", "yes", false)]
    [InlineData("select", "a", true)]
    [InlineData("select", "z", false)]
    [InlineData("select", 1, false)]
    public void A_value_is_checked_by_the_input_kind_of_its_field(string input, object value, bool fine) =>
        Assert.Equal(fine, PluginRules.CheckValue(Field(input, options: ["a", "b"]), Json(value)) is null);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void A_number_has_to_be_within_its_bounds(int value, bool fine) =>
        Assert.Equal(fine, PluginRules.CheckValue(Field("number", min: 0, max: 5), Json(value)) is null);

    [Fact]
    public void A_text_has_to_fit_its_length_and_the_default_length_applies_otherwise()
    {
        Assert.NotNull(PluginRules.CheckValue(Field("text", maxLength: 3), Json("abcd")));
        Assert.Null(PluginRules.CheckValue(Field("text", maxLength: 3), Json("abc")));
        Assert.NotNull(PluginRules.CheckValue(Field("text"), Json(new string('x', PluginRules.MaxLabelLength + 1))));
        Assert.Null(PluginRules.CheckValue(Field("multiline"), Json(new string('x', PluginRules.MaxTextLength))));
    }

    [Fact]
    public void A_required_field_needs_a_value_but_a_stored_password_is_one()
    {
        var password = Field("password", required: true);

        Assert.Equal("is required.", PluginRules.CheckField(password, Json(""), hasSecret: false));
        Assert.Equal("is required.", PluginRules.CheckField(password, default, hasSecret: false));
        Assert.Null(PluginRules.CheckField(password, Json(""), hasSecret: true));
        Assert.Null(PluginRules.CheckField(Field("checkbox", required: true), Json(false), hasSecret: false)); // a flag is never missing
        Assert.Equal("is required.", PluginRules.CheckField(Field("text", required: true), Json("   "), hasSecret: false));
    }

    [Fact]
    public void The_values_of_a_whole_form_are_checked_with_its_lists_and_their_rows()
    {
        var form = PluginTestKit.Layout(PluginTestKit.AcmeForm);
        IReadOnlyDictionary<string, JsonElement> Values(params (string, object?)[] pairs) => PluginTestKit.Values(pairs);

        Assert.Null(PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu")), _ => false));
        Assert.Contains("ApiKey", PluginRules.CheckValues(form, Values(("Region", "eu")), _ => false)); // required, nothing stored
        Assert.Null(PluginRules.CheckValues(form, Values(("Region", "eu")), path => path == "ApiKey")); // a stored one is enough
        Assert.Contains("Region", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "mars")), _ => false));
        Assert.Contains("Retries", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu"), ("Retries", 9)), _ => false));
        Assert.Contains("must be a list", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu"), ("Rows", "x")), _ => false));
        Assert.Contains("at most 3 rows", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu"), ("Rows", Enumerable.Range(0, 4).Select(_ => new { Name = "n" }).ToArray())), _ => false));
        Assert.Contains("must be an object", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu"), ("Rows", new object[] { "x" })), _ => false));
        Assert.Contains("Rows[2].Name", PluginRules.CheckValues(form, Values(("ApiKey", "k"), ("Region", "eu"), ("Rows", new object[] { new { Name = "ok" }, new { Name = 5 } })), _ => false));
    }

    [Fact]
    public void A_row_that_is_marked_as_a_stored_one_may_leave_a_required_password_empty()
    {
        var layout = PluginTestKit.Layout("""{ "elements": [ { "type": "list", "id": "L", "label": "L", "elements": [ { "type": "field", "id": "P", "label": "P", "input": "password", "required": true } ] } ] }""");
        var kept = PluginTestKit.Values(("L", new object[] { new { P = "", _keep = 0 } }));
        var fresh = PluginTestKit.Values(("L", new object[] { new { P = "" } }));

        Assert.Null(PluginRules.CheckValues(layout, kept, path => path == "L/0/P"));
        Assert.NotNull(PluginRules.CheckValues(layout, kept, _ => false));
        Assert.NotNull(PluginRules.CheckValues(layout, fresh, path => path == "L/0/P")); // a new row has nothing stored
    }

    [Fact]
    public void The_localized_text_is_the_german_one_only_for_a_german_client_and_only_when_there_is_one()
    {
        Assert.Equal("Hello", PluginRules.Localized("Hello", "Hallo", isGerman: false));
        Assert.Equal("Hallo", PluginRules.Localized("Hello", "Hallo", isGerman: true));
        Assert.Equal("Hello", PluginRules.Localized("Hello", null, isGerman: true));
        Assert.Equal("Hello", PluginRules.Localized("Hello", " ", isGerman: true));
        Assert.Equal("", PluginRules.Localized(null, null, isGerman: true));
    }

    [Theory]
    [InlineData("3", true, 3)]
    [InlineData("-2.5", true, -2.5)]
    [InlineData("NaN", false, 0)]
    [InlineData("1e999", false, 0)]
    [InlineData("", false, 0)]
    public void A_number_is_read_as_text_with_a_point(string text, bool ok, double expected)
    {
        Assert.Equal(ok, PluginRules.TryNumber(Json(text), out var number));
        if (ok) Assert.Equal(expected, number);
    }

    // --- Fields that belong together ----------------------------------------------------------------------------

    private const string LoginRule = """{ "groups": [["Email", "PhoneNumber"], ["Password"]], "message": "A login needs both." }""";

    private static string Login(string rule = LoginRule, string extra = "") => $$"""
        { "elements": [
            { "type": "field", "id": "Email", "label": "Email", "input": "text" },
            { "type": "field", "id": "PhoneNumber", "label": "Phone", "input": "text" },
            { "type": "field", "id": "Password", "label": "Password", "input": "password", "together": {{rule}} }{{extra}}
        ] }
        """;

    [Fact]
    public void A_rule_between_fields_is_a_valid_layout_even_when_it_names_fields_that_come_after_it_in_any_case()
    {
        Assert.Null(Check(Login(), PluginUiUse.DataSourceSettings));
        Assert.Null(Check(Login("""{ "groups": [["email"], ["PASSWORD", "phonenumber"]], "message": "m", "messageDe": "n" }"""), PluginUiUse.DataSourceSettings));
        Assert.Null(Check("""
            { "elements": [ { "type": "field", "id": "A", "label": "A", "input": "text", "together": { "groups": [["A"], ["B"]], "message": "m" } },
                            { "type": "field", "id": "B", "label": "B", "input": "text" } ] }
            """, PluginUiUse.DataSourceSettings));
    }

    [Theory]
    [InlineData("""{ "groups": [["Email"]], "message": "m" }""", "2 to 5 groups")]
    [InlineData("""{ "groups": [["Email"], ["Password"], ["Email2"], ["x1"], ["x2"], ["x3"]], "message": "m" }""", "2 to 5 groups")]
    [InlineData("""{ "message": "m" }""", "2 to 5 groups")]
    [InlineData("""{ "groups": [["Email"], []], "message": "m" }""", "1 to 5 field ids")]
    [InlineData("""{ "groups": [["Email"], ["1bad"]], "message": "m" }""", "1 to 5 field ids")]
    [InlineData("""{ "groups": [["Email", "PhoneNumber", "a", "b", "c", "d"], ["Password"]], "message": "m" }""", "1 to 5 field ids")]
    [InlineData("""{ "groups": [["Email"], ["email"]], "message": "m" }""", "two groups")]
    [InlineData("""{ "groups": [["Email"], ["Password"]] }""", "needs a message")]
    [InlineData("""{ "groups": [["Email"], ["Password"]], "message": "  " }""", "needs a message")]
    [InlineData("""{ "groups": [["Email"], ["Nobody"]], "message": "m" }""", "no field next to it")]
    public void A_rule_between_fields_that_is_not_well_formed_is_refused_with_the_reason(string rule, string reason)
    {
        var problem = Check(Login(rule), PluginUiUse.DataSourceSettings);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void A_rule_between_fields_needs_fields_not_checkboxes_headings_or_fields_of_another_list_row()
    {
        var checkbox = """
            { "elements": [ { "type": "field", "id": "Flag", "label": "F", "input": "checkbox" },
                            { "type": "field", "id": "P", "label": "P", "input": "password", "together": { "groups": [["Flag"], ["P"]], "message": "m" } } ] }
            """;
        Assert.Contains("no field next to it", Check(checkbox, PluginUiUse.DataSourceSettings));
        Assert.Contains("only for fields", Check("""{ "elements": [ { "type": "text", "text": "x", "together": { "groups": [["A"], ["B"]], "message": "m" } } ] }""", PluginUiUse.DataSourceSettings));
        Assert.Contains("needs a message", Check(Login($$"""{ "groups": [["Email"], ["Password"]], "message": "{{new string('x', PluginRules.MaxLabelLength + 1)}}" }"""), PluginUiUse.DataSourceSettings));

        // A row of a list only knows its own fields: the Email outside of the list is not one of them.
        var row = """
            { "elements": [ { "type": "field", "id": "Email", "label": "E", "input": "text" },
                { "type": "list", "id": "L", "label": "L", "elements": [ { "type": "field", "id": "Password", "label": "P", "input": "password",
                    "together": { "groups": [["Email"], ["Password"]], "message": "m" } } ] } ] }
            """;
        Assert.Contains("no field next to it", Check(row, PluginUiUse.DataSourceSettings));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_rule_holds_when_every_group_has_a_value_or_none_has(bool first, bool second, bool holds)
    {
        var rule = new PluginUiTogether([["a", "b"], ["c"]], "m");

        Assert.Equal(holds, PluginRules.TogetherHolds(rule, id => id switch { "a" => first, "b" => false, "c" => second, _ => false }));
    }

    [Fact]
    public void A_group_has_a_value_when_any_of_its_fields_has_and_three_groups_are_all_or_none_too()
    {
        var rule = new PluginUiTogether([["a", "b"], ["c"], ["d"]], "m");

        Assert.True(PluginRules.TogetherHolds(rule, id => id is "b" or "c" or "d"));
        Assert.False(PluginRules.TogetherHolds(rule, id => id is "b" or "c"));
        Assert.False(PluginRules.TogetherHolds(rule, id => id is "d"));
        Assert.True(PluginRules.TogetherHolds(rule, _ => false));
    }

    private static string? CheckSpond(Func<IReadOnlyDictionary<string, JsonElement>> values, Func<string, bool>? hasSecret = null) =>
        PluginRules.CheckValues(PluginTestKit.Layout(PluginTestKit.SpondForm), values(), hasSecret ?? (_ => false));

    [Theory]
    [InlineData("me@example.org", "", "pw", true)]
    [InlineData("", "+491701234567", "pw", true)]
    [InlineData("me@example.org", "+491701234567", "pw", true)]
    [InlineData("", "", "", true)] // nothing: a login can be added later
    [InlineData("me@example.org", "", "", false)]
    [InlineData("", "+491701234567", "", false)]
    [InlineData("", "", "pw", false)]
    [InlineData("   ", "", "pw", false)] // blanks are no email
    [InlineData("me@example.org", "", "   ", false)] // and no password
    public void A_login_in_the_settings_needs_the_email_or_phone_number_together_with_the_password(string email, string phone, string password, bool fine)
    {
        var problem = CheckSpond(() => PluginTestKit.Values(("Email", email), ("PhoneNumber", phone), ("Password", password)));

        if (fine) Assert.Null(problem);
        else Assert.Equal("Password: A Spond login needs the email (or phone number) and the password.", problem);
    }

    [Fact]
    public void A_stored_password_counts_for_the_login_and_alone_it_is_not_one()
    {
        IReadOnlyDictionary<string, JsonElement> Typed(string email) => PluginTestKit.Values(("Email", email), ("PhoneNumber", ""), ("Password", ""));

        Assert.Null(CheckSpond(() => Typed("me@example.org"), path => path == "Password"));
        Assert.NotNull(CheckSpond(() => Typed(""), path => path == "Password")); // the email was taken away: the password has nothing to go with
        Assert.NotNull(CheckSpond(() => Typed("me@example.org"))); // the password never was there
    }

    [Fact]
    public void A_login_that_is_missing_from_the_values_altogether_is_nothing_and_half_of_one_is_a_problem()
    {
        Assert.Null(CheckSpond(() => PluginTestKit.Values()));
        Assert.NotNull(CheckSpond(() => PluginTestKit.Values(("Email", "me@example.org"))));
        Assert.NotNull(CheckSpond(() => PluginTestKit.Values(("Email", null), ("PhoneNumber", "+49"), ("Password", null))));
    }

    [Fact]
    public void Every_row_of_a_list_is_asked_the_rule_and_its_message_names_the_row_and_the_field()
    {
        Assert.Null(CheckSpond(() => PluginTestKit.Values(("Accounts", new object[] { new { Email = "a@x.org", Password = "pw" }, new { PhoneNumber = "+49", Password = "pw" }, new { Email = "", Password = "" } }))));
        Assert.Equal("Accounts[2].Password: A backup Spond login needs the email (or phone number) and the password.",
            CheckSpond(() => PluginTestKit.Values(("Accounts", new object[] { new { Email = "a@x.org", Password = "pw" }, new { Email = "b@x.org", Password = "" } }))));
        Assert.Equal("Accounts[1].Password: A backup Spond login needs the email (or phone number) and the password.",
            CheckSpond(() => PluginTestKit.Values(("Accounts", new object[] { new { Password = "pw" } }))));
    }

    [Fact]
    public void A_row_that_says_which_stored_row_it_was_has_that_rows_password_and_one_that_does_not_has_none()
    {
        var stored = new[] { "Accounts/0/Password" };
        IReadOnlyDictionary<string, JsonElement> Row(object row) => PluginTestKit.Values(("Accounts", new[] { row }));

        Assert.Null(CheckSpond(() => Row(new { Email = "a@x.org", Password = "", _keep = 0 }), path => stored.Contains(path)));
        Assert.NotNull(CheckSpond(() => Row(new { Email = "a@x.org", Password = "" }), path => stored.Contains(path))); // a new row does not take it
        Assert.NotNull(CheckSpond(() => Row(new { Email = "a@x.org", Password = "", _keep = 1 }), path => stored.Contains(path))); // nothing is stored for the second row
        Assert.NotNull(CheckSpond(() => Row(new { Email = "", Password = "", _keep = 0 }), path => stored.Contains(path))); // the login of the row was taken away
    }

    // --- The data source kinds --------------------------------------------------------------------------------

    [Theory]
    [InlineData("spond", "spond")]
    [InlineData("  ACME ", "acme")]
    [InlineData("custom", "custom")]
    [InlineData("Demo", "demo")]
    [InlineData("Not A Kind!", "spond")]
    [InlineData("", "spond")]
    [InlineData(null, "spond")]
    public void A_data_source_kind_is_normalised_and_anything_that_cannot_be_one_means_spond(string? kind, string expected) =>
        Assert.Equal(expected, DataSourceKinds.Normalize(kind));

    [Fact]
    public void The_programs_own_data_sources_do_not_include_spond_but_a_plugin_could_offer_it()
    {
        Assert.Equal(["custom", "manual", "demo"], DataSourceKinds.All);
        Assert.True(DataSourceKinds.IsKnown("spond"));
        Assert.True(DataSourceKinds.IsKnown("acme"));
        Assert.False(DataSourceKinds.IsKnown("Bad Kind"));
        Assert.False(DataSourceKinds.IsKnown(null));
    }
}
