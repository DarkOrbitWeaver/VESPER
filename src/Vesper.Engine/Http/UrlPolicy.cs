using System.Net;
using System.Net.Sockets;

namespace Vesper.Engine;

/// <summary>Validation of every address VESPER is asked to contact. External input is untrusted.</summary>
public static class UrlPolicy
{
    public const int MaxUrlLength = 8192;

    public static bool TryParse(string? text, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "Enter a link."; return false; }
        text = text.Trim();
        if (text.Length > MaxUrlLength) { error = "The link is too long."; return false; }
        if (text.Any(char.IsControl) || text.Any(char.IsWhiteSpace)) { error = "The link contains spaces or control characters."; return false; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)) { error = "That is not a valid link."; return false; }
        if (parsed.Scheme is not ("http" or "https")) { error = "Only http and https links are supported."; return false; }
        if (string.IsNullOrEmpty(parsed.Host)) { error = "The link has no host name."; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            // Credentials inside URLs leak into logs, history and referrers.
            error = "Links with embedded user names or passwords are not supported.";
            return false;
        }
        uri = parsed;
        return true;
    }

    /// <summary>Canonical form used for duplicate detection: no fragment, lower-case host, default port removed.</summary>
    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = "" };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    /// <summary>For logs and diagnostics: keeps the host and path, drops query strings and credentials.</summary>
    public static string Redact(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "(invalid link)";
        string query = string.IsNullOrEmpty(uri.Query) ? "" : "?…";
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}{query}";
    }

    /// <summary>Same scheme, host and port: the boundary for forwarding cookies and credentials.</summary>
    public static bool IsSameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>Same host and path; only the query (typically a signed token) may differ.</summary>
    public static bool IsSameResource(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.Ordinal);

    /// <summary>Rejects literal private targets before connecting. The connect callback re-checks resolved addresses.</summary>
    public static void EnsureAllowed(Uri uri, bool allowPrivate)
    {
        if (allowPrivate) return;
        string host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new BlockedAddressException(host);
        if (IPAddress.TryParse(host, out IPAddress? literal) && !IsPublicAddress(literal))
            throw new BlockedAddressException(host);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10
                || (b[0] == 100 && b[1] is >= 64 and <= 127)       // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                    // link-local
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);                                   // multicast and reserved
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                         // unique local fc00::/7
        }
        return false;
    }
}

public sealed class BlockedAddressException(string host)
    : IOException($"VESPER blocked a connection to a private or local address ({host}). Allow private networks in Settings if this is intentional.");
