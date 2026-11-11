using System.Web;
using Vesper.MediaResolver.Security;

namespace Vesper.MediaResolver.Utilities;

public static class UrlUtil
{
    public static bool IsHttp(Uri u) => u.IsAbsoluteUri && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>True when the host equals one of the domains or is a subdomain of it.</summary>
    public static bool HostIs(Uri u, params string[] domains)
    {
        string h = u.Host;
        foreach (var d in domains)
            if (h.Equals(d, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Resolves a (possibly relative) reference against a base. Anything that is not http(s) after resolution
    /// (file:, javascript:, data:, ftp: ...) or that carries credentials yields null.
    /// </summary>
    public static Uri? Resolve(Uri baseUri, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        reference = reference.Trim();
        if (reference.Length > UrlValidator.MaxUrlLength) return null;
        if (!Uri.TryCreate(baseUri, reference, out var u)) return null;
        return IsHttp(u) && string.IsNullOrEmpty(u.UserInfo) ? u : null;
    }

    public static Uri? TryAbsolute(string? s) =>
        Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) && IsHttp(u) && string.IsNullOrEmpty(u.UserInfo) ? u : null;

    public static string? QueryValue(Uri u, string name)
    {
        if (string.IsNullOrEmpty(u.Query)) return null;
        return HttpUtility.ParseQueryString(u.Query)[name];
    }

    /// <summary>scheme://host[:port] with default ports omitted.</summary>
    public static string Origin(Uri u) => u.GetLeftPart(UriPartial.Authority);

    public static Uri WithoutFragment(Uri u) => string.IsNullOrEmpty(u.Fragment) ? u : new Uri(u.GetLeftPart(UriPartial.Query));

    public static string RedactText(string text) => UrlRedactor.Redact(text);

    public static string? Extension(Uri u)
    {
        string path = u.AbsolutePath;
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        if (dot < 0 || dot < slash || path.Length - dot > 8) return null;
        return path[(dot + 1)..].ToLowerInvariant();
    }
}
