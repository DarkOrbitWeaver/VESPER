using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using static VESPER.MediaTransferBridge;

namespace VESPER;

internal sealed partial class BrowserBridgeHandler
{
    private sealed record ChoiceRecipe(FormatPreferences Preferences, MediaRepresentation? Exact, MediaOutputPreference Output);
    private sealed record MediaSession(DateTimeOffset Expires, MediaDownloadChoice[] Choices, MediaItem Item, ChoiceRecipe[] Recipes);
    private sealed record BrowserOption(int Index, string Label, string Detail, string? Container, bool CanClip, bool ClipUsesWholeFile,
        int? Height = null, bool? HasVideo = null, bool? HasAudio = null, long? SizeBytes = null, bool SizeEstimated = false);
    private readonly Dictionary<Guid, MediaSession> _mediaSessions = [];
    private readonly object _mediaSessionsLock = new();

    internal static bool HasRelatedCollection(MediaItem item) => item.Related is
        { Url: { IsAbsoluteUri: true, Scheme: "http" or "https" } };

    internal static string MapResolverError(MediaErrorCode? code) => code switch
    {
        MediaErrorCode.NoDownloadableMedia => "no-media",
        MediaErrorCode.VideoUnavailable => "unavailable",
        MediaErrorCode.DrmProtected => "protected",
        MediaErrorCode.RateLimited => "rate-limited",
        MediaErrorCode.LoginRequired => "sign-in",
        MediaErrorCode.AuthorizationRequired => "site-refused",
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
        Title = request.PageTitle, UserAgent = request.MediaAuth?.UserAgent,
        Authorization = MediaAuthorization(request, page),
        Candidates = (request.Candidates ?? []).Select(h => new BrowserMediaCandidate
        { Url = h.Url, Kind = h.Kind, MimeType = h.MimeType, Source = "network", MutedLoop = h.MutedLoop }).ToList()
    };

    private static BrowserAuthorization? MediaAuthorization(BridgeRequest request, Uri page) =>
        request.MediaAuth is not { } auth ? null : new BrowserAuthorization
        {
            Origin = page.GetLeftPart(UriPartial.Authority), ConsentGranted = true,
            CookieHeader = auth.Cookie, AllowedHosts = [page.Host],
            Headers = auth.Authorization is { } value ? new() { ["Authorization"] = value } : null
        };

