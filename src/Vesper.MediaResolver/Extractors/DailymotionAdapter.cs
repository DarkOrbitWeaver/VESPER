using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Dailymotion via the public player metadata endpoint (HLS master and, when offered, progressive MP4).</summary>
internal sealed partial class DailymotionAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "dailymotion", "Dailymotion", Matches, svc => new DailymotionAdapter(svc),
        new AdapterCapabilities(
            Domains: ["dailymotion.com", "dai.ly"],
            UrlPatterns: ["dailymotion.com/video/{id}", "dailymotion.com/embed/video/{id}", "dai.ly/{id}", "geo.dailymotion.com/player.html?video={id}"],
            SingleItem: true, Playlist: false, AudioOnly: false, Subtitles: true,
            Authentication: "None (public videos)",
            KnownUnsupported: ["playlists and channels", "private/password videos", "geo-blocked videos", "DRM"],
            Notes: "Reads www.dailymotion.com/player/metadata/video/{id}."));

    [GeneratedRegex("^[a-z0-9]{5,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex IdRegex();

    public DailymotionAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "dailymotion.com", "dai.ly") && TryGetId(u) is not null;

    public override bool CanHandle(Uri url) => Matches(url);

    internal static string? TryGetId(Uri u)
    {
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (u.Host.Equals("dai.ly", StringComparison.OrdinalIgnoreCase)) id = segs.FirstOrDefault();
        else if (UrlUtil.QueryValue(u, "video") is { Length: > 0 } q) id = q;
        else
        {
            int i = Array.IndexOf(segs, "video");
            if (i >= 0 && i + 1 < segs.Length) id = segs[i + 1].Split('_')[0]; // slug form: x7abc_title
        }
        return id is not null && IdRegex().IsMatch(id) ? id : null;
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        string? id = TryGetId(request.Url);
        if (id is null) return Fail(MediaErrors.NotThisSite("Not a Dailymotion video URL"));

        var ctx = new MediaRequestContext { Referer = "https://www.dailymotion.com/" };
        using var doc = await GetJsonAsync(new Uri($"https://www.dailymotion.com/player/metadata/video/{id}"), ctx, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.P("error") is { } err)
        {
            string text = $"{err.P("title").Str()} {err.P("message").Str()} {err.P("code").Str()}".ToLowerInvariant();
            if (text.Contains("countr") || text.Contains("geo") || text.Contains("region")) return Fail(MediaErrors.Region($"Dailymotion: {text.Trim()}"));
            if (text.Contains("private") || text.Contains("password") || text.Contains("access") || text.Contains("login")) return Fail(MediaErrors.LoginRequired($"Dailymotion: {text.Trim()}"));
            return Fail(MediaErrors.Unavailable($"Dailymotion: {text.Trim()}"));
        }

        var reps = new List<MediaRepresentation>();
        var audio = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();
        var problems = new List<MediaError>();
        TimeSpan? manifestDuration = null;

        foreach (var q in root.P("qualities").Props())
        {
            foreach (var source in q.Value.Items())
            {
                var url = U(source.P("url").Str());
                string type = source.P("type").Str() ?? "";
                if (url is null) continue;

                if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                {
                    if (q.Name == "auto")
                        await TryAddManifestAsync(url, ctx, "dm-", reps, audio, subs, problems, ct, d => manifestDuration = d).ConfigureAwait(false);
                }
                else if (type.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) && int.TryParse(q.Name, out var height))
                {
                    reps.Add(new MediaRepresentation
                    {
                        Id = $"dm-http-{height}p",
                        Url = url,
                        Protocol = StreamProtocol.Http,
                        Content = StreamContent.VideoAndAudio,
                        Container = "mp4",
                        Height = height,
                        ExpiresAt = ExpiryParser.FromUrl(url),
                        Context = ctx
                    });
                }
            }
        }

        if (reps.Count == 0) return Fail(PickProblem(problems, "Dailymotion metadata listed no playable sources"));

        if (request.Options.IncludeSubtitles)
        {
            foreach (var s in root.P("subtitles").P("data").Props())
            {
                var u = U(s.Value.P("urls").At(0).Str());
                if (u is not null)
                    subs.Add(new SubtitleTrack(u, TextUtil.NormalizeLang(s.Name), s.Value.P("label").Str(), UrlUtil.Extension(u) ?? "vtt"));
            }
        }

        string? poster = root.P("posters").Props().Where(p => int.TryParse(p.Name, out _))
            .OrderByDescending(p => int.Parse(p.Name)).Select(p => p.Value.Str()).FirstOrDefault();

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "dailymotion",
            MediaId = id,
            Title = root.P("title").Str() ?? $"Dailymotion {id}",
            Uploader = root.P("owner").P("screenname").Str() ?? root.P("owner").P("username").Str(),
            Duration = root.P("duration").Int() is { } s2 ? TimeSpan.FromSeconds(s2) : manifestDuration,
            Thumbnail = U(poster),
            Representations = reps,
            AudioTracks = audio,
            Subtitles = subs
        };
        return MediaDiscoveryResult.FromItem(item);
    }
}
