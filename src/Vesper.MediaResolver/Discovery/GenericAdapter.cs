using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

/// <summary>
/// Fallback for everything without a dedicated adapter, and the engine behind browser-assisted discovery:
/// direct files, HLS/DASH URLs, HTML5 media, page metadata, JSON-LD, supported embeds and podcast feeds.
/// Nothing is reported as downloadable unless a probe or a manifest parse verified it.
/// </summary>
internal sealed class GenericAdapter : IMediaSourceAdapter
{
    public static readonly AdapterDescriptor Descriptor = new(
        "generic",
        "Generic media (direct files, HTML5, HLS, DASH, feeds)",
        _ => true,
        svc => new GenericAdapter(svc),
        new AdapterCapabilities(
            Domains: ["*"],
            UrlPatterns:
            [
                "Direct media files (mp4, webm, mkv, mov, mp3, m4a, ogg, opus, flac, wav ...) verified by HEAD/ranged GET",
                "HLS (.m3u8) and DASH (.mpd) manifest URLs",
                "HTML pages: video/audio/source/track, og:video, twitter:player:stream, JSON-LD VideoObject, inline manifest URLs",
                "Embedded players that a dedicated adapter supports (one level deep)",
                "RSS/Atom podcast feeds with enclosures (collection)",
                "Browser-supplied candidates (mode B)"
            ],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: true,
            Authentication: "Only via validated browser authorization context (same origin, explicit consent)",
            KnownUnsupported: ["DRM streams", "live streams", "media created by running page scripts", "multi-period DASH"],
            Notes: "Success requires a verified, actionable representation."));

