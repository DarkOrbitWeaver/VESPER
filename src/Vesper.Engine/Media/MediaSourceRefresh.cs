using System.Text.Json.Serialization;

namespace Vesper.Engine;

/// <summary>A newly resolved plain-file URL, not permission to append a different representation.</summary>
public sealed record ResolvedMediaSource(string Provider, string MediaId, string FormatId, string Url,
    DateTimeOffset? ExpiresAt = null,
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? Secrets = null);

public sealed partial class DownloadEngine
{
    /// <summary>
    /// Explicit source re-resolution for HTTP media and HLS recordings. Requires exact provider/media/format
    /// identities; direct files delegate byte identity to Rescue, while HLS checks the saved prefix or live sequence. Optional
    /// request credentials are memory-only and scoped to the replacement origin.
    /// </summary>
    public async Task<RescueResult> RefreshMediaSourceAsync(Guid id, ResolvedMediaSource replacement, CancellationToken ct = default,
        string? expectedExpiredSource = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        string? liveState = null;
        bool vodHls = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return Rejected("That entry no longer exists.");
            if (_cancelling.Contains(id)) return Rejected("The download is already being cancelled.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource))
                return Rejected("The download changed since automatic refresh was scheduled. Saved data was kept.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its source can be refreshed.");
            if (job.Kind == JobKind.Hls && HlsTransfer.IsLiveState(job.MediaState))
            {
                if (_sessions.ContainsKey(id)) return Rejected("Pause the live recording before refreshing its source.");
                if (string.IsNullOrEmpty(job.MediaProvider) || string.IsNullOrEmpty(job.MediaId) || string.IsNullOrEmpty(job.MediaFormatId))
                    return Rejected("This live recording has no stable provider, media and format identity. Saved data was kept.");
                if (job.MediaProvider != replacement.Provider || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                    return Rejected("The newly resolved provider, media identity or selected format differs. Saved segments were kept.");
                if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
                if (job.State is JobState.Completed or JobState.Finalizing or JobState.Verifying or JobState.Converting or JobState.Suspect)
                    return Rejected("This live recording can no longer be resumed.");
                liveState = job.MediaState;
            }
            else if (job.Kind is not (JobKind.Http or JobKind.Hls))
                return Rejected("Streaming sources require a compatible fragment plan. Saved data was kept; resolve a separate copy.");
            else if (string.IsNullOrEmpty(job.MediaProvider) || string.IsNullOrEmpty(job.MediaId) || string.IsNullOrEmpty(job.MediaFormatId))
                return Rejected("This older job does not have enough source identity for automatic re-resolution. Use ordinary Rescue with a trusted identity.");
            else if (job.MediaProvider != replacement.Provider || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                return Rejected("The newly resolved provider, media identity or selected format differs. Restart as a separate copy; no bytes were changed.");
            else if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            else vodHls = job.Kind == JobKind.Hls;
        }
        ct.ThrowIfCancellationRequested();
        if (!TryCreateReplacementCredentials(replacement, out RequestCredentials? credentials, out string credentialError))
            return Rejected(credentialError);
        if (liveState is not null)
            return await RefreshLiveHlsSourceAsync(id, replacement, liveState, credentials, ct, expectedExpiredSource).ConfigureAwait(false);
        if (vodHls)
            return await RefreshVodHlsSourceAsync(id, replacement, credentials, ct, expectedExpiredSource).ConfigureAwait(false);
        return await ReplaceLinkCoreAsync(id, replacement.Url, null, ct, credentials, replacement.ExpiresAt, expectedExpiredSource)
            .ConfigureAwait(false);
    }

    private static bool TryCreateReplacementCredentials(ResolvedMediaSource replacement,
        out RequestCredentials? credentials, out string error)
    {
        credentials = null;
        error = "";
        if (replacement.Secrets is not { Count: > 0 } supplied) return true;
        if (!UrlPolicy.TryParse(replacement.Url, out Uri uri, out _))
        { error = "Credentials were supplied for an invalid refreshed link."; return false; }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in supplied)
        {
            string canonical = name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "Cookie"
                : name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Authorization" : "";
            if (canonical.Length == 0 || headers.ContainsKey(canonical) || string.IsNullOrEmpty(value) || value.Length > 8192
                || value.Any(char.IsControl))
            { error = "The refreshed link contains credentials VESPER cannot safely use."; return false; }
            headers[canonical] = value;
        }

        var origin = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "", UserName = "", Password = "" }.Uri;
        credentials = new RequestCredentials(origin, headers);
        return true;
    }

