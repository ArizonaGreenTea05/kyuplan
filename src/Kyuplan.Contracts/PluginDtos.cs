using System.Text.Json;

namespace Kyuplan.Contracts;

/// <summary>
/// A choice of a <see cref="PluginUiElement"/> of the input <c>select</c>.
/// </summary>
/// <param name="Value">What is stored when the choice is made.</param>
/// <param name="Label">What the user sees; <paramref name="LabelDe"/> for a German client (none = <paramref name="Label"/>).</param>
public sealed record PluginUiOption(string Value, string Label, string? LabelDe = null);

/// <summary>A column of a <c>table</c>.</summary>
/// <param name="Key">The property of a row (a JSON object) the column shows.</param>
public sealed record PluginUiColumn(string Key, string Label, string? LabelDe = null);

/// <summary>A call of an endpoint of the plugin: what a <c>button</c> does and where a <c>form</c> is sent.</summary>
/// <param name="Method"><c>POST</c>, <c>PUT</c> or <c>DELETE</c>.</param>
/// <param name="Path">Relative to <c>/api/plugins/&lt;plugin id&gt;/</c>: <c>jobs/refresh</c>.</param>
/// <param name="Confirm">A question the user has to answer with yes before the call is made (<paramref name="ConfirmDe"/> in German).</param>
public sealed record PluginUiAction(string Method, string Path, string? Confirm = null, string? ConfirmDe = null);

/// <summary>
/// One element of a <see cref="PluginUiLayout"/>. <see cref="Type"/> says what it is and which of the other properties it reads:
/// <list type="bullet">
/// <item><c>heading</c>, <c>text</c>: <see cref="Text"/>.</item>
/// <item><c>field</c>: <see cref="Id"/>, <see cref="Input"/>, <see cref="Label"/>, <see cref="Hint"/>, <see cref="Required"/>, <see cref="Default"/>, <see cref="Options"/>, <see cref="Min"/>, <see cref="Max"/>, <see cref="MaxLength"/>.</item>
/// <item><c>list</c> (a list of rows of fields the user can add to and remove from): <see cref="Id"/>, <see cref="Label"/>, <see cref="Hint"/>, <see cref="Max"/> (rows), <see cref="Elements"/> (the fields of a row).</item>
/// <item><c>table</c>: <see cref="Label"/> (a title), <see cref="Source"/> (an endpoint of the plugin that answers a list of objects), <see cref="Columns"/>, <see cref="Text"/> (shown while it is empty).</item>
/// <item><c>button</c>: <see cref="Label"/>, <see cref="Action"/>.</item>
/// <item><c>form</c>: <see cref="Label"/> (the button's text), <see cref="Elements"/> (fields), <see cref="Action"/> (where the values are sent, as a JSON object).</item>
/// </list>
/// The <c>…De</c> properties are the German texts; without one the English text is shown.
/// </summary>
public sealed record PluginUiElement(
    string Type,
    string? Id = null,
    string? Text = null,
    string? TextDe = null,
    string? Label = null,
    string? LabelDe = null,
    string? Hint = null,
    string? HintDe = null,
    string? Input = null,
    bool Required = false,
    string? Default = null,
    IReadOnlyList<PluginUiOption>? Options = null,
    int? Min = null,
    int? Max = null,
    int? MaxLength = null,
    string? Source = null,
    IReadOnlyList<PluginUiColumn>? Columns = null,
    PluginUiAction? Action = null,
    IReadOnlyList<PluginUiElement>? Elements = null);

/// <summary>What a page of a plugin or the settings form of its data source looks like. The clients build the view from it; no UI code is in the plugin.</summary>
public sealed record PluginUiLayout(IReadOnlyList<PluginUiElement> Elements);

/// <summary>What the plugin says in its <c>plugin.json</c>: a permission it adds, as <see cref="Name"/> (<c>view</c> becomes <c>&lt;plugin id&gt;.view</c>).</summary>
public sealed record PluginPermissionDef(string Name, string Label, string? LabelDe = null);

/// <summary>A page of a plugin in <c>plugin.json</c>.</summary>
/// <param name="Permission">The name of the permission (one of the plugin's) that is needed to open the page; empty = every signed-in user may.</param>
/// <param name="Layout">The JSON file of the layout, relative to the plugin's folder.</param>
public sealed record PluginPageDef(string Id, string Title, string? TitleDe, string? Icon, string? Permission, string Layout);

