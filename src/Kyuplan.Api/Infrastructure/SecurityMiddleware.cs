using System.Net;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// Two protections for every response. <b>Transport</b>: logins are sent in clear text with HTTP Basic, so a request that did not come over
/// HTTPS is refused (400) unless it comes from this machine or <see cref="ApiSecurityOptions.AllowInsecureHttp"/> is set.
/// <b>Browser hardening</b>: headers that keep the pages from being framed, sniffed or fed scripts from elsewhere. The content security policy
/// only lets the page run its own scripts (and WebAssembly) and talk to its own origin (and the allowed ones); it is skipped for the
/// development docs, which load their viewer from a CDN. A header that an endpoint set itself (the branding images) is kept.
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, IOptions<ApiSecurityOptions> options, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        var settings = options.Value;
        // /healthz carries no login and no data, and probes of a platform (Azure, Kubernetes, a load balancer) come over plain HTTP from elsewhere.
        if (!settings.AllowInsecureHttp && !context.Request.IsHttps && !IsLocal(context) && !context.Request.Path.StartsWithSegments("/healthz"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(new
            {
                status = StatusCodes.Status400BadRequest,
                title = "HTTPS required",
                detail = "This server only takes logins over HTTPS. Use an https:// address (behind a reverse proxy that ends HTTPS, set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true), or set Api:AllowInsecureHttp=true on a network you trust.",
            });
        }

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
            headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");
            if (!(environment.IsDevelopment() && IsDevelopmentDocs(context.Request.Path)))
                headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy(settings, context.Request.Path.StartsWithSegments("/docs")));
            return Task.CompletedTask;
        });

        return next(context);
    }

    // A request that went through a proxy (on this very machine, say) is not local, whatever address the proxy has: the visitor is somebody else.
    private static bool IsLocal(HttpContext context) =>
        !context.Request.Headers.ContainsKey("X-Forwarded-For") && !context.Request.Headers.ContainsKey("X-Original-For") &&
        (context.Connection.RemoteIpAddress is not { } address || IPAddress.IsLoopback(address)
         || (context.Connection.LocalIpAddress is { } local && address.Equals(local)));

    private static bool IsDevelopmentDocs(PathString path) => path.StartsWithSegments("/scalar") || path.StartsWithSegments("/openapi");

    // Blazor WebAssembly needs 'wasm-unsafe-eval' to compile its runtime and inline style attributes for the charts and belts.
    // The documentation (Docusaurus, built by us and static) starts with a small inline script that sets the colour theme, so its pages may run inline scripts. The files are
    // only ever ours, never a user's input.
    private static string ContentSecurityPolicy(ApiSecurityOptions settings, bool documentation)
    {
        var connect = string.Join(' ', new[] { "'self'" }.Concat(settings.AllowedOrigins));
        var scripts = documentation ? "'self' 'unsafe-inline'" : "'self' 'wasm-unsafe-eval'";
        return $"default-src 'self'; script-src {scripts}; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
               $"connect-src {connect}; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    }
}
