using System.Text.Json.Nodes;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// Theme packs (<see cref="ThemePackDto"/>). <b>Reading the catalog is open to everybody</b>, like the branding, because the clients paint the
/// default theme on the login screen already (<see cref="ApiKeyMiddleware"/>). Installing and deleting packs needs <c>themes.edit</c>.
/// </summary>
public static class ThemeEndpoints
{
    public const string Route = "/api/themes";

    public static IEndpointRouteBuilder MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, (ThemePackStore store) => store.Catalog())
            .WithTags("Themes").AllowAnonymous()
            .Produces<ThemeCatalogDto>()
            .WithSummary("Every installed theme pack (the built-in ones and the uploaded ones) and a version that changes whenever the set does. Needs no login.");

        var admin = app.MapGroup(Route).WithTags("Themes (editing)");

        admin.MapPut("/", async (HttpRequest request, ThemePackStore store, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body);
                var json = await ReadLimitedAsync(reader, ct);

                var pack = store.Parse(json);
                AuditFilter.SetDetails(request.HttpContext, new JsonObject { ["id"] = pack.Id, ["name"] = pack.Name }.ToJsonString());
                return Results.Ok(store.Save(pack));
            })
            .Audited(AuditAreas.Themes, AuditActions.Update)
            .Accepts<string>("application/json")
            .WithSummary($"Install a theme pack (JSON: id, name, nameDe, scheme, highContrast, accent, colors) or replace the one with the same id; at most {ThemePackStore.MaxBytes / 1024} KB. 400 with the reason when it is no usable pack. Answers with the catalog.")
            .RequirePermission(Permission.ThemesEdit);

        admin.MapDelete("/{id}", (string id, ThemePackStore store) =>
                BuiltInThemePacks.Contains(id) ? Results.Conflict(new { title = "A built-in theme cannot be deleted." })
                : store.Catalog().Packs.Any(p => p.Id == id && p.Source is not null) ? Results.Conflict(new { title = "A theme of a GitHub theme pack cannot be deleted on its own; remove the theme pack." })
                : store.Delete(id) ? Results.Ok(store.Catalog())
                : Results.NotFound())
            .Audited(AuditAreas.Themes, AuditActions.Delete)
            .WithSummary("Delete an uploaded theme pack. Users who had chosen it see the default theme from then on. 404 when there is no such pack, 409 for a built-in theme or a theme of a GitHub theme pack. Answers with the catalog.")
            .RequirePermission(Permission.ThemesEdit);

        // GitHub theme packs: repositories with theme files that are read now and then.
        admin.MapGet("/sources", (ThemeSourceStore sources) => sources.List())
            .Produces<IReadOnlyList<ThemeSourceDto>>()
            .WithSummary("The GitHub theme packs (repositories with a folder 'themes' of theme files) and how reading each one went.")
            .RequirePermission(Permission.ThemesEdit);

        admin.MapPost("/sources", async (HttpContext context, ThemeSourceInputDto? input, ThemeSourceStore sources, ThemeSourceSync sync, CancellationToken ct) =>
            {
                var source = sources.Add(input?.Url);
                var read = await sync.SyncAsync(source.Id, ct);
                if (read is not { LastSucceeded: true })
                {
                    sources.Delete(source.Id); // nothing worth keeping: the address is wrong, the repository is private or has no themes
                    throw new InvalidInputException(read?.LastMessage ?? "The theme pack could not be read.");
                }

                AuditFilter.SetDetails(context, new JsonObject { ["name"] = read.Name, ["themes"] = read.ThemeCount }.ToJsonString());
                return Results.Ok(read);
            })
            .Audited(AuditAreas.Themes, AuditActions.Create)
            .Produces<ThemeSourceDto>()
            .WithSummary($"Add a GitHub theme pack (body: {{ \"url\": \"https://github.com/owner/repo\" }}, a public repository). It is read at once, and again every {ThemeSourceRules.RefreshInterval.TotalHours:0} hours. 400 with the reason when the address is none, the pack is there already, the repository cannot be read or offers no valid theme.")
            .RequirePermission(Permission.ThemesEdit);

        admin.MapPost("/sources/{id}/refresh", async (string id, ThemeSourceSync sync, CancellationToken ct) =>
                await sync.SyncAsync(id, ct) is { } read ? Results.Ok(read) : Results.NotFound())
            .Audited(AuditAreas.Themes, AuditActions.Run)
            .Produces<ThemeSourceDto>()
            .WithSummary("Read a GitHub theme pack again now. Answers with its state (a failure is in 'lastMessage', not an error status; the themes it offered so far stay). 404 for an unknown pack.")
            .RequirePermission(Permission.ThemesEdit);

        admin.MapDelete("/sources/{id}", (string id, ThemeSourceStore sources) =>
                sources.Delete(id) ? Results.NoContent() : Results.NotFound())
            .Audited(AuditAreas.Themes, AuditActions.Delete)
            .WithSummary("Remove a GitHub theme pack and its themes. Users who had chosen one of them see the default theme from then on. 404 for an unknown pack.")
            .RequirePermission(Permission.ThemesEdit);

        return app;
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[ThemePackStore.MaxBytes + 1];
        var read = await reader.ReadBlockAsync(buffer, ct);
        return new string(buffer, 0, read); // one character over the limit is enough for Parse to refuse it
    }
}
