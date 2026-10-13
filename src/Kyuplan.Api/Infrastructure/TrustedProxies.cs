using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Kyuplan.Api.Infrastructure;

/// <summary>Which senders of <c>X-Forwarded-*</c> headers are believed (<see cref="Configuration.ApiSecurityOptions.TrustedProxies"/>).</summary>
public static class TrustedProxies
{
    private static readonly string[] PrivateNetworks = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7", "fe80::/10"];

    public static void Apply(ForwardedHeadersOptions options, string[]? configured)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in configured is { Length: > 0 } ? configured : PrivateNetworks)
        {
            var text = entry.Trim();
            if (text.Contains('/') && System.Net.IPNetwork.TryParse(text, out var network)) options.KnownIPNetworks.Add(network);
            else if (IPAddress.TryParse(text, out var address)) options.KnownProxies.Add(address);
            else throw new InvalidOperationException($"Api:TrustedProxies: '{text}' is not an address or a network (like 10.0.0.0/8).");
        }
    }
}
