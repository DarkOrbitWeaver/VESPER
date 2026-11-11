using System.Net;
using System.Net.Sockets;

namespace Vesper.MediaResolver.Security;

/// <summary>Decides whether an address is publicly routable. Used on literal hosts and on every resolved DNS answer at connect time.</summary>
public static class IpAddressClassifier
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(ip.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(ip.GetAddressBytes()),
            _ => false
        };
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> b)
    {
        if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;                    // this-net, private, loopback
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;                        // 100.64/10 CGNAT
        if (b[0] == 169 && b[1] == 254) return false;                                // link-local / cloud metadata
        if (b[0] == 172 && (b[1] & 0xF0) == 16) return false;                        // 172.16/12
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false;      // IETF / TEST-NET-1
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;                 // benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;                  // TEST-NET-2
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;                   // TEST-NET-3
        if (b[0] >= 224) return false;                                               // multicast, reserved, broadcast
        return true;
    }

    private static bool IsPublicV6(ReadOnlySpan<byte> b)
    {
        bool first12Zero = true;
        for (int i = 0; i < 12; i++) if (b[i] != 0) { first12Zero = false; break; }
        if (first12Zero) return false;                                               // ::, ::1, v4-compatible

        if (b[0] == 0xFE && (b[1] & 0x80) != 0) return false;                        // fe80::/10 link-local, fec0::/10 site-local
        if ((b[0] & 0xFE) == 0xFC) return false;                                     // fc00::/7 unique local
        if (b[0] == 0xFF) return false;                                              // multicast
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;    // Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b.Slice(2, 4));          // 6to4 embeds IPv4
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)               // 64:ff9b::/96 NAT64
        {
            bool zeros = true;
            for (int i = 4; i < 12; i++) if (b[i] != 0) { zeros = false; break; }
            if (zeros) return IsPublicV4(b.Slice(12, 4));
        }
        return true;
    }
}
