# Plugin development

A kyuplan **plugin** adds three kinds of things to an installation:

1. **Endpoints** to the API (below `/api/plugins/<plugin id>/`),
2. **Pages** with **permissions** of their own, which show up in the navigation of every client,
3. **Data sources** (where groups, members and events come from), with a settings form in the data source settings of the clients.

A plugin is C# code (it implements one interface) plus JSON files. The JSON describes the layout of the pages and of the settings form; **the clients draw it** (web, Windows, Linux), so a plugin has no UI code of its own and nothing platform specific. The Spond data source is a plugin that is installed by default: it is a complete, small example (repository `ArizonaGreenTea05/kyuplan.plugins.spond`).

How administrators install and trust plugins: [Plugins in the administrator guide](admin-guide.md#plugins); the API: [reference](reference.md#plugins).

## What a plugin is

A **package** is a zip file with the extension `.kyuplugin` attached to a **release** of a public GitHub repository (one such file per release). It holds, in its root:

| File | |
| --- | --- |
| `plugin.json` | The manifest: who the plugin is and what it adds. |
| the assembly (`assembly` in the manifest) | A .NET 10 library with **one** public class that implements `IKyuplanPlugin`, and the libraries only the plugin needs. Not the framework and not `Kyuplan.Plugins.Abstractions`: the API provides them. |
| JSON files | The layout of each page and of each data source's settings form (any folder below the root, e.g. `ui/`). |

The API loads it **once, when it starts**, into a load context of its own; installing, updating and removing a plugin restarts the API. Everything a plugin does is checked when it is installed (the manifest, the JSON) and when it is loaded; a plugin that throws while it starts does not run, and the API starts anyway.

## The code

Reference `src/Kyuplan.Plugins.Abstractions` (a project reference while the interface is not on NuGet) **without copying it** into the package:

```xml
<ProjectReference Include="…/Kyuplan/src/Kyuplan.Plugins.Abstractions/Kyuplan.Plugins.Abstractions.csproj" Private="false" ExcludeAssets="runtime" />
```

and implement the interface:

```csharp
public sealed class AcmePlugin : IKyuplanPlugin
{
    public void Initialize(IPluginBuilder builder)
    {
        // A data source declared in plugin.json (dataSources[].id): created when the API starts with it chosen.
        builder.AddDataSource("acme", context => new AcmeDataSource(context.Configuration, context.Time, context.ClubTimeZone, context.Loggers));

        // Endpoints below /api/plugins/acme/…; they need a login. RequirePermission narrows them to a permission of the manifest.
        builder.MapEndpoints(routes =>
        {
            routes.MapGet("items", () => Items.All).RequirePermission(builder.Permission("view"));
            routes.MapPost("items/add", (NewItem item) => Items.Add(item)).RequirePermission(builder.Permission("edit"));
        });
    }
}
```

- `IPluginBuilder.Id`, `Version` and `Directory` (the plugin's unpacked folder), `Loggers`, `Permission(name)` (the full id of one of the manifest's permissions).
- `AddDataSource(id, create)`: `create` gets a `PluginDataSourceContext`: the **configuration section** of the data source (its settings, see below), the clock (`TimeProvider`), the club's time zone, the logger factory and the API's services. It returns an `IDataSource`.
- `MapEndpoints(map)`: `map` gets the route group `/api/plugins/<id>`; use minimal API as usual. Without `RequirePermission` an endpoint is open to **every signed-in user**. Never trust a body: it comes from a browser.

### The data source

`IDataSource` (namespace `Kyuplan.Plugins`) delivers what every part of kyuplan analyses:

```csharp
public interface IDataSource
{
    bool IsDemo => false;
    string Kind { get; }                                          // the id, as in plugin.json
    Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken);   // groups with subgroups and members
    Task<IReadOnlyList<EventInfo>> GetEventsAsync(string groupId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    void InvalidateCache();                                       // the Refresh button
}
```

`GroupInfo`, `SubGroupInfo`, `MemberInfo`, `EventInfo` (with the answers of the members, `Rsvp`) are in the same assembly. Rules that matter:

- **Read only.** The API never writes to a data source. Cache what is expensive (the Refresh button calls `InvalidateCache`).
- **Ids are stable** (group, member, event): members, exams and filters of kyuplan refer to them.
- Times are in the club's time zone (`context.ClubTimeZone`); an event can have `Trainers` (the members who host it; it counts for none of their statistics).
- When the service cannot be reached, is not configured or refuses the login, **throw `DataSourceUnavailableException`** with a message that says what to do: the API answers `503` and shows it.
- The object lives as long as the API runs (a change of the settings restarts it); implement `IDisposable` if it holds resources.

## The manifest

```json
{
  "id": "acme",
  "name": "Acme",
  "nameDe": "Acme",
  "version": "2.1.0",
  "description": "Things of Acme.",
  "author": "Acme Inc.",
  "homepage": "https://github.com/acme/kyuplan-acme",
  "assembly": "Acme.Kyuplan.dll",
  "permissions": [
    { "name": "view", "label": "See the Acme items", "labelDe": "Acme-Dinge sehen" },
    { "name": "edit", "label": "Change the Acme items" }
  ],
  "pages": [
    { "id": "items", "title": "Acme items", "titleDe": "Acme-Dinge", "icon": "chart", "permission": "view", "layout": "ui/items.json" }
  ],
  "dataSources": [
    { "id": "acme", "name": "Acme", "description": "Groups from Acme.", "configSection": "Acme", "settings": "ui/settings.json" }
  ]
}
```

| Property | |
| --- | --- |
| `id` | Lower case letters, digits and hyphens, 2–32 characters, starting with a letter. It must not be the name of a permission domain of the program (`members`, `settings`, ...), nor `api` or `plugins`. It starts the plugin's permissions and API addresses. |
| `name`, `nameDe` | The name in English and German (no German name = the English one). |
| `version` | Dotted numbers (`1.2.0`). The release should match. |
| `assembly` | The `.dll` in the root of the package that holds the `IKyuplanPlugin`. |
| `permissions[]` | A permission `<id>.<name>` (`acme.view`); `name` is letters and digits starting with a lower case letter; `label`/`labelDe` is what the role form shows. At most 20. |
| `pages[]` | A page: `id`, `title`/`titleDe`, optional `icon` (a name the clients know: `dashboard`, `chart`, `clock`, `calendar`, `members`, `belt`, `tag`, `sliders`, `users`, `shield`, `palette`, `puzzle`; the default is `puzzle`), optional `permission` (the **name** of one of the plugin's permissions; none = every signed-in user may open it) and `layout` (the JSON file). At most 20. |
| `dataSources[]` | A data source: `id` (lower case letters, digits, hyphens; unique among all plugins, and not `demo`, `custom` or `manual`), `name`/`nameDe`, `description`/`descriptionDe`, `configSection` and `settings` (the JSON file of its form; none = no settings). At most 5. |

A data source's **settings** are stored in the section `configSection` of the configuration (`Plugins:<plugin id>` when it is left out; another section of your own is fine, but not one the program uses: `Database`, `Accounts`, `DataSource`, `Setup`, ...). The id of a field is the key in the section. Because the section is ordinary configuration, an administrator may also set the keys by hand or with environment variables (`Acme__ApiKey`); the Spond plugin uses the section `Spond`, which is where Spond's settings always were.

## The JSON of pages and forms

A layout is `{ "elements": [ … ] }`. The elements, in order, are drawn one under the other. Texts have a German twin (`textDe`, `labelDe`, `hintDe`); without it the English text is shown.

| `type` | Properties | Where |
| --- | --- | --- |
| `heading`, `text` | `text` | everywhere |
| `field` | `id`, `label`, `hint`, `input`, `required`, `default`, `options`, `min`, `max`, `maxLength` | settings form; on a page only inside a `form` |
| `list` | `id`, `label`, `hint`, `max` (rows, at most 50), `elements` (fields of a row) | settings form |
| `table` | `label` (title), `source`, `columns` (`key`, `label`), `text` (shown while empty) | page |
| `button` | `label`, `action` | page |
| `form` | `label` (the button's text), `elements` (fields, texts), `action` | page |

- **`input`** of a field: `text`, `password`, `number` (`min`, `max`; typed with a point), `checkbox`, `select` (`options`: `value`, `label`, `labelDe`) and `multiline`. `default` is a text (`"true"` for a checkbox). A `password` is stored **encrypted** and never sent back to a client: the form shows that one is stored and an empty field keeps it. A list is saved as a whole; a row that is left out is gone, and a row keeps the passwords of the row it was.
- **`table`**: `source` is the path of an endpoint of your plugin (`items`), called with `GET` whenever the page is shown or an action finished. It answers a JSON **array of objects**; `columns[].key` picks a property of each (a number, text or flag).
- **`action`**: `{ "method": "POST" | "PUT" | "DELETE", "path": "items/add", "confirm": "Really?", "confirmDe": "Wirklich?" }`. The path is relative to `/api/plugins/<id>/`. A button calls it; a `form` sends its values as a **JSON object** (`{ "name": "…" }`, field id → value) as the body. `confirm` makes the client ask first. After a successful call the tables are read again.

The rules the API enforces when a package is installed (`PluginRules` in `Kyuplan.Contracts`): at most 200 elements per layout, nested at most three deep; texts at most 2000 characters, labels 200; ids letters, digits and underscores starting with a letter; every id once per form; at most 100 options; 12 columns; paths relative, without `..`, hidden or empty parts. The same rules check the values typed in a settings form, in the API and in the clients.

## Pages and permissions

A page is listed in the navigation for a user who has the page's permission, and its layout is only sent to such users. **The endpoints the page uses must ask for the permission themselves** (`RequirePermission(builder.Permission("view"))`): hiding a page is no protection. The administrator role has every permission of every plugin; a role keeps the permissions of a plugin that is removed.

## Packaging and releasing

```bash
dotnet publish src/Acme.Kyuplan -c Release -o publish          # assembly + dependencies; no Kyuplan.Plugins.Abstractions.dll
(cd publish && zip -r ../acme-2.1.0.kyuplugin .)                 # plugin.json and ui/ are in the publish output
```

`plugin.json` and the JSON files go into the output with `<Content Include="plugin.json" CopyToPublishDirectory="PreserveNewest" />`. Attach the one `.kyuplugin` file to a GitHub **release** of a **public** repository; the API asks for the *latest* release. A package is at most 50 MB (150 MB unpacked, 500 files); file names are plain relative paths. A tag like `v2.1.0` and a workflow that builds the package on a tag keep the version in the manifest and in the release equal (the Spond plugin's repository has one).

Install it on the Plugins page with the address of the repository. **Update** fetches the latest release again.

## Testing

- Unit-test the data source against fakes (the Spond plugin's tests do: its `Spond.API` client is replaced by a fake per account).
- To try the plugin in a running API, build it, zip it and put the file into the folder `Plugins:BundledDirectory` before the first start, or serve it from a release.
- The tests of this repository load a plugin through `tests/Kyuplan.TestPlugin` and `PluginTestKit` (build a package in memory, install it into the folder of a test's settings file, start the API). Use the same approach to test endpoints and permissions end to end.

## Safety

A plugin runs with the rights of the API: it can read the databases, the settings file and the network. Administrators decide to trust it; the API only keeps it from being unpacked outside its folder, from taking more than the limits above, and from replacing the program's own types (the framework and `Kyuplan.Plugins.Abstractions` always come from the API). A plugin should therefore: keep its secrets in `password` fields (encrypted at rest), not log them, call only the services it needs, answer with errors that do not give secrets away, and ask for a permission on every endpoint that returns club data.
