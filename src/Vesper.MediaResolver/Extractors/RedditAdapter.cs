using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Reddit-hosted video (v.redd.it): post JSON gives the DASH/HLS manifests; v.redd.it links go straight to the manifests.</summary>
internal sealed class RedditAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "reddit", "Reddit", Matches, svc => new RedditAdapter(svc),
        new AdapterCapabilities(
            Domains: ["reddit.com", "redd.it", "v.redd.it"],
            UrlPatterns: ["reddit.com/r/{sub}/comments/{id}/*", "redd.it/{id}", "v.redd.it/{id}"],
            SingleItem: true, Playlist: false, AudioOnly: true, Subtitles: false,
            Authentication: "None (public posts); login-walled posts fail with LoginRequired",
            KnownUnsupported: ["external embeds (YouTube, Streamable...) inside posts", "galleries and images", "NSFW posts requiring login"],
            Notes: "Video and audio are separate DASH streams: merging them needs an external muxer."));

    public RedditAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "reddit.com", "redd.it") && Parse(u) is not null;

    public override bool CanHandle(Uri url) => Matches(url);

    private sealed record Target(string Id, bool IsVideoHost);

    private static Target? Parse(Uri u)
    {
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (u.Host.Equals("v.redd.it", StringComparison.OrdinalIgnoreCase))
            return segs.Length > 0 && segs[0].All(char.IsAsciiLetterOrDigit) ? new Target(segs[0], true) : null;
        if (u.Host.Equals("redd.it", StringComparison.OrdinalIgnoreCase))
            return segs.Length > 0 && segs[0].All(char.IsAsciiLetterOrDigit) ? new Target(segs[0], false) : null;
        int i = Array.IndexOf(segs, "comments");
        return i >= 0 && i + 1 < segs.Length && segs[i + 1].All(char.IsAsciiLetterOrDigit) ? new Target(segs[i + 1], false) : null;
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var target = Parse(request.Url);
        if (target is null) return Fail(MediaErrors.NotThisSite("Not a Reddit post or video URL"));

        var ctx = new MediaRequestContext { Referer = "https://www.reddit.com/" };
        var reps = new List<MediaRepresentation>();
        var audio = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();
        var problems = new List<MediaError>();
        TimeSpan? duration = null;
        string title = $"Reddit video {target.Id}";
        string? uploader = null, mediaId = target.Id;
        Uri? thumb = null;

        if (target.IsVideoHost)
        {
            if (!await TryAddManifestAsync(new Uri($"https://v.redd.it/{target.Id}/DASHPlaylist.mpd"), ctx, "reddit-", reps, audio, subs, problems, ct, d => duration = d).ConfigureAwait(false))
                await TryAddManifestAsync(new Uri($"https://v.redd.it/{target.Id}/HLSPlaylist.m3u8"), ctx, "reddit-hls-", reps, audio, subs, problems, ct, d => duration = d).ConfigureAwait(false);
        }
        else
        {
            using var doc = await GetJsonAsync(new Uri($"https://www.reddit.com/comments/{target.Id}.json?raw_json=1&limit=1"), ctx, ct).ConfigureAwait(false);
            var post = doc.RootElement.At(0).P("data").P("children").At(0).P("data");
            if (post is null) return Fail(MediaErrors.SourceChanged("Reddit post JSON has an unexpected shape"));

            var rv = post.P("secure_media").P("reddit_video")
                  ?? post.P("media").P("reddit_video")
                  ?? post.P("crosspost_parent_list").At(0).P("secure_media").P("reddit_video")
                  ?? post.P("preview").P("reddit_video_preview");
            if (rv is null)
                return Fail(MediaErrors.NoMedia("The post has no Reddit-hosted video (it may be an image, gallery, text or external embed)", browserMayHelp: false));

            title = post.P("title").Str() ?? title;
            uploader = post.P("author").Str();
            mediaId = post.P("id").Str() ?? mediaId;
            thumb = U(post.P("preview").P("images").At(0).P("source").P("url").Str());
            if (rv.P("duration").Int() is { } secs) duration = TimeSpan.FromSeconds(secs);

            var dashUrl = U(rv.P("dash_url").Str());
            var hlsUrl = U(rv.P("hls_url").Str());
            bool ok = dashUrl is not null && await TryAddManifestAsync(dashUrl, ctx, "reddit-", reps, audio, subs, problems, ct, d => duration ??= d).ConfigureAwait(false);
            if (!ok && hlsUrl is not null)
                ok = await TryAddManifestAsync(hlsUrl, ctx, "reddit-hls-", reps, audio, subs, problems, ct, d => duration ??= d).ConfigureAwait(false);

            // Last resort: the fallback MP4 is the video track only (audio lives in a separate stream); say so.
            if (!ok && U(rv.P("fallback_url").Str()) is { } fallback)
            {
                reps.Add(new MediaRepresentation
                {
                    Id = "reddit-fallback",
                    Url = fallback,
                    Protocol = StreamProtocol.Http,
                    Content = StreamContent.VideoOnly,
                    Container = "mp4",
                    Width = rv.P("width").Int(),
                    Height = rv.P("height").Int(),
                    Context = ctx
                });
            }
        }

        if (reps.Count == 0) return Fail(PickProblem(problems, "Reddit video manifests could not be read"));

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "reddit",
            MediaId = mediaId,
            Title = title,
            Uploader = uploader,
            Duration = duration,
            Thumbnail = thumb,
            Representations = reps,
            AudioTracks = audio,
            Subtitles = subs
        };
        return MediaDiscoveryResult.FromItem(item);
    }
}
