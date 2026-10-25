using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// How the instance presents itself. <b>Reading it is open to everybody</b>, without a login and without the API key
/// (<see cref="ApiKeyMiddleware"/>): the clients show it before anybody has signed in, and an <c>&lt;img&gt;</c> can send neither. It is
/// nothing a visitor of the login screen does not see anyway. <b>Editing it needs permissions</b>: <c>branding.view</c> for the settings as they
/// are edited, <c>branding.edit</c> for every change.
/// </summary>
public static class BrandingEndpoints
{
    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        var open = app.MapGroup(BrandingService.Route).WithTags("Branding").AllowAnonymous();

        open.MapGet("/", (BrandingService service, CancellationToken ct) => service.GetAsync(ct))
            .Produces<BrandingDto>()
            .WithSummary("Name, accent colour and the addresses of the logos and the icon of this instance. Needs no login.");

        open.MapGet("/{name}", async (string name, BrandingService service, HttpContext context, CancellationToken ct) =>
            {
                if (await service.GetImageAsync(name, ct) is not { } image) return Results.NotFound();

                // The same address serves another image once the branding was changed, and a browser must not go on showing the old logo
                // for hours: it asks every time and gets a 304 while nothing changed.
                context.Response.Headers.CacheControl = "no-cache";

                // An uploaded SVG is a document of the API's origin when somebody opens its address; it may not run anything there.
                context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
                context.Response.Headers.XContentTypeOptions = "nosniff";

                if (image.Bytes is { } bytes)
                {
                    var tag = new EntityTagHeaderValue($"\"u{bytes.Length:x}-{image.Updated.UtcTicks:x}\"");
                    return Results.Bytes(bytes, image.ContentType, lastModified: image.Updated, entityTag: tag);
                }

                if (!File.Exists(image.Path)) return Results.NotFound();

                // The tag is made of the file's size and its time, so that a file replaced by one with an older time (a volume, an
                // image layer) is noticed too.
                var file = new FileInfo(image.Path!);
                var fileTag = new EntityTagHeaderValue($"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"");
                return Results.File(image.Path!, image.ContentType, lastModified: file.LastWriteTimeUtc, entityTag: fileTag);
            })
            .WithSummary("One image: logo, logo-on-dark or icon. 404 when this instance has none. Needs no login.");

        var admin = app.MapGroup(BrandingService.Route).WithTags("Branding (editing)");

        admin.MapGet("/settings", (BrandingService service, CancellationToken ct) => service.GetSettingsAsync(ct))
            .WithSummary("The branding as it is edited: the name, the organisation and the colour in effect, each image slot's own image (null = none of its own), and whether anything was saved here.")
            .RequirePermission(Permission.BrandingView);

        admin.MapPut("/", async (BrandingInputDto input, BrandingService service, ThemePackStore packs, CancellationToken ct) =>
            {
                if (Validate(input, packs) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await service.SaveAsync(input, ct));
            })
            .Audited(AuditAreas.Branding, AuditActions.Update)
            .WithSummary("Set the product name, the organisation (may be empty) and the accent colour (#RGB or #RRGGBB). They apply instead of the API's Branding settings from then on. Answers with the settings.")
            .RequirePermission(Permission.BrandingEdit);

        admin.MapPut("/{slot}", async (string slot, HttpRequest request, BrandingService service, CancellationToken ct) =>
            {
                if (BrandingSlots.ContentTypes(slot) is null) return Results.NotFound();

                AuditFilter.SetDetails(request.HttpContext, SlotDetails(slot));

                return Results.Ok(await service.SetImageAsync(slot, await ReadImageAsync(request.Body, ct), ct));
            })
            .Audited(AuditAreas.Branding, AuditActions.Update)
            .Accepts<byte[]>("*/*") // whatever the client calls it: the bytes decide
            .WithSummary($"Upload the image of a slot (logo, logo-on-dark, icon) as the request body. The bytes decide what format it is, not the Content-Type; at most {BrandingRules.MaxImageBytes / 1024} KB. 400 when it is not an image the slot takes. Answers with the settings.")
            .RequirePermission(Permission.BrandingEdit);

        admin.MapDelete("/{slot}", async (string slot, HttpContext context, BrandingService service, CancellationToken ct) =>
            {
                if (BrandingSlots.ContentTypes(slot) is null) return Results.NotFound();

                AuditFilter.SetDetails(context, SlotDetails(slot));
                return Results.Ok(await service.RemoveImageAsync(slot, ct));
            })
            .Audited(AuditAreas.Branding, AuditActions.Delete)
            .WithSummary("Take the image of a slot away: it has none from now on, even if the API's Branding settings name a file. Answers with the settings.")
            .RequirePermission(Permission.BrandingEdit);

        admin.MapDelete("/", async (BrandingService service, CancellationToken ct) => Results.Ok(await service.ResetAsync(ct)))
            .Audited(AuditAreas.Branding, AuditActions.Delete)
            .WithSummary("Forget everything saved here (texts, colour and images): the API's Branding settings apply again. Answers with the settings.")
            .RequirePermission(Permission.BrandingEdit);

        return app;
    }

    private static string SlotDetails(string slot) => new JsonObject { ["slot"] = slot }.ToJsonString();

    internal static Dictionary<string, string[]>? Validate(BrandingInputDto? input, ThemePackStore? packs = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors[nameof(input)] = ["A request body is required."];
            return errors;
        }

        Check(errors, nameof(input.ProductName), BrandingRules.CheckProductName(input.ProductName));
        Check(errors, nameof(input.OrganizationName), BrandingRules.CheckOrganizationName(input.OrganizationName));
        Check(errors, nameof(input.AccentColor), BrandingRules.CheckAccentColor(input.AccentColor));
        if (input.Currency is not null) Check(errors, nameof(input.Currency), BrandingRules.CheckCurrency(input.Currency));
        if (input.DefaultWebTheme is not null) Check(errors, nameof(input.DefaultWebTheme), ThemeRules.Check(ThemeClients.Web, input.DefaultWebTheme, "defaultWebTheme") ?? packs?.CheckInstalled(input.DefaultWebTheme, "defaultWebTheme"));
        if (input.DefaultWindowsTheme is not null) Check(errors, nameof(input.DefaultWindowsTheme), ThemeRules.Check(ThemeClients.Windows, input.DefaultWindowsTheme, "defaultWindowsTheme") ?? packs?.CheckInstalled(input.DefaultWindowsTheme, "defaultWindowsTheme"));

        return errors.Count == 0 ? null : errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string key, string? problem)
    {
        if (problem is not null) errors[key] = [problem];
    }

    /// <summary>The request body, at most <see cref="BrandingRules.MaxImageBytes"/>: a larger one is refused without reading it all.</summary>
    private static async Task<byte[]> ReadImageAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BrandingRules.MaxImageBytes)
                throw new InvalidInputException($"The image is larger than {BrandingRules.MaxImageBytes / 1024} KB.");
        }

        return buffer.ToArray();
    }
}
