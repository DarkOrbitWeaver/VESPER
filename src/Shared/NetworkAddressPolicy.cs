using System.Net;
using System.Net.Sockets;

namespace Vesper.Shared;

/// <summary>
/// The one answer to "is this address on the public internet?". The download engine and the media resolver
/// both link this file, so a private target is refused the same way wherever the connection is made.
/// Checked on literal hosts and again on every DNS answer at connect time.
/// </summary>
internal static class NetworkAddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out int length)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork && length == 4) return IsPublicV4(bytes[..4]);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && length == 16) return IsPublicV6(bytes);
        return false;
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> b)
    {
        if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;                    // this-network, private, loopback
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;                        // 100.64/10 carrier-grade NAT
        if (b[0] == 169 && b[1] == 254) return false;                                // link-local, cloud metadata
        if (b[0] == 172 && (b[1] & 0xF0) == 16) return false;                        // 172.16/12
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false;      // IETF protocol block, TEST-NET-1
        if (b[0] == 192 && b[1] == 88 && b[2] == 99) return false;                   // old 6to4 relay anycast
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;                 // benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;                  // TEST-NET-2
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;                   // TEST-NET-3
        if (b[0] >= 224) return false;                                               // multicast, reserved, broadcast
        return true;
    }

    private static bool IsPublicV6(ReadOnlySpan<byte> b)
    {
        // ::, ::1 and the old IPv4-compatible form ::a.b.c.d
        if (AllZero(b[..12])) return false;

        if (b[0] == 0xFF) return false;                                              // multicast
        if (b[0] == 0xFE && (b[1] & 0x80) != 0) return false;                        // fe80::/10 link-local, fec0::/10 site-local
        if ((b[0] & 0xFE) == 0xFC) return false;                                     // fc00::/7 unique local
        if (b[0] == 0x01 && AllZero(b[1..8])) return false;                          // 100::/64 discard-only
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;    // 2001::/32 Teredo tunnels an IPv4 host

        // These forms carry an IPv4 address inside. A gateway delivers the packet to that IPv4 host,
        // so 64:ff9b::7f00:1 is really 127.0.0.1 and has to be judged as such.
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b.Slice(2, 4));          // 2002::/16 6to4
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
        {
            if (AllZero(b[4..12])) return IsPublicV4(b.Slice(12, 4));                // 64:ff9b::/96 NAT64
            if (b[4] == 0 && b[5] == 1) return false;                                // 64:ff9b:1::/48 local-use NAT64
        }
        if (AllZero(b[..8]) && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0)
            return IsPublicV4(b.Slice(12, 4));                                       // ::ffff:0:a.b.c.d IPv4-translated
        return true;
    }

    private static bool AllZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
}
