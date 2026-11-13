using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// TikTok videos from the page's embedded rehydration JSON. The anonymous session cookies the page itself sets are
/// replayed to TikTok's own hosts only (they are server-issued, not user credentials). Photo posts and
/// captcha/login pages are reported honestly.
/// </summary>
internal sealed class TikTokAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "tiktok", "TikTok", Matches, svc => new TikTokAdapter(svc),
        new AdapterCapabilities(
            Domains: ["tiktok.com", "vm.tiktok.com", "vt.tiktok.com"],
            UrlPatterns: ["tiktok.com/@{user}/video/{id}", "vm.tiktok.com/{code}", "vt.tiktok.com/{code}", "tiktok.com/t/{code}"],
            SingleItem: true, Playlist: false, AudioOnly: false, Subtitles: false,
            Authentication: "None (public videos)",
            KnownUnsupported: ["user pages and collections", "photo posts", "private videos", "pages gated by captcha"],
            Notes: "Experimental: depends on TikTok's page JSON layout; a captcha page yields BrowserContextRequired."));

    private static readonly string[] CredentialHosts = ["tiktok.com", "tiktokcdn.com", "tiktokcdn-us.com", "tiktokv.com", "tiktokv.us"];

    public TikTokAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u)
    {
        if (!UrlUtil.HostIs(u, "tiktok.com")) return false;
        if (u.Host.StartsWith("vm.", StringComparison.OrdinalIgnoreCase) || u.Host.StartsWith("vt.", StringComparison.OrdinalIgnoreCase)) return true;
        string p = u.AbsolutePath;
        return p.Contains("/video/", StringComparison.Ordinal) || p.StartsWith("/t/", StringComparison.Ordinal);
    }

    public override bool CanHandle(Uri url) => Matches(url);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var page = await GetPageAsync(request.Url, request.AuthContext, ct).ConfigureAwait(false);

        request.PageResponse = page;
        string? raw = null;
        foreach (var tag in HtmlScanner.Scan(page.Content))
        {
            if (!tag.IsClosing && tag.Name == "script" && tag.Attr("id") == "__UNIVERSAL_DATA_FOR_REHYDRATION__") { raw = tag.Body; break; }
        }
        if (raw is null)
        {
            bool captcha = page.Content.Contains("captcha", StringComparison.OrdinalIgnoreCase);
            return Fail(captcha
                ? MediaErrors.BrowserContext("TikTok returned a verification (captcha) page")
                : MediaErrors.SourceChanged("TikTok rehydration data was not found in the page"));
        }

        using var doc = JsonExt.TryParse(raw) ?? throw new MediaResolverException(MediaErrors.SourceChanged("TikTok rehydration data is not valid JSON"));
        var detail = doc.RootElement.P("__DEFAULT_SCOPE__").P("webapp.video-detail");
        if (detail is null) return Fail(MediaErrors.SourceChanged("webapp.video-detail missing"));

        int code = detail.P("statusCode").Int() ?? 0;
        if (code == 10222) return Fail(MediaErrors.LoginRequired("TikTok: private video"));
        if (code != 0) return Fail(MediaErrors.Unavailable($"TikTok statusCode {code}: {detail.P("statusMsg").Str()}"));

        var s = detail.P("itemInfo").P("itemStruct");
        if (s is null) return Fail(MediaErrors.SourceChanged("TikTok itemStruct missing"));
        if (s.P("imagePost") is not null) return Fail(MediaErrors.NoMedia("This TikTok post is a photo slideshow, not a video", browserMayHelp: false));

        string cookie = string.Join("; ", page.Cookies);
        var ctx = new MediaRequestContext
        {
            Referer = "https://www.tiktok.com/",
            CookieHeader = cookie.Length is > 0 and < 4000 ? cookie : null,
            CredentialOrigin = new Uri("https://www.tiktok.com/"),
            CredentialHosts = CredentialHosts
        };

        var video = s.P("video");
        request.Publish(new DiscoveryProgress(s.P("desc").Str(), U(video.P("cover").Str())));
        var reps = new List<MediaRepresentation>();
        int i = 0;
        foreach (var b in video.P("bitrateInfo").Items())
        {
            var play = b.P("PlayAddr");
            var url = U(play.P("UrlList").At(0).Str());
            if (url is null) continue;
            string codecName = b.P("CodecType").Str() ?? "";
            reps.Add(new MediaRepresentation
            {
                Id = $"tiktok-{b.P("GearName").Str() ?? i.ToString()}-{i}",
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = StreamContent.VideoAndAudio,
                Container = "mp4",
                VideoCodec = CodecInfo.NormalizeVideo(codecName.Replace("_", "")) ?? CodecInfo.NormalizeVideo(codecName),
                Width = play.P("Width").Int(),
                Height = play.P("Height").Int(),
                BitrateKbps = b.P("Bitrate").Int() is { } bps ? bps / 1000 : null,
                FileSize = play.P("DataSize").Long(),
                ExpiresAt = ExpiryParser.FromUrl(url),
                Context = ctx
            });
            i++;
        }

        foreach (var (name, key) in new[] { ("play", "playAddr"), ("download", "downloadAddr") })
        {
            var url = U(video.P(key).Str());
            if (url is null) continue;
            reps.Add(new MediaRepresentation
            {
                Id = $"tiktok-{name}",
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = StreamContent.VideoAndAudio,
                Container = "mp4",
                Width = video.P("width").Int(),
                Height = video.P("height").Int(),
                ExpiresAt = ExpiryParser.FromUrl(url),
                Context = ctx
            });
        }

        if (reps.Count == 0) return Fail(MediaErrors.NoMedia("TikTok item has no playable addresses", browserMayHelp: true));

        string? author = s.P("author").P("uniqueId").Str();
        string desc = s.P("desc").Str() ?? "";
        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "tiktok",
            MediaId = s.P("id").Str(),
            Title = string.IsNullOrWhiteSpace(desc) ? $"TikTok video by @{author}" : desc,
            Description = desc,
            Uploader = author,
            Duration = video.P("duration").Int() is { } d ? TimeSpan.FromSeconds(d) : null,
            Thumbnail = U(video.P("cover").Str()),
            Representations = reps
        };
        return MediaDiscoveryResult.FromItem(item);
    }
}
