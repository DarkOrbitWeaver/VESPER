using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Models;

/// <summary>
/// Request headers a transfer engine must send when downloading a representation.
/// Supports both page-origin credentials and per-host CDN cookies captured from Chrome's cookie store.
/// </summary>
public sealed record MediaRequestContext
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public static MediaRequestContext Empty { get; } = new();

    public string? Referer { get; init; }
    public string? Origin { get; init; }
    public string? UserAgent { get; init; }
    public string? AcceptLanguage { get; init; }

    /// <summary>Secret. Raw Cookie header value for the primary origin / allowed hosts.</summary>
    public string? CookieHeader { get; init; }

    /// <summary>Secret. Cookie headers scoped to the HTTPS origin and path observed in Chrome.</summary>
    public IReadOnlyDictionary<string, string> HostCookies { get; init; } = NoHeaders;

    /// <summary>Secret. e.g. Authorization. Allow-listed by the validator.</summary>
    public IReadOnlyDictionary<string, string> SensitiveHeaders { get; init; } = NoHeaders;

    /// <summary>The origin that issued the credentials.</summary>
    public Uri? CredentialOrigin { get; init; }

    /// <summary>Exact hosts allowed to receive <see cref="CookieHeader"/> / <see cref="SensitiveHeaders"/>.</summary>
    public IReadOnlyList<string> CredentialHosts { get; init; } = [];

    public bool HasCredentials => CookieHeader is not null || HostCookies.Count > 0 || SensitiveHeaders.Count > 0;

    public string? CookieFor(Uri target)
    {
        if (target.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(target.UserInfo)) return null;
        if (target.Scheme == "https" && HostCookies.TryGetValue(target.GetLeftPart(UriPartial.Path), out string? cookie))
            return cookie;
        return MayForwardPrimaryCredentialsTo(target) ? CookieHeader : null;
    }

    public bool MayForwardCredentialsTo(Uri target) =>
        CookieFor(target) is not null || MayForwardPrimaryCredentialsTo(target) && SensitiveHeaders.Count > 0;

    public bool SupportsNativeTransfer => AcceptLanguage is null
        && SensitiveHeaders.Keys.All(name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string>? PrimaryCredentialsFor(Uri target)
    {
        if (!MayForwardPrimaryCredentialsTo(target)) return null;
        var headers = new Dictionary<string, string>(SensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        if (CookieHeader is { } cookie) headers.Add("Cookie", cookie);
        return headers.Count == 0 ? null : headers;
    }

    private bool MayForwardPrimaryCredentialsTo(Uri target)
    {
        if (CredentialOrigin is not { } origin
            || origin.Scheme != target.Scheme || origin.Port != target.Port
            || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(target.UserInfo))
            return false;

        foreach (var h in CredentialHosts)
            if (target.Host.Equals(h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Headers to apply to one specific request URL, with credentials and referrer scoped per hop.</summary>
    public IEnumerable<KeyValuePair<string, string>> HeadersFor(Uri url)
    {
        if (ReferrerFor(url) is { } safeReferer) yield return new("Referer", safeReferer);
        if (Origin is not null && url.Scheme == "https") yield return new("Origin", Origin);
        if (UserAgent is not null) yield return new("User-Agent", UserAgent);
        if (AcceptLanguage is not null) yield return new("Accept-Language", AcceptLanguage);
        if (CookieFor(url) is { } cookie) yield return new("Cookie", cookie);
        if (MayForwardPrimaryCredentialsTo(url))
        {
            foreach (var kv in SensitiveHeaders) yield return kv;
        }
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
        $"Cookie={(CookieHeader is null && HostCookies.Count == 0 ? "none" : "[redacted]")}, SensitiveHeaders={SensitiveHeaders.Count}, " +
        $"CredentialHosts=[{string.Join(",", CredentialHosts)}])";
}
