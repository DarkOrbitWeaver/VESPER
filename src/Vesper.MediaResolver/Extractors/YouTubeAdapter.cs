using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// YouTube through a layered, bounded pipeline:
/// <list type="number">
/// <item>One watch-page request supplies metadata, captions, the visitor session id and the signature timestamp.</item>
/// <item>InnerTube player requests use the configured client profiles (default: visionOS, which needs no player
///   JavaScript and no proof-of-origin token). A profile only wins after one of its media URLs answers a mid-file
///   byte probe, so "first megabyte only" URLs are never offered as downloadable.</item>
/// <item>The watch page's own player response is the last resort, restricted to formats that need no player-script
///   transform. Signature/throttling challenges are never executed and no verification token is manufactured.</item>
/// </list>
/// DRM, login, age, region and bot checks are reported as such. Mix/radio lists are never treated as fixed playlists.
/// </summary>
internal sealed partial class YouTubeAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "youtube", "YouTube", Matches, svc => new YouTubeAdapter(svc),
        new AdapterCapabilities(
            Domains: ["youtube.com", "youtu.be", "youtube-nocookie.com", "music.youtube.com"],
            UrlPatterns: ["watch?v={id}", "shorts/{id}", "live/{id}", "embed/{id}", "youtu.be/{id}", "music.youtube.com/watch?v={id}", "playlist?list={id}", "watch?v={id}&list={id}[&start_radio=1] (item + collection hint)"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: true,
            Authentication: "None; login/age/members-only videos return LoginRequired; bot checks return BrowserContextRequired",
            KnownUnsupported: ["live capture (live streams are detected and reported)", "'made for kids' videos (only offered to clients that need player-JS challenge solving)", "DRM", "channels/search", "server-ABR-only playback"],
            Notes: "Native InnerTube extraction with byte-probe verification. Separate video/audio streams are merged by VESPER's native remuxer."));

    internal static readonly Uri PlayerEndpoint = new("https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
    internal static readonly Uri BrowseEndpoint = new("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false");
    internal const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    /// <summary>
    /// "Reject all" consent choice for youtube.com only (avoids the EU consent interstitial). Never sent to media hosts.
    /// </summary>
    private static readonly MediaRequestContext ConsentContext = new()
    {
        UserAgent = DesktopUserAgent, CookieHeader = "SOCS=CAI",
        CredentialOrigin = new Uri("https://www.youtube.com/"), CredentialHosts = ["youtube.com"]
    };

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
                case "shorts" or "live" or "embed" or "v" or "e": vid = segs.Length > 1 ? segs[1] : null; break;
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

    private MediaRequestContext PageContext(MediaDiscoveryRequest request) =>
        request.AuthContext is { HasCredentials: true } auth ? auth with { UserAgent = auth.UserAgent ?? DesktopUserAgent } : ConsentContext;

    // ------------------------------------------------------------------------------------------------ video

    internal sealed class FormatStats
    {
        public int Cipher, Drm, Throttled, Otf, NoUrl;
        public bool ServerAbr;
        public int Advertised;
    }

    private sealed class Attempt(string key) : IDisposable
    {
        public string Key { get; } = key;
        public JsonDocument? Doc;
        public List<MediaRepresentation> Reps = [];
        public FormatStats Stats = new();
        public MediaError? Error;
        public string Summary = "";
        public bool Usable => Error is null && Reps.Count > 0;
        public void Dispose() => Doc?.Dispose();
    }

    private async Task<MediaDiscoveryResult> ResolveVideoAsync(MediaDiscoveryRequest request, YtTarget t, CancellationToken ct)
    {
        string videoId = t.VideoId!;
        var page = await GetPageAsync(new Uri($"https://www.youtube.com/watch?v={videoId}"), PageContext(request), ct).ConfigureAwait(false);
        request.PageResponse = page;
        if (page.FinalUrl.Host.StartsWith("consent.", StringComparison.OrdinalIgnoreCase))
            return Fail(MediaErrors.BrowserContext("YouTube served a consent interstitial instead of the video page"));

        var cfg = YtPageConfig.Parse(page.Content);
        using var pagePlayer = JsonBalanced.ExtractAfter(page.Content, "ytInitialPlayerResponse");
        JsonElement? pageRoot = pagePlayer?.RootElement;
        string? pageVideoId = pageRoot.P("videoDetails").P("videoId").Str();
        if (pageVideoId is not null && pageVideoId != videoId)
            return Fail(MediaErrors.SourceChanged("The watch page belongs to a different video; no streams were accepted."));
        var pageDetails = pageRoot.P("videoDetails");
        request.Publish(new DiscoveryProgress(pageDetails.P("title").Str(), U(pageDetails.P("thumbnail").P("thumbnails").Items().LastOrDefault().P("url").Str())));

        var attempts = new List<Attempt>();
        Attempt? chosen = null;
        try
        {
            foreach (var profile in YouTubeClients.Resolve(Options.YouTubeClients))
            {
                var attempt = await TryClientAsync(profile, videoId, cfg, ct).ConfigureAwait(false);
                attempts.Add(attempt);
                if (attempt.Usable) { chosen = attempt; break; }
            }
            if (chosen is null && pageRoot is { } pr)
            {
                var fallback = await FromPagePlayerAsync(pr, videoId, page.FinalUrl, ct).ConfigureAwait(false);
                attempts.Add(fallback);
                if (fallback.Usable) chosen = fallback;
            }
            if (chosen is null) return Fail(BestError(attempts, videoId));

            JsonElement root = chosen.Doc?.RootElement ?? pageRoot!.Value;
            var details = root.P("videoDetails");
            string title = details.P("title").Str() ?? pageDetails.P("title").Str() ?? $"YouTube {videoId}";
            string description = details.P("shortDescription").Str() ?? pageDetails.P("shortDescription").Str() ?? "";
            long? secs = NumL(details.P("lengthSeconds")) ?? NumL(pageDetails.P("lengthSeconds"));
            TimeSpan? duration = secs is > 0 ? TimeSpan.FromSeconds(secs.Value) : null;
            var thumb = details.P("thumbnail").P("thumbnails").Items().LastOrDefault();
            var reps = chosen.Reps;
            request.Publish(new DiscoveryProgress(RepresentationCount: reps.Count));

            var subs = new List<SubtitleTrack>();
            if (request.Options.IncludeSubtitles)
            {
                subs = ParseCaptions(root);
                if (subs.Count == 0 && pageRoot is { } p2) subs = ParseCaptions(p2);
            }

            CollectionHint? related = null;
            if (t.ListId is not null)
                related = new CollectionHint(t.IsDynamicMix ? CollectionKind.DynamicMix : CollectionKind.Playlist, t.ListId,
                    new Uri($"https://www.youtube.com/watch?v={videoId}&list={Uri.EscapeDataString(t.ListId)}"), t.IsDynamicMix, null);

            var item = new MediaItem
            {
                SourceUrl = request.Url,
                Provider = "youtube",
                MediaId = videoId,
                Title = title,
                Description = description,
                Uploader = details.P("author").Str() ?? pageDetails.P("author").Str(),
                Duration = duration,
                Thumbnail = U(thumb.P("url").Str()) ?? U(pageDetails.P("thumbnail").P("thumbnails").Items().LastOrDefault().P("url").Str()),
                IsLive = false,
                Representations = reps,
                AudioTracks = BuildAudioTracks(reps),
                Subtitles = subs,
                Chapters = ChapterParser.FromDescription(description, duration),
                Related = related
            };

            var notes = new List<string> { $"Formats from the '{chosen.Key}' strategy; {chosen.Summary}." };
            var s = chosen.Stats;
            if (s.Cipher > 0) notes.Add($"{s.Cipher} formats were omitted because they require signature solving with YouTube's player script (not executed).");
            if (s.Throttled > 0) notes.Add($"{s.Throttled} formats were omitted because their URLs carry the 'n' throttling challenge (player-script transform not executed).");
            if (s.Otf > 0) notes.Add($"{s.Otf} segmented (OTF) formats were omitted; that streaming feature is not supported.");
            var failed = attempts.Where(a => a != chosen).Select(a => $"{a.Key}: {a.Summary}").ToList();
            if (failed.Count > 0) notes.Add("Earlier strategies: " + string.Join("; ", failed));
            return MediaDiscoveryResult.FromItem(item, [], notes);
        }
        finally
        {
            foreach (var a in attempts) a.Dispose();
        }
    }

    private async Task<Attempt> TryClientAsync(YouTubeClientProfile profile, string videoId, YtPageConfig cfg, CancellationToken ct)
    {
        var attempt = new Attempt(profile.Key);
        HttpTextResponse resp;
        try
        {
            resp = await Http.PostJsonAsync(PlayerEndpoint, profile.BuildPlayerRequest(videoId, cfg.VisitorData, cfg.SignatureTimestamp),
                new MediaRequestContext { UserAgent = profile.UserAgent }, profile.Headers(cfg.VisitorData), ct).ConfigureAwait(false);
        }
        catch (MediaResolverException ex)
        {
            attempt.Error = ex.Error;
            attempt.Summary = $"request failed ({ex.Error.Code})";
            return attempt;
        }
        if (!resp.IsSuccess || JsonExt.TryParse(resp.Content) is not { } doc)
        {
            // An API failure says nothing about the video itself: keep it low-priority (page evidence wins).
            attempt.Error = MediaErrors.SourceChanged($"player API ({profile.Key}) answered HTTP {resp.StatusCode} without a usable JSON body");
            attempt.Summary = $"HTTP {resp.StatusCode}";
            return attempt;
        }
        attempt.Doc = doc;
        var root = doc.RootElement;
        string? returned = root.P("videoDetails").P("videoId").Str();
        if (returned is not null && returned != videoId)
        {
            attempt.Error = MediaErrors.SourceChanged($"player API ({profile.Key}) answered for a different video");
            attempt.Summary = "answered for a different video";
            return attempt;
        }
        if (Playability(root, out var status, out var reason) is { } statusError)
        {
            attempt.Error = statusError;
            attempt.Summary = $"playabilityStatus={status} ({TextUtil.Clip(reason, 80)})";
            return attempt;
        }
        if (root.P("videoDetails").P("isLive").Bool() == true)
        {
            attempt.Error = MediaErrors.Live("The player reports a live broadcast; live capture is not supported (completed recordings are).");
            attempt.Summary = "live broadcast";
            return attempt;
        }
        var ctx = new MediaRequestContext { UserAgent = profile.UserAgent };
        attempt.Reps = ParseFormats(root.P("streamingData"), videoId, ctx, attempt.Stats, allowThrottled: false);
        if (attempt.Reps.Count == 0)
        {
            attempt.Error = NoFormatError(attempt.Stats, profile.Key);
            attempt.Summary = $"no directly usable formats ({attempt.Stats.Advertised} advertised)";
            return attempt;
        }
        await VerifyAndCompleteAsync(attempt, root.P("streamingData"), ctx, ct).ConfigureAwait(false);
        return attempt;
    }

    /// <summary>
    /// Byte-probes the formats and, concurrently, adds a self-contained HLS alternative when no muxed HTTP format exists
    /// (adaptive tracks must not hide a complete stream). A failed probe discards the whole strategy.
    /// </summary>
    private async Task VerifyAndCompleteAsync(Attempt attempt, JsonElement? streamingData, MediaRequestContext ctx, CancellationToken ct)
    {
        var verify = VerifyAccessAsync(attempt.Reps, ct);
        var hls = new List<MediaRepresentation>();
        Task? manifest = null;
        if (!attempt.Reps.Any(r => r.Content == StreamContent.VideoAndAudio) && U(streamingData.P("hlsManifestUrl").Str()) is { } hlsUrl)
            manifest = TryAddManifestAsync(hlsUrl, ctx, "yt-hls-", hls, [], [], [], ct);
        var (ok, detail) = await verify.ConfigureAwait(false);
        if (manifest is not null) await manifest.ConfigureAwait(false);
        attempt.Summary = $"{attempt.Reps.Count} formats{(hls.Count > 0 ? $" + {hls.Count} HLS" : "")}, {detail}";
        if (!ok)
        {
            attempt.Error = new MediaError(MediaErrorCode.AuthorizationRequired,
                "YouTube listed formats but refused to serve them beyond the first bytes. VESPER does not manufacture proof-of-origin tokens.",
                $"{attempt.Key}: {detail}", true, true, false);
            attempt.Reps = [];
            return;
        }
        attempt.Reps.AddRange(hls);
    }

    private async Task<Attempt> FromPagePlayerAsync(JsonElement root, string videoId, Uri pageUrl, CancellationToken ct)
    {
        var attempt = new Attempt("watch-page");
        if (Playability(root, out var status, out var reason) is { } statusError)
        {
            attempt.Error = statusError;
            attempt.Summary = $"playabilityStatus={status} ({TextUtil.Clip(reason, 80)})";
            return attempt;
        }
        if (root.P("videoDetails").P("isLive").Bool() == true)
        {
            attempt.Error = MediaErrors.Live("The player reports a live broadcast; live capture is not supported (completed recordings are).");
            attempt.Summary = "live broadcast";
            return attempt;
        }
        var ctx = new MediaRequestContext { UserAgent = DesktopUserAgent, Referer = pageUrl.AbsoluteUri };
        attempt.Reps = ParseFormats(root.P("streamingData"), videoId, ctx, attempt.Stats, allowThrottled: false);
        if (attempt.Reps.Count == 0)
        {
            attempt.Error = NoFormatError(attempt.Stats, "watch-page");
            attempt.Summary = $"no directly usable formats ({attempt.Stats.Advertised} advertised)";
            return attempt;
        }
        await VerifyAndCompleteAsync(attempt, root.P("streamingData"), ctx, ct).ConfigureAwait(false);
        return attempt;
    }

    /// <summary>
    /// One 1-byte range request near the end of a representation: proves the media is served past the first bytes.
    /// </summary>
    private async Task<(bool Ok, string Detail)> VerifyAccessAsync(List<MediaRepresentation> reps, CancellationToken ct)
    {
        var target = reps.Where(r => r.FileSize is > 1_500_000).OrderBy(r => r.FileSize).FirstOrDefault()
                     ?? reps.OrderBy(r => r.FileSize ?? long.MaxValue).First();
        long offset = target.FileSize is long size && size > 1 ? size - 1 : 0;
        try
        {
            int status = await Http.ProbeRangeAsync(target.Url, target.Context, offset, ct).ConfigureAwait(false);
            return (status is 206 or 200, $"byte probe {target.Id}@{offset} -> HTTP {status}");
        }
        catch (MediaResolverException ex)
        {
            return (false, $"byte probe {target.Id} failed ({ex.Error.Code})");
        }
    }

    private static MediaError? Playability(JsonElement root, out string status, out string reason)
    {
        var ps = root.P("playabilityStatus");
        status = ps.P("status").Str() ?? "";
        reason = ps.P("reason").Str() ?? ps.P("errorScreen").P("playerErrorMessageRenderer").P("reason").P("simpleText").Str() ?? status;
        return status == "OK" ? null : MapStatus(status, reason);
    }

    private static MediaError NoFormatError(FormatStats s, string strategy)
    {
        if (s.Drm > 0) return MediaErrors.Drm($"{strategy}: {s.Drm} DRM-protected formats; no clear formats offered");
        if (s.Cipher > 0 || s.Throttled > 0)
            return new MediaError(MediaErrorCode.BrowserContextRequired,
                "YouTube only offers this video's streams to players that run its JavaScript challenges, which VESPER does not execute. Use the VESPER extension on the open page.",
                $"{strategy}: {s.Cipher} formats use signatureCipher, {s.Throttled} carry the n challenge (unsupported player transformation)", false, true, false);
        if (s.ServerAbr)
            return MediaErrors.BrowserContext($"{strategy}: only a server-ABR playback endpoint was supplied (unsupported streaming feature)");
        if (s.Otf > 0) return MediaErrors.UnsupportedManifest($"{strategy}: only segmented OTF formats were offered");
        return MediaErrors.BrowserContext(s.Advertised > 0
            ? $"{strategy}: {s.Advertised} formats advertised but none exposes a directly usable media URL"
            : $"{strategy}: metadata but no playback URLs");
    }

    /// <summary>The most informative failure across strategies: facts about the video beat strategy/network noise.</summary>
    private static MediaError BestError(List<Attempt> attempts, string videoId)
    {
        static int Rank(MediaError e) => e.Code switch
        {
            MediaErrorCode.DrmProtected => 0,
            MediaErrorCode.RegionRestricted => 1,
            MediaErrorCode.LoginRequired => 2,
            MediaErrorCode.LiveStreamNotSupported => 3,
            MediaErrorCode.VideoUnavailable => 4,
            MediaErrorCode.BrowserContextRequired when e.Diagnostic.Contains("bot", StringComparison.OrdinalIgnoreCase) => 5,
            MediaErrorCode.RateLimited => 6,
            MediaErrorCode.AuthorizationRequired => 7,
            MediaErrorCode.BrowserContextRequired => 8,
            MediaErrorCode.UnsupportedManifest => 9,
            _ => 10
        };
        var errors = attempts.Where(a => a.Error is not null).Select(a => a.Error!).ToList();
        if (errors.Count == 0) return MediaErrors.NoMedia($"No strategy produced formats for {videoId}");
        var best = errors.OrderBy(Rank).First();
        string trail = string.Join("; ", attempts.Select(a => $"{a.Key}: {a.Summary}"));
        return best with { Diagnostic = TextUtil.Clip($"{best.Diagnostic} | strategies: {trail}", 900) };
    }

    private static MediaError MapStatus(string status, string reason)
    {
        string r = reason.ToLowerInvariant();
        string diag = $"playabilityStatus={status}: {TextUtil.Clip(reason, 200)}";
        switch (status)
        {
            case "LOGIN_REQUIRED" when r.Contains("bot"):
                return new MediaError(MediaErrorCode.BrowserContextRequired,
                    "YouTube is asking this network to sign in to confirm it's not a bot. Use the VESPER extension from a page where you are signed in, or try again later.", diag, false, true, false);
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

    private static long? NumL(JsonElement? e) =>
        e.Long() ?? (long.TryParse(e.Str(), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null);

    internal static List<MediaRepresentation> ParseFormats(JsonElement? streamingData, string videoId, MediaRequestContext ctx,
        FormatStats stats, bool allowThrottled)
    {
        var reps = new List<MediaRepresentation>();
        if (streamingData is null) return reps;
        stats.ServerAbr = streamingData.P("serverAbrStreamingUrl").Str() is not null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in streamingData.P("formats").Items().Concat(streamingData.P("adaptiveFormats").Items()))
        {
            stats.Advertised++;
            if (f.P("drmFamilies") is not null || f.P("drmTrackType") is not null) { stats.Drm++; continue; }
            var url = U(f.P("url").Str());
            if (url is null)
            {
                if (f.P("signatureCipher") is not null || f.P("cipher") is not null) stats.Cipher++; else stats.NoUrl++;
                continue;
            }
            if (f.P("type").Str() == "FORMAT_STREAM_TYPE_OTF") { stats.Otf++; continue; }
            if (!allowThrottled && UrlUtil.QueryValue(url, "n") is not null) { stats.Throttled++; continue; }

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

            string itag = f.P("itag").Str() ?? NumL(f.P("itag"))?.ToString(CultureInfo.InvariantCulture) ?? reps.Count.ToString(CultureInfo.InvariantCulture);
            bool drc = f.P("isDrc").Bool() == true || UrlUtil.QueryValue(url, "xtags")?.Contains("drc=1", StringComparison.Ordinal) == true;
            string? trackId = f.P("audioTrack").P("id").Str();
            string? lang = trackId is null ? null : TextUtil.NormalizeLang(trackId.Split('.')[0]);
            string? trackName = f.P("audioTrack").P("displayName").Str();
            string id = $"yt-{itag}{(drc ? "-drc" : "")}{(trackId is null ? "" : "-" + new string(trackId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray()))}";
            if (!seen.Add(id)) continue;

            long? size = NumL(f.P("contentLength")) ?? (long.TryParse(UrlUtil.QueryValue(url, "clen"), NumberStyles.None, CultureInfo.InvariantCulture, out long clen) ? clen : null);
            string? lmt = f.P("lastModified").Str() ?? UrlUtil.QueryValue(url, "lmt");
            string? identity = size is > 0 && lmt is { Length: > 0 and <= 32 }
                ? $"youtube:{videoId}:{itag}:{lmt}:{size}{(drc ? ":drc" : "")}{(trackId is null ? "" : ":" + trackId)}" : null;

            int? avgBitrate = f.P("averageBitrate").Int();
            int? peakBitrate = f.P("bitrate").Int();
            int? kbps = avgBitrate is not null ? avgBitrate / 1000 : peakBitrate / 1000;
            string? quality = f.P("qualityLabel").Str();
            string label = isAudio
                ? $"{(kbps is null ? "" : kbps + "k ")}{aCodec ?? "audio"}{(trackName is null ? "" : " · " + trackName)}{(drc ? " · DRC" : "")}"
                : $"{quality ?? (f.P("height").Int() is { } hh ? hh + "p" : "video")}{(hdr != HdrKind.Sdr && quality?.Contains("HDR") != true ? " HDR" : "")}{(f.P("projectionType").Str() is "MESH" or "EQUIRECTANGULAR" ? " 360°" : "")}";

            reps.Add(new MediaRepresentation
            {
                Id = id,
                Url = url,
                Protocol = StreamProtocol.Http,
                Content = content,
                Container = CodecInfo.ContainerFromMime(baseMime),
                VideoCodec = isVideo ? vCodec : null,
                AudioCodec = aCodec,
                RawCodecs = codecs,
                Width = f.P("width").Int(),
                Height = f.P("height").Int(),
                FrameRate = f.P("fps").Dbl(),
                Hdr = isVideo ? hdr : HdrKind.Sdr,
                BitrateKbps = kbps,
                AudioChannels = f.P("audioChannels").Int(),
                AudioSampleRate = (int?)NumL(f.P("audioSampleRate")),
                AudioLanguage = lang,
                AudioTrackId = trackId,
                IsDefaultAudio = f.P("audioTrack").P("audioIsDefault").Bool() == true || (trackId is null && isAudio && !drc),
                IsAudioDescription = trackName?.Contains("descriptive", StringComparison.OrdinalIgnoreCase) == true
                    || trackName?.Contains("description", StringComparison.OrdinalIgnoreCase) == true,
                IsDrc = drc,
                FileSize = size,
                ExpiresAt = ExpiryParser.FromUrl(url),
                ContentIdentity = identity,
                Label = label,
                Context = ctx
            });
        }
        return reps;
    }

    private static List<SubtitleTrack> ParseCaptions(JsonElement root)
    {
        var subs = new List<SubtitleTrack>();
        foreach (var track in root.P("captions").P("playerCaptionsTracklistRenderer").P("captionTracks").Items())
        {
            var baseUrl = U(track.P("baseUrl").Str());
            if (baseUrl is null || !UrlUtil.HostIs(baseUrl, "youtube.com")) continue;
            string raw = baseUrl.AbsoluteUri;
            var vtt = new Uri(UrlUtil.QueryValue(baseUrl, "fmt") is null ? raw + "&fmt=vtt" : Regex.Replace(raw, "([?&])fmt=[^&]*", "$1fmt=vtt"));
            string? name = track.P("name").P("simpleText").Str() ?? track.P("name").P("runs").At(0).P("text").Str();
            subs.Add(new SubtitleTrack(vtt, TextUtil.NormalizeLang(track.P("languageCode").Str()), name, "vtt", track.P("kind").Str() == "asr"));
        }
        return subs;
    }

    private static List<AudioTrack> BuildAudioTracks(IEnumerable<MediaRepresentation> reps) =>
        reps.Where(r => r.Content == StreamContent.AudioOnly && r.AudioTrackId is not null)
            .GroupBy(r => r.AudioTrackId!)
            .Select(g => new AudioTrack(g.Key, g.First().AudioLanguage, null, g.Any(r => r.IsDefaultAudio), g.First().AudioChannels, g.First().AudioCodec))
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
        var page = await GetPageAsync(pageUrl, PageContext(request), ct).ConfigureAwait(false);
        using var data = JsonBalanced.ExtractAfter(page.Content, "ytInitialData");
        if (data is null) return Fail(MediaErrors.SourceChanged("ytInitialData was not found"));
        var root = data.RootElement;
        var cfg = YtPageConfig.Parse(page.Content);

        var entries = new List<CollectionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int limit = Math.Min(request.Options.MaxCollectionItems, Options.MaxCollectionItems);
        string rendererName = dynamicMix ? "playlistPanelVideoRenderer" : "playlistVideoRenderer";
        if (dynamicMix)
        {
            // Only the watch-next playlist panel holds Mix entries; related-video lockups elsewhere on the page are not part of it.
            var panel = root.FindAll("playlist", 8).FirstOrDefault(p => p.P("contents") is not null);
            if (panel.ValueKind == JsonValueKind.Object) AddEntries(panel, rendererName, entries, seen, limit);
        }
        else AddEntries(root, rendererName, entries, seen, limit);

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
        else
        {
            string? token = ContinuationToken(root);
            if (token is null && (root.FindAll("continuationItemRenderer", 1).Any() || root.FindAll("continuationItemViewModel", 1).Any()))
            {
                complete = false;
                warnings.Add(MediaErrors.PartialPlaylist("The playlist page announces more entries, but its continuation format was not recognised (page structure change)."));
            }
            int pages = 1;
            while (token is not null && entries.Count < limit && pages < Options.MaxCollectionPages)
            {
                ct.ThrowIfCancellationRequested();
                using var next = await BrowseContinuationAsync(token, cfg, ct).ConfigureAwait(false);
                if (next.Doc is null)
                {
                    warnings.Add(MediaErrors.PartialPlaylist($"Further playlist pages could not be loaded ({next.Problem}); {entries.Count} entries are listed."));
                    break;
                }
                int before = entries.Count;
                AddEntries(next.Doc.RootElement, "playlistVideoRenderer", entries, seen, limit);
                token = ContinuationToken(next.Doc.RootElement);
                pages++;
                if (entries.Count == before) { token = null; break; }
            }
            if (token is not null)
            {
                complete = false;
                if (warnings.Count == 0)
                    warnings.Add(MediaErrors.PartialPlaylist(entries.Count >= limit
                        ? $"The playlist has more entries than the configured limit ({limit})."
                        : $"Paging stopped after {pages} pages (configured page limit)."));
            }
            else if (warnings.Count > 0) complete = false;
        }

        if (entries.Count == 0 && dynamicMix)
            return Fail(MediaErrors.SourceChanged("The watch page no longer embeds the Mix panel (page structure change). Mix enumeration needs the InnerTube 'next' endpoint, which is not implemented yet; the current video itself still resolves."));
        if (entries.Count == 0)
            return Fail(MediaErrors.Unavailable("The playlist is empty, private or unavailable"));

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

    /// <summary>Accepts both the classic renderer and the 2025+ lockup view model (video lockups only).</summary>
    private static void AddEntries(JsonElement root, string rendererName, List<CollectionEntry> entries, HashSet<string> seen, int limit)
    {
        AddRendererEntries(root, rendererName, entries, seen, limit);
        foreach (var e in root.FindAll("lockupViewModel", limit * 2 + 10))
        {
            if (entries.Count >= limit) break;
            if (e.P("contentType").Str() is { } type && type != "LOCKUP_CONTENT_TYPE_VIDEO") continue;
            string? id = e.P("contentId").Str();
            if (id is null || !VideoIdRegex().IsMatch(id) || !seen.Add(id)) continue;
            string? title = e.P("metadata").P("lockupMetadataViewModel").P("title").P("content").Str();
            TimeSpan? dur = e.FindAll("thumbnailBadgeViewModel", 4).Select(b => TextUtil.ParseDuration(b.P("text").Str())).FirstOrDefault(d => d is not null);
            string? thumb = e.FindAll("sources", 2).SelectMany(x => x.Items()).Select(x => x.P("url").Str()).LastOrDefault(x => x is not null);
            entries.Add(new CollectionEntry
            {
                MediaId = id, Url = new Uri($"https://www.youtube.com/watch?v={id}"), Title = title, Duration = dur,
                Thumbnail = U(thumb), Index = entries.Count
            });
        }
    }

    private static void AddRendererEntries(JsonElement root, string rendererName, List<CollectionEntry> entries, HashSet<string> seen, int limit)
    {
        foreach (var e in root.FindAll(rendererName, limit * 2 + 10))
        {
            if (entries.Count >= limit) break;
            string? id = e.P("videoId").Str();
            if (id is null || !VideoIdRegex().IsMatch(id) || !seen.Add(id)) continue;
            string? title = e.P("title").P("simpleText").Str() ?? e.P("title").P("runs").At(0).P("text").Str();
            TimeSpan? dur = NumL(e.P("lengthSeconds")) is { } ls ? TimeSpan.FromSeconds(ls) : TextUtil.ParseDuration(e.P("lengthText").P("simpleText").Str());
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
    }

    /// <summary>Continuation token under any continuationCommand (renderer or view-model wrappers).</summary>
    private static string? ContinuationToken(JsonElement root) =>
        root.FindAll("continuationCommand", 8)
            .Select(c => c.P("token").Str())
            .FirstOrDefault(s => s is { Length: > 8 and < 4096 });

    private sealed class BrowsePage(JsonDocument? doc, string problem) : IDisposable
    {
        public JsonDocument? Doc { get; } = doc;
        public string Problem { get; } = problem;
        public void Dispose() => Doc?.Dispose();
    }

    private async Task<BrowsePage> BrowseContinuationAsync(string token, YtPageConfig cfg, CancellationToken ct)
    {
        string version = cfg.WebClientVersion ?? YtPageConfig.DefaultWebClientVersion;
        var client = new JsonObject { ["clientName"] = "WEB", ["clientVersion"] = version, ["hl"] = "en", ["gl"] = "US" };
        if (cfg.VisitorData is not null) client["visitorData"] = cfg.VisitorData;
        string body = new JsonObject { ["context"] = new JsonObject { ["client"] = client }, ["continuation"] = token }.ToJsonString();
        var headers = new Dictionary<string, string>
        {
            ["X-YouTube-Client-Name"] = "1", ["X-YouTube-Client-Version"] = version, ["Origin"] = "https://www.youtube.com"
        };
        if (cfg.VisitorData is not null) headers["X-Goog-Visitor-Id"] = cfg.VisitorData;
        try
        {
            var resp = await Http.PostJsonAsync(BrowseEndpoint, body, new MediaRequestContext { UserAgent = DesktopUserAgent }, headers, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return new BrowsePage(null, $"HTTP {resp.StatusCode}");
            return JsonExt.TryParse(resp.Content) is { } doc ? new BrowsePage(doc, "") : new BrowsePage(null, "invalid JSON");
        }
        catch (MediaResolverException ex) { return new BrowsePage(null, ex.Error.Code.ToString()); }
    }
}
