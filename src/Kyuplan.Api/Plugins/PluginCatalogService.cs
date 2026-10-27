using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Plugins;

/// <summary>What the plugins that run look like to the clients and to the administrators.</summary>
public sealed class PluginCatalogService(PluginHost host, PluginStore store)
{
    /// <summary>
    /// The running plugins. A page (with its layout) is only in it for somebody who may open it, and the settings form of a data source only for
    /// somebody who may see the settings; the permissions the plugins add are listed for everybody, so that a role can be given them.
    /// </summary>
    public PluginCatalogDto Catalog(ClaimsPrincipal user) => Catalog(user, user.Has(Permission.SettingsView), includePages: true);

    /// <summary>What the setup screen needs (nobody is signed in yet): the data sources with their forms, and no pages.</summary>
    public PluginCatalogDto ForSetup() => Catalog(new ClaimsPrincipal(), mayConfigure: true, includePages: false);

    private PluginCatalogDto Catalog(ClaimsPrincipal user, bool mayConfigure, bool includePages)
    {
        return new PluginCatalogDto([.. host.Running.Select(plugin =>
        {
            var manifest = plugin.Manifest;
            var pages = (includePages ? manifest.Pages ?? [] : [])
                .Where(p => string.IsNullOrEmpty(p.Permission) || user.Has(PluginRules.PermissionId(plugin.Id, p.Permission)))
                .Where(p => plugin.Installed.Layouts.ContainsKey(p.Layout))
                .Select(p => new PluginPageDto(p.Id, p.Title, p.TitleDe, p.Icon, string.IsNullOrEmpty(p.Permission) ? null : PluginRules.PermissionId(plugin.Id, p.Permission), plugin.Installed.Layouts[p.Layout]));
            var sources = (manifest.DataSources ?? []).Select(s => new PluginDataSourceDto(s.Id, s.Name, s.NameDe, s.Description, s.DescriptionDe, mayConfigure ? plugin.Installed.FormOf(s) : null));
            var permissions = (manifest.Permissions ?? []).Select(p => new PluginPermissionDto(PluginRules.PermissionId(plugin.Id, p.Name), p.Label, p.LabelDe));
            return new PluginDto(plugin.Id, manifest.Name, manifest.NameDe, manifest.Version, manifest.Description, manifest.DescriptionDe, [.. permissions], [.. pages], [.. sources]);
        })]);
    }

    /// <summary>The installed plugins for the administration: also those that were installed after the API started (they run after the restart) and those that could not be loaded.</summary>
    public IReadOnlyList<PluginAdminDto> List()
    {
        var installed = store.ReadInstalled().ToDictionary(p => p.Id);
        return [.. store.List().Select(stored => Describe(stored, installed.GetValueOrDefault(stored.Id))).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public PluginAdminDto? Find(string id) => List().FirstOrDefault(p => p.Id == id);

    private PluginAdminDto Describe(StoredPlugin stored, InstalledPlugin? files)
    {
        var loaded = host.Plugins.FirstOrDefault(p => p.Id == stored.Id && p.Installed.Stored.Sha256 == stored.Sha256);
        var manifest = files?.Manifest ?? loaded?.Manifest;
        var problem = files is null ? "The files of the plugin cannot be read." : loaded?.Problem;
        return new PluginAdminDto(
            stored.Id, manifest?.Name ?? stored.Id, stored.Version, manifest?.Description, stored.Source, stored.InstalledAt,
            loaded?.IsRunning == true, problem, [.. (manifest?.DataSources ?? []).Select(s => s.Id)], (manifest?.Pages ?? []).Count, stored.Sha256);
    }
}
