using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kyuplan.Contracts;

/// <summary>Where a <see cref="PluginUiLayout"/> is used: a page shows data and runs actions, the settings form of a data source only has fields.</summary>
public enum PluginUiUse
{
    Page,
    DataSourceSettings,
}

/// <summary>What a plugin has to look like. The API enforces it when a plugin is installed and loaded; the clients use the value rules to offer only what will be accepted.</summary>
public static partial class PluginRules
{
    /// <summary>Plugins that can be installed at most; the catalog goes to every client.</summary>
    public const int MaxPlugins = 20;

    public const int MaxPermissions = 20;
    public const int MaxPages = 20;
    public const int MaxDataSources = 5;

    /// <summary>Elements of one layout, nested ones included.</summary>
    public const int MaxElements = 200;

    public const int MaxDepth = 3;
    public const int MaxOptions = 100;
    public const int MaxColumns = 12;
    public const int MaxTextLength = 2000;
    public const int MaxLabelLength = 200;

    /// <summary>The package of a plugin (the .zip) can be this large at most.</summary>
    public const int MaxPackageBytes = 50 * 1024 * 1024;

    /// <summary>What a package can hold when it is unpacked, all files together.</summary>
    public const long MaxUnpackedBytes = 150L * 1024 * 1024;

    public const int MaxFiles = 500;

    /// <summary>A JSON file of a plugin (<c>plugin.json</c>, a layout) is read up to this size.</summary>
    public const int MaxJsonBytes = 256 * 1024;

    /// <summary>The file extension of a plugin package.</summary>
    public const string PackageExtension = ".kyuplugin";

    public const string ManifestFile = "plugin.json";

    /// <summary>The source of a plugin that was installed from a file that comes with the program, not from GitHub.</summary>
    public const string BundledSource = "bundled";

    /// <summary>The kinds of data sources the program has itself; a plugin cannot use their ids.</summary>
    public static IReadOnlyList<string> ReservedSourceIds { get; } = [DataSourceKinds.Demo, DataSourceKinds.Custom, DataSourceKinds.Manual];

    public static readonly string[] Inputs = ["text", "password", "number", "checkbox", "select", "multiline"];

