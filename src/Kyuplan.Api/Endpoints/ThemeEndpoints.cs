using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
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

                return Results.Ok(store.Save(store.Parse(json)));
            })
            .Accepts<string>("application/json")
            .WithSummary($"Install a theme pack (JSON: id, name, nameDe, scheme, highContrast, accent, colors) or replace the one with the same id; at most {ThemePackStore.MaxBytes / 1024} KB. 400 with the reason when it is no usable pack. Answers with the catalog.")
            .RequirePermission(Permission.ThemesEdit);

        admin.MapDelete("/{id}", (string id, ThemePackStore store) =>
                BuiltInThemePacks.Contains(id) ? Results.Conflict(new { title = "A built-in theme cannot be deleted." })
                : store.Delete(id) ? Results.Ok(store.Catalog())
                : Results.NotFound())
            .WithSummary("Delete an uploaded theme pack. Users who had chosen it see the default theme from then on. 404 when there is no such pack, 409 for a built-in theme. Answers with the catalog.")
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
