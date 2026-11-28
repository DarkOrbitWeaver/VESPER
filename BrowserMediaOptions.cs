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
        MediaErrorCode.SourceExpired => "source-expired",
        MediaErrorCode.SourceChanged => "source-changed",
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
        {
            Url = h.Url, Kind = h.Kind, MimeType = h.MimeType, Source = "network", MutedLoop = h.MutedLoop,
            ObservedLength = h.ObservedLength, ObservedETag = h.ObservedETag, ObservedMinimumLength = h.ObservedMinimumLength
        }).ToList()
    };

    private static BrowserAuthorization? MediaAuthorization(BridgeRequest request, Uri page)
    {
        if (request.MediaAuth is null && request.HostCredentials is not { Count: > 0 }) return null;
        Dictionary<string, string>? hostCookies = null;
        var allowedHosts = new List<string> { page.Host };
        if (request.HostCredentials is { Count: > 0 } creds)
        {
            hostCookies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (BridgeHostCredential hc in creds)
            {
                hostCookies.Add(hc.Url, hc.Cookie);
            }
        }
        return new BrowserAuthorization
        {
            Origin = page.GetLeftPart(UriPartial.Authority),
            ConsentGranted = true,
            CookieHeader = request.MediaAuth?.Cookie,
            HostCookies = hostCookies,
            AllowedHosts = allowedHosts,
            Headers = request.MediaAuth?.Authorization is { } value ? new() { ["Authorization"] = value } : null
        };
    }

    private async Task<BridgeResponse> MediaOptionsAsync(BridgeRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? page))
            return BridgeResponse.Fail(request.Id, "invalid", "This page address is invalid.");

        using var timing = ProcessingTiming.Measure(Guid.TryParse(request.Id, out var id) ? id : Guid.Empty, ProcessingPhase.Discovery);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        discovery.CancelAfter(TimeSpan.FromSeconds(42)); // Leave time for the browser helper's cold start.
        var resolver = SharedResolver;
        MediaDiscoveryResult resolved = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = page, Browser = MediaHints(request, page),
            Options = new DiscoveryOptions
            {
                CollectionIntent = CollectionIntent.ItemOnly,
                ExactBrowserSource = request.Type is "media-source-options" or "media-range-options",
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
            object data = new { retryAt = error?.RetryAt, status = error?.HttpStatus, resolver = error?.Code.ToString() };
            if (request.Type is "media-source-options" or "media-range-options" or "media-group-options" && error?.Code == MediaErrorCode.NoDownloadableMedia)
                return BridgeResponse.Fail(request.Id, "source-unavailable", "VESPER couldn't verify this source. Go back to try another source.") with { Data = data };
            string code = MapResolverError(error?.Code);
            var failure = BridgeResponse.Fail(request.Id, code, error?.UserMessage ?? "No downloadable media was found.");
            return failure with { Data = data };
        }

        return CreateBrowserOptions(request.Id, item, page.AbsoluteUri, discovery.Token, request.PlaybackFingerprints);
    }

    private BridgeResponse CreateBrowserOptions(string requestId, MediaItem item, string pageUrl, CancellationToken ct, IReadOnlyList<string>? playbackFingerprints = null)
    {
        var playable = PlayableOnly(item);
        JobKind[] deliveries = playable.Representations.Where(r => r.Content == StreamContent.VideoAndAudio)
            .Select(r => r.Protocol switch
            {
                StreamProtocol.Http => JobKind.Http,
                StreamProtocol.Hls => JobKind.Hls,
                StreamProtocol.Dash => JobKind.Dash,
                _ => JobKind.Merge
            }).Distinct().ToArray();
        var knownSizes = playable.Representations.Where(r => r.Content == StreamContent.VideoAndAudio
            && r.Protocol is StreamProtocol.Http or StreamProtocol.Hls or StreamProtocol.Dash).ToArray();
        // Only a comparable known size can qualify a speed-based preference.
        long? expectedBytes = knownSizes.Length > 0 && knownSizes.All(r => r.FileSize is > 0 && !r.FileSizeIsEstimate)
            && knownSizes.Max(r => r.FileSize!.Value) / 2 <= knownSizes.Min(r => r.FileSize!.Value)
            ? knownSizes.Max(r => r.FileSize!.Value) : null;
        StreamProtocol? preferredDelivery = (deliveries.Length < 2 ? null : engine.PreferredMediaDelivery(item.Provider, deliveries, expectedBytes, pageUrl)) switch
        {
            JobKind.Http => StreamProtocol.Http,
            JobKind.Hls => StreamProtocol.Hls,
            JobKind.Dash => StreamProtocol.Dash,
            _ => null
        };
        var jobs = new List<MediaDownloadChoice>();
        var recipes = new List<ChoiceRecipe>();
        ChoiceRecipe? recipe = null;
        var problems = new List<string>();
        (MediaDownloadChoice? Choice, string? Error) Plan(FormatPreferences preferences, MediaRepresentation? exact = null,
            MediaOutputPreference output = MediaOutputPreference.Original)
        {
            if (exact is null) preferences = preferences with { PreferredDelivery = preferredDelivery };
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
        static bool SameRecipe(MediaDownloadChoice a, MediaDownloadChoice b) =>
            a.Url == b.Url && a.Kind == b.Kind && a.Output == b.Output
            && a.FormatId == b.FormatId && a.MediaPlanJson == b.MediaPlanJson
            && a.ConvertedVideoQuality == b.ConvertedVideoQuality && a.ConvertedAudioCodec == b.ConvertedAudioCodec
            && a.Referer == b.Referer && a.UserAgent == b.UserAgent;

        static string PresetDetail(MediaDownloadChoice job, MediaRepresentation[] tracks, MediaRepresentation? video, bool? hasVideo, bool? hasAudio)
        {
            if (job.Output is MediaOutput.M4a or MediaOutput.Mp3 or MediaOutput.Opus or MediaOutput.Wav || hasVideo == false)
            {
                var audio = tracks.FirstOrDefault(rep => rep.HasAudio) ?? tracks.FirstOrDefault();
                return audio?.BitrateKbps is int kbps ? $"{kbps} kbps" : job.DisplayContainer is { Length: > 0 } c ? c : "Audio";
            }
            if (hasVideo == true)
            {
                var parts = new List<string>(3) { video?.Height is int h ? $"{h}p" : "Video" };
                if (video?.FrameRate is > 30.5) parts.Add($"{Math.Round(video.FrameRate.Value)} fps");
                if (hasAudio == false) parts.Add("no audio");
                return string.Join(" · ", parts);
            }
            return job.DisplayDetail;
        }

        BrowserOption? OptionFor(MediaDownloadChoice? job, string label, bool preset = false)
        {
            if (job is null || job.ExpiresAt is { } expires && expires <= now) return null;
            int index = jobs.FindIndex(existing => SameRecipe(existing, job));
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
            string detail = preset ? PresetDetail(job, tracks, video, hasVideo, hasAudio) : job.DisplayDetail;
            return new BrowserOption(index, label, detail, container,
                clipMode != ClipMode.None, clipMode is ClipMode.FullFile or ClipMode.Keyframes,
                video?.Height, hasVideo, hasAudio, size, estimate);
        }
        void Offer(MediaDownloadChoice? job, string label, List<BrowserOption> options, bool preset = false)
        {
            if (OptionFor(job, label, preset) is { } option && !options.Any(previous => previous.Index == option.Index))
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
                Offer(Plan(preference, output: MediaOutputPreference.CompatibleVideo).Choice, label, presets, preset: true);
                Offer(Plan(preference).Choice, label, originalPresets, preset: true);
            }
        }
        MediaDownloadChoice? sourceAudio = Plan(FormatPreferences.AudioOnly).Choice;
        Offer(sourceAudio, "Audio only", presets, preset: true);
        Offer(sourceAudio, "Audio only", originalPresets, preset: true);
        MediaDownloadChoice? alternate = Plan(FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice;
        BrowserOption? audioM4a = alternate != sourceAudio && presets.Any(c => c.Label == "Audio only")
            ? OptionFor(alternate, "Audio only", preset: true) : null;

        var audioOutputs = new List<object>();
        void AudioOutput(MediaOutputPreference output, string label)
        {
            var planned = Plan(FormatPreferences.AudioOnly, output: output).Choice;
            if (planned is null || output == MediaOutputPreference.M4a && planned.DisplayContainer != "M4A") return;
            if (OptionFor(planned, "Audio only") is { } option) audioOutputs.Add(new { label, choice = option });
        }
        AudioOutput(MediaOutputPreference.M4a, "M4A (compatible)");
        AudioOutput(MediaOutputPreference.Original, "Original audio");
        AudioOutput(MediaOutputPreference.Opus, "Opus (original)");
        AudioOutput(MediaOutputPreference.Wav, "WAV (uncompressed)");
        AudioOutput(MediaOutputPreference.Mp3, "MP3 (universal)");

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
        DateTimeOffset sessionExpires = now.AddMinutes(5);
        DateTimeOffset formatsExpire = jobs.Where(job => job.ExpiresAt.HasValue)
            .Select(job => job.ExpiresAt!.Value).DefaultIfEmpty(sessionExpires).Min();
        if (formatsExpire > sessionExpires) formatsExpire = sessionExpires;
        lock (_mediaSessionsLock)
        {
            foreach (Guid expired in _mediaSessions.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                _mediaSessions.Remove(expired);
            if (_mediaSessions.Count >= 12) _mediaSessions.Remove(_mediaSessions.MinBy(entry => entry.Value.Expires).Key);
            _mediaSessions[session] = new MediaSession(sessionExpires, jobs.ToArray(), playable, recipes.ToArray());
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
        int playbackMatches = ManifestPlaybackEvidence.CountMatches(playable.Representations, playbackFingerprints, ct);
        return BridgeResponse.Success(requestId, data: new
        {
            playbackMatches,
            mediaSession = session.ToString("N"), formatsExpireAt = formatsExpire, deliveryFingerprint, deliveryRecipeFingerprints, deliveryTrackFingerprint, item.Title, item.Provider, item.MediaId, item.VerifiedPostId, duration = item.Duration?.TotalSeconds,
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

    // A receipt query is read-only, even if the original session is still live.
    private BridgeResponse AcceptedMediaSelection(string requestId)
    {
        if (engine.FindAcceptedSubmission(requestId + ":0") is not Guid first)
            return BridgeResponse.Fail(requestId, "expired-selection", "No accepted selection was found.");
        var accepted = new List<BridgeResult> { new("", "already-submitted", first.ToString("D"), "Previously accepted.") };
        for (int i = 1; i < 64; i++)
            if (engine.FindAcceptedSubmission(requestId + ":" + i) is Guid extra)
                accepted.Add(new BridgeResult("", "already-submitted", extra.ToString("D"), "Previously accepted.", Subtitle: true));
        return BridgeResponse.Success(requestId, message: "The original download was accepted. Check any requested subtitle jobs before retrying.", results: accepted.ToArray());
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
            return AcceptedMediaSelection(request.Id);
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
        bool queue = request.Mode == "queue";
        var jobs = ExpandSubtitleChoices([job]);
        var siteRules = SiteRule.Normalize(preferences().SiteRules);
        if (jobs.Any(choice => MediaSelectionBlocked(choice, siteRules)))
            return AcceptedMediaSelection(request.Id) is { Ok: true } receipt ? receipt
                : BridgeResponse.Fail(request.Id, "site-capture-disabled", "This site is set to keep downloads in Chrome.");
        BridgeResult primary = QueueMedia(jobs[0], queue, request.Id + ":0");
        if (primary.JobId is null)
            return BridgeResponse.Fail(request.Id, "rejected", primary.Message) with { Results = [primary] };

        var results = new List<BridgeResult> { primary };
        for (int index = 1; index < jobs.Count; index++)
            results.Add(QueueMedia(jobs[index], queue, $"{request.Id}:{index}"));
        return BridgeResponse.Success(request.Id, message: job.SubtitleNotice ?? "", results: results);
    }

    internal static bool MediaSelectionBlocked(MediaDownloadChoice job, IReadOnlyList<SiteRule> rules)
    {
        if (!rules.Any(rule => rule.NeverCapture)) return false;
        bool Blocked(string? address) => SiteRule.ForPage(rules, address)?.NeverCapture == true;
        if (Blocked(job.Url) || Blocked(job.Referer) || Blocked(job.OriginalPageUrl)
            || job.SubtitleTracks?.Any(track => Blocked(track.Url.AbsoluteUri)) == true) return true;
        if (job.MediaPlanJson is not { } json || job.Kind is not (JobKind.Merge or JobKind.Dash)) return false;
        // Inspect the concrete planned tracks and fragments before any job is queued.
        bool DashBlocked(DashDownloadPlan plan) => Blocked(plan.Initialization.Url) || plan.Fragments.Any(part => Blocked(part.Url));
        try
        {
            if (job.Kind == JobKind.Dash)
                return System.Text.Json.JsonSerializer.Deserialize<DashDownloadPlan>(json) is not { } dash || DashBlocked(dash);
            var merge = System.Text.Json.JsonSerializer.Deserialize<MediaMergePlan>(json);
            return merge is null || merge.Tracks.Any(track => Blocked(track.Url) || Blocked(track.Source?.PlaylistUrl)
                || track.Source?.Dash is { } nested && DashBlocked(nested)) || merge.Subtitles.Any(track => Blocked(track.Url));
        }
        catch (System.Text.Json.JsonException) { return true; }
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
            MediaMetadataJson: job.MediaMetadataJson, Secrets: job.Secrets,
            HostCookies: job.HostCookies, OriginHeader: job.OriginHeader));
        string status = added.Status switch
        {
            AddStatus.Added => queue || added.StartDeferred ? "queued" : "started",
            AddStatus.AlreadySubmitted => "already-submitted",
            _ => "rejected"
        };
        return new BridgeResult(UrlPolicy.Redact(job.Url), status, added.Ok ? added.JobId.ToString("D") : null,
            added.Message, Subtitle: job.IsSubtitle ? true : null);
    }
}