    public static readonly string[] Methods = ["POST", "PUT", "DELETE"];

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,49}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[0-9]{1,5}(\.[0-9]{1,5}){0,3}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,49}(:[A-Za-z][A-Za-z0-9_]{0,49}){0,3}$")]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex PathPattern();

    /// <summary>
    /// A plugin id: lower case letters, digits and hyphens, starting with a letter. It must not be the name of a domain of the permissions
    /// (<c>members</c>, <c>settings</c>, ...): the permissions of a plugin start with its id and must not be mistaken for the program's own.
    /// </summary>
    public static bool IsPluginId(string? id) =>
        id is not null && IdPattern().IsMatch(id) && !Permission.Groups.Any(g => g.Name == id) && id is not ("api" or "plugins");

    /// <summary>The name of a permission or an id of a page, as <c>plugin.json</c> writes it.</summary>
    public static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>An id of a field: also the key in the configuration it is stored under.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static bool IsVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    /// <summary>The id of a data source: the kind it is chosen by in the settings.</summary>
    public static bool IsSourceId(string? id) => id is not null && IdPattern().IsMatch(id) && !ReservedSourceIds.Contains(id);

    /// <summary>The full id of the permission <paramref name="name"/> of the plugin <paramref name="pluginId"/>.</summary>
    public static string PermissionId(string pluginId, string name) => $"{pluginId}.{name}";

    /// <summary>
    /// Whether this can be a permission of a plugin (<c>myplugin.view</c>): a role can carry it while the plugin is not installed (it works again
    /// once it is). Whether the plugin exists is not asked here.
    /// </summary>
    public static bool IsPluginPermission(string? permission)
    {
        if (permission is null || permission.Length > 70) return false;

        var dot = permission.IndexOf('.');
        return dot > 0 && IsPluginId(permission[..dot]) && IsName(permission[(dot + 1)..]);
    }

    /// <summary>The default section of the configuration a data source of the plugin keeps its settings in.</summary>
    public static string DefaultSection(string pluginId) => $"Plugins:{pluginId}";

    /// <summary>A file path inside a package: relative, with <c>/</c>, no <c>..</c> and no hidden or empty parts.</summary>
    public static bool IsPackagePath(string? path) =>
        path is { Length: > 0 and <= 200 } && PathPattern().IsMatch(path) && !path.Split('/').Any(part => part is "." or ".." || part.StartsWith('.'));

    /// <summary>An address inside the API of a plugin (<c>jobs/refresh</c>): never leaves it, no query.</summary>
    public static bool IsEndpointPath(string? path) => path is not null && IsPackagePath(path);

    /// <summary>The problem with a manifest, or <c>null</c>. Checks what the files do not: the shape of every id, the limits and that the pieces refer to each other.</summary>
    public static string? CheckManifest(PluginManifest? manifest)
    {
        if (manifest is null) return "plugin.json is empty.";
        if (!IsPluginId(manifest.Id)) return "id must be lower case letters, digits and hyphens (2 to 32 characters, starting with a letter) and must not be the name of a permission domain of the program.";
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > MaxLabelLength || manifest.NameDe?.Length > MaxLabelLength) return "name is required (at most 200 characters).";
        if (!IsVersion(manifest.Version)) return "version must be dotted numbers like 1.2.0.";
        if (manifest.Description?.Length > MaxTextLength || manifest.DescriptionDe?.Length > MaxTextLength) return $"description is too long (at most {MaxTextLength} characters).";
        if (manifest.Author?.Length > MaxLabelLength) return "author is too long.";
        if (manifest.Homepage is { Length: > 0 } homepage && (homepage.Length > 300 || !Uri.TryCreate(homepage, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return "homepage must be an https address.";
        if (!IsPackagePath(manifest.Assembly) || manifest.Assembly.Contains('/') || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return "assembly must be the file name of a .dll in the root of the package.";

        var permissions = manifest.Permissions ?? [];
        if (permissions.Count > MaxPermissions) return $"at most {MaxPermissions} permissions.";
        foreach (var permission in permissions)
        {
            if (permission is null || !IsName(permission.Name)) return "a permission name must be letters and digits, starting with a lower case letter (view, editJobs).";
            if (string.IsNullOrWhiteSpace(permission.Label) || permission.Label.Length > MaxLabelLength || permission.LabelDe?.Length > MaxLabelLength) return $"permission {permission.Name}: label is required (at most 200 characters).";
        }

        if (permissions.GroupBy(p => p.Name).Any(g => g.Count() > 1)) return "a permission is listed twice.";

        var pages = manifest.Pages ?? [];
        if (pages.Count > MaxPages) return $"at most {MaxPages} pages.";
        foreach (var page in pages)
        {
            if (page is null || !IsName(page.Id)) return "a page id must be letters and digits, starting with a lower case letter.";
            if (string.IsNullOrWhiteSpace(page.Title) || page.Title.Length > MaxLabelLength || page.TitleDe?.Length > MaxLabelLength) return $"page {page.Id}: title is required (at most 200 characters).";
            if (page.Icon is { Length: > 0 } icon && !IsName(icon)) return $"page {page.Id}: icon must be a name like 'chart'.";
            if (!IsPackagePath(page.Layout) || !page.Layout.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return $"page {page.Id}: layout must be the path of a .json file in the package.";
            if (!string.IsNullOrEmpty(page.Permission) && permissions.All(p => p.Name != page.Permission)) return $"page {page.Id}: the permission '{page.Permission}' is not one of the plugin's permissions.";
        }

        if (pages.GroupBy(p => p.Id).Any(g => g.Count() > 1)) return "a page id is used twice.";

        var sources = manifest.DataSources ?? [];
        if (sources.Count > MaxDataSources) return $"at most {MaxDataSources} data sources.";
        foreach (var source in sources)
        {
            if (source is null || !IsSourceId(source.Id)) return "a data source id must be lower case letters, digits and hyphens (2 to 32 characters) and not demo, custom or manual.";
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > MaxLabelLength || source.NameDe?.Length > MaxLabelLength) return $"data source {source.Id}: name is required (at most 200 characters).";
            if (source.Description?.Length > MaxTextLength || source.DescriptionDe?.Length > MaxTextLength) return $"data source {source.Id}: description is too long.";
            if (source.ConfigSection is { Length: > 0 } section && !IsSection(section)) return $"data source {source.Id}: configSection must be names separated by ':' (Spond, Plugins:myplugin).";
            if (source.Settings is { Length: > 0 } settings && (!IsPackagePath(settings) || !settings.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                return $"data source {source.Id}: settings must be the path of a .json file in the package.";
        }

        return sources.GroupBy(s => s.Id).Any(g => g.Count() > 1) ? "a data source id is used twice." : null;
    }

    private static readonly string[] ReservedSections =
    [
        "Database", "Accounts", "Setup", "DataSource", "Logging", "AllowedHosts", "Api", "Metrics", "Backup", "Statistics", "Sync", "Themes", "Branding",
        "Documentation", "ClientDownload", "DefaultData", "Kestrel", "ConnectionStrings", "Urls",
    ];

    /// <summary>
    /// A section of the configuration a data source keeps its settings in: <c>Plugins:&lt;something&gt;</c>, or a section of its own such as <c>Spond</c>
    /// (the settings that were there before Spond was a plugin). Those of the program itself (the databases, the accounts, ...) are not for a plugin.
    /// </summary>
    public static bool IsSection(string? section)
    {
        if (section is null || !SectionPattern().IsMatch(section)) return false;

        var first = section.Split(':')[0];
        return first == "Plugins" ? section.Contains(':') : !ReservedSections.Contains(first);
    }

    /// <summary>The problem with a layout, or <c>null</c>. A page can have <c>heading</c>, <c>text</c>, <c>table</c>, <c>button</c> and <c>form</c>; a settings form only <c>heading</c>, <c>text</c>, <c>field</c> and <c>list</c>.</summary>
    public static string? CheckLayout(PluginUiLayout? layout, PluginUiUse use)
    {
        if (layout?.Elements is null) return "elements is required.";

        var count = 0;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CheckElements(layout.Elements, use, 1, ref count, ids, inForm: false, inList: false);
    }

    private static string? CheckElements(IReadOnlyList<PluginUiElement> elements, PluginUiUse use, int depth, ref int count, HashSet<string> ids, bool inForm, bool inList)
    {
        if (depth > MaxDepth) return "elements are nested too deeply.";

        var rowIds = inList ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : ids;
        foreach (var element in elements)
        {
            if (element is null) return "an element is empty.";
            if (++count > MaxElements) return $"at most {MaxElements} elements.";

            var problem = CheckElement(element, use, depth, ref count, rowIds, ids, inForm, inList);
            if (problem is not null) return $"{(element.Id ?? element.Type)}: {problem}";
        }

        return null;
    }

    private static string? CheckElement(PluginUiElement e, PluginUiUse use, int depth, ref int count, HashSet<string> scope, HashSet<string> all, bool inForm, bool inList)
    {
        if (!TextsFit(e)) return "a text is too long.";

        switch (e.Type)
        {
            case "heading":
            case "text":
                return string.IsNullOrWhiteSpace(e.Text) ? "text is required." : null;

            case "field":
                if (use == PluginUiUse.Page && !inForm) return "a field on a page must be inside a form.";
                if (!IsKey(e.Id)) return "id must be letters, digits and underscores, starting with a letter.";
                if (!scope.Add(e.Id!)) return "the id is used twice.";
                if (string.IsNullOrWhiteSpace(e.Label)) return "label is required.";
                if (e.Input is null || !Inputs.Contains(e.Input)) return $"input must be one of: {string.Join(", ", Inputs)}.";
                if (inList && e.Input == "multiline") return "a row of a list cannot hold a multiline field.";
                if (e.Input == "select")
                {
                    if (e.Options is not { Count: > 0 and <= MaxOptions }) return $"a select needs 1 to {MaxOptions} options.";
                    if (e.Options.Any(o => o is null || string.IsNullOrEmpty(o.Value) || o.Value.Length > MaxLabelLength || string.IsNullOrWhiteSpace(o.Label) || o.Label.Length > MaxLabelLength || o.LabelDe?.Length > MaxLabelLength))
                        return "an option needs a value and a label.";
                    if (e.Options.GroupBy(o => o.Value).Any(g => g.Count() > 1)) return "an option value is used twice.";
                }
                else if (e.Options is { Count: > 0 })
                {
                    return "only a select has options.";
                }

                if (e.Input == "number" && e.Min > e.Max) return "min is larger than max.";
                if (e.MaxLength is < 1 or > MaxTextLength) return $"maxLength must be between 1 and {MaxTextLength}.";
                if (e.Default is { } fallback)
                {
                    var bad = e.Input == "checkbox" ? fallback is not ("true" or "false") : CheckValue(e, JsonSerializer.SerializeToElement(fallback)) is not null;
                    if (bad) return "default does not fit the field.";
                }

                return null;

            case "list":
                if (use != PluginUiUse.DataSourceSettings) return "a list is only for the settings form of a data source.";
                if (inList) return "a list cannot hold a list.";
                if (!IsKey(e.Id)) return "id must be letters, digits and underscores, starting with a letter.";
                if (!scope.Add(e.Id!)) return "the id is used twice.";
                if (string.IsNullOrWhiteSpace(e.Label)) return "label is required.";
                if (e.Max is < 1 or > 50) return "max must be between 1 and 50.";
                if (e.Elements is not { Count: > 0 }) return "a list needs the fields of a row (elements).";
                if (e.Elements.Any(r => r is null || r.Type != "field")) return "a row of a list only holds fields.";
                return CheckElements(e.Elements, use, depth + 1, ref count, all, inForm, inList: true);

            case "table":
                if (use != PluginUiUse.Page) return "a table is only for pages.";
                if (!IsEndpointPath(e.Source)) return "source must be the path of an endpoint of the plugin (jobs/list).";
                if (e.Columns is not { Count: > 0 and <= MaxColumns }) return $"a table needs 1 to {MaxColumns} columns.";
                if (e.Columns.Any(c => c is null || !IsKey(c.Key) || string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > MaxLabelLength || c.LabelDe?.Length > MaxLabelLength)) return "a column needs a key and a label.";
                return null;

            case "button":
                if (use != PluginUiUse.Page) return "a button is only for pages.";
                if (string.IsNullOrWhiteSpace(e.Label)) return "label is required.";
                return CheckAction(e.Action);

            case "form":
                if (use != PluginUiUse.Page) return "a form is only for pages.";
                if (inForm) return "a form cannot hold a form.";
                if (string.IsNullOrWhiteSpace(e.Label)) return "label is required (the text of the button that sends it).";
                if (e.Elements is not { Count: > 0 }) return "a form needs fields (elements).";
                if (e.Elements.Any(r => r is null || r.Type is not ("field" or "text"))) return "a form only holds fields and texts.";
                return CheckAction(e.Action) ?? CheckElements(e.Elements, use, depth + 1, ref count, all, inForm: true, inList: false);

            default:
                return $"type '{e.Type}' is not known (heading, text, field, list, table, button, form).";
        }
    }

    private static bool TextsFit(PluginUiElement e) =>
        (e.Text?.Length ?? 0) <= MaxTextLength && (e.TextDe?.Length ?? 0) <= MaxTextLength
        && (e.Label?.Length ?? 0) <= MaxLabelLength && (e.LabelDe?.Length ?? 0) <= MaxLabelLength
        && (e.Hint?.Length ?? 0) <= MaxTextLength && (e.HintDe?.Length ?? 0) <= MaxTextLength
        && (e.Default?.Length ?? 0) <= MaxTextLength;

    private static string? CheckAction(PluginUiAction? action)
    {
        if (action is null) return "action is required.";
        if (!Methods.Contains(action.Method)) return $"action.method must be one of: {string.Join(", ", Methods)}.";
        if (!IsEndpointPath(action.Path)) return "action.path must be the path of an endpoint of the plugin (jobs/refresh).";

        return action.Confirm?.Length > MaxLabelLength || action.ConfirmDe?.Length > MaxLabelLength ? "action.confirm is too long." : null;
    }

    /// <summary>The paths of the password fields of a settings form, as <see cref="PluginSettingsDto.SecretsSet"/> names them: <c>Password</c> and <c>Accounts/*/Password</c> (a row is <c>*</c> here).</summary>
    public static IReadOnlyList<string> SecretPaths(PluginUiLayout layout)
    {
        var paths = new List<string>();
        foreach (var e in layout.Elements)
        {
            if (e.Type == "field" && e.Input == "password" && e.Id is not null) paths.Add(e.Id);
            else if (e.Type == "list" && e.Id is not null)
                paths.AddRange((e.Elements ?? []).Where(r => r.Input == "password" && r.Id is not null).Select(r => $"{e.Id}/*/{r.Id}"));
        }

        return paths;
    }

    /// <summary>The label of an element, a table column or an option in the language of the client (<c>de</c>); without a German text the English one.</summary>
    public static string Localized(string? english, string? german, bool isGerman) => isGerman && !string.IsNullOrWhiteSpace(german) ? german : english ?? "";

    /// <summary>The problem with the value of one field (a JSON value as the form sends it), or <c>null</c>. A missing value is allowed unless the field is required.</summary>
    public static string? CheckValue(PluginUiElement field, JsonElement value)
    {
        var empty = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()));
        switch (field.Input)
        {
            case "checkbox":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined ? null : "must be true or false.";

            case "number":
                if (empty) return null;
                if (!TryNumber(value, out var number)) return "must be a number.";
                if (field.Min is { } min && number < min) return $"must be at least {min}.";
                return field.Max is { } max && number > max ? $"must be at most {max}." : null;

            case "select":
                if (empty) return null;
                return value.ValueKind == JsonValueKind.String && (field.Options ?? []).Any(o => o.Value == value.GetString()) ? null : "is not one of the options.";

            default: // text, password, multiline
                if (empty) return null;
                if (value.ValueKind != JsonValueKind.String) return "must be text.";

                var text = value.GetString()!;
                var limit = field.MaxLength ?? (field.Input == "multiline" ? MaxTextLength : MaxLabelLength);
                if (text.Length > limit) return $"must be at most {limit} characters.";
                return field.Input != "multiline" && text.Any(char.IsControl) ? "must be one line of text." : null;
        }
    }

    /// <summary>A number as text or as a JSON number (the forms of the clients send either).</summary>
    public static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out number) && double.IsFinite(number),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number),
            _ => false,
        };
    }

    /// <summary>
    /// The problem with the values of a settings form (field id → value; a list is an array of objects), or <c>null</c>. <paramref name="hasSecret"/> says
    /// whether a stored value exists for the path of a password field (<see cref="PluginSettingsDto.SecretsSet"/>), which makes an empty required password fine.
    /// </summary>
    public static string? CheckValues(PluginUiLayout layout, IReadOnlyDictionary<string, JsonElement> values, Func<string, bool> hasSecret)
    {
        foreach (var e in layout.Elements)
        {
            if (e.Type == "field" && e.Id is not null)
            {
                if (CheckField(e, values.GetValueOrDefault(e.Id), hasSecret(e.Id)) is { } problem) return $"{e.Id}: {problem}";
            }
            else if (e.Type == "list" && e.Id is not null)
            {
                if (!values.TryGetValue(e.Id, out var list) || list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                if (list.ValueKind != JsonValueKind.Array) return $"{e.Id}: must be a list.";
                if (list.GetArrayLength() > (e.Max ?? 50)) return $"{e.Id}: at most {e.Max ?? 50} rows.";

                var row = 0;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) return $"{e.Id}: a row must be an object.";

                    var current = row;
                    var kept = item.TryGetProperty(KeepProperty, out var keep) && keep.ValueKind == JsonValueKind.Number && keep.TryGetInt32(out var stored) ? stored : -1;
                    foreach (var f in e.Elements ?? [])
                    {
                        if (f.Id is null) continue;

                        item.TryGetProperty(f.Id, out var value);
                        if (CheckField(f, value, kept >= 0 && hasSecret($"{e.Id}/{kept}/{f.Id}")) is { } problem) return $"{e.Id}[{current + 1}].{f.Id}: {problem}";
                    }

                    row++;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The property of a row of a list that says which stored row it was (its index): an empty password of the row then keeps the stored one. A new row has none.
    /// </summary>
    public const string KeepProperty = "_keep";

    public static string? CheckField(PluginUiElement field, JsonElement value, bool hasSecret)
    {
        var missing = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
        if (field.Required && field.Input != "checkbox" && missing && !(field.Input == "password" && hasSecret)) return "is required.";

        return CheckValue(field, value);
    }
}
