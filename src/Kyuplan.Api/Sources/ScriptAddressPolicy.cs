using System.Net;
using System.Net.Sockets;

namespace Kyuplan.Api.Sources;

/// <summary>Where an address leads, as far as a custom script's HTTP requests are concerned.</summary>
public enum AddressScope
{
    /// <summary>Anything that is reachable from the internet.</summary>
    Public,

    /// <summary>The internal network: RFC 1918, CGNAT, unique-local, multicast, reserved and documentation ranges.</summary>
    Private,

    /// <summary>The server itself and the cloud metadata services: loopback, "this network", link-local, <c>fd00:ec2::/32</c>.</summary>
    Local,
}

/// <summary>
/// Decides which addresses <see cref="CustomScriptDataSource"/> may connect to. Local addresses need <c>DataSource:AllowLocalAddresses</c>, private ones
/// <c>DataSource:AllowPrivateAddresses</c>. IPv6 forms that carry an IPv4 address (mapped, compatible, NAT64, 6to4, Teredo) count as the stricter of both.
/// </summary>
internal static class ScriptAddressPolicy
{
    public static AddressScope Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? ClassifyV4(b) : ClassifyV6(b);
    }

    private static AddressScope ClassifyV4(ReadOnlySpan<byte> b)
    {
        if (b[0] is 0 or 127 || (b[0] == 169 && b[1] == 254)) return AddressScope.Local;
        var isPrivate = b[0] is 10 or >= 224 // 224/4 multicast, 240/4 reserved, broadcast
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) // CGNAT
            || (b[0] == 198 && b[1] is 18 or 19) // benchmarking
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF protocol assignments, documentation
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)
            || (b[0] == 203 && b[1] == 0 && b[2] == 113);
        return isPrivate ? AddressScope.Private : AddressScope.Public;
    }

    private static AddressScope ClassifyV6(ReadOnlySpan<byte> b)
    {
        var own = AddressScope.Public;
        if (IsZero(b[..15]) && b[15] <= 1) return AddressScope.Local; // :: and ::1
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return AddressScope.Local; // fe80::/10
        if (b[0] == 0xfd && b[1] == 0x00 && b[2] == 0x0e && b[3] == 0xc2) return AddressScope.Local; // fd00:ec2::/32, the AWS metadata endpoint
        if ((b[0] & 0xfe) == 0xfc // fc00::/7 unique-local
            || b[0] == 0xff // multicast
            || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0) // fec0::/10 site-local
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // 2001:db8::/32 documentation
            || (b[0] == 0x01 && b[1] == 0x00 && IsZero(b[2..8])) // 100::/64 discard
            || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01)) // 64:ff9b:1::/48 local-use NAT64
            own = AddressScope.Private;

        var embedded = new List<AddressScope>();
        if (IsZero(b[..12])) embedded.Add(ClassifyV4(b[12..])); // ::a.b.c.d (IPv4-compatible)
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && IsZero(b[4..12])) embedded.Add(ClassifyV4(b[12..])); // 64:ff9b::/96 NAT64
        if (b[0] == 0x20 && b[1] == 0x02) embedded.Add(ClassifyV4(b[2..6])); // 2002::/16 6to4
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) // 2001::/32 Teredo: server, and the client with its bits inverted
        {
            embedded.Add(ClassifyV4(b[4..8]));
            embedded.Add(ClassifyV4([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]));
        }

        return embedded.Append(own).Max();
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);
}
