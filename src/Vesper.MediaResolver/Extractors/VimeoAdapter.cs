using System.Text.Json;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Vimeo via the public player configuration document (progressive MP4 + HLS). Private/domain-locked videos fail honestly.</summary>
internal sealed partial class VimeoAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "vimeo", "Vimeo", Matches, svc => new VimeoAdapter(svc),
        new AdapterCapabilities(
            Domains: ["vimeo.com", "player.vimeo.com"],
            UrlPatterns: ["vimeo.com/{id}", "vimeo.com/{id}/{hash}", "vimeo.com/channels/*/{id}", "vimeo.com/groups/*/videos/{id}", "player.vimeo.com/video/{id}?h={hash}"],
            SingleItem: true, Playlist: false, AudioOnly: false, Subtitles: true,
            Authentication: "None (public and unlisted-with-hash videos only)",
            KnownUnsupported: ["private/password/domain-restricted videos", "showcases and channels", "DRM / Vimeo OTT"],
            Notes: "Reads player.vimeo.com/video/{id}/config."));

    [GeneratedRegex("^[0-9a-f]{8,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashRegex();

    public VimeoAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "vimeo.com") && TryParse(u, out _, out _);

    public override bool CanHandle(Uri url) => Matches(url);

    internal static bool TryParse(Uri u, out string id, out string? hash)
    {
        id = "";
        hash = null;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (u.Host.Equals("player.vimeo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segs.Length >= 2 && segs[0] == "video" && segs[1].All(char.IsAsciiDigit)) { id = segs[1]; hash = UrlUtil.QueryValue(u, "h"); return true; }
            return false;
        }
        for (int i = 0; i < segs.Length; i++)
        {
            if (segs[i].Length >= 3 && segs[i].All(char.IsAsciiDigit))
            {
                id = segs[i];
                hash = i + 1 < segs.Length && HashRegex().IsMatch(segs[i + 1]) ? segs[i + 1] : UrlUtil.QueryValue(u, "h");
                return true;
            }
        }
        return false;
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        if (!TryParse(request.Url, out var id, out var hash))
            return Fail(MediaErrors.NotThisSite("Not a Vimeo video URL"));

        var ctx = new MediaRequestContext { Referer = "https://vimeo.com/" };
        var cfgUrl = new Uri($"https://player.vimeo.com/video/{id}/config" + (hash is null ? "" : $"?h={Uri.EscapeDataString(hash)}"));
        var resp = await Http.GetStringAsync(cfgUrl, ctx, ct, Options.MaxJsonBytes, "application/json").ConfigureAwait(false);
        if (resp.StatusCode is 401 or 403)
            return Fail(MediaErrors.LoginRequired("Vimeo denied the player config (private, password-protected or domain-restricted)"));
        resp.EnsureSuccess();

        using var doc = JsonExt.TryParse(resp.Content)
            ?? throw new MediaResolverException(MediaErrors.SourceChanged("Vimeo config was not JSON"));
        var root = doc.RootElement;
        var video = root.P("video");
        var files = root.P("request").P("files");
        if (video is null || files is null)
        {
            string? msg = root.P("message").Str();
            return Fail(msg is null ? MediaErrors.SourceChanged("Vimeo config has no video/files section") : MediaErrors.Unavailable($"Vimeo: {msg}"));
        }

        var reps = new List<MediaRepresentation>();
        var audio = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();
        var problems = new List<MediaError>();

        foreach (var p in files.P("progressive").Items())
        {
            var url = U(p.P("url").Str());
            if (url is null) continue;
            int? h = p.P("height").Int();
            reps.Add(new MediaRepresentation
            {
                Id = $"vimeo-http-{h?.ToString() ?? p.P("quality").Str() ?? reps.Count.ToString()}",
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = StreamContent.VideoAndAudio,
                Container = Formats.CodecInfo.ContainerFromMime(p.P("mime").Str()) ?? "mp4",
                Width = p.P("width").Int(),
                Height = h,
                FrameRate = p.P("fps").Dbl(),
                ExpiresAt = ExpiryParser.FromUrl(url),
                Context = ctx
            });
        }

        TimeSpan? manifestDuration = null;
        var hls = files.P("hls");
        var cdns = hls.P("cdns");
        string? defCdn = hls.P("default_cdn").Str();
        JsonElement? cdn = defCdn is not null ? cdns.P(defCdn) : null;
        cdn ??= cdns.Props().Select(p => (JsonElement?)p.Value).FirstOrDefault();
        var hlsUrl = U(cdn.P("url").Str() ?? cdn.P("avc_url").Str());
        if (hlsUrl is not null)
            await TryAddManifestAsync(hlsUrl, ctx, "vimeo-", reps, audio, subs, problems, ct, d => manifestDuration = d).ConfigureAwait(false);

        if (reps.Count == 0)
            return Fail(PickProblem(problems, "Vimeo config contained no progressive or HLS sources"));

        var tracksBase = new Uri("https://player.vimeo.com/");
        foreach (var t in root.P("request").P("text_tracks").Items())
        {
            var u = UrlUtil.Resolve(tracksBase, t.P("url").Str());
            if (u is not null && request.Options.IncludeSubtitles)
                subs.Add(new SubtitleTrack(u, TextUtil.NormalizeLang(t.P("lang").Str()), t.P("label").Str(), "vtt"));
        }

        var thumbs = video.P("thumbs");
        string? thumb = thumbs.Props().Where(p => int.TryParse(p.Name, out _)).OrderByDescending(p => int.Parse(p.Name)).Select(p => p.Value.Str()).FirstOrDefault()
                        ?? video.P("thumbnail_url").Str();

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "vimeo",
            MediaId = id,
            Title = video.P("title").Str() ?? $"Vimeo {id}",
            Uploader = video.P("owner").P("name").Str(),
            Duration = video.P("duration").Int() is { } secs ? TimeSpan.FromSeconds(secs) : manifestDuration,
            Thumbnail = U(thumb),
            Representations = reps,
            AudioTracks = audio,
            Subtitles = subs
        };
        return MediaDiscoveryResult.FromItem(item, problems.Where(p => p.Code != MediaErrorCode.NoDownloadableMedia));
    }
}
