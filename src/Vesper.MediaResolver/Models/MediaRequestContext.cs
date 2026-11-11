using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Models;

/// <summary>
/// Request headers a transfer engine must send when downloading a representation.
/// Non-secret headers (Referer, Origin, User-Agent, Accept-Language) may go to any host the resolver validated.
/// Cookie and sensitive headers are forwarded ONLY to hosts matching <see cref="CredentialHosts"/> (exact or subdomain),
/// on every request hop including redirects. Engines must enforce this when following redirects.
/// </summary>
public sealed record MediaRequestContext
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public static MediaRequestContext Empty { get; } = new();

    public string? Referer { get; init; }
    public string? Origin { get; init; }
    public string? UserAgent { get; init; }
    public string? AcceptLanguage { get; init; }

    /// <summary>Secret. Raw Cookie header value.</summary>
    public string? CookieHeader { get; init; }

    /// <summary>Secret. e.g. Authorization. Allow-listed by the validator.</summary>
    public IReadOnlyDictionary<string, string> SensitiveHeaders { get; init; } = NoHeaders;

    /// <summary>Hosts (and their subdomains) that may receive <see cref="CookieHeader"/> / <see cref="SensitiveHeaders"/>.</summary>
    public IReadOnlyList<string> CredentialHosts { get; init; } = [];

    public bool HasCredentials => CookieHeader is not null || SensitiveHeaders.Count > 0;

    public bool MayForwardCredentialsTo(string host)
    {
        foreach (var h in CredentialHosts)
            if (host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Headers to apply to one specific request URL (credentials filtered per host).</summary>
    public IEnumerable<KeyValuePair<string, string>> HeadersFor(Uri url)
    {
        if (Referer is not null) yield return new("Referer", Referer);
        if (Origin is not null) yield return new("Origin", Origin);
        if (UserAgent is not null) yield return new("User-Agent", UserAgent);
        if (AcceptLanguage is not null) yield return new("Accept-Language", AcceptLanguage);
        if (!MayForwardCredentialsTo(url.Host)) yield break;
        if (CookieHeader is not null) yield return new("Cookie", CookieHeader);
        foreach (var kv in SensitiveHeaders) yield return kv;
    }

    public override string ToString() =>
        $"MediaRequestContext(Referer={(Referer is null ? "-" : UrlUtil.RedactText(Referer))}, " +
        $"Cookie={(CookieHeader is null ? "none" : "[redacted]")}, SensitiveHeaders={SensitiveHeaders.Count}, " +
        $"CredentialHosts=[{string.Join(",", CredentialHosts)}])";
}
