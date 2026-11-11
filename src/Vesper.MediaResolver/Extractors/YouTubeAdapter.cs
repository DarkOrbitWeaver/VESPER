using System.Text.Json;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// YouTube from the watch/playlist page's embedded player response. Only streams that carry a plain URL are returned:
/// formats that need the player script to solve signature ciphers are NOT executed (by design) and are reported.
/// When nothing usable remains the error says browser-assisted discovery may help. Mix/radio lists are never treated as
/// fixed playlists.
/// </summary>
internal sealed partial class YouTubeAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "youtube", "YouTube", Matches, svc => new YouTubeAdapter(svc),
        new AdapterCapabilities(
            Domains: ["youtube.com", "youtu.be", "youtube-nocookie.com"],
            UrlPatterns: ["watch?v={id}", "shorts/{id}", "live/{id}", "embed/{id}", "youtu.be/{id}", "playlist?list={id}", "watch?v={id}&list={id}[&start_radio=1] (item + collection hint)"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: true,
            Authentication: "None; login/age/bot-gated videos return LoginRequired or BrowserContextRequired",
            KnownUnsupported: ["formats requiring signature/n-parameter solving (needs running player JS)", "DRM", "live streams", "playlist continuation pages beyond the first ~100", "channels/search"],
            Notes: "Partial by design. Streams are only listed when the page exposes plain URLs; the browser extension can supply stream URLs otherwise."));

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    public YouTubeAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "youtube.com", "youtube-nocookie.com", "youtu.be");

    public override bool CanHandle(Uri url) => Matches(url);

    internal sealed record YtTarget(string? VideoId, string? ListId, bool StartRadio)
    {
        /// <summary>Mix / radio lists are regenerated on every request and are not fixed playlists.</summary>
        public bool IsDynamicMix => StartRadio || (ListId is not null && ListId.StartsWith("RD", StringComparison.Ordinal) && !ListId.StartsWith("RDCLAK", StringComparison.Ordinal));
    }

    internal static YtTarget? Parse(Uri u)
    {
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? vid = null;
        if (u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) vid = segs.FirstOrDefault();
        else if (segs.Length > 0)
        {
            switch (segs[0])
            {
                case "watch": vid = UrlUtil.QueryValue(u, "v"); break;
                case "shorts" or "live" or "embed" or "v": vid = segs.Length > 1 ? segs[1] : null; break;
            }
        }
        if (vid is not null && !VideoIdRegex().IsMatch(vid)) vid = null;

        string? list = UrlUtil.QueryValue(u, "list");
        if (list is not null && !(list.Length is >= 2 and <= 64 && list.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) list = null;
        bool radio = UrlUtil.QueryValue(u, "start_radio") == "1";

        if (vid is null && list is null) return null;
        if (vid is null && segs.Length > 0 && segs[0] is not ("playlist" or "watch")) return null;
        return new YtTarget(vid, list, radio);
    }

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var t = Parse(request.Url);
        if (t is null)
            return Fail(new MediaError(MediaErrorCode.InvalidInput,
                "This YouTube page type (channel, search or home) is not supported. Paste a video or playlist link.",
                "No video id or list id in URL", false, false, true));

        bool wantCollection = request.Options.CollectionIntent == CollectionIntent.CollectionOnly || t.VideoId is null;
        return wantCollection ? await ResolveCollectionAsync(request, t, ct).ConfigureAwait(false)
                              : await ResolveVideoAsync(request, t, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ video

    private async Task<MediaDiscoveryResult> ResolveVideoAsync(MediaDiscoveryRequest request, YtTarget t, CancellationToken ct)
    {
        var page = await GetPageAsync(new Uri($"https://www.youtube.com/watch?v={t.VideoId}"), request.AuthContext, ct).ConfigureAwait(false);
        request.PageResponse = page;
        if (page.FinalUrl.Host.StartsWith("consent.", StringComparison.OrdinalIgnoreCase))
            return Fail(MediaErrors.BrowserContext("YouTube served a consent interstitial instead of the video page"));

        using var player = JsonBalanced.ExtractAfter(page.Content, "ytInitialPlayerResponse");
        if (player is null) return Fail(MediaErrors.SourceChanged("ytInitialPlayerResponse was not found in the watch page"));
        var root = player.RootElement;

        var details = root.P("videoDetails");
        string? returnedId = details.P("videoId").Str();
        if (returnedId is not null && returnedId != t.VideoId)
            return Fail(MediaErrors.SourceChanged("The player response belongs to a different video; no streams were accepted."));
        var earlyThumb = details.P("thumbnail").P("thumbnails").Items().LastOrDefault();
        request.Publish(new DiscoveryProgress(details.P("title").Str(), U(earlyThumb.P("url").Str())));

        string status = root.P("playabilityStatus").P("status").Str() ?? "";
        if (status != "OK")
        {
            var ps = root.P("playabilityStatus");
            string reason = ps.P("reason").Str() ?? ps.P("errorScreen").P("playerErrorMessageRenderer").P("reason").P("simpleText").Str() ?? status;
            return Fail(MapStatus(status, reason));
        }

        string? videoId = details.P("videoId").Str() ?? t.VideoId;
        bool isLive = details.P("isLive").Bool() == true;
        if (isLive) return Fail(MediaErrors.Live("The player reports a live broadcast; VOD transfer only."));
        var ctx = request.AuthContext with { UserAgent = Options.UserAgent, Referer = page.FinalUrl.AbsoluteUri };

        var reps = ParseFormats(root.P("streamingData"), ctx, out int cipherCount, out int drmCount, out bool throttled);
        request.Publish(new DiscoveryProgress(RepresentationCount: reps.Count));
        var problems = new List<MediaError>();
        var audioTracks = new List<AudioTrack>();
        var subs = new List<SubtitleTrack>();

        // Direct adaptive tracks must not hide an available self-contained HLS representation.
        // HLS and DASH are independent requests; use isolated lists and merge deterministically afterwards.
        if (!reps.Any(r => r.Content == StreamContent.VideoAndAudio))
        {
            var tasks = new List<Task<(List<MediaRepresentation> Reps, List<AudioTrack> Audio, List<SubtitleTrack> Subs, List<MediaError> Errors)>>();
            foreach (var (key, prefix) in new[] { ("hlsManifestUrl", "yt-hls-"), ("dashManifestUrl", "yt-dash-") })
                if (U(root.P("streamingData").P(key).Str()) is { } manifest)
                    tasks.Add(ResolveManifestAsync(manifest, prefix));
            foreach (var resolved in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                reps.AddRange(resolved.Reps); audioTracks.AddRange(resolved.Audio);
                subs.AddRange(resolved.Subs); problems.AddRange(resolved.Errors);
                request.Publish(new DiscoveryProgress(RepresentationCount: reps.Count));
            }

            async Task<(List<MediaRepresentation>, List<AudioTrack>, List<SubtitleTrack>, List<MediaError>)> ResolveManifestAsync(Uri url, string prefix)
            {
                var streams = new List<MediaRepresentation>(); var audio = new List<AudioTrack>();
                var captions = new List<SubtitleTrack>(); var errors = new List<MediaError>();
                await TryAddManifestAsync(url, ctx, prefix, streams, audio, captions, errors, ct).ConfigureAwait(false);
                return (streams, audio, captions, errors);
            }
        }

        if (reps.Count == 0)
        {
            if (isLive) return Fail(MediaErrors.Live("Live stream"));
            if (drmCount > 0) return Fail(MediaErrors.Drm($"{drmCount} DRM-protected formats; no clear formats offered"));
            if (cipherCount > 0)
                return Fail(new MediaError(MediaErrorCode.BrowserContextRequired,
                    "YouTube only offers streams that need browser-side processing. Use the VESPER extension on the open page to supply them.",
                    $"{cipherCount} formats use signatureCipher; this library does not execute player scripts", false, true, false));
            if (root.P("streamingData").P("serverAbrStreamingUrl").Str() is not null)
                return Fail(MediaErrors.BrowserContext("YouTube supplied a server-ABR playback endpoint, not a supported HTTP/HLS/DASH media URL. No player emulation or verification bypass is attempted."));
            int advertised = root.P("streamingData").P("formats").Items().Count() + root.P("streamingData").P("adaptiveFormats").Items().Count();
            return Fail(problems.FirstOrDefault() ?? (advertised > 0
                ? MediaErrors.BrowserContext($"The player advertises {advertised} formats but exposes no directly usable media URLs.")
                : MediaErrors.BrowserContext("The watch page contains metadata but no playback URLs. This is not proof that the video has no media; authorized browser playback context may be required.")));
        }

        string description = details.P("shortDescription").Str() ?? "";
        TimeSpan? duration = details.P("lengthSeconds").Long() is { } secs ? TimeSpan.FromSeconds(secs) : null;

        if (request.Options.IncludeSubtitles)
        {
            foreach (var track in root.P("captions").P("playerCaptionsTracklistRenderer").P("captionTracks").Items())
            {
                var baseUrl = U(track.P("baseUrl").Str());
                if (baseUrl is null || !UrlUtil.HostIs(baseUrl, "youtube.com")) continue;
                var vtt = new Uri(baseUrl.AbsoluteUri + "&fmt=vtt");
                string? name = track.P("name").P("simpleText").Str() ?? track.P("name").P("runs").At(0).P("text").Str();
                subs.Add(new SubtitleTrack(vtt, TextUtil.NormalizeLang(track.P("languageCode").Str()), name, "vtt", track.P("kind").Str() == "asr"));
            }
        }

        var thumb = details.P("thumbnail").P("thumbnails").Items().LastOrDefault();
        CollectionHint? related = null;
        if (t.ListId is not null)
        {
            related = new CollectionHint(
                t.IsDynamicMix ? CollectionKind.DynamicMix : CollectionKind.Playlist, t.ListId,
                new Uri($"https://www.youtube.com/watch?v={videoId}&list={Uri.EscapeDataString(t.ListId)}"), t.IsDynamicMix, null);
        }

        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "youtube",
            MediaId = videoId,
            Title = details.P("title").Str() ?? $"YouTube {videoId}",
            Description = description,
            Uploader = details.P("author").Str(),
            Duration = duration,
            Thumbnail = U(thumb.P("url").Str()),
            IsLive = isLive,
            Representations = reps,
            AudioTracks = audioTracks.Count > 0 ? audioTracks : BuildAudioTracks(reps),
            Subtitles = subs,
            Chapters = ChapterParser.FromDescription(description, duration),
            Related = related
        };

        var notes = new List<string>();
        if (throttled) notes.Add("Stream URLs carry an 'n' throttling parameter; without player-script challenge solving (intentionally not executed) transfers may be bandwidth-limited by the server.");
        if (cipherCount > 0) notes.Add($"{cipherCount} additional formats were omitted because they require signature solving.");
        return MediaDiscoveryResult.FromItem(item, problems, notes);
    }

    private static MediaError MapStatus(string status, string reason)
    {
        string r = reason.ToLowerInvariant();
        string diag = $"playabilityStatus={status}: {TextUtil.Clip(reason, 200)}";
        switch (status)
        {
            case "LOGIN_REQUIRED" when r.Contains("bot"):
                return new MediaError(MediaErrorCode.BrowserContextRequired,
                    "YouTube is asking for sign-in or verification. Use the VESPER extension from a page where you are signed in.", diag, false, true, false);
            case "LOGIN_REQUIRED" or "AGE_VERIFICATION_REQUIRED" or "CONTENT_CHECK_REQUIRED":
                return MediaErrors.LoginRequired(diag);
            case "LIVE_STREAM_OFFLINE":
                return MediaErrors.Live(diag);
            case "UNPLAYABLE" when r.Contains("country") || r.Contains("region") || r.Contains("location"):
                return MediaErrors.Region(diag);
            case "UNPLAYABLE" when r.Contains("private") || r.Contains("members") || r.Contains("sign in"):
                return MediaErrors.LoginRequired(diag);
            default:
                return MediaErrors.Unavailable(diag);
        }
    }

    private static List<MediaRepresentation> ParseFormats(JsonElement? streamingData, MediaRequestContext ctx, out int cipher, out int drm, out bool throttled)
    {
        cipher = drm = 0;
        throttled = false;
        var reps = new List<MediaRepresentation>();
        if (streamingData is null) return reps;

        foreach (var f in streamingData.P("formats").Items().Concat(streamingData.P("adaptiveFormats").Items()))
        {
            if (f.P("drmFamilies") is not null || f.P("drmTrackType") is not null) { drm++; continue; }
            var url = U(f.P("url").Str());
            if (url is null)
            {
                if (f.P("signatureCipher") is not null || f.P("cipher") is not null) cipher++;
                continue;
            }

            string? mime = f.P("mimeType").Str();
            string? baseMime = mime?.Split(';')[0].Trim();
            string? codecs = null;
            int ci = mime?.IndexOf("codecs=\"", StringComparison.Ordinal) ?? -1;
            if (mime is not null && ci >= 0)
            {
                int s = ci + 8, e = mime.IndexOf('"', s);
                if (e > s) codecs = mime[s..e];
            }
            CodecInfo.Split(codecs, out var vCodec, out var aCodec, out var hdr0);
            bool isVideo = baseMime?.StartsWith("video/", StringComparison.Ordinal) == true;
            bool isAudio = baseMime?.StartsWith("audio/", StringComparison.Ordinal) == true;
            if (!isVideo && !isAudio) continue;

            var content = isAudio ? StreamContent.AudioOnly : aCodec is not null ? StreamContent.VideoAndAudio : StreamContent.VideoOnly;
            string transfer = f.P("colorInfo").P("transferCharacteristics").Str() ?? "";
            var hdr = transfer.Contains("SMPTEST2084") ? HdrKind.Hdr10 : transfer.Contains("ARIB_STD_B67") ? HdrKind.Hlg : hdr0;

            string? trackId = f.P("audioTrack").P("id").Str();
            string? lang = trackId is null ? null : TextUtil.NormalizeLang(trackId.Split('.')[0]);
            string? trackName = f.P("audioTrack").P("displayName").Str();
            int? avgBitrate = f.P("averageBitrate").Int();
            int? peakBitrate = f.P("bitrate").Int();
            int? kbps = avgBitrate is not null ? avgBitrate / 1000 : peakBitrate is not null ? peakBitrate / 1000 : null;
            if (UrlUtil.QueryValue(url, "n") is not null) throttled = true;

            string? container = CodecInfo.ContainerFromMime(baseMime);
            reps.Add(new MediaRepresentation
            {
                Id = $"yt-{f.P("itag").Str() ?? reps.Count.ToString()}",
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = content,
                Container = container,
                VideoCodec = isVideo ? vCodec : null,
                AudioCodec = aCodec,
                RawCodecs = codecs,
                Width = f.P("width").Int(),
                Height = f.P("height").Int(),
                FrameRate = f.P("fps").Dbl(),
                Hdr = isVideo ? hdr : HdrKind.Sdr,
                BitrateKbps = kbps,
                AudioChannels = f.P("audioChannels").Int(),
                AudioSampleRate = f.P("audioSampleRate").Int(),
                AudioLanguage = lang,
                AudioTrackId = trackId,
                IsDefaultAudio = f.P("audioTrack").P("audioIsDefault").Bool() == true,
                IsAudioDescription = trackName?.Contains("descriptive", StringComparison.OrdinalIgnoreCase) == true,
                FileSize = f.P("contentLength").Long(),
                ExpiresAt = ExpiryParser.FromUrl(url),
                Label = f.P("qualityLabel").Str(),
                Context = ctx
            });
        }
        return reps;
    }

    private static List<AudioTrack> BuildAudioTracks(IEnumerable<MediaRepresentation> reps) =>
        reps.Where(r => r.Content == StreamContent.AudioOnly && r.AudioTrackId is not null)
            .GroupBy(r => r.AudioTrackId!)
            .Select(g => new AudioTrack(g.Key, g.First().AudioLanguage, null, g.Any(r => r.IsDefaultAudio)))
            .ToList();

    // ------------------------------------------------------------------------------------------------ collections

    private async Task<MediaDiscoveryResult> ResolveCollectionAsync(MediaDiscoveryRequest request, YtTarget t, CancellationToken ct)
    {
        if (t.ListId is null) return Fail(MediaErrors.Invalid("The link has no playlist id"));
        bool dynamicMix = t.IsDynamicMix;
        if (dynamicMix && t.VideoId is null) return Fail(MediaErrors.Invalid("A Mix link needs the video it was generated from"));

        var pageUrl = dynamicMix
            ? new Uri($"https://www.youtube.com/watch?v={t.VideoId}&list={Uri.EscapeDataString(t.ListId)}")
            : new Uri($"https://www.youtube.com/playlist?list={Uri.EscapeDataString(t.ListId)}");
        var page = await GetPageAsync(pageUrl, null, ct).ConfigureAwait(false);
        using var data = JsonBalanced.ExtractAfter(page.Content, "ytInitialData");
        if (data is null) return Fail(MediaErrors.SourceChanged("ytInitialData was not found"));
        var root = data.RootElement;

        var entries = new List<CollectionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string rendererName = dynamicMix ? "playlistPanelVideoRenderer" : "playlistVideoRenderer";
        foreach (var e in root.FindAll(rendererName, Options.MaxCollectionItems * 2))
        {
            string? id = e.P("videoId").Str();
            if (id is null || !VideoIdRegex().IsMatch(id) || !seen.Add(id)) continue;
            if (entries.Count >= request.Options.MaxCollectionItems) break;

            string? title = e.P("title").P("simpleText").Str() ?? e.P("title").P("runs").At(0).P("text").Str();
            TimeSpan? dur = e.P("lengthSeconds").Long() is { } ls ? TimeSpan.FromSeconds(ls) : TextUtil.ParseDuration(e.P("lengthText").P("simpleText").Str());
            var thumb = e.P("thumbnail").P("thumbnails").Items().LastOrDefault();
            entries.Add(new CollectionEntry
            {
                MediaId = id,
                Url = new Uri($"https://www.youtube.com/watch?v={id}"),
                Title = title,
                Duration = dur,
                Thumbnail = U(thumb.P("url").Str()),
                Index = entries.Count
            });
        }

        if (entries.Count == 0)
            return Fail(MediaErrors.Unavailable("The playlist is empty, private or unavailable"));

        string title2 = dynamicMix
            ? root.FindAll("playlist").Select(p => p.P("title").Str()).FirstOrDefault(s => s is not null) ?? "YouTube Mix"
            : root.FindAll("playlistMetadataRenderer").Select(p => p.P("title").Str()).FirstOrDefault(s => s is not null) ?? "YouTube playlist";

        var warnings = new List<MediaError>();
        bool complete = true;
        if (dynamicMix)
        {
            complete = false;
            warnings.Add(MediaErrors.PartialPlaylist($"A Mix is generated per request; this is a snapshot of {entries.Count} entries, not a fixed playlist."));
        }
        else if (root.FindAll("continuationItemRenderer", 1).Any())
        {
            complete = false;
            warnings.Add(MediaErrors.PartialPlaylist("Only the first page is listed; further pages need YouTube's private continuation API, which this library does not use."));
        }

        return MediaDiscoveryResult.FromCollection(new MediaCollection
        {
            SourceUrl = request.Url,
            Provider = "youtube",
            CollectionId = t.ListId,
            Title = title2,
            Kind = dynamicMix ? CollectionKind.DynamicMix : CollectionKind.Playlist,
            IsDynamic = dynamicMix,
            TotalCount = complete ? entries.Count : null,
            Entries = entries,
            IsComplete = complete
        }, warnings);
    }
}
