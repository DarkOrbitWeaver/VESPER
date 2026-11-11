using System.Text.Json;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// PeerTube (any instance) through its documented REST API. Matching is path-based, so the adapter confirms the host
/// really is a PeerTube instance (API shape) and otherwise defers to the generic adapter.
/// </summary>
internal sealed partial class PeerTubeAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "peertube", "PeerTube (any instance)", Matches, svc => new PeerTubeAdapter(svc),
        new AdapterCapabilities(
            Domains: ["any PeerTube instance (confirmed via /api/v1)"],
            UrlPatterns: ["{host}/w/{id}", "{host}/videos/watch/{id}", "{host}/videos/embed/{id}", "{host}/w/p/{id}", "{host}/videos/watch/playlist/{id}"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: true,
            Authentication: "None (public/unlisted videos); private and password videos return LoginRequired",
            KnownUnsupported: ["private/internal videos", "password-protected videos", "live streams", "channel and account pages"],
            Notes: "Instances that disable web-video files serve HLS-fragmented MP4 files; both are plain HTTPS downloads."));

    [GeneratedRegex("^[A-Za-z0-9_-]{6,40}$")]
    private static partial Regex IdRegex();

    public PeerTubeAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => Parse(u) is not null;

    public override bool CanHandle(Uri url) => Matches(url);

    private sealed record Target(string Id, bool IsPlaylist);

    private static Target? Parse(Uri u)
    {
        var s = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        bool playlist = false;
        if (s.Length == 2 && s[0] == "w") id = s[1];
        else if (s.Length == 3 && s[0] == "w" && s[1] == "p") { id = s[2]; playlist = true; }
        else if (s.Length == 3 && s[0] == "videos" && s[1] is "watch" or "embed") id = s[2];
        else if (s.Length == 4 && s[0] == "videos" && s[1] == "watch" && s[2] == "playlist") { id = s[3]; playlist = true; }
        return id is not null && IdRegex().IsMatch(id) ? new Target(id, playlist) : null;
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var target = Parse(request.Url);
        if (target is null) return Fail(MediaErrors.NotThisSite("Not a PeerTube URL shape"));
        string origin = request.Url.GetLeftPart(UriPartial.Authority);
        return target.IsPlaylist
            ? await ResolvePlaylistAsync(request, origin, target.Id, ct).ConfigureAwait(false)
            : await ResolveVideoAsync(request, origin, target.Id, ct).ConfigureAwait(false);
    }

    private async Task<MediaDiscoveryResult> ResolveVideoAsync(MediaDiscoveryRequest request, string origin, string id, CancellationToken ct)
    {
        var resp = await Http.GetStringAsync(new Uri($"{origin}/api/v1/videos/{Uri.EscapeDataString(id)}"), null, ct, Options.MaxJsonBytes, "application/json").ConfigureAwait(false);
        using var doc = JsonExt.TryParse(resp.Content);
        var root = doc?.RootElement;

        bool apiError = root is { ValueKind: JsonValueKind.Object } r0 && (r0.P("detail") is not null || r0.P("error") is not null);
        if (resp.StatusCode is 401 or 403 && apiError) return Fail(MediaErrors.LoginRequired("PeerTube: video is private, internal or password-protected"));
        if (resp.StatusCode == 404 && apiError) return Fail(MediaErrors.Unavailable("PeerTube: video not found"));
        if (resp.StatusCode is 429 or >= 500) return Fail(MediaErrors.FromHttpStatus(resp.StatusCode, resp.FinalUrl));
        if (resp.StatusCode != 200 || root is not { ValueKind: JsonValueKind.Object } v || v.P("uuid") is null)
            return Fail(MediaErrors.NotThisSite("Host does not look like a PeerTube instance"));

        if (v.P("isLive").Bool() == true) return Fail(MediaErrors.Live("PeerTube live stream"));

        var reps = new List<MediaRepresentation>();
        var audio = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();
        var problems = new List<MediaError>();
        var ctx = new MediaRequestContext { Referer = origin + "/" };

        void AddFile(JsonElement f, string prefix)
        {
            var url = U(f.P("fileUrl").Str()) ?? U(f.P("fileDownloadUrl").Str());
            if (url is null) return;
            int res = f.P("resolution").P("id").Int() ?? 0;
            reps.Add(new MediaRepresentation
            {
                Id = $"{prefix}-{res}p-{reps.Count}",
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = res == 0 ? StreamContent.AudioOnly : StreamContent.VideoAndAudio,
                Container = UrlUtil.Extension(url) ?? "mp4",
                Height = res == 0 ? null : res,
                FrameRate = f.P("fps").Dbl() is > 0 and var fps ? fps : null,
                FileSize = f.P("size").Long(),
                Label = f.P("resolution").P("label").Str(),
                Context = ctx
            });
        }

        foreach (var f in v.P("files").Items()) AddFile(f, "pt-web");
        if (reps.Count == 0)
            foreach (var sp in v.P("streamingPlaylists").Items())
                foreach (var f in sp.P("files").Items()) AddFile(f, "pt-hls");

        if (reps.Count == 0)
        {
            foreach (var sp in v.P("streamingPlaylists").Items())
                if (U(sp.P("playlistUrl").Str()) is { } pl)
                    await TryAddManifestAsync(pl, ctx, "pt-", reps, audio, subs, problems, ct).ConfigureAwait(false);
        }
        if (reps.Count == 0) return Fail(PickProblem(problems, "PeerTube video has no downloadable files"));

        if (request.Options.IncludeSubtitles)
        {
            try
            {
                using var caps = await GetJsonAsync(new Uri($"{origin}/api/v1/videos/{Uri.EscapeDataString(id)}/captions"), null, ct).ConfigureAwait(false);
                foreach (var c in caps.RootElement.P("data").Items())
                {
                    var u = UrlUtil.Resolve(new Uri(origin + "/"), c.P("captionPath").Str() ?? c.P("fileUrl").Str());
                    if (u is not null)
                        subs.Add(new SubtitleTrack(u, TextUtil.NormalizeLang(c.P("language").P("id").Str()), c.P("language").P("label").Str(), UrlUtil.Extension(u) ?? "vtt"));
                }
            }
            catch (MediaResolverException) { /* captions are optional */ }
        }

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "peertube",
            MediaId = v.P("shortUUID").Str() ?? v.P("uuid").Str(),
            Title = v.P("name").Str() ?? id,
            Description = v.P("description").Str(),
            Uploader = v.P("channel").P("displayName").Str() ?? v.P("account").P("displayName").Str(),
            Duration = v.P("duration").Int() is { } secs ? TimeSpan.FromSeconds(secs) : null,
            Thumbnail = UrlUtil.Resolve(new Uri(origin + "/"), v.P("thumbnailPath").Str()),
            Representations = reps,
            AudioTracks = audio,
            Subtitles = subs
        };
        return MediaDiscoveryResult.FromItem(item);
    }

    private async Task<MediaDiscoveryResult> ResolvePlaylistAsync(MediaDiscoveryRequest request, string origin, string id, CancellationToken ct)
    {
        int start = int.TryParse(request.Options.PageToken, out var st) && st >= 0 ? st : 0;
        const int count = 50;

        var head = await Http.GetStringAsync(new Uri($"{origin}/api/v1/video-playlists/{Uri.EscapeDataString(id)}"), null, ct, Options.MaxJsonBytes, "application/json").ConfigureAwait(false);
        using var headDoc = JsonExt.TryParse(head.Content);
        if (head.StatusCode == 404 && headDoc?.RootElement.P("detail") is not null) return Fail(MediaErrors.Unavailable("PeerTube playlist not found"));
        if (head.StatusCode is 401 or 403 && headDoc?.RootElement.P("detail") is not null) return Fail(MediaErrors.LoginRequired("PeerTube playlist is private"));
        if (head.StatusCode != 200 || headDoc is null || headDoc.RootElement.P("displayName") is null)
            return Fail(MediaErrors.NotThisSite("Host does not look like a PeerTube instance"));

        using var list = await GetJsonAsync(new Uri($"{origin}/api/v1/video-playlists/{Uri.EscapeDataString(id)}/videos?start={start}&count={count}"), null, ct).ConfigureAwait(false);
        int total = list.RootElement.P("total").Int() ?? 0;
        var entries = new List<CollectionEntry>();
        foreach (var e in list.RootElement.P("data").Items())
        {
            var video = e.P("video");
            string? vid = video.P("shortUUID").Str() ?? video.P("uuid").Str();
            if (vid is null || !IdRegex().IsMatch(vid)) continue;
            entries.Add(new CollectionEntry
            {
                MediaId = vid,
                Url = new Uri($"{origin}/w/{vid}"),
                Title = video.P("name").Str(),
                Duration = TextUtil.Seconds(video.P("duration").Dbl()),
                Thumbnail = UrlUtil.Resolve(new Uri(origin + "/"), video.P("thumbnailPath").Str()),
                Index = start + entries.Count
            });
        }

        int next = start + count;
        bool more = entries.Count > 0 && next < total;
        return MediaDiscoveryResult.FromCollection(new MediaCollection
        {
            SourceUrl = request.Url,
            Provider = "peertube",
            CollectionId = id,
            Title = headDoc.RootElement.P("displayName").Str() ?? id,
            Kind = CollectionKind.Playlist,
            TotalCount = total,
            Entries = entries,
            NextPageToken = more ? next.ToString() : null,
            IsComplete = !more
        });
    }
}
