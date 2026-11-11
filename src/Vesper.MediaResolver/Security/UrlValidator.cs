using System.Net;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Security;

/// <summary>Scheme/host gatekeeping applied to every URL before any request and to every redirect hop.</summary>
public static class UrlValidator
{
    public const int MaxUrlLength = 4096;

    private static readonly string[] BlockedSuffixes =
        [".localhost", ".local", ".internal", ".home.arpa", ".lan", ".intranet", ".corp", ".localdomain"];

    public static bool TryParse(string? text, out Uri uri, out string? reason)
    {
        uri = null!;
        reason = null;
        if (string.IsNullOrWhiteSpace(text)) { reason = "empty"; return false; }
        text = text.Trim();
        if (text.Length > MaxUrlLength) { reason = "too long"; return false; }
        if (text.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0) { reason = "control characters"; return false; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) { reason = "not an absolute URL"; return false; }
        uri = parsed;
        return IsAllowedShape(parsed, out reason);
    }

    /// <summary>Syntactic checks only (no DNS). Literal private IPs and local host names are rejected here.</summary>
    public static bool IsAllowed(Uri uri, ResolverOptions options, out string? reason)
    {
        if (!IsAllowedShape(uri, out reason)) return false;
        if (options.AllowPrivateNetworks) return true;

        string host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { reason = "localhost"; return false; }
        foreach (var s in BlockedSuffixes)
            if (host.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { reason = "local/internal host name"; return false; }
        if (!host.Contains('.') && !host.Contains(':')) { reason = "single-label host name"; return false; }
        if (IPAddress.TryParse(host, out var ip) && !IpAddressClassifier.IsPublic(ip)) { reason = "non-public IP address"; return false; }
        return true;
    }

    public static void EnsureAllowed(Uri uri, ResolverOptions options)
    {
        if (!IsAllowed(uri, options, out var reason))
            throw new MediaResolverException(MediaErrors.Blocked($"URL rejected ({reason}): {UrlRedactor.Redact(uri)}"));
    }

    private static bool IsAllowedShape(Uri uri, out string? reason)
    {
        reason = null;
        if (!uri.IsAbsoluteUri) { reason = "relative"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) { reason = $"scheme '{uri.Scheme}' not allowed"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { reason = "embedded credentials"; return false; }
        if (string.IsNullOrEmpty(uri.Host)) { reason = "missing host"; return false; }
        if (uri.AbsoluteUri.Length > MaxUrlLength) { reason = "too long"; return false; }
        return true;
    }
}
