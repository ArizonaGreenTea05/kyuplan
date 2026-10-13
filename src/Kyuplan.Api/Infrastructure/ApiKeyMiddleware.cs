using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// Guards <c>/api</c> with a shared secret in the <c>X-Api-Key</c> header when <see cref="ApiSecurityOptions.ApiKey"/> is set.
/// An extra layer in front of the login every user needs anyway (see <c>BasicAuthenticationHandler</c>). Note that a key
/// shipped to a browser (Blazor WebAssembly) is visible to every user of that site, so it keeps nobody out who can open the site.
/// Reading the branding (<see cref="BrandingService.Route"/>) is exempt: it is public, and an <c>&lt;img&gt;</c> cannot send a header.
/// Changing it, and the view of it as it is edited, is not.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiSecurityOptions> options)
{
    public const string HeaderName = "X-Api-Key";

    public Task InvokeAsync(HttpContext context)
    {
        var expected = options.Value.ApiKey;
        if (string.IsNullOrEmpty(expected) ||
            !context.Request.Path.StartsWithSegments("/api") ||
            IsPublicBranding(context.Request) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            return next(context);
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status401Unauthorized,
            title = "Unauthorized",
            detail = $"Missing or invalid {HeaderName} header.",
        });
    }

    /// <summary>The branding and its images may be read by anybody; everything else below the route needs permissions (branding.view, branding.edit).</summary>
    private static bool IsPublicBranding(HttpRequest request) =>
        request.Path.StartsWithSegments(BrandingService.Route) &&
        !request.Path.StartsWithSegments(BrandingService.SettingsRoute) &&
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));
}
