using System.Text.Json;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Reads verified Instagram media links already present in a post page; player-only media stays browser-led.</summary>
internal sealed class InstagramAdapter : AdapterBase
{
    private const string PageAccept = "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5";
    private const int MaxSjsScripts = 8;
    private const int MaxEmbeddedJsonChars = 1_500_000;
    private const int MaxVideoChoices = 16;
    private const int MaxCarouselItems = 20;
    private readonly GenericAdapter _generic;

    private sealed record EmbeddedVideo(string MediaId, int Index, string? Title, string? Description,
        string? Uploader, Uri? Thumbnail, double? DurationSeconds, List<MediaCandidate> Candidates);
    private sealed record EmbeddedPageMedia(IReadOnlyList<EmbeddedVideo> Videos, bool IsCarousel,
        int SlideCount, bool Truncated, string? Caption, string? Uploader);

    public static readonly AdapterDescriptor Descriptor = new(
        "instagram", "Instagram", Matches, svc => new InstagramAdapter(svc),
        new AdapterCapabilities(
            Domains: ["instagram.com"],
            UrlPatterns: ["instagram.com/p/{code}", "instagram.com/reel/{code}", "instagram.com/tv/{code}", "instagram.com/stories/{user}/{id}"],
            SingleItem: true, Playlist: true, AudioOnly: false, Subtitles: true,
            Authentication: "Uses the page or explicitly authorized browser context; private content still requires access",
            KnownUnsupported: ["player-only media without an observed whole-object source", "multi-story sequence enumeration", "private, expired or access-gated media"],
            Notes: "Reads bounded page-embedded media URLs, including video slides in accessible carousels, and verifies them through the shared candidate pipeline."));

    private InstagramAdapter(ResolverServices svc) : base(svc) => _generic = new GenericAdapter(svc);

    public override bool CanHandle(Uri url) => InstagramTarget.TryParse(url, out _);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        if (!InstagramTarget.TryParse(request.Url, out InstagramTarget target))
            return Fail(MediaErrors.NotThisSite("Not an Instagram post, Reel or story URL"));

        HttpTextResponse page;
        try
        {
            page = await Http.GetStringAsync(request.Url, request.AuthContext, ct, Options.MaxPageBytes, PageAccept)
                .ConfigureAwait(false);
        }
        catch (MediaResolverException ex)
        {
            return Fail(ex.Error);
        }

        if (!page.IsSuccess) return Fail(MediaErrors.FromHttpStatus(page.StatusCode, page.FinalUrl));
        if (IsLoginPage(page.FinalUrl, page.Content))
            return Fail(MediaErrors.LoginRequired("Instagram redirected this page to sign-in. Open it in a browser account that can access the post."));
        if (!page.IsText)
            return Fail(MediaErrors.NoMedia("Instagram returned a non-text page without a verified media URL. Open the post in Chrome and use the VESPER picker.", true));

        request.PageResponse = page;
        EmbeddedPageMedia? embedded = ExtractEmbeddedMedia(page.Content, target, Options.MaxCandidatesPerPage, out bool ambiguous);
        if (ambiguous)
            return Fail(MediaErrors.BrowserContext("Instagram exposed competing media without a verified match for the requested post. Select the intended player in Chrome."));
        if (embedded is { IsCarousel: true })
            return await ResolveCarouselAsync(request, target, page, embedded, ct).ConfigureAwait(false);

        List<MediaCandidate> candidates = embedded?.Videos.FirstOrDefault()?.Candidates ?? [];
        MediaDiscoveryResult result = await _generic.ResolveAsync(request, candidates, ct, includeBrowserCandidates: candidates.Count == 0)
            .ConfigureAwait(false);
        if (result.Item is { } item)
            return MediaDiscoveryResult.FromItem(item with
            {
                Provider = "instagram",
                MediaId = embedded?.Videos.FirstOrDefault()?.MediaId ?? target.MediaId
            }, result.Warnings, result.Notes);
        if (result.Collection is not null) return result;

