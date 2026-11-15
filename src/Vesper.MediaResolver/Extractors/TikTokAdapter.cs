using System.Text.Json;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Reads public player state. No signature generation, challenge execution or authentication bypass.</summary>
internal sealed class TikTokAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "tiktok", "TikTok", Matches, svc => new TikTokAdapter(svc),
        new AdapterCapabilities(
            Domains: ["tiktok.com", "vm.tiktok.com", "vt.tiktok.com"],
            UrlPatterns: ["tiktok.com/@{user}/video/{id}", "vm.tiktok.com/{code}", "vt.tiktok.com/{code}", "tiktok.com/t/{code}"],
            SingleItem: true, Playlist: false, AudioOnly: false, Subtitles: false,
            Authentication: "Public videos; optional explicitly authorized browser context",
            KnownUnsupported: ["user pages and collections", "photo posts", "private videos", "pages gated by captcha"],
            Notes: "Reads universal and legacy player state; preserves signed media URLs and alternate CDN addresses."));

    public TikTokAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "tiktok.com")
        && (u.Host.StartsWith("vm.", StringComparison.OrdinalIgnoreCase)
            || u.Host.StartsWith("vt.", StringComparison.OrdinalIgnoreCase)
            || u.AbsolutePath.StartsWith("/@", StringComparison.Ordinal) && u.AbsolutePath.Contains("/video/", StringComparison.Ordinal)
            || u.AbsolutePath.StartsWith("/t/", StringComparison.Ordinal));

    public override bool CanHandle(Uri url) => Matches(url);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var page = await GetPageAsync(request.Url, request.AuthContext, ct).ConfigureAwait(false);
        request.PageResponse = page;
        JsonDocument? state = null;
        JsonElement? item = null;
        try
        {
            string? videoId = VideoId(page.FinalUrl) ?? VideoId(request.Url);
            var found = FindPlayerState(page.Content, videoId);
            state = found.Document;
            item = found.Item;
            if (found.Status is 10222 or 10223)
                return Fail(MediaErrors.LoginRequired("TikTok: private video"));
            if (found.Status == 10204)
                return Fail(MediaErrors.Region($"TikTok statusCode 10204: {found.Message} (not available from this network or region)"));
            if (found.Status != 0)
                return Fail(MediaErrors.Unavailable($"TikTok statusCode {found.Status}: {found.Message}"));
            if (item is null)
                return Fail(page.Content.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                    ? MediaErrors.BrowserContext("TikTok returned a verification page. Complete verification in your browser; VESPER does not bypass it.")
                    : MediaErrors.SourceChanged($"TikTok player state was not found{(found.Layout is null ? "" : $" in {found.Layout}")}. Refresh the page or update VESPER."));
            var s = item;
            if (s.P("imagePost") is not null)
                return Fail(MediaErrors.NoMedia("This TikTok post is a photo slideshow, not a video", browserMayHelp: false));
            var video = s.P("video");
            request.Publish(new DiscoveryProgress(s.P("desc").Str(), Address(video.P("cover"))));

            var auth = request.AuthContext;
            MediaRequestContext ContextFor(Uri target)
            {
                // CookieContainer preserves the server's Domain/Path/Secure/expiry rules. Anonymous
                // .tiktok.com sessions work on its media subdomains; host-only cookies stay host-only.
                string issued = page.SessionCookies.GetCookieHeader(target);
                bool signedIn = auth.MayForwardCredentialsTo(target);
                string? cookie = signedIn && auth.CookieHeader is not null ? auth.CookieHeader : issued.Length > 0 ? issued : null;
                return new MediaRequestContext
                {
                    Referer = page.FinalUrl.AbsoluteUri,
                    UserAgent = auth.UserAgent ?? Options.UserAgent,
                    CookieHeader = cookie is { Length: <= 8192 } ? cookie : null,
                    SensitiveHeaders = signedIn ? auth.SensitiveHeaders : MediaRequestContext.Empty.SensitiveHeaders,
                    CredentialOrigin = new Uri(target.GetLeftPart(UriPartial.Authority)), CredentialHosts = [target.Host]
                };
            }
            var reps = new List<MediaRepresentation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void AddAddresses(JsonElement? address, string id, JsonElement? bitrate = null)
            {
                int mirror = 0;
                foreach (Uri url in Addresses(address))
                {
                    if (!seen.Add(url.AbsoluteUri)) continue;
                    string? codec = bitrate.P("CodecType").Str() ?? video.P("codecType").Str();
                    string? normalized = codec?.StartsWith("h265", StringComparison.OrdinalIgnoreCase) == true
                        || codec?.StartsWith("bytevc1", StringComparison.OrdinalIgnoreCase) == true ? "h265"
                        : CodecInfo.NormalizeVideo(codec);
                    reps.Add(new MediaRepresentation
                    {
                        Id = mirror == 0 ? id : $"{id}-mirror-{mirror}", Url = url,
                        Protocol = StreamProtocol.Http, Content = StreamContent.VideoAndAudio,
                        Container = "mp4", VideoCodec = normalized, DeliveryId = url.GetLeftPart(UriPartial.Authority),
                        Width = address.P("Width").Int() ?? video.P("width").Int(),
                        Height = address.P("Height").Int() ?? video.P("height").Int(),
                        FrameRate = bitrate.P("BitrateFPS").Int(),
                        BitrateKbps = (bitrate.P("Bitrate").Int() ?? video.P("bitrate").Int()) is { } bps ? bps / 1000 : null,
                        FileSize = address.P("DataSize").Long(), ExpiresAt = ExpiryParser.FromUrl(url), Context = ContextFor(url)
                    });
                    mirror++;
                }
            }
            int index = 0;
            foreach (var b in video.P("bitrateInfo").Items())
            {
                AddAddresses(b.P("PlayAddr") ?? b.P("playAddr"), $"tiktok-{b.P("GearName").Str() ?? "quality"}-{index}", b);
                index++;
            }
            AddAddresses(video.P("playAddr"), "tiktok-play");
            AddAddresses(video.P("downloadAddr"), "tiktok-download");
            if (reps.Count == 0)
                return Fail(MediaErrors.NoMedia("TikTok supplied title and artwork but no clear media URL. The video may require sign-in, region access, or a refreshed player session.", browserMayHelp: true));
            string? author = s.P("author").P("uniqueId").Str() ?? s.P("author").Str();
            string desc = s.P("desc").Str() ?? "";
            return MediaDiscoveryResult.FromItem(new MediaItem
            {
                SourceUrl = page.FinalUrl, Provider = "tiktok", MediaId = s.P("id").Str(),
                Title = string.IsNullOrWhiteSpace(desc) ? $"TikTok video by @{author}" : desc,
                Description = desc, Uploader = author,
                Duration = video.P("duration").Int() is { } d ? TimeSpan.FromSeconds(d) : null,
                Thumbnail = Address(video.P("cover")) ?? Address(video.P("originCover")), Representations = reps
            });
        }
        finally { state?.Dispose(); }
    }

    private static Uri? Address(JsonElement? address) => Addresses(address).FirstOrDefault();

    private static (JsonDocument? Document, JsonElement? Item, int Status, string? Message, string? Layout)
        FindPlayerState(string html, string? videoId)
    {
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in HtmlScanner.Scan(html))
        {
            if (tag.IsClosing || tag.Name != "script") continue;
            string? id = tag.Attr("id");
            if (id is "__UNIVERSAL_DATA_FOR_REHYDRATION__" or "SIGI_STATE" or "__NEXT_DATA__")
                scripts.TryAdd(id, tag.Body ?? string.Empty);
        }

        foreach (var (layout, id) in new[]
        {
            ("universal", "__UNIVERSAL_DATA_FOR_REHYDRATION__"),
            ("sigi", "SIGI_STATE"),
            ("next", "__NEXT_DATA__")
        })
        {
            if (!scripts.TryGetValue(id, out string? json) || JsonExt.TryParse(json) is not { } document) continue;
            var found = FindItem(document.RootElement, layout, videoId);
            if (found.Item is not null || found.Status != 0)
                return (document, found.Item, found.Status, found.Message, layout);
            document.Dispose();
        }
        return (null, null, 0, null, null);
    }

    private static (JsonElement? Item, int Status, string? Message) FindItem(JsonElement root, string layout, string? videoId)
    {
        if (layout == "universal")
        {
            var scope = root.P("__DEFAULT_SCOPE__");
            var detail = scope.P("webapp.video-detail") ?? scope.P("webapp.reflow.video.detail");
            return (detail.P("itemInfo").P("itemStruct"), detail.P("statusCode").Int() ?? 0, detail.P("statusMsg").Str());
        }

        if (layout == "sigi")
        {
            var module = root.P("ItemModule");
            JsonElement? item = videoId is not null ? module.P(videoId) : null;
            item ??= videoId is null ? module.Props().Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
            return (item, root.P("VideoPage").P("statusCode").Int() ?? 0, root.P("VideoPage").P("statusMsg").Str());
        }

        var props = root.P("props").P("pageProps");
        return (props.P("itemInfo").P("itemStruct"), props.P("statusCode").Int() ?? 0, props.P("statusMsg").Str());
    }

    private static string? VideoId(Uri url)
    {
        string path = url.AbsolutePath;
        int at = path.IndexOf("/video/", StringComparison.Ordinal);
        if (at < 0) return null;
        string id = path[(at + 7)..].Trim('/');
        return id.Length is > 0 and <= 32 && id.All(char.IsAsciiDigit) ? id : null;
    }

    private static IEnumerable<Uri> Addresses(JsonElement? address)
    {
        if (U(address.Str()) is { } direct) { yield return direct; yield break; }
        foreach (var entry in (address.P("UrlList") ?? address.P("urlList")).Items())
            if (U(entry.Str()) is { } url) yield return url;
    }
}
