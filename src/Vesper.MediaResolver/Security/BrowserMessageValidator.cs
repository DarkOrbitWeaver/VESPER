using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Security;

internal sealed class ValidatedBrowserContext
{
    public required Uri PageUrl { get; init; }
    public required MediaRequestContext Context { get; init; }
    public string? Title { get; init; }
    public List<MediaCandidate> Candidates { get; } = new();
    public List<SubtitleTrack> Subtitles { get; } = new();
    public List<AudioTrack> AudioTracks { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Treats the browser message as hostile. Credentials are used only with explicit consent, only for the page's own
/// origin (plus declared subdomains), only for allow-listed headers, and never with header-injection characters.
/// </summary>
internal static class BrowserMessageValidator
{
    private const int MaxCandidates = 64, MaxSubtitles = 64, MaxAudioTracks = 32, MaxCookieChars = 8192, MaxHeaderChars = 4096, MaxExtraHosts = 5;

    public static ValidatedBrowserContext Validate(BrowserDiscoveryMessage m, DateTimeOffset now, ResolverOptions options)
    {
        if (m.Schema is null || !m.Schema.StartsWith("vesper.discovery/1", StringComparison.Ordinal))
            throw new MediaResolverException(MediaErrors.Invalid($"Unsupported browser message schema '{TextUtil.Clip(m.Schema, 40)}'"));
        if (!UrlValidator.TryParse(m.PageUrl, out var page, out var why))
            throw new MediaResolverException(MediaErrors.Invalid($"Invalid pageUrl: {why}"));
        UrlValidator.EnsureAllowed(page, options);

        var warnings = new List<string>();
        MediaRequestContext ctx = BuildContext(m, page, now, warnings);

        var v = new ValidatedBrowserContext
        {
            PageUrl = page,
            Context = ctx,
            Title = string.IsNullOrWhiteSpace(m.Title) ? null : TextUtil.Clip(StripControl(m.Title).Trim(), 300)
        };
        v.Warnings.AddRange(warnings);

        foreach (var c in m.Candidates.Take(MaxCandidates))
        {
            if (!UrlValidator.TryParse(c.Url, out var u, out _) || !UrlValidator.IsAllowed(u, options, out var reason))
            {
                v.Warnings.Add($"Dropped candidate with invalid or blocked URL ({UrlRedactor.Redact(c.Url)})");
                continue;
            }
            var kind = c.Kind?.ToLowerInvariant() switch
            {
                "hls" => CandidateKind.Hls,
                "dash" => CandidateKind.Dash,
                "direct" => CandidateKind.Direct,
                _ => MediaCandidate.Classify(u, c.MimeType)
            };
            v.Candidates.Add(new MediaCandidate(
                UrlUtil.WithoutFragment(u), kind, Short(c.MimeType, 80), Dim(c.Width), Dim(c.Height),
                c.Bitrate is > 0 and < 10_000_000_000 ? c.Bitrate : null,
                c.DurationSeconds is > 0 and < 8_640_000 ? c.DurationSeconds : null,
                Short(c.Label, 80)));
        }
        if (m.Candidates.Count > MaxCandidates) v.Warnings.Add($"Only the first {MaxCandidates} candidates were considered");

        foreach (var s in m.Subtitles.Take(MaxSubtitles))
        {
            if (UrlValidator.TryParse(s.Url, out var su, out _) && UrlValidator.IsAllowed(su, options, out _))
                v.Subtitles.Add(new SubtitleTrack(su, TextUtil.NormalizeLang(s.Language), Short(s.Label, 80),
                    Short(s.Format, 12) ?? UrlUtil.Extension(su) ?? "vtt"));
        }
        foreach (var a in m.AudioTracks.Take(MaxAudioTracks))
            v.AudioTracks.Add(new AudioTrack($"browser:{v.AudioTracks.Count}", a.Language is null ? null : TextUtil.NormalizeLang(a.Language), Short(a.Label, 80)));

        return v;
    }

    private static MediaRequestContext BuildContext(BrowserDiscoveryMessage m, Uri page, DateTimeOffset now, List<string> warnings)
    {
        string? ua = m.UserAgent is { Length: > 0 and <= 256 } u && IsPrintableAscii(u) ? u : null;
        var baseCtx = new MediaRequestContext
        {
            Referer = UrlUtil.WithoutFragment(page).AbsoluteUri,
            UserAgent = ua
        };

        var auth = m.Authorization;
        if (auth is null) return baseCtx;

        if (!auth.ConsentGranted)
        {
            warnings.Add("Authorization context ignored: user consent flag is not set");
            return baseCtx;
        }
        if (!string.Equals(auth.Origin?.TrimEnd('/'), UrlUtil.Origin(page), StringComparison.OrdinalIgnoreCase))
            throw new MediaResolverException(MediaErrors.Blocked("Authorization origin does not match the page origin; credentials rejected"));
        if (auth.ExpiresAt is { } exp && exp <= now)
            throw new MediaResolverException(MediaErrors.Expired("Browser authorization context has expired"));

        string? cookie = null;
        if (!string.IsNullOrEmpty(auth.CookieHeader))
        {
            if (auth.CookieHeader.Length > MaxCookieChars || HasControl(auth.CookieHeader))
                throw new MediaResolverException(MediaErrors.Invalid("Cookie header is too long or contains control characters"));
            cookie = auth.CookieHeader;
        }

        var sensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? acceptLanguage = null;
        foreach (var kv in auth.Headers ?? new())
        {
            if (kv.Value.Length > MaxHeaderChars || HasControl(kv.Value) || HasControl(kv.Key))
                throw new MediaResolverException(MediaErrors.Invalid("Forwarded header contains control characters or is too long"));
            if (kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) sensitive["Authorization"] = kv.Value;
            else if (kv.Key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)) acceptLanguage = kv.Value;
            else warnings.Add($"Header '{TextUtil.Clip(kv.Key, 30)}' is not on the forward allow-list and was dropped");
        }

        var hosts = new List<string> { page.Host };
        foreach (var h in (auth.AllowedHosts ?? new()).Take(MaxExtraHosts))
        {
            string host = h.Trim().ToLowerInvariant();
            bool ok = Uri.CheckHostName(host) == UriHostNameType.Dns &&
                      (host == page.Host.ToLowerInvariant() || host.EndsWith("." + page.Host.ToLowerInvariant(), StringComparison.Ordinal));
            if (ok) hosts.Add(host);
            else warnings.Add($"Credential host '{TextUtil.Clip(host, 60)}' is not the page host or one of its subdomains and was dropped");
        }

        return baseCtx with
        {
            Origin = UrlUtil.Origin(page),
            AcceptLanguage = acceptLanguage,
            CookieHeader = cookie,
            SensitiveHeaders = sensitive,
            CredentialOrigin = new Uri(UrlUtil.Origin(page)),
            CredentialHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static int? Dim(int? v) => v is > 0 and <= 16384 ? v : null;
    private static string? Short(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : TextUtil.Clip(StripControl(s).Trim(), max);
    private static string StripControl(string s) => new(s.Where(c => c >= 32 && c != 127).ToArray());
    private static bool HasControl(string s) => s.Any(c => c is '\r' or '\n' or '\0');
    private static bool IsPrintableAscii(string s) => s.All(c => c is >= ' ' and <= '~');
}
