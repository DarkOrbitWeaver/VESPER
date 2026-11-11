using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// Bilibili videos through the site's public web API (view + playurl, DASH). Guest access exposes the qualities the
/// site gives anonymous viewers; paid, region-locked and members-only videos return typed errors. Segments are plain
/// HTTP files that need the Bilibili Referer (carried in the request context).
/// </summary>
internal sealed class BilibiliAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "bilibili", "Bilibili", Matches, svc => new BilibiliAdapter(svc),
        new AdapterCapabilities(
            Domains: ["bilibili.com", "b23.tv"],
            UrlPatterns: ["bilibili.com/video/BV{id}[?p=N]", "bilibili.com/video/av{aid}", "b23.tv/{code}"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: false,
            Authentication: "None (guest qualities only)",
            KnownUnsupported: ["bangumi / cheese (paid) content", "live rooms", "login-only qualities", "subtitles (need login)"],
            Notes: "Multi-part videos expose their parts as a collection (CollectionIntent.CollectionOnly)."));

    public BilibiliAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) =>
        u.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase) || (UrlUtil.HostIs(u, "bilibili.com") && ParseVideo(u) is not null);

    public override bool CanHandle(Uri url) => Matches(url);

    internal static (string? Bvid, long? Aid)? ParseVideo(Uri u)
    {
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.IndexOf(segs, "video");
        if (i < 0 || i + 1 >= segs.Length) return null;
        string s = segs[i + 1];
        if (s.Length == 12 && s.StartsWith("BV", StringComparison.Ordinal) && s.All(char.IsAsciiLetterOrDigit)) return (s, null);
        if (s.Length > 2 && s[..2].Equals("av", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], out var aid)) return (null, aid);
        return null;
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var pageUrl = request.Url;
        if (pageUrl.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
            pageUrl = (await Http.ProbeAsync(pageUrl, null, ct).ConfigureAwait(false)).FinalUrl;

        var id = UrlUtil.HostIs(pageUrl, "bilibili.com") ? ParseVideo(pageUrl) : null;
        if (id is null) return Fail(MediaErrors.NotThisSite("Not a Bilibili video URL"));

        var ctx = new MediaRequestContext { Referer = "https://www.bilibili.com/" };
        string idQuery = id.Value.Bvid is { } bv ? $"bvid={bv}" : $"aid={id.Value.Aid}";

        using var viewDoc = await GetJsonAsync(new Uri($"https://api.bilibili.com/x/web-interface/view?{idQuery}"), ctx, ct).ConfigureAwait(false);
        var view = viewDoc.RootElement;
        int code = view.P("code").Int() ?? -1;
        if (code != 0) return Fail(MapCode(code, view.P("message").Str(), "view"));
        var data = view.P("data");
        if (data is null) return Fail(MediaErrors.SourceChanged("Bilibili view response has no data"));

        string bvid = data.P("bvid").Str() ?? id.Value.Bvid ?? "";
        string title = data.P("title").Str() ?? bvid;
        var pages = data.P("pages").Items().ToList();
        int pageNo = int.TryParse(UrlUtil.QueryValue(pageUrl, "p"), out var pn) && pn >= 1 && pn <= Math.Max(1, pages.Count) ? pn : 1;
        long? cid = pages.Count >= pageNo ? pages[pageNo - 1].P("cid").Long() : data.P("cid").Long();
        if (cid is null) return Fail(MediaErrors.SourceChanged("Bilibili view response has no cid"));

        // Multi-part videos: collection on request, otherwise the single selected part plus a hint.
        if (pages.Count > 1 && request.Options.CollectionIntent == CollectionIntent.CollectionOnly)
        {
            var entries = pages.Take(request.Options.MaxCollectionItems).Select((p, i) => new CollectionEntry
            {
                MediaId = $"{bvid}_p{p.P("page").Int() ?? i + 1}",
                Url = new Uri($"https://www.bilibili.com/video/{bvid}?p={p.P("page").Int() ?? i + 1}"),
                Title = p.P("part").Str(),
                Duration = TextUtil.Seconds(p.P("duration").Dbl()),
                Index = i
            }).ToList();
            return MediaDiscoveryResult.FromCollection(new MediaCollection
            {
                SourceUrl = request.Url, Provider = "bilibili", CollectionId = bvid, Title = title, Kind = CollectionKind.Playlist,
                TotalCount = pages.Count, Entries = entries, IsComplete = entries.Count == pages.Count
            });
        }

        using var playDoc = await GetJsonAsync(new Uri($"https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&fnval=4048&fourk=1"), ctx, ct).ConfigureAwait(false);
        var play = playDoc.RootElement;
        int pcode = play.P("code").Int() ?? -1;
        if (pcode != 0) return Fail(MapCode(pcode, play.P("message").Str(), "playurl"));
        var pdata = play.P("data");

        var reps = new List<MediaRepresentation>();
        var dash = pdata.P("dash");
        int idx = 0;
        if (dash is not null)
        {
            foreach (var v in dash.P("video").Items())
            {
                var url = U(v.P("baseUrl").Str() ?? v.P("base_url").Str());
                if (url is null) continue;
                int qn = v.P("id").Int() ?? 0;
                string? codecs = v.P("codecs").Str();
                CodecInfo.Split(codecs, out var vc, out _, out var hdr0);
                reps.Add(new MediaRepresentation
                {
                    Id = $"bili-v-{qn}-{vc ?? "x"}-{idx++}",
                    Url = url,
                    Protocol = StreamProtocol.Http,
                    Content = StreamContent.VideoOnly,
                    Container = "mp4",
                    VideoCodec = vc,
                    RawCodecs = codecs,
                    Width = v.P("width").Int(),
                    Height = v.P("height").Int(),
                    FrameRate = v.P("frameRate").Dbl() is > 0 and var fps ? fps : null,
                    Hdr = qn == 125 ? HdrKind.Hdr10 : qn == 126 ? HdrKind.DolbyVision : hdr0,
                    BitrateKbps = v.P("bandwidth").Int() is { } bw ? bw / 1000 : null,
                    ExpiresAt = ExpiryParser.FromUrl(url),
                    Context = ctx
                });
            }
            foreach (var a in dash.P("audio").Items())
            {
                var url = U(a.P("baseUrl").Str() ?? a.P("base_url").Str());
                if (url is null) continue;
                string? codecs = a.P("codecs").Str();
                reps.Add(new MediaRepresentation
                {
                    Id = $"bili-a-{a.P("id").Int() ?? 0}-{idx++}",
                    Url = url,
                    Protocol = StreamProtocol.Http,
                    Content = StreamContent.AudioOnly,
                    Container = "m4a",
                    AudioCodec = CodecInfo.NormalizeAudio(codecs),
                    RawCodecs = codecs,
                    BitrateKbps = a.P("bandwidth").Int() is { } bw ? bw / 1000 : null,
                    ExpiresAt = ExpiryParser.FromUrl(url),
                    Context = ctx
                });
            }
        }
        else
        {
            var durl = pdata.P("durl").Items().ToList();
            if (durl.Count > 1)
                return Fail(MediaErrors.UnsupportedManifest("Legacy multi-part FLV delivery is not supported"));
            if (durl.Count == 1 && U(durl[0].P("url").Str()) is { } single)
            {
                string fmt = (pdata.P("format").Str() ?? "").ToLowerInvariant();
                reps.Add(new MediaRepresentation
                {
                    Id = "bili-legacy",
                    Url = single,
                    Protocol = StreamProtocol.Http,
                    Content = StreamContent.VideoAndAudio,
                    Container = fmt.StartsWith("flv") ? "flv" : "mp4",
                    FileSize = durl[0].P("size").Long(),
                    ExpiresAt = ExpiryParser.FromUrl(single),
                    Context = ctx
                });
            }
        }

        if (reps.Count == 0)
            return Fail(MediaErrors.NoMedia("Bilibili playurl returned no streams for guest access", browserMayHelp: true));

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "bilibili",
            MediaId = pages.Count > 1 ? $"{bvid}_p{pageNo}" : bvid,
            Title = pages.Count > 1 && pages[pageNo - 1].P("part").Str() is { Length: > 0 } part ? $"{title} - {part}" : title,
            Description = data.P("desc").Str(),
            Uploader = data.P("owner").P("name").Str(),
            Duration = TextUtil.Seconds(pages.Count >= pageNo ? pages[pageNo - 1].P("duration").Dbl() : data.P("duration").Dbl()),
            Thumbnail = U(data.P("pic").Str()),
            Representations = reps,
            Related = pages.Count > 1
                ? new CollectionHint(CollectionKind.Playlist, bvid, new Uri($"https://www.bilibili.com/video/{bvid}"), false, title)
                : null
        };
        return MediaDiscoveryResult.FromItem(item);
    }

    private static MediaError MapCode(int code, string? message, string stage)
    {
        string diag = $"Bilibili {stage} code {code}: {TextUtil.Clip(message, 120)}";
        return code switch
        {
            -404 or 62002 or 62004 or 62012 => MediaErrors.Unavailable(diag),
            -403 or 87007 or 87008 => MediaErrors.LoginRequired(diag),
            -10403 => MediaErrors.Region(diag),
            -412 or -799 => MediaErrors.RateLimited(diag),
            _ => MediaErrors.SourceChanged(diag)
        };
    }
}
