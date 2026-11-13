using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using static VESPER.MediaTransferBridge;

namespace VESPER;

internal sealed partial class BrowserBridgeHandler
{
    private sealed record MediaSession(DateTimeOffset Expires, MediaDownloadChoice[] Choices);
    private sealed record BrowserOption(int Index, string Label, string Detail, string? Container);
    private readonly Dictionary<Guid, MediaSession> _mediaSessions = [];
    private readonly object _mediaSessionsLock = new();

    internal static string MapResolverError(MediaErrorCode? code) => code switch
    {
        MediaErrorCode.NoDownloadableMedia => "no-media",
        MediaErrorCode.VideoUnavailable => "unavailable",
        MediaErrorCode.DrmProtected => "protected",
        MediaErrorCode.RateLimited => "rate-limited",
        MediaErrorCode.LoginRequired or MediaErrorCode.AuthorizationRequired => "sign-in",
        MediaErrorCode.SourceExpired or MediaErrorCode.SourceChanged => "source-expired",
        MediaErrorCode.BlockedBySecurityPolicy or MediaErrorCode.RegionRestricted => "blocked",
        MediaErrorCode.NetworkFailure => "network",
        MediaErrorCode.BrowserContextRequired => "browser-context",
        MediaErrorCode.UnsupportedSite or MediaErrorCode.UnsupportedManifest or MediaErrorCode.LiveStreamNotSupported
            or MediaErrorCode.NoMatchingFormat or MediaErrorCode.ResponseTooLarge => "unsupported",
        MediaErrorCode.InvalidInput => "invalid",
        MediaErrorCode.InternalError => "resolver-error",
        _ => "unavailable"
    };

    private static BrowserDiscoveryMessage MediaHints(BridgeRequest request, Uri page) => new()
    {
        Schema = BrowserDiscoveryMessage.CurrentSchema,
        PageUrl = page.AbsoluteUri,
        Title = request.PageTitle,
        Candidates = (request.Candidates ?? []).Select(h => new BrowserMediaCandidate
        { Url = h.Url, Kind = h.Kind, MimeType = h.MimeType, Source = "network" }).ToList()
    };