        if (result.Error?.Code == MediaErrorCode.NoDownloadableMedia)
        {
            if (IsLoginPage(page.FinalUrl, page.Content))
                return Fail(MediaErrors.LoginRequired("Instagram returned a sign-in page instead of accessible media."));
            return Fail(MediaErrors.BrowserContext("Instagram exposed page metadata but no verified playable URL. Open the accessible post or story in Chrome and use the VESPER picker; the browser can observe media that the page does not publish in its HTML."));
        }
        return result;
    }

    private async Task<MediaDiscoveryResult> ResolveCarouselAsync(MediaDiscoveryRequest request, InstagramTarget target,
        HttpTextResponse page, EmbeddedPageMedia carousel, CancellationToken ct)
    {
        PageMetadata metadata = PageScanner.Scan(page.Content, page.FinalUrl, Options.MaxCandidatesPerPage);
        MediaRequestContext context = request.ValidatedBrowser?.Context
            ?? new MediaRequestContext { Referer = UrlUtil.WithoutFragment(page.FinalUrl).AbsoluteUri };
        var entries = new List<CollectionEntry>();
        var warnings = new List<MediaError>();
        bool collectionRequested = request.Options.CollectionIntent == CollectionIntent.CollectionOnly;

        foreach (EmbeddedVideo video in carousel.Videos)
        {
            ct.ThrowIfCancellationRequested();
            CandidateOutcome outcome = await Svc.Candidates.ProcessAsync(video.Candidates, context, ct).ConfigureAwait(false);
            if (outcome.Representations.Count == 0)
            {
                warnings.Add(MediaErrors.PartialPlaylist($"Instagram carousel video {video.Index + 1} had no verified media URL"));
                continue;
            }

            string collectionTitle = carousel.Caption ?? metadata.Title ?? $"Instagram post {target.MediaId}";
            var item = new MediaItem
            {
                SourceUrl = request.Url,
                Provider = "instagram",
                MediaId = video.MediaId,
                Title = video.Title ?? $"{collectionTitle} · video {video.Index + 1}",
                Description = video.Description ?? carousel.Caption ?? metadata.Description,
                Uploader = video.Uploader ?? carousel.Uploader,
                Duration = outcome.Duration ?? TextUtil.Seconds(video.DurationSeconds),
                Thumbnail = video.Thumbnail ?? metadata.Thumbnail,
                Representations = outcome.Representations,
                AudioTracks = outcome.AudioTracks,
                Subtitles = request.Options.IncludeSubtitles ? metadata.Subtitles : []
            };
            entries.Add(new CollectionEntry
            {
                MediaId = video.MediaId,
                Url = request.Url,
                Title = item.Title,
                Duration = item.Duration,
                Thumbnail = item.Thumbnail,
                Index = video.Index,
                Resolved = item
            });
            if (!collectionRequested) break;
        }

        if (collectionRequested && carousel.Truncated)
            warnings.Add(MediaErrors.PartialPlaylist("The Instagram carousel exceeded the bounded discovery limit; later slides were not inspected"));

        string title = carousel.Caption ?? metadata.Title ?? $"Instagram post {target.MediaId}";
        var collection = new MediaCollection
        {
            SourceUrl = request.Url,
            Provider = "instagram",
            CollectionId = target.MediaId,
            Title = title,
            Kind = CollectionKind.Album,
            Entries = entries,
            IsComplete = warnings.Count == 0 && !carousel.Truncated
        };

        if (collectionRequested)
            return MediaDiscoveryResult.FromCollection(collection, warnings);
        if (entries.Count == 0)
            return Fail(MediaErrors.BrowserContext("Instagram exposed a carousel but no video slide had a verified playable URL. Open the post in Chrome and use the VESPER picker."));

        MediaItem first = entries[0].Resolved!;
        if (carousel.SlideCount > 1)
            first = first with { Related = new CollectionHint(CollectionKind.Album, target.MediaId, request.Url, false, title) };
        return MediaDiscoveryResult.FromItem(first, warnings);
    }

    private static EmbeddedPageMedia? ExtractEmbeddedMedia(string html, InstagramTarget target, int maxCandidates, out bool ambiguous)
    {
        ambiguous = false;
        maxCandidates = Math.Max(1, maxCandidates);
        int scripts = 0;
        EmbeddedPageMedia? unlabelled = null;
        bool competing = false;
        foreach (HtmlTag tag in HtmlScanner.Scan(html))
        {
            if (tag.Name != "script" || tag.Attr("data-sjs") is null) continue;
            if (++scripts > MaxSjsScripts)
            {
                if (unlabelled is not null) competing = true;
                break;
            }
            if (tag.Body is not { Length: > 0 and <= MaxEmbeddedJsonChars } body) continue;
            using JsonDocument? document = JsonExt.TryParse(body);
            if (document is null) continue;

            foreach (JsonElement media in document.RootElement.FindAll("if_not_gated_logged_out", maxResults: 32))
            {
                string? code = target.Shortcode is null ? null : media.P("code").Str() ?? media.P("shortcode").Str();
                bool hasShortcode = !string.IsNullOrWhiteSpace(code);
                if (hasShortcode && !string.Equals(code, target.Shortcode, StringComparison.Ordinal))
                {
                    if (HasVideoSource(media) || media.P("carousel_media") is { ValueKind: JsonValueKind.Array })
                        competing = true;
                    continue;
                }
                bool useImmediately = hasShortcode || target.Shortcode is null;
                string? caption = media.P("caption").P("text").Str();
                string? uploader = media.P("owner").P("username").Str() ?? media.P("user").P("username").Str();
                if (media.P("carousel_media") is { ValueKind: JsonValueKind.Array } slides)
                {
                    int slideCount = slides.GetArrayLength();
                    var videos = new List<EmbeddedVideo>();
                    int candidateCount = 0;
                    int slideIndex = 0;
                    bool truncated = slideCount > MaxCarouselItems;
                    foreach (JsonElement slide in slides.Items().Take(MaxCarouselItems))
                    {
                        if (candidateCount >= maxCandidates)
                        {
                            truncated = true;
                            break;
                        }
                        int index = slideIndex++;
                        int choices = Math.Min(4, maxCandidates - candidateCount);
                        EmbeddedVideo? video = ReadVideo(slide, $"{target.MediaId}-{index + 1}", index, caption, uploader, choices);
                        if (video is not null)
                        {
                            videos.Add(video);
                            candidateCount += video.Candidates.Count;
                        }
                    }
                    if (videos.Count > 0)
                    {
                        var candidate = new EmbeddedPageMedia(videos, true, slideCount, truncated, caption, uploader);
                        if (useImmediately) return candidate;
                        if (unlabelled is null) unlabelled = candidate;
                        else if (!SameSources(unlabelled, candidate)) competing = true;
                        continue;
                    }
                }

                EmbeddedVideo? single = ReadVideo(media, target.MediaId, 0, caption, uploader, Math.Min(MaxVideoChoices, maxCandidates));
                if (single is not null)
                {
                    var candidate = new EmbeddedPageMedia([single], false, 1, false, caption, uploader);
                    if (useImmediately) return candidate;
                    if (unlabelled is null) unlabelled = candidate;
                    else if (!SameSources(unlabelled, candidate)) competing = true;
                }
            }
        }
        ambiguous = competing;
        return competing ? null : unlabelled;
    }

    private static bool SameSources(EmbeddedPageMedia first, EmbeddedPageMedia second) =>
        first.IsCarousel == second.IsCarousel && first.SlideCount == second.SlideCount
        && first.Videos.Count == second.Videos.Count
        && first.Videos.Zip(second.Videos).All(pair => pair.First.Index == pair.Second.Index
            && pair.First.Candidates.Select(c => c.Url.AbsoluteUri).ToHashSet(StringComparer.Ordinal)
                .SetEquals(pair.Second.Candidates.Select(c => c.Url.AbsoluteUri)));

    private static bool HasVideoSource(JsonElement media) =>
        media.P("video_versions") is { ValueKind: JsonValueKind.Array }
        || !string.IsNullOrWhiteSpace(media.P("video_url").Str());

    private static EmbeddedVideo? ReadVideo(JsonElement media, string fallbackId, int index,
        string? sharedCaption, string? sharedUploader, int maxChoices)
    {
        if (!HasVideoSource(media) || maxChoices <= 0) return null;
        double? duration = media.P("video_duration").Dbl();
        if (duration is not { } seconds || !double.IsFinite(seconds) || seconds < 0 || seconds > 86_400)
            duration = null;

        var candidates = new List<MediaCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement version in media.P("video_versions").Items())
            AddCandidate(version.P("url").Str(), version, duration, candidates, seen, maxChoices);
        AddCandidate(media.P("video_url").Str(), media, duration, candidates, seen, maxChoices);
        if (candidates.Count == 0) return null;

        string mediaId = media.P("id").Str() ?? media.P("pk").Str() ?? fallbackId;
        string? title = media.P("title").Str();
        string? caption = media.P("caption").P("text").Str() ?? sharedCaption;
        string? uploader = media.P("owner").P("username").Str() ?? media.P("user").P("username").Str() ?? sharedUploader;
        Uri? thumbnail = UrlUtil.TryAbsolute(media.P("display_url").Str() ?? media.P("thumbnail_src").Str());
        return new EmbeddedVideo(mediaId, index, title, caption, uploader, thumbnail, duration, candidates);
    }

    private static void AddCandidate(string? rawUrl, JsonElement metadata, double? duration,
        List<MediaCandidate> results, HashSet<string> seen, int maxChoices)
    {
        if (results.Count >= maxChoices || UrlUtil.TryAbsolute(rawUrl) is not { } url
            || url.Scheme is not ("http" or "https") || !IsInstagramMediaHost(url) || !seen.Add(url.AbsoluteUri)) return;

        int? width = PositiveDimension(metadata.P("width").Int());
        int? height = PositiveDimension(metadata.P("height").Int());
        results.Add(new MediaCandidate(url, CandidateKind.Direct, "video/mp4", width, height,
            DurationSeconds: duration));
    }

    private static int? PositiveDimension(int? value) => value is > 0 and <= 16_384 ? value : null;

    private static bool IsInstagramMediaHost(Uri url)
    {
        string host = url.IdnHost;
        return host.Equals("cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cdninstagram.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("fbcdn.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginPage(Uri url, string body)
    {
        string host = url.IdnHost;
        bool isInstagramHost = host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);
        if (isInstagramHost
            && url.AbsolutePath.StartsWith("/accounts/login", StringComparison.OrdinalIgnoreCase))
            return true;
        return isInstagramHost && (body.Contains("login_required", StringComparison.OrdinalIgnoreCase)
            || body.Contains("loginAndSignupPage", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(Uri url) => InstagramTarget.TryParse(url, out _);

    private readonly record struct InstagramTarget(string MediaId, string? Shortcode = null)
    {
        public static bool TryParse(Uri url, out InstagramTarget target)
        {
            target = default;
            string host = url.IdnHost;
            if (!host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase)) return false;

            string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int kind = parts.Length == 3 && parts[0] is not ("stories" or "p" or "reel" or "reels" or "tv") && parts[0].Length <= 30
                && parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') ? 1 : 0;
            if (parts.Length == kind + 2 && parts[kind] is "p" or "reel" or "reels" or "tv"
                && parts[kind + 1] is { Length: <= 128 } code && code != "audio"
                && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                target = new InstagramTarget(code, code);
                return true;
            }

            if (parts.Length >= 2 && parts[0] == "stories")
            {
                string id = parts.Length >= 3 ? parts[2] : parts[1];
                if (id.Length is > 0 and <= 128)
                {
                    target = new InstagramTarget("story-" + id);
                    return true;
                }
            }
            return false;
        }
    }
}
