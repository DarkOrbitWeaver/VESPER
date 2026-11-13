using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Models;

/// <summary>
/// Request headers a transfer engine must send when downloading a representation.
/// Credentials are forwarded only to explicitly allowed hosts on the same scheme and port as <see cref="CredentialOrigin"/>.
/// Referrers follow the browser default: full URL for the same origin, origin only cross-origin, and none on downgrade.
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

    /// <summary>The origin that issued the credentials. Credentials never cross its scheme or port.</summary>
    public Uri? CredentialOrigin { get; init; }

    /// <summary>Hosts (and their subdomains) allowed to receive credentials from <see cref="CredentialOrigin"/>.</summary>
    public IReadOnlyList<string> CredentialHosts { get; init; } = [];

    public bool HasCredentials => CookieHeader is not null || SensitiveHeaders.Count > 0;

    public bool MayForwardCredentialsTo(Uri target)
    {
        if (CredentialOrigin is not { } origin
            || origin.Scheme != target.Scheme
            || origin.Port != target.Port
            || !string.IsNullOrEmpty(origin.UserInfo))
            return false;

        foreach (var h in CredentialHosts)
            if (target.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                || target.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Headers to apply to one specific request URL, with credentials and referrer scoped per hop.</summary>
    public IEnumerable<KeyValuePair<string, string>> HeadersFor(Uri url)
    {
        if (ReferrerFor(url) is { } safeReferer) yield return new("Referer", safeReferer);
        if (Origin is not null) yield return new("Origin", Origin);
        if (UserAgent is not null) yield return new("User-Agent", UserAgent);
        if (AcceptLanguage is not null) yield return new("Accept-Language", AcceptLanguage);
        if (!MayForwardCredentialsTo(url)) yield break;
        if (CookieHeader is not null) yield return new("Cookie", CookieHeader);
        foreach (var kv in SensitiveHeaders) yield return kv;
    }

    private string? ReferrerFor(Uri target)
    {
        if (!Uri.TryCreate(Referer, UriKind.Absolute, out Uri? source)
            || source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(source.UserInfo)
            || source.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
            return null;

        bool sameOrigin = source.Scheme == target.Scheme
            && source.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)
            && source.Port == target.Port;
        var builder = new UriBuilder(source) { Fragment = "" };
        if (!sameOrigin)
        {
            builder.Path = "/";
            builder.Query = "";
        }
        return builder.Uri.AbsoluteUri;
    }

    public override string ToString() =>
        $"MediaRequestContext(Referer={(Referer is null ? "-" : UrlUtil.RedactText(Referer))}, " +
        $"Cookie={(CookieHeader is null ? "none" : "[redacted]")}, SensitiveHeaders={SensitiveHeaders.Count}, " +
        $"CredentialHosts=[{string.Join(",", CredentialHosts)}])";
}