    private async Task<BridgeResponse> MediaOptionsAsync(BridgeRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? page))
            return BridgeResponse.Fail(request.Id, "invalid", "This page address is invalid.");

        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        discovery.CancelAfter(TimeSpan.FromSeconds(42)); // Leave time for the browser helper's cold start.
        using var resolver = new DefaultMediaResolver(EngineHost.MediaNetwork(options: engine.Options));
        MediaDiscoveryResult resolved = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = page, Browser = MediaHints(request, page),
            Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly }
        }, discovery.Token);
        if (resolved.Item is not { } item)
        {
            var error = resolved.Error;
            string code = MapResolverError(error?.Code);
            var failure = BridgeResponse.Fail(request.Id, code, error?.UserMessage ?? "No downloadable media was found.");
            return error?.RetryAt is { } retryAt ? failure with { Data = new { retryAt } } : failure;
        }

        return CreateBrowserOptions(request.Id, item, discovery.Token);
    }

    private BridgeResponse CreateBrowserOptions(string requestId, MediaItem item, CancellationToken ct)
    {
        var playable = PlayableOnly(item);
        var jobs = new List<MediaDownloadChoice>();
        var presets = new List<BrowserOption>();
        var originalPresets = new List<BrowserOption>();
        var exact = new List<BrowserOption>();
        long cacheBytes = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrowserOption? OptionFor(MediaDownloadChoice? job, string label)
        {
            if (job is null || job.ExpiresAt is { } expires && expires <= now) return null;
            int index = jobs.IndexOf(job);
            if (index < 0)
            {
                long bytes = 2L * ((job.MediaPlanJson?.Length ?? 0) + (job.MediaMetadataJson?.Length ?? 0) + job.Url.Length + 1024);
                if (jobs.Count >= 64 || cacheBytes + bytes > 2 * 1024 * 1024) return null;
                cacheBytes += bytes;
                index = jobs.Count;
                jobs.Add(job);
            }
            return new BrowserOption(index, label, job.DisplayDetail, job.DisplayContainer);
        }
        void Offer(MediaDownloadChoice? job, string label, List<BrowserOption> options)
        {
            if (OptionFor(job, label) is { } option && !options.Any(previous => previous.Index == option.Index))
                options.Add(option);
        }

        (string Label, FormatPreferences Preferences)[] quality =
        [
            ("Best quality", FormatPreferences.BestQuality), ("Balanced", FormatPreferences.BalancedQuality),
            ("Smallest", FormatPreferences.SmallestSuitable)
        ];
        if (playable.Representations.Any(r => r.HasVideo))
        {
            foreach (var (label, preference) in quality)
            {
                Offer(Prepare(playable, preference, output: MediaOutputPreference.CompatibleVideo).Choice, label, presets);
                Offer(Prepare(playable, preference).Choice, label, originalPresets);
            }
        }
        MediaDownloadChoice? sourceAudio = Prepare(playable, FormatPreferences.AudioOnly).Choice;
        Offer(sourceAudio, "Audio only", presets);
        Offer(sourceAudio, "Audio only", originalPresets);
        MediaDownloadChoice? alternate = Prepare(playable, FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice;
        BrowserOption? audioM4a = alternate != sourceAudio && presets.Any(c => c.Label == "Audio only")
            ? OptionFor(alternate, "Audio only") : null;

        foreach (var format in playable.Representations.OrderByDescending(r => r.Height ?? 0)
                     .ThenByDescending(r => r.FrameRate ?? 0).ThenByDescending(r => r.BitrateKbps ?? 0).Take(56))
        {
            ct.ThrowIfCancellationRequested();
            var parts = new[] { format.Content == StreamContent.AudioOnly ? "Audio" : format.Height is int height ? $"{height}p" : "Video",
                format.FrameRate is > 30.5 ? $"{Math.Round(format.FrameRate.Value)} fps" : null,
                (format.VideoCodec ?? format.AudioCodec)?.ToUpperInvariant(),
                format.BitrateKbps is int bitrate ? $"{bitrate} kbps" : null };
            Offer(Prepare(playable, FormatPreferences.BestQuality, format).Choice,
                string.Join(" · ", parts.Where(part => part is not null)), exact);
        }

        if (jobs.Count == 0) return BridgeResponse.Fail(requestId, "unavailable", "No compatible formats are available for this media.");
        if (presets.Count == 0)
        {
            if (originalPresets.Count > 0) presets.AddRange(originalPresets);
            else
            {
                presets.Add(exact[0]);
                exact.RemoveAt(0);
            }
        }

        Guid session = Guid.NewGuid();
        lock (_mediaSessionsLock)
        {
            foreach (Guid expired in _mediaSessions.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                _mediaSessions.Remove(expired);
            if (_mediaSessions.Count >= 6) _mediaSessions.Remove(_mediaSessions.MinBy(entry => entry.Value.Expires).Key);
            _mediaSessions[session] = new MediaSession(now.AddMinutes(5), jobs.ToArray());
        }
        return BridgeResponse.Success(requestId, data: new
        {
            mediaSession = session.ToString("N"), item.Title, item.Provider, duration = item.Duration?.TotalSeconds,
            thumbnail = item.Thumbnail is { Scheme: "https" } thumb ? thumb.AbsoluteUri : null,
            choices = presets, originalChoices = originalPresets.SequenceEqual(presets) ? [] : originalPresets, audioM4a, more = exact
        });
    }

    private BridgeResponse SelectBrowserMedia(BridgeRequest request)
    {
        MediaDownloadChoice? job = null;
        lock (_mediaSessionsLock)
        {
            if (request.MediaSession is Guid id && request.FormatChoice is int index
                && _mediaSessions.TryGetValue(id, out var session) && session.Expires > DateTimeOffset.UtcNow
                && index >= 0 && index < session.Choices.Length)
                job = session.Choices[index];
        }
        if (job is null || job.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
            return BridgeResponse.Fail(request.Id, "expired-selection", "These formats have expired. Refresh them to try again.");
        BridgeResult result = QueueMedia(job, request.Mode == "queue", request.Id);
        return result.JobId is not null
            ? BridgeResponse.Success(request.Id, results: [result])
            : BridgeResponse.Fail(request.Id, "rejected", result.Message);
    }

    internal BridgeResult QueueMedia(MediaDownloadChoice job, bool queue, string requestId)
    {
        AddResult added = engine.Add(new AddRequest(job.Url, StartNow: !queue,
            FileName: job.FileName, Referer: job.Referer, UserAgent: job.UserAgent,
            Kind: job.Kind, RequestId: requestId, OriginalPageUrl: job.OriginalPageUrl,
            MediaProvider: job.Provider, MediaId: job.MediaId, MediaQuality: job.Quality,
            MediaThumbnailUrl: job.ThumbnailUrl, MediaExpiresAt: job.ExpiresAt,
            MediaPlanJson: job.MediaPlanJson, MediaFormatId: job.FormatId, Output: job.Output,
            MediaMetadataJson: job.MediaMetadataJson));
        string status = added.Status switch
        {
            AddStatus.Added => queue ? "queued" : "started",
            AddStatus.AlreadySubmitted => "already-submitted",
            _ => "rejected"
        };
        return new BridgeResult(UrlPolicy.Redact(job.Url), status, added.Ok ? added.JobId.ToString("D") : null, added.Message);
    }
}
