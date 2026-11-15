using System.Text.Json;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Reads verified Instagram media links already present in a post page; player-only media stays browser-led.</summary>
internal sealed class InstagramAdapter : AdapterBase
{
    private const string PageAccept = "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5";
    private const int MaxSjsScripts = 8;
    private const int MaxEmbeddedJsonChars = 1_500_000;
    private const int MaxVideoChoices = 16;
    private readonly GenericAdapter _generic;

    public static readonly AdapterDescriptor Descriptor = new(
        "instagram", "Instagram", Matches, svc => new InstagramAdapter(svc),
        new AdapterCapabilities(
            Domains: ["instagram.com"],
            UrlPatterns: ["instagram.com/p/{code}", "instagram.com/reel/{code}", "instagram.com/tv/{code}", "instagram.com/stories/{user}/{id}"],
            SingleItem: true, Playlist: false, AudioOnly: false, Subtitles: true,
            Authentication: "Uses the page or explicitly authorized browser context; private content still requires access",
            KnownUnsupported: ["player-only media without an observed whole-object source", "multi-video carousel/story sequence enumeration", "private, expired or access-gated media"],
            Notes: "Reads bounded page-embedded media URLs and verifies them through the shared candidate pipeline."));

    private InstagramAdapter(ResolverServices svc) : base(svc) => _generic = new GenericAdapter(svc);

    public override bool CanHandle(Uri url) => InstagramTarget.TryParse(url, out _);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        if (!InstagramTarget.TryParse(request.Url, out InstagramTarget target))
            return Fail(MediaErrors.NotThisSite("Not an Instagram post, Reel or story URL"));

        HttpTextResponse page;
        try
        {
            page = await Http.GetStringAsync(request.Url, request.AuthContext, ct, Options.MaxPageBytes, PageAccept)
                .ConfigureAwait(false);
        }
        catch (MediaResolverException ex)
        {
            return Fail(ex.Error);
        }

        if (!page.IsSuccess) return Fail(MediaErrors.FromHttpStatus(page.StatusCode, page.FinalUrl));
        if (IsLoginPage(page.FinalUrl, page.Content))
            return Fail(MediaErrors.LoginRequired("Instagram redirected this page to sign-in. Open it in a browser account that can access the post."));
        if (!page.IsText)
            return Fail(MediaErrors.NoMedia("Instagram returned a non-text page without a verified media URL. Open the post in Chrome and use the VESPER picker.", true));

        request.PageResponse = page;
        List<MediaCandidate> candidates = ExtractEmbeddedVideos(page.Content);
        MediaDiscoveryResult result = await _generic.ResolveAsync(request, candidates, ct, includeBrowserCandidates: false)
            .ConfigureAwait(false);
        if (result.Item is { } item)
            return MediaDiscoveryResult.FromItem(item with { Provider = "instagram", MediaId = target.MediaId }, result.Warnings, result.Notes);
        if (result.Collection is not null) return result;

        if (result.Error?.Code == MediaErrorCode.NoDownloadableMedia)
        {
            if (IsLoginPage(page.FinalUrl, page.Content))
                return Fail(MediaErrors.LoginRequired("Instagram returned a sign-in page instead of accessible media."));
            return Fail(MediaErrors.BrowserContext("Instagram exposed page metadata but no verified playable URL. Open the accessible post or story in Chrome and use the VESPER picker; the browser can observe media that the page does not publish in its HTML."));
        }
        return result;
    }

    private static List<MediaCandidate> ExtractEmbeddedVideos(string html)
    {
        var results = new List<MediaCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int scripts = 0;
        foreach (HtmlTag tag in HtmlScanner.Scan(html))
        {
            if (tag.Name != "script" || tag.Attr("data-sjs") is null) continue;
            if (++scripts > MaxSjsScripts) break;
            if (tag.Body is not { Length: > 0 and <= MaxEmbeddedJsonChars } body) continue;
            using JsonDocument? document = JsonExt.TryParse(body);
            if (document is null) continue;

            foreach (JsonElement media in document.RootElement.FindAll("if_not_gated_logged_out", maxResults: 32))
            {
                if (!HasVideoSource(media)) continue;
                double? duration = media.P("video_duration").Dbl();
                if (duration is not { } seconds || !double.IsFinite(seconds) || seconds < 0 || seconds > 86_400)
                    duration = null;

                foreach (JsonElement version in media.P("video_versions").Items())
                    AddCandidate(version.P("url").Str(), version, duration, results, seen);
                AddCandidate(media.P("video_url").Str(), media, duration, results, seen);
                if (results.Count > 0) return results;
            }
        }
        return results;
    }

    private static bool HasVideoSource(JsonElement media) =>
        media.P("video_versions") is { ValueKind: JsonValueKind.Array }
        || !string.IsNullOrWhiteSpace(media.P("video_url").Str());

    private static void AddCandidate(string? rawUrl, JsonElement metadata, double? duration,
        List<MediaCandidate> results, HashSet<string> seen)
    {
        if (results.Count >= MaxVideoChoices || UrlUtil.TryAbsolute(rawUrl) is not { } url
            || url.Scheme is not ("http" or "https") || !IsInstagramMediaHost(url) || !seen.Add(url.AbsoluteUri))
            return;

        int? width = PositiveDimension(metadata.P("width").Int());
        int? height = PositiveDimension(metadata.P("height").Int());
        results.Add(new MediaCandidate(url, CandidateKind.Direct, "video/mp4", width, height,
            DurationSeconds: duration));
    }

    private static int? PositiveDimension(int? value) => value is > 0 and <= 16_384 ? value : null;

    private static bool IsInstagramMediaHost(Uri url)
    {
        string host = url.IdnHost;
        return host.Equals("cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("fbcdn.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginPage(Uri url, string body)
    {
        if (url.Host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.StartsWith("/accounts/login", StringComparison.OrdinalIgnoreCase))
            return true;
        return body.Contains("login_required", StringComparison.OrdinalIgnoreCase)
            || body.Contains("loginAndSignupPage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(Uri url) => InstagramTarget.TryParse(url, out _);

    private readonly record struct InstagramTarget(string MediaId)
    {
        public static bool TryParse(Uri url, out InstagramTarget target)
        {
            target = default;
            string host = url.IdnHost;
            if (!host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase)) return false;

            string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] is "p" or "reel" or "reels" or "tv"
                && parts[1] is not "audio")
            {
                target = new InstagramTarget(parts[1]);
                return parts[1].Length is > 0 and <= 128;
            }

            if (parts.Length >= 2 && parts[0] == "stories")
            {
                string id = parts.Length >= 3 ? parts[2] : parts[1];
                if (id.Length is > 0 and <= 128)
                {
                    target = new InstagramTarget("story-" + id);
                    return true;
                }
            }
            return false;
        }
    }
}
