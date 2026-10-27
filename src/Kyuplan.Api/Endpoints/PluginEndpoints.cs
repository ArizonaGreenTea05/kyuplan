using System.Text.Json.Nodes;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;
using Kyuplan.Plugins;
using System.Security.Claims;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// Plugins. <b>Every signed-in user</b> reads the catalog of what the running plugins add (their pages, as far as the user may open them, and the
/// permissions). <b>Installing, updating and removing needs <c>plugins.edit</c></b>, reading the list <c>plugins.view</c>: a plugin is code that
/// runs in the API with all its rights. The endpoints a plugin adds itself are below <c>/api/plugins/&lt;id&gt;</c> (<see cref="PluginHost.MapEndpoints"/>).
/// </summary>
public static class PluginEndpoints
{
    public const string Route = "/api/plugins";

    public const string AdminRoute = "/api/plugin-admin";

    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, (PluginCatalogService catalog, ClaimsPrincipal user) => catalog.Catalog(user))
            .WithTags("Plugins")
            .Produces<PluginCatalogDto>()
            .WithSummary("What the plugins that run add: their pages (with the layout, only those the user may open), their permissions and their data sources (with the settings form only for those who may see the settings).");

        var admin = app.MapGroup(AdminRoute).WithTags("Plugins (administration)");

        admin.MapGet("/", (PluginCatalogService catalog) => catalog.List())
            .Produces<IReadOnlyList<PluginAdminDto>>()
            .WithSummary("The installed plugins, whether they run and why not. A plugin that was installed since the API started runs after its restart.")
            .RequirePermission(Permission.PluginsView);

        admin.MapPost("/", async (HttpContext context, PluginInstallInputDto? input, PluginInstaller installer, PluginCatalogService catalog, IAppRestarter restarter, CancellationToken ct) =>
            {
                var (plugin, changed) = await installer.InstallFromGitHubAsync(input?.Url, ct);
                AuditFilter.SetDetails(context, new JsonObject { ["id"] = plugin.Id, ["version"] = plugin.Version, ["source"] = plugin.Source }.ToJsonString());
                if (changed) restarter.Request();
                return Results.Ok(new PluginChangedDto(catalog.Find(plugin.Id), changed));
            })
            .Audited(AuditAreas.Plugins, AuditActions.Create)
            .Produces<PluginChangedDto>()
            .WithSummary("Install the plugin of a public GitHub repository (body: { \"url\": \"https://github.com/owner/repo\" }): its latest release has to carry one .kyuplugin file. The plugin is loaded when the API starts, so the API restarts itself. 400 with the reason when the repository, the package or the plugin is no good.")
            .RequirePermission(Permission.PluginsEdit);

        admin.MapPost("/{id}/update", async (string id, HttpContext context, PluginInstaller installer, PluginCatalogService catalog, IAppRestarter restarter, CancellationToken ct) =>
            {
                if (await installer.UpdateAsync(id, ct) is not var (plugin, changed)) return Results.NotFound();

                AuditFilter.SetDetails(context, new JsonObject { ["id"] = plugin.Id, ["version"] = plugin.Version, ["changed"] = changed }.ToJsonString());
                if (changed) restarter.Request();
                return Results.Ok(new PluginChangedDto(catalog.Find(plugin.Id), changed));
            })
            .Audited(AuditAreas.Plugins, AuditActions.Update)
            .Produces<PluginChangedDto>()
            .WithSummary("Fetch the latest release of the repository the plugin came from. When it is another package the API restarts to run it. 404 for an unknown plugin, 400 when it was not installed from GitHub or cannot be fetched.")
            .RequirePermission(Permission.PluginsEdit);

        admin.MapDelete("/{id}", (string id, HttpContext context, PluginInstaller installer, IAppRestarter restarter) =>
            {
                if (!installer.Remove(id)) return Results.NotFound();

                AuditFilter.SetDetails(context, new JsonObject { ["id"] = id }.ToJsonString());
                restarter.Request();
                return Results.Ok(new PluginChangedDto(null, true));
            })
            .Audited(AuditAreas.Plugins, AuditActions.Delete)
            .Produces<PluginChangedDto>()
            .WithSummary("Remove a plugin. Its files are deleted when the API starts the next time, and the API restarts itself. What a role or user was allowed through it stays and works again when the plugin is installed again. 404 for an unknown plugin.")
            .RequirePermission(Permission.PluginsEdit);

        return app;
    }
}
