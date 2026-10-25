using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Kyuplan.Api.Infrastructure;

/// <summary>Which senders of <c>X-Forwarded-*</c> headers are believed (<see cref="Configuration.ApiSecurityOptions.TrustedProxies"/>).</summary>
public static class TrustedProxies
{
    // Only this machine: a private network is no protection, any host on the LAN, the Docker network or the pod network could forge the headers.
    private static readonly string[] Loopback = ["127.0.0.0/8", "::1/128"];

    public static void Apply(ForwardedHeadersOptions options, string[]? configured)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in IsExplicit(configured) ? configured! : Loopback)
        {
            var text = entry.Trim();
            if (text.Contains('/') && System.Net.IPNetwork.TryParse(text, out var network)) options.KnownIPNetworks.Add(network);
            else if (IPAddress.TryParse(text, out var address)) options.KnownProxies.Add(address);
            else throw new InvalidOperationException($"Api:TrustedProxies: '{text}' is not an address or a network (like 10.0.0.0/8).");
        }
    }

    public static bool IsExplicit(string[]? configured) => configured is { Length: > 0 };

    /// <summary>Forwarded headers are switched on but no proxy is named: a reverse proxy on another machine or container is then not believed.</summary>
    public static bool NeedsWarning(string? forwardedHeadersEnabled, string[]? configured) =>
        bool.TryParse(forwardedHeadersEnabled, out var enabled) && enabled && !IsExplicit(configured);
}