    private const string PageAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,application/rss+xml;q=0.9,*/*;q=0.5";

    private readonly ResolverServices _svc;

    public GenericAdapter(ResolverServices svc) => _svc = svc;

    public bool CanHandle(Uri url) => true;

    public async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var browser = request.ValidatedBrowser;
        var candidates = new List<MediaCandidate>();
        if (browser is not null) candidates.AddRange(browser.Candidates);

        PageMetadata? meta = null;
        Uri pageUrl = request.Url;

        if (candidates.Count == 0)
        {
            var kind = MediaCandidate.Classify(pageUrl, null);
            if (kind != CandidateKind.Unknown)
            {
                candidates.Add(new MediaCandidate(pageUrl, kind));
            }
            else
            {
                var resp = (request.PageResponse ?? await _svc.Http.GetStringAsync(pageUrl, request.AuthContext, ct, accept: PageAccept).ConfigureAwait(false)).EnsureSuccess();
                request.PageResponse = resp;
                pageUrl = resp.FinalUrl;

                if (!resp.IsText)
                {
                    var k = MediaCandidate.Classify(resp.FinalUrl, resp.ContentType);
                    candidates.Add(new MediaCandidate(resp.FinalUrl, k == CandidateKind.Unknown ? CandidateKind.Direct : k, resp.ContentType));
                }
                else if (SniffManifest(resp.Content) is { } mk)
                {
                    candidates.Add(new MediaCandidate(resp.FinalUrl, mk));
                }
                else if (PodcastFeedParser.LooksLikeFeed(resp.ContentType, resp.Content))
                {
                    var feed = PodcastFeedParser.Parse(resp.Content, resp.FinalUrl, request.Options.MaxCollectionItems);
                    if (feed is not null) return MediaDiscoveryResult.FromCollection(feed with { SourceUrl = request.Url });
                }
                else
                {
                    meta = PageScanner.Scan(resp.Content, resp.FinalUrl, _svc.Options.MaxCandidatesPerPage);
                    candidates.AddRange(meta.Candidates);
                    request.Publish(new DiscoveryProgress(meta.Title, meta.Thumbnail));
                }
            }
        }

        var ctx = browser?.Context
            ?? (meta is not null ? new MediaRequestContext { Referer = UrlUtil.WithoutFragment(pageUrl).AbsoluteUri } : MediaRequestContext.Empty);

        var outcome = await _svc.Candidates.ProcessAsync(candidates, ctx, ct).ConfigureAwait(false);

        if (outcome.Representations.Count == 0)
        {
            var embedded = await TryEmbedsAsync(request, meta, ct).ConfigureAwait(false);
            if (embedded is not null) return embedded;
            return MediaDiscoveryResult.Failure(PickError(outcome, candidates.Count, browser is not null));
        }

        string title = browser?.Title ?? meta?.Title ?? TitleFromUrl(request.Url);
        var subtitles = new List<SubtitleTrack>();
        if (request.Options.IncludeSubtitles)
        {
            subtitles.AddRange(outcome.Subtitles);
            if (meta is not null) subtitles.AddRange(meta.Subtitles);
            if (browser is not null) subtitles.AddRange(browser.Subtitles);
        }

        double? candidateDuration = candidates.Select(c => c.DurationSeconds).FirstOrDefault(d => d is not null);
        var item = new MediaItem
        {
            SourceUrl = request.Url,
            Provider = "generic",
            Title = title,
            Description = meta?.Description,
            Duration = outcome.Duration ?? meta?.Duration ?? TextUtil.Seconds(candidateDuration),
            Thumbnail = meta?.Thumbnail,
            Representations = outcome.Representations,
            AudioTracks = outcome.AudioTracks.Concat(browser?.AudioTracks ?? []).ToList(),
            Subtitles = subtitles
        };
        return MediaDiscoveryResult.FromItem(item, outcome.Problems, browser?.Warnings);
    }

    private async Task<MediaDiscoveryResult?> TryEmbedsAsync(MediaDiscoveryRequest request, PageMetadata? meta, CancellationToken ct)
    {
        if (meta is null || meta.Embeds.Count == 0 || !request.Options.ResolveEmbeds
            || request.Options.EmbedDepth >= 1 || _svc.EmbeddedResolver is null)
            return null;

        foreach (var embed in meta.Embeds.Take(3))
        {
            var r = await _svc.EmbeddedResolver(new MediaDiscoveryRequest
            {
                Url = embed,
                Options = new DiscoveryOptions
                {
                    ResolveEmbeds = false,
                    EmbedDepth = request.Options.EmbedDepth + 1,
                    CollectionIntent = CollectionIntent.ItemOnly,
                    IncludeSubtitles = request.Options.IncludeSubtitles
                }
            }, ct).ConfigureAwait(false);
            if (r.IsSuccess) return r;
        }
        return null;
    }

    private static MediaError PickError(CandidateOutcome outcome, int candidateCount, bool browserMode)
    {
        var specific = outcome.Problems.FirstOrDefault(p => p.Code is
            MediaErrorCode.DrmProtected or MediaErrorCode.LiveStreamNotSupported or MediaErrorCode.UnsupportedManifest
            or MediaErrorCode.LoginRequired or MediaErrorCode.AuthorizationRequired or MediaErrorCode.VideoUnavailable
            or MediaErrorCode.RateLimited or MediaErrorCode.RegionRestricted or MediaErrorCode.ResponseTooLarge
            or MediaErrorCode.BlockedBySecurityPolicy);
        if (specific is not null) return specific;

        string diag = candidateCount == 0
            ? "No media tags, metadata, manifest URLs or feed enclosures were found in the page"
            : $"{candidateCount} candidate(s) found but none verified as media" + (outcome.Problems.Count > 0 ? $": {outcome.Problems[0].Diagnostic}" : "");
        return MediaErrors.NoMedia(diag, browserMayHelp: !browserMode);
    }

    private static CandidateKind? SniffManifest(string content)
    {
        var head = content.AsSpan(0, Math.Min(content.Length, 2048)).TrimStart('\uFEFF').TrimStart();
        if (head.StartsWith("#EXTM3U", StringComparison.Ordinal)) return CandidateKind.Hls;
        if (head.IndexOf("<MPD", StringComparison.Ordinal) >= 0) return CandidateKind.Dash;
        return null;
    }

    private static string TitleFromUrl(Uri url)
    {
        string last = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]).Trim('/') : "";
        string stem = Path.GetFileNameWithoutExtension(last);
        return string.IsNullOrWhiteSpace(stem) ? url.Host : stem;
    }
}