/// <summary>A data source of a plugin in <c>plugin.json</c>.</summary>
/// <param name="Id">The id the data source is chosen by in the settings (<c>DataSource:Kind</c>); unique among all plugins and not one of the built-in sources.</param>
/// <param name="ConfigSection">The section of the configuration the settings form is stored in and the data source reads (default: <c>Plugins:&lt;plugin id&gt;</c>).</param>
/// <param name="Settings">The JSON file of the settings form, relative to the plugin's folder; none = the data source has no settings.</param>
public sealed record PluginDataSourceDef(string Id, string Name, string? NameDe, string? Description, string? DescriptionDe, string? ConfigSection, string? Settings);

/// <summary>
/// <c>plugin.json</c>, the description of a plugin package. A plugin is a .zip (<c>*.kyuplugin</c>) that holds this file, the assembly
/// (<see cref="Assembly"/>, an implementation of <c>IKyuplanPlugin</c>) and the JSON files the manifest points to.
/// </summary>
/// <param name="Id">Lower case letters, digits and hyphens (<see cref="PluginRules.IsPluginId"/>); also the start of the plugin's permissions and of its API addresses.</param>
/// <param name="Version">Dotted numbers (<c>1.2.0</c>).</param>
/// <param name="Assembly">The file name of the assembly, in the root of the package.</param>
public sealed record PluginManifest(
    string Id,
    string Name,
    string? NameDe,
    string Version,
    string? Description,
    string? DescriptionDe,
    string? Author,
    string? Homepage,
    string Assembly,
    IReadOnlyList<PluginPermissionDef>? Permissions = null,
    IReadOnlyList<PluginPageDef>? Pages = null,
    IReadOnlyList<PluginDataSourceDef>? DataSources = null);

/// <summary>A permission a plugin adds, with the full id (<c>myplugin.view</c>).</summary>
public sealed record PluginPermissionDto(string Id, string Label, string? LabelDe);

/// <summary>A page of a plugin that the signed-in user may open, with its layout.</summary>
/// <param name="Permission">The full id of the permission the page needs; <c>null</c> = every signed-in user.</param>
public sealed record PluginPageDto(string Id, string Title, string? TitleDe, string? Icon, string? Permission, PluginUiLayout Layout);

/// <summary>A data source a plugin offers. <paramref name="Form"/> is its settings form; <c>null</c> for somebody who may not see the settings, or when it has none.</summary>
public sealed record PluginDataSourceDto(string Id, string Name, string? NameDe, string? Description, string? DescriptionDe, PluginUiLayout? Form);

/// <summary>A loaded plugin as the clients see it: what it adds to the navigation, to the permissions and to the data sources.</summary>
public sealed record PluginDto(
    string Id,
    string Name,
    string? NameDe,
    string Version,
    string? Description,
    string? DescriptionDe,
    IReadOnlyList<PluginPermissionDto> Permissions,
    IReadOnlyList<PluginPageDto> Pages,
    IReadOnlyList<PluginDataSourceDto> DataSources);

/// <summary>The plugins that run. <b>Pages and forms are only given to those who may use them</b>; the permissions are listed for everybody who may give permissions.</summary>
public sealed record PluginCatalogDto(IReadOnlyList<PluginDto> Plugins);

/// <summary>An installed plugin as administrators see it.</summary>
/// <param name="Source">Where it came from: the address of the GitHub repository, or <c>bundled</c> (it was installed from a file of the program folder).</param>
/// <param name="IsRunning">The plugin was loaded when the API started. A plugin that was installed or changed since runs after the restart that follows.</param>
/// <param name="Problem">Why the plugin does not run (it could not be loaded), or <c>null</c>.</param>
public sealed record PluginAdminDto(
    string Id,
    string Name,
    string Version,
    string? Description,
    string Source,
    DateTimeOffset InstalledAt,
    bool IsRunning,
    string? Problem,
    IReadOnlyList<string> DataSources,
    int PageCount,
    string Sha256);

/// <summary>The plugin to install.</summary>
/// <param name="Url">A public GitHub repository, like <c>https://github.com/owner/repo</c>: its latest release holds the package.</param>
public sealed record PluginInstallInputDto(string? Url);

/// <summary>What installing, updating or removing a plugin did. A plugin is loaded when the API starts, so the API restarts itself.</summary>
public sealed record PluginChangedDto(PluginAdminDto? Plugin, bool Restarting);

/// <summary>The settings of one data source of a plugin, as the settings page shows them.</summary>
/// <param name="Values">By field id: text, a number or a flag; a <c>list</c> is an array of objects. <b>Fields of the input <c>password</c> are never in it.</b></param>
/// <param name="SecretsSet">The paths of the password fields that have a stored value: <c>Password</c>, or <c>Accounts/0/Password</c> for the first row of the list <c>Accounts</c>.</param>
public sealed record PluginSettingsDto(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> SecretsSet);
