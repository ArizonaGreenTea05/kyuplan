using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// Guards <c>/metrics</c> with <c>Authorization: Bearer &lt;token&gt;</c> (what Prometheus sends with <c>authorization.credentials</c>). Without a
/// configured <see cref="MetricsOptions.Token"/> nobody gets in. Registered only when the metrics are switched on.
/// </summary>
public sealed class MetricsTokenMiddleware(RequestDelegate next, IOptions<MetricsOptions> options)
{
    private const string Scheme = "Bearer ";

    public Task InvokeAsync(HttpContext context)
    {
        var expected = options.Value.Token;
        var header = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(expected) && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(Hash(header[Scheme.Length..].Trim()), Hash(expected)))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