    private static bool TryCreateMergeCredentials(MediaMergePlan plan, Uri requestUrl,
        IReadOnlyDictionary<string, string>? supplied, out RequestCredentials? credentials, out string error,
        bool allowSegmented = false)
    {
        credentials = null;
        error = "";
        if (supplied is not { Count: > 0 }) return true;
        if ((!allowSegmented && plan.IsSegmented) || plan.Tracks.Count == 0
            || !UrlPolicy.TryParse(plan.Tracks[0].Url, out Uri first, out _)
            || !UrlPolicy.IsSameOrigin(requestUrl, first))
        {
            error = "The merge credentials require compatible tracks on one source origin.";
            return false;
        }
        foreach (MergeTrack track in plan.Tracks)
        {
            if ((!allowSegmented && track.Source is not null) || !UrlPolicy.TryParse(track.Url, out Uri target, out _)
                || !UrlPolicy.IsSameOrigin(first, target))
            {
                error = "The requested credential scope is not supported for these merge tracks.";
                return false;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in supplied)
        {
            string canonical = name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "Cookie"
                : name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Authorization" : "";
            if (canonical.Length == 0 || headers.ContainsKey(canonical) || string.IsNullOrEmpty(value)
                || value.Length > 8192 || value.Any(char.IsControl))
            {
                error = "The merge refresh contains credentials VESPER cannot safely use.";
                return false;
            }
            headers.Add(canonical, value);
        }

        var origin = new UriBuilder(first) { Path = "/", Query = "", Fragment = "", UserName = "", Password = "" }.Uri;
        credentials = new RequestCredentials(origin, headers);
        return true;
    }

    /// <summary>
    /// Credentialed merge refresh verifies direct same-origin tracks by range probe or segmented tracks by their
    /// saved prefix before the engine replaces either its URLs or its in-memory credentials.
    /// </summary>
    public async Task<RescueResult> RefreshMergePlanWithCredentialsAsync(Guid id, string replacementPlanJson,
        DateTimeOffset? expiresAt, IReadOnlyDictionary<string, string> secrets, CancellationToken ct = default,
        string? expectedExpiredSource = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out var parsed, out _)
            && parsed.IsSegmented)
            return await RefreshSegmentedMergePlanAsync(id, replacementPlanJson, expiresAt, ct,
                expectedExpiredSource, secrets).ConfigureAwait(false);
        DownloadJob original;
        DownloadJob snapshot;
        ExtentSet savedExtents;
        MediaMergePlan current;
        MediaMergePlan next;
        RequestCredentials? credentials;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource)) return Rejected(AutomaticRefreshChanged);
            if (_cancelling.Contains(id)) return Rejected("The download is already being cancelled.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its source can be refreshed.");
            if (_sessions.ContainsKey(id)) return Rejected("Pause the download before refreshing its source.");
            if (job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("This merge job is not in a resumable state.");
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out current, out _))
                return Rejected("The saved plan is unreadable. Saved data was kept; restart as a separate copy.");
            if (!MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out next, out string parseError))
                return Rejected(parseError);
            next = next with { ClipRange = current.ClipRange, OutputRange = current.OutputRange };
            if (!current.CanContinueWith(next, out string why)) return Rejected(why);
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            if (next.Tracks.Count == 0 || !UrlPolicy.TryParse(next.Tracks[0].Url, out Uri firstTrack, out _))
                return Rejected("The refreshed merge plan has no valid track address.");
            if (!TryCreateMergeCredentials(next, firstTrack, secrets, out credentials, out string credentialError)
                || credentials is null)
                return Rejected(credentialError.Length > 0 ? credentialError : "The refreshed merge credentials are unavailable.");
            original = job;
            savedExtents = job.Extents;
            snapshot = job.CopyForUpdate();
        }

        string? probeError = await ProbeCredentialedMergeTracksAsync(next, snapshot.Referer, snapshot.UserAgent, credentials!, ct)
            .ConfigureAwait(false);
        if (probeError is not null) return Rejected(probeError);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, original)
                || _cancelling.Contains(id)
                || IsCleanupPending(job) || job.PendingFinalPath is not null
                || !MatchesAutomaticRefreshLocked(job, expectedExpiredSource)
                || _sessions.ContainsKey(id) || job.MediaPlanJson != snapshot.MediaPlanJson
                || job.MediaState != snapshot.MediaState || !ReferenceEquals(job.Extents, savedExtents)
                || job.State != snapshot.State || job.SourceUrl != snapshot.SourceUrl
                || job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                    or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("The merge job changed while its refreshed tracks were being checked. Saved data was kept.");
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The refreshed source expired before it could be installed.");

            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.MediaPlanJson = next.ToJson();
            updated.SourceUrl = next.Tracks[0].Url;
            updated.MediaExpiresAt = expiresAt;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
                (updated.State, updated.Reason, updated.Message, updated.RetryCount) = (JobState.Queued, "", "Source refreshed; continuing saved data.", 0);
            ct.ThrowIfCancellationRequested();
            SaveRefreshedJobLocked(job, updated);
            _credentials[id] = credentials!;
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0,
            "The refreshed tracks match the saved identities and passed same-origin range probes. Download continues.", 0);
    }

    private async Task<string?> ProbeCredentialedMergeTracksAsync(MediaMergePlan plan, string? referer, string? userAgent,
        RequestCredentials credentials, CancellationToken ct)
    {
        foreach (MergeTrack track in plan.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!UrlPolicy.TryParse(track.Url, out Uri uri, out _))
                return "A refreshed merge track URL is invalid. Saved data and credentials were kept.";
            long end = Math.Min(511, track.Size - 1);
            try
            {
                using HttpResponseMessage response = await _http.SendAsync(uri, new RequestSpec(0, end,
                    Referer: referer, UserAgent: userAgent, Secrets: credentials.Headers,
                    CredentialOrigin: credentials.Origin.AbsoluteUri), ct).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    return "A refreshed merge track requires different sign-in access. Saved data and credentials were kept.";
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    return $"A refreshed merge track did not honor the byte-range check (HTTP {(int)response.StatusCode}). Saved data and credentials were kept.";
                if (!HttpFacts.IsIdentityEncoded(response) || HttpFacts.IsHtml(response))
                    return "A refreshed merge track returned compressed data or an HTML page. Saved data and credentials were kept.";
                RangeCheck check = HttpFacts.CheckRangeResponse(response, 0, end);
                if (!check.IsValid || check.EndInclusive != end || check.Total != track.Size)
                    return "A refreshed merge track has a different size or range response. Saved data and credentials were kept.";
                byte[] prefix = new byte[checked((int)(end + 1))];
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await body.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
                if (HttpFacts.LooksLikeHtml(prefix))
                    return "A refreshed merge track returned an HTML sign-in or error page. Saved data and credentials were kept.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException)
            {
                return "A refreshed merge track could not be verified: " + ErrorClassifier.Describe(ex) + ". Saved data and credentials were kept.";
            }
        }
        return null;
    }

    private async Task<RescueResult> RefreshLiveHlsSourceAsync(Guid id, ResolvedMediaSource replacement,
        string savedState, RequestCredentials? credentials, CancellationToken ct, string? expectedExpiredSource)
    {
        DownloadJob job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? current) || current.Kind != JobKind.Hls
                || _cancelling.Contains(id)
                || IsCleanupPending(current) || current.PendingFinalPath is not null
                || !MatchesAutomaticRefreshLocked(current, expectedExpiredSource)
                || _sessions.ContainsKey(id) || current.MediaState != savedState)
                return Rejected("The live recording changed before its replacement could be checked. Saved data was kept.");
            job = current;
        }
        string? nextState;
        string? error;
        using (var session = new JobSession(this, job))
        {
            try { (nextState, error) = await HlsTransfer.PrepareLiveRefreshAsync(this, session, _http, savedState,
                replacement.Url, credentials, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException)
            { return Rejected("The refreshed live playlist could not be reached: " + ErrorClassifier.Describe(ex)); }
        }
        if (nextState is null) return Rejected(error ?? "The refreshed live playlist could not continue the saved recording.");

        Failpoint("refresh.live.beforeCommit");
        ct.ThrowIfCancellationRequested();

        long retained;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? current) || current.Kind != JobKind.Hls
                || _cancelling.Contains(id)
                || IsCleanupPending(current) || current.PendingFinalPath is not null || _sessions.ContainsKey(id)
                || !MatchesAutomaticRefreshLocked(current, expectedExpiredSource)
                || current.MediaState != savedState || !HlsTransfer.IsLiveState(current.MediaState))
                return Rejected("The live recording changed while its replacement was being checked. Saved data was kept.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow)
                return Rejected("The refreshed live source expired before it could be installed.");
            job = current;
            retained = job.Extents.TotalBytes;
            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.SourceUrl = replacement.Url;
            updated.MediaState = nextState;
            updated.MediaExpiresAt = replacement.ExpiresAt;
            updated.State = JobState.Queued;
            updated.Reason = "";
            updated.Message = "Source refreshed; continuing at the next live sequence.";
            updated.RetryCount = 0;
            updated.NextRetryUtc = null;
            bool removeCredentials = credentials is null && _credentials.TryGetValue(job.Id, out RequestCredentials? prior)
                && Uri.TryCreate(replacement.Url, UriKind.Absolute, out Uri? replacementUri)
                && !UrlPolicy.IsSameOrigin(prior.Origin, replacementUri);
            ct.ThrowIfCancellationRequested();
            SaveRefreshedJobLocked(job, updated);
            if (credentials is not null) _credentials[job.Id] = credentials;
            else if (removeCredentials) _credentials.Remove(job.Id);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.None,
            "The provider, media and format identity matches, and the refreshed playlist still covers the next sequence. Saved segments were kept.", retained);
    }

    /// <summary>Refresh direct plans by identity and segmented plans by checking their saved parts.</summary>
    public async Task<RescueResult> RefreshMergePlanAsync(Guid id, string replacementPlanJson,
        DateTimeOffset? expiresAt = null, CancellationToken ct = default, string? expectedExpiredSource = null)
    {
        bool parsed = false;
        bool segmented = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource)) return Rejected(AutomaticRefreshChanged);
            if (_cancelling.Contains(id)) return Rejected("The download is already being cancelled.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its source can be refreshed.");
            if (MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan current, out _)
                && MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan next, out _))
            {
                parsed = true;
                segmented = current.IsSegmented || next.IsSegmented;
            }
        }

        ct.ThrowIfCancellationRequested();
        if (!parsed || !segmented) return RefreshMergePlan(id, replacementPlanJson, expiresAt, expectedExpiredSource);
        return await RefreshSegmentedMergePlanAsync(id, replacementPlanJson, expiresAt, ct, expectedExpiredSource).ConfigureAwait(false);
    }

    private async Task<RescueResult> RefreshSegmentedMergePlanAsync(Guid id, string replacementPlanJson,
        DateTimeOffset? expiresAt, CancellationToken ct, string? expectedExpiredSource,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        RequestCredentials? credentials = null;
        DownloadJob snapshot;
        DownloadJob original;
        ExtentSet savedExtents;
        string? savedMediaState;
        string savedPlanJson;
        MediaMergePlan current;
        MediaMergePlan next;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource)) return Rejected(AutomaticRefreshChanged);
            if (_cancelling.Contains(id)) return Rejected("The download is already being cancelled.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its source can be refreshed.");
            if (_sessions.ContainsKey(id)) return Rejected("Pause the download before refreshing its source.");
            if (job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("This merge job is not in a resumable state.");
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out current, out _))
                return Rejected("The saved plan is unreadable. Saved data was kept; restart as a separate copy.");
            if (!MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out next, out string parseError))
                return Rejected(parseError);
            next = next with { ClipRange = current.ClipRange, OutputRange = current.OutputRange };
            if (!CanRebaseSegmentedPlan(current, next, out string shapeError)) return Rejected(shapeError);
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            if (secrets is not null)
            {
                if (!UrlPolicy.TryParse(next.Tracks[0].Url, out Uri firstTrack, out _))
                    return Rejected("The refreshed merge plan has no valid track address.");
                if (!TryCreateMergeCredentials(next, firstTrack, secrets, out credentials, out string credentialError,
                        allowSegmented: true) || credentials is null)
                    return Rejected(credentialError.Length > 0 ? credentialError : "The refreshed merge credentials are unavailable.");
            }
            savedPlanJson = job.MediaPlanJson!;
            savedExtents = job.Extents;
            savedMediaState = job.MediaState;
            original = job;
            snapshot = job.CopyForUpdate();
        }

        string? rebasedCursor;
        try
        {
            using var session = new JobSession(this, snapshot);
            var transfer = new SegmentedMergeTransfer(this, session, _http, credentials, useStoredCredentials: false);
            (bool valid, rebasedCursor, string message) = await transfer.VerifyCommittedPrefixAsync(current, next, ct)
                .ConfigureAwait(false);
            if (!valid) return Rejected(message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex)
            || ex is RedirectException or IOException or UnauthorizedAccessException or MediaContainerException)
        {
            return Rejected("The refreshed HLS/DASH source could not be checked against saved parts: "
                + ErrorClassifier.Describe(ex) + ". Saved data was kept.");
        }

        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, original)
                || !ReferenceEquals(job.Extents, savedExtents) || job.MediaState != savedMediaState
                || job.State != snapshot.State || job.SourceUrl != snapshot.SourceUrl
                || !MatchesAutomaticRefreshLocked(job, expectedExpiredSource)
                || _cancelling.Contains(id)
                || IsCleanupPending(job) || job.PendingFinalPath is not null
                || _sessions.ContainsKey(id) || job.MediaPlanJson != savedPlanJson
                || job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                    or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("The merge job changed while its refreshed parts were being checked. Saved data was kept.");
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The refreshed source expired before it could be installed.");

            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.MediaPlanJson = next.ToJson();
            updated.MediaState = rebasedCursor;
            updated.SourceUrl = next.Tracks[0].Url;
            updated.MediaExpiresAt = expiresAt;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
                (updated.State, updated.Reason, updated.Message, updated.RetryCount) = (JobState.Queued, "", "Source refreshed; saved parts were verified.", 0);
            ct.ThrowIfCancellationRequested();
            SaveRefreshedJobLocked(job, updated);
            if (credentials is not null) _credentials[id] = credentials;
            else _credentials.Remove(id);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0,
            "The refreshed HLS/DASH source matches every saved part byte-for-byte. Download continues.", snapshot.Extents.TotalBytes);
    }

    private static bool CanRebaseSegmentedPlan(MediaMergePlan current, MediaMergePlan next, out string error)
    {
        error = "The refreshed HLS/DASH formats or timelines differ. Saved data was kept; start a separate copy.";
        if (!current.IsSegmented || !next.IsSegmented || current.Version != next.Version
            || current.Provider != next.Provider || current.MediaId != next.MediaId
            || current.OutputContainer != next.OutputContainer || current.RequireAudio != next.RequireAudio
            || current.RequireVideo != next.RequireVideo || current.ClipRange != next.ClipRange
            || current.OutputRange != next.OutputRange || current.Tracks.Count != next.Tracks.Count)
            return false;

        for (int i = 0; i < current.Tracks.Count; i++)
        {
            MergeTrack oldTrack = current.Tracks[i], newTrack = next.Tracks[i];
            MergeMediaSource oldSource = oldTrack.Source!, newSource = newTrack.Source!;
            if (oldTrack.Role != newTrack.Role || oldTrack.FormatId != newTrack.FormatId || oldTrack.Size != newTrack.Size
                || oldTrack.Container != newTrack.Container || oldTrack.Codec != newTrack.Codec || oldTrack.Language != newTrack.Language
                || oldTrack.Identity != newTrack.Identity || oldSource.Protocol != newSource.Protocol)
                return false;

            if (oldSource.Protocol == "dash")
            {
                if (oldSource.Dash is not { } oldDash || newSource.Dash is not { } newDash
                    || oldDash.RepresentationId != newDash.RepresentationId || oldDash.Provider != newDash.Provider
                    || oldDash.MediaId != newDash.MediaId || oldDash.RequireAudio != newDash.RequireAudio
                    || oldDash.RequireVideo != newDash.RequireVideo || oldDash.ClipRange != newDash.ClipRange
                    || !SameFragmentShape(oldDash.Initialization, newDash.Initialization)
                    || oldDash.Fragments.Count != newDash.Fragments.Count
                    || oldDash.Fragments.Where((fragment, index) => !SameFragmentShape(fragment, newDash.Fragments[index])).Any())
                    return false;
            }
        }

        error = "";
        return true;
    }

    private static bool SameFragmentShape(DashFragment first, DashFragment second) =>
        first.Offset == second.Offset && first.Length == second.Length
        && first.DurationSeconds == second.DurationSeconds && first.StartSeconds == second.StartSeconds;

    /// <summary>Synchronous direct-track refresh. Segmented plans must use RefreshMergePlanAsync to compare saved parts.</summary>
    public RescueResult RefreshMergePlan(Guid id, string replacementPlanJson, DateTimeOffset? expiresAt = null,
        string? expectedExpiredSource = null)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource)) return Rejected(AutomaticRefreshChanged);
            if (_cancelling.Contains(id)) return Rejected("The download is already being cancelled.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its source can be refreshed.");
            if (_sessions.ContainsKey(id)) return Rejected("Pause the download before refreshing its source.");
            if (job.State == JobState.Completed) return Rejected("The download is already complete.");
            if (job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("This merge job is not in a resumable state.");
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out var current, out _))
                return Rejected("The saved plan is unreadable. Saved data was kept; restart as a separate copy.");
            if (!MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out var next, out var error))
                return Rejected(error);
            next = next with { ClipRange = current.ClipRange, OutputRange = current.OutputRange };
            if (!current.CanContinueWith(next, out var why)) return Rejected(why);
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.MediaPlanJson = next.ToJson();
            updated.SourceUrl = next.Tracks[0].Url;
            updated.MediaExpiresAt = expiresAt;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
                (updated.State, updated.Reason, updated.Message, updated.RetryCount) = (JobState.Queued, "", "Source refreshed; continuing saved data.", 0);
            SaveRefreshedJobLocked(job, updated);
            _credentials.Remove(id);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0, "The refreshed source matches the saved tracks byte-for-byte by provider identity. Download continues.", 0);
    }

    private const string AutomaticRefreshChanged = "The download changed since automatic refresh was scheduled. Saved data was kept.";

    // Automatic recovery must never resume a job that the user paused, replaced or restarted meanwhile.
    private bool MatchesAutomaticRefreshLocked(DownloadJob job, string? expectedExpiredSource) =>
        expectedExpiredSource is null || (job.State == JobState.NeedsLink && job.Reason is ("expired" or "forbidden")
            && job.SourceUrl == expectedExpiredSource && !_sessions.ContainsKey(job.Id)
            && job.BrowserProof is not { AwaitingConfirmation: true });

    private void SaveRefreshedJobLocked(DownloadJob job, DownloadJob updated)
    {
        bool wasCandidate = IsDuplicateCandidate(job);
        string previousSourceUrl = job.SourceUrl;
        SaveLocked(updated);
        job.ApplyPersistedState(updated);
        ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
        Changed(job);
    }

    private static string AppendPreviousUrl(string previousUrls, string sourceUrl) =>
        string.Join('\n', previousUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Append(sourceUrl).TakeLast(10));
}