    private async Task<BridgeResponse> MediaOptionsAsync(BridgeRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? page))
            return BridgeResponse.Fail(request.Id, "invalid", "This page address is invalid.");

        using var timing = ProcessingTiming.Measure(Guid.TryParse(request.Id, out var id) ? id : Guid.Empty, ProcessingPhase.Discovery);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        discovery.CancelAfter(TimeSpan.FromSeconds(42)); // Leave time for the browser helper's cold start.
        using var resolver = new DefaultMediaResolver(EngineHost.MediaNetwork(options: engine.Options));
        MediaDiscoveryResult resolved = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = page, Browser = MediaHints(request, page),
            Options = new DiscoveryOptions
            {
                CollectionIntent = CollectionIntent.ItemOnly,
                ExactBrowserSource = request.Type == "media-source-options",
                ExactBrowserAssetGroup = request.Type == "media-group-options"
            }
        }, discovery.Token);
        if (resolved.Collection is { } collection)
            return BridgeResponse.Success(request.Id, data: new
            {
                title = collection.Title,
                provider = collection.Provider,
                hasRelatedCollection = true,
                collectionKind = collection.Kind.ToString().ToLowerInvariant(),
                collectionUrl = collection.SourceUrl.AbsoluteUri,
                collectionIsDynamic = collection.IsDynamic,
                collectionIsComplete = collection.IsComplete
            });
        if (resolved.Item is not { } item)
        {
            var error = resolved.Error;
            if (request.Type is "media-source-options" or "media-group-options" && error?.Code == MediaErrorCode.NoDownloadableMedia)
                return BridgeResponse.Fail(request.Id, "source-unavailable", "VESPER couldn't verify this source. Go back to try another source.");
            string code = MapResolverError(error?.Code);
            var failure = BridgeResponse.Fail(request.Id, code, error?.UserMessage ?? "No downloadable media was found.");
            return error?.RetryAt is { } retryAt ? failure with { Data = new { retryAt } } : failure;
        }

        return CreateBrowserOptions(request.Id, item, page.AbsoluteUri, discovery.Token);
    }

    private BridgeResponse CreateBrowserOptions(string requestId, MediaItem item, string pageUrl, CancellationToken ct)
    {
        var playable = PlayableOnly(item);
        var jobs = new List<MediaDownloadChoice>();
        var recipes = new List<ChoiceRecipe>();
        ChoiceRecipe? recipe = null;
        var problems = new List<string>();
        (MediaDownloadChoice? Choice, string? Error) Plan(FormatPreferences preferences, MediaRepresentation? exact = null,
            MediaOutputPreference output = MediaOutputPreference.Original)
        {
            recipe = new ChoiceRecipe(preferences, exact, output);
            var planned = Prepare(playable, preferences, exact, output);
            if (planned.Error is { } error && !problems.Contains(error)) problems.Add(error);
            return planned;
        }
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
                recipes.Add(recipe ?? new ChoiceRecipe(FormatPreferences.BestQuality, null, MediaOutputPreference.Original));
            }
            var clipMode = ClipSelection.ModeFor(job);
            // Report the chosen recipe's facts, not the tallest unrelated representation.
            var tracks = playable.Representations.Where(rep => job.FormatId?.Split('+').Contains(rep.Id, StringComparer.Ordinal) == true).ToArray();
            var video = tracks.FirstOrDefault(rep => rep.HasVideo);
            bool? hasVideo = tracks.Length == 0 ? null : tracks.Any(rep => rep.HasVideo);
            bool? hasAudio = tracks.Any(rep => rep.HasAudio && !rep.AudioUnverified) ? true
                : tracks.Length == 0 || tracks.Any(rep => rep.AudioUnverified) ? null : false;
            if (job.Output is MediaOutput.M4a or MediaOutput.Mp3 or MediaOutput.Opus or MediaOutput.Wav)
            { hasVideo = false; video = null; }
            long? size = null;
            bool estimate = false;
            if (job.Output == MediaOutput.Original && tracks.Length > 0)
            {
                if (tracks.All(rep => rep.FileSize is > 0))
                {
                    double sum = tracks.Sum(rep => (double)rep.FileSize!.Value);
                    if (sum < long.MaxValue) size = (long)sum;
                    estimate = job.Kind != JobKind.Http || tracks.Any(rep => rep.FileSizeIsEstimate);
                }
                else if (item.Duration is { TotalSeconds: > 0 } duration && tracks.All(rep => rep.BitrateKbps is > 0))
                {
                    double bytes = tracks.Sum(rep => (double)rep.BitrateKbps!.Value) * 125 * duration.TotalSeconds;
                    if (bytes is > 0 and < long.MaxValue) size = (long)bytes;
                    estimate = true;
                }
            }
            string container = job.Kind == JobKind.Hls && job.Output == MediaOutput.Original
                ? (tracks.FirstOrDefault()?.Container ?? "Video").ToUpperInvariant() : job.DisplayContainer;
            return new BrowserOption(index, label, job.DisplayDetail, container,
                clipMode != ClipMode.None, clipMode == ClipMode.FullFile,
                video?.Height, hasVideo, hasAudio, size, estimate);
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
                Offer(Plan(preference, output: MediaOutputPreference.CompatibleVideo).Choice, label, presets);
                Offer(Plan(preference).Choice, label, originalPresets);
            }
        }
        MediaDownloadChoice? sourceAudio = Plan(FormatPreferences.AudioOnly).Choice;
        Offer(sourceAudio, "Audio only", presets);
        Offer(sourceAudio, "Audio only", originalPresets);
        MediaDownloadChoice? alternate = Plan(FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice;
        BrowserOption? audioM4a = alternate != sourceAudio && presets.Any(c => c.Label == "Audio only")
            ? OptionFor(alternate, "Audio only") : null;

        var audioOutputs = new List<object>();
        void AudioOutput(MediaOutputPreference output, string label)
        {
            var planned = Plan(FormatPreferences.AudioOnly, output: output).Choice;
            if (planned is null || output == MediaOutputPreference.M4a && planned.DisplayContainer != "M4A") return;
            if (OptionFor(planned, "Audio only") is { } option) audioOutputs.Add(new { label, choice = option });
        }
        AudioOutput(MediaOutputPreference.M4a, "M4A (compatible)");
        AudioOutput(MediaOutputPreference.Original, "Original audio");
        AudioOutput(MediaOutputPreference.Opus, "Opus — copied, no re-encoding");
        AudioOutput(MediaOutputPreference.Wav, "WAV — decoded PCM");
        AudioOutput(MediaOutputPreference.Mp3, "MP3 — re-encode when needed");

        foreach (var format in playable.Representations.OrderByDescending(r => r.Height ?? 0)
                     .ThenByDescending(r => r.FrameRate ?? 0).ThenByDescending(r => r.BitrateKbps ?? 0).Take(56))
        {
            ct.ThrowIfCancellationRequested();
            var parts = new[] { format.Content == StreamContent.AudioOnly ? "Audio" : format.Height is int height ? $"{height}p" : "Video",
                format.FrameRate is > 30.5 ? $"{Math.Round(format.FrameRate.Value)} fps" : null,
                (format.VideoCodec ?? format.AudioCodec)?.ToUpperInvariant(),
                format.BitrateKbps is int bitrate ? $"{bitrate} kbps" : null };
            Offer(Plan(FormatPreferences.BestQuality, format).Choice,
                string.Join(" · ", parts.Where(part => part is not null)), exact);
        }

        if (jobs.Count == 0)
        {
            if (HasRelatedCollection(item))
                return BridgeResponse.Success(requestId, data: new
                {
                    item.Title,
                    item.Provider,
                    hasRelatedCollection = true,
                    collectionKind = item.Related!.Kind.ToString().ToLowerInvariant(),
                    collectionUrl = item.Related!.Url.AbsoluteUri,
                    collectionIsDynamic = item.Related!.IsDynamic
                });
            return BridgeResponse.Fail(requestId, "unavailable", playable.Representations.Count == 0
                ? "The page supplied metadata but no supported clear media address. Play the video and refresh formats; sign-in, DRM or a changed provider response may prevent access."
                : problems.FirstOrDefault() ?? "All source addresses have expired. Refresh the page and try again.");
        }
        if (presets.Count == 0)
        {
            if (originalPresets.Count > 0) presets.AddRange(originalPresets);
            else
            {
                presets.Add(exact[0]);
                exact.RemoveAt(0);
            }
        }

        if (SiteRule.ForPage(preferences().SiteRules, pageUrl)?.AlwaysAudio == true)
        {
            int audioChoice = presets.FindIndex(option => option.Label == "Audio only");
            if (audioChoice > 0)
            {
                BrowserOption audio = presets[audioChoice];
                presets.RemoveAt(audioChoice);
                presets.Insert(0, audio);
            }
        }

        Guid session = Guid.NewGuid();
        lock (_mediaSessionsLock)
        {
            foreach (Guid expired in _mediaSessions.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                _mediaSessions.Remove(expired);
            if (_mediaSessions.Count >= 12) _mediaSessions.Remove(_mediaSessions.MinBy(entry => entry.Value.Expires).Key);
            _mediaSessions[session] = new MediaSession(now.AddMinutes(5), jobs.ToArray(), playable, recipes.ToArray());
        }
        // Compare actual executable transfer recipes, not quality labels or
        // duration. Different URLs, track plans or outputs are not equivalent.
        // Only an opaque digest leaves the native resolver.
        var deliveryRecipes = jobs.Select(job => System.Text.Json.JsonSerializer.Serialize(new
        {
            job.Url, job.Kind, job.MediaPlanJson, job.Output, job.FormatId,
            job.Quality, job.Referer, job.UserAgent, job.MediaMetadataJson,
            Subtitles = job.SubtitleTracks?.Select(track => new { track.Url, track.Language, track.Format })
        })).Distinct(StringComparer.Ordinal).OrderBy(recipe => recipe, StringComparer.Ordinal).ToArray();
        string deliveryFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(deliveryRecipes)));
        var deliveryRecipeFingerprints = deliveryRecipes.Select(recipe =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(recipe)))).ToArray();
        string deliveryTrackFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { item.AudioTracks, item.Subtitles, item.Duration, item.IsLive, item.Related })));
        return BridgeResponse.Success(requestId, data: new
        {
            mediaSession = session.ToString("N"), deliveryFingerprint, deliveryRecipeFingerprints, deliveryTrackFingerprint, item.Title, item.Provider, duration = item.Duration?.TotalSeconds,
            isLive = item.IsLive,
            thumbnail = item.Thumbnail is { Scheme: "https" } thumb ? thumb.AbsoluteUri : null,
            choices = presets, originalChoices = originalPresets.SequenceEqual(presets) ? [] : originalPresets, audioM4a, audioOutputs, more = exact,
            hasRelatedCollection = HasRelatedCollection(item),
            collectionKind = item.Related?.Kind.ToString().ToLowerInvariant(),
            collectionUrl = HasRelatedCollection(item) ? item.Related!.Url.AbsoluteUri : null,
            audioTracks = item.AudioTracks.Select((track, index) => new { track.Id,
                label = (track.Name ?? track.Language ?? "Audio") + (track.IsDefault ? " · original" : "") + " · " + (index + 1) }),
            subtitleOptions = SelectableSubtitleTracks(item.Subtitles)
                .Select(track => new
                {
                    Language = Vesper.MediaResolver.Utilities.TextUtil.NormalizeLang(track.Language),
                    label = string.IsNullOrWhiteSpace(track.Name) ? track.Language : track.Name,
                    auto = track.IsAutoGenerated,
                    format = track.Format.ToLowerInvariant(),
                    embedSupported = IsEmbeddableSubtitle(track)
                })
        });
    }

    private BridgeResponse SelectBrowserMedia(BridgeRequest request)
    {
        MediaDownloadChoice? job = null;
        MediaItem? item = null;
        ChoiceRecipe? how = null;
        lock (_mediaSessionsLock)
        {
            if (request.MediaSession is Guid id && request.FormatChoice is int index
                && _mediaSessions.TryGetValue(id, out var session) && session.Expires > DateTimeOffset.UtcNow
                && index >= 0 && index < session.Choices.Length)
            {
                job = session.Choices[index];
                item = session.Item;
                how = index < session.Recipes.Length ? session.Recipes[index] : null;
            }
        }
        if (job is null || job.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            // The worker may retry the original request after its format session
            // expired. A durable acceptance is not authority to create another job.
            var accepted = new List<BridgeResult>();
            if (engine.FindAcceptedSubmission(request.Id + ":0") is Guid first)
            {
                accepted.Add(new BridgeResult("", "already-submitted", first.ToString("D"), "Previously accepted."));
                for (int i = 1; i < 64; i++)
                    if (engine.FindAcceptedSubmission(request.Id + ":" + i) is Guid extra)
                        accepted.Add(new BridgeResult("", "already-submitted", extra.ToString("D"), "Previously accepted."));
                return BridgeResponse.Success(request.Id, message: "The original download was accepted. Check any requested subtitle jobs before retrying.", results: accepted.ToArray());
            }
            return BridgeResponse.Fail(request.Id, "expired-selection", "These formats have expired. Refresh them to try again.");
        }
        if (request.Clip is not null || request.AudioTrackId is not null || request.SubtitleLanguages is not null
            || request.EmbedSubtitles)
        {
            if (item is null || how is null || request.Clip is not null && !SupportsTimeRange(job))
                return BridgeResponse.Fail(request.Id, "invalid", "This format can't use the requested selection.");
            if (request.EmbedSubtitles && request.SubtitleLanguages is not { Count: > 0 })
                return BridgeResponse.Fail(request.Id, "invalid", "Choose at least one subtitle language before embedding.");
            if (request.AudioTrackId is { } track && !item.AudioTracks.Any(t => t.Id == track)
                && !item.Representations.Any(r => r.AudioTrackId == track))
                return BridgeResponse.Fail(request.Id, "invalid", "The selected audio track is no longer available.");
            if (request.SubtitleLanguages?.Any(language => !item.Subtitles.Any(s =>
                string.Equals(Vesper.MediaResolver.Utilities.TextUtil.NormalizeLang(s.Language), language,
                    StringComparison.OrdinalIgnoreCase))) == true)
                return BridgeResponse.Fail(request.Id, "invalid", "A selected subtitle language is no longer available.");
            var preferences = how.Preferences with { AudioTrackId = request.AudioTrackId,
                SubtitleLanguages = request.SubtitleLanguages ?? [], IncludeAutoSubtitles = request.IncludeAutoSubtitles,
                EmbedSubtitles = request.EmbedSubtitles };
            var planned = Prepare(item, preferences, how.Exact, how.Output, request.Clip?.StartSeconds, request.Clip?.EndSeconds);
            if (planned.Choice is null) return BridgeResponse.Fail(request.Id, "invalid", planned.Error ?? "This selection cannot be downloaded.");
            job = planned.Choice;
        }
        var jobs = ExpandSubtitleChoices([job]);
        var results = jobs.Select((choice, index) => QueueMedia(choice, request.Mode == "queue", $"{request.Id}:{index}")).ToArray();
        return results.Any(result => result.JobId is not null)
            ? BridgeResponse.Success(request.Id, message: job.SubtitleNotice ?? "", results: results)
            : BridgeResponse.Fail(request.Id, "rejected", results[0].Message);
    }

    internal BridgeResult QueueMedia(MediaDownloadChoice job, bool queue, string requestId)
    {
        string? destination = SiteRule.ForPage(preferences().SiteRules, job.OriginalPageUrl)?.Folder;
        AddResult added = engine.Add(new AddRequest(job.Url, DestinationFolder: destination, StartNow: !queue,
            FileName: job.FileName, Referer: job.Referer, UserAgent: job.UserAgent,
            Kind: job.Kind, RequestId: requestId, OriginalPageUrl: job.OriginalPageUrl,
            MediaProvider: job.Provider, MediaId: job.MediaId, MediaQuality: job.Quality,
            MediaThumbnailUrl: job.ThumbnailUrl, MediaExpiresAt: job.ExpiresAt,
            MediaPlanJson: job.MediaPlanJson, MediaFormatId: job.FormatId, Output: job.Output,
            MediaMetadataJson: job.MediaMetadataJson, Secrets: job.Secrets));
        string status = added.Status switch
        {
            AddStatus.Added => queue ? "queued" : "started",
            AddStatus.AlreadySubmitted => "already-submitted",
            _ => "rejected"
        };
        return new BridgeResult(UrlPolicy.Redact(job.Url), status, added.Ok ? added.JobId.ToString("D") : null, added.Message);
    }
}
