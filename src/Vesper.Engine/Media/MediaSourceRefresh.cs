using System.Text.Json.Serialization;

namespace Vesper.Engine;

/// <summary>A newly resolved plain-file URL, not permission to append a different representation.</summary>
public sealed record ResolvedMediaSource(string Provider, string MediaId, string FormatId, string Url,
    DateTimeOffset? ExpiresAt = null,
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? Secrets = null);

public sealed partial class DownloadEngine
{
    /// <summary>
    /// Explicit source re-resolution for HTTP media and a saved live-HLS recording. Requires exact provider/media/format
    /// identities; direct files then delegate byte identity to Rescue, while live HLS checks the next sequence. Optional
    /// request credentials are memory-only and scoped to the replacement origin.
    /// </summary>
    public async Task<RescueResult> RefreshMediaSourceAsync(Guid id, ResolvedMediaSource replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        string? liveState = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return Rejected("That entry no longer exists.");
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
            else if (job.Kind != JobKind.Http)
                return Rejected("Streaming sources require a compatible fragment plan. Saved data was kept; resolve a separate copy.");
            else if (string.IsNullOrEmpty(job.MediaProvider) || string.IsNullOrEmpty(job.MediaId) || string.IsNullOrEmpty(job.MediaFormatId))
                return Rejected("This older job does not have enough source identity for automatic re-resolution. Use ordinary Rescue with a trusted identity.");
            else if (job.MediaProvider != replacement.Provider || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                return Rejected("The newly resolved provider, media identity or selected format differs. Restart as a separate copy; no bytes were changed.");
            else if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
        }
        ct.ThrowIfCancellationRequested();
        if (!TryCreateReplacementCredentials(replacement, out RequestCredentials? credentials, out string credentialError))
            return Rejected(credentialError);
        if (liveState is not null)
            return await RefreshLiveHlsSourceAsync(id, replacement, liveState, credentials, ct).ConfigureAwait(false);
        var result = await ReplaceLinkCoreAsync(id, replacement.Url, null, ct, credentials).ConfigureAwait(false);
        if (result.Accepted)
        {
            lock (_lock)
            {
                if (_jobs.TryGetValue(id, out var job) && job.SourceUrl == replacement.Url)
                { job.MediaExpiresAt = replacement.ExpiresAt; SaveLocked(job); Changed(job); }
            }
            FlushOutbox();
        }
        return result;
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
        IReadOnlyDictionary<string, string>? supplied, out RequestCredentials? credentials, out string error)
    {
        credentials = null;
        error = "";
        if (supplied is not { Count: > 0 }) return true;
        if (plan.IsSegmented || plan.Tracks.Count == 0
            || !UrlPolicy.TryParse(plan.Tracks[0].Url, out Uri first, out _)
            || !UrlPolicy.IsSameOrigin(requestUrl, first))
        {
            error = "Credentials are supported only for direct merge tracks on the source origin.";
            return false;
        }
        foreach (MergeTrack track in plan.Tracks)
        {
            if (track.Source is not null || !UrlPolicy.TryParse(track.Url, out Uri target, out _)
                || !UrlPolicy.IsSameOrigin(first, target))
            {
                error = "Credentials cannot be used with segmented or cross-origin merge tracks.";
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
    /// Credentialed merge refresh is limited to direct same-origin tracks. Every fresh track is range-probed before
    /// the engine replaces either its URLs or its in-memory credentials.
    /// </summary>
    public async Task<RescueResult> RefreshMergePlanWithCredentialsAsync(Guid id, string replacementPlanJson,
        DateTimeOffset? expiresAt, IReadOnlyDictionary<string, string> secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        DownloadJob snapshot;
        string savedPlanJson;
        MediaMergePlan current;
        MediaMergePlan next;
        RequestCredentials? credentials;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (_sessions.ContainsKey(id)) return Rejected("Pause the download before refreshing its source.");
            if (job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("This merge job is not in a resumable state.");
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out current, out _))
                return Rejected("The saved plan is unreadable. Saved data was kept; restart as a separate copy.");
            if (!MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out next, out string parseError))
                return Rejected(parseError);
            if (!current.CanContinueWith(next, out string why)) return Rejected(why);
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            if (next.Tracks.Count == 0 || !UrlPolicy.TryParse(next.Tracks[0].Url, out Uri firstTrack, out _))
                return Rejected("The refreshed merge plan has no valid track address.");
            if (!TryCreateMergeCredentials(next, firstTrack, secrets, out credentials, out string credentialError)
                || credentials is null)
                return Rejected(credentialError.Length > 0 ? credentialError : "The refreshed merge credentials are unavailable.");
            savedPlanJson = job.MediaPlanJson!;
            snapshot = job;
        }

        string? probeError = await ProbeCredentialedMergeTracksAsync(next, snapshot.Referer, snapshot.UserAgent, credentials!, ct)
            .ConfigureAwait(false);
        if (probeError is not null) return Rejected(probeError);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, snapshot)
                || _sessions.ContainsKey(id) || job.MediaPlanJson != savedPlanJson
                || job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                    or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("The merge job changed while its refreshed tracks were being checked. Saved data was kept.");
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The refreshed source expired before it could be installed.");

            job.MediaPlanJson = next.ToJson();
            ChangeSourceUrlLocked(job, next.Tracks[0].Url);
            job.MediaExpiresAt = expiresAt;
            _credentials[id] = credentials!;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
                (job.State, job.Reason, job.Message, job.RetryCount) = (JobState.Queued, "", "Source refreshed; continuing saved data.", 0);
            AddDuplicateCandidateLocked(job);
            SaveLocked(job);
            Changed(job);
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
        string savedState, RequestCredentials? credentials, CancellationToken ct)
    {
        DownloadJob job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? current) || current.Kind != JobKind.Hls
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

        long retained;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? current) || current.Kind != JobKind.Hls || _sessions.ContainsKey(id)
                || current.MediaState != savedState || !HlsTransfer.IsLiveState(current.MediaState))
                return Rejected("The live recording changed while its replacement was being checked. Saved data was kept.");
            job = current;
            retained = job.Extents.TotalBytes;
            string[] previous = job.PreviousUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            job.PreviousUrls = string.Join('\n', previous.Append(job.SourceUrl).TakeLast(10));
            ChangeSourceUrlLocked(job, replacement.Url);
            if (credentials is not null) _credentials[job.Id] = credentials;
            else if (_credentials.TryGetValue(job.Id, out RequestCredentials? prior)
                && Uri.TryCreate(replacement.Url, UriKind.Absolute, out Uri? replacementUri)
                && !UrlPolicy.IsSameOrigin(prior.Origin, replacementUri)) _credentials.Remove(job.Id);
            job.MediaState = nextState;
            job.MediaExpiresAt = replacement.ExpiresAt;
            job.State = JobState.Queued;
            AddDuplicateCandidateLocked(job);
            job.Reason = "";
            job.Message = "Source refreshed; continuing at the next live sequence.";
            job.RetryCount = 0;
            job.NextRetryUtc = null;
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.None,
            "The provider, media and format identity matches, and the refreshed playlist still covers the next sequence. Saved segments were kept.", retained);
    }

    /// <summary>
    /// Replacement links for an interrupted merge job (for example after signed URLs expired). Accepted only when every
    /// track of the new plan is byte-identical by provider evidence (same role, format id, exact size, codec and
    /// encoding identity such as YouTube itag + last-modified + content length). Then only the URLs change and the
    /// committed bytes are continued; otherwise nothing on disk changes and the user is told to start a separate copy.
    /// </summary>
    public RescueResult RefreshMergePlan(Guid id, string replacementPlanJson, DateTimeOffset? expiresAt = null)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Merge) return Rejected("Only merge jobs accept a replacement media plan.");
            if (_sessions.ContainsKey(id)) return Rejected("Pause the download before refreshing its source.");
            if (job.State == JobState.Completed) return Rejected("The download is already complete.");
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out var current, out _))
                return Rejected("The saved plan is unreadable. Saved data was kept; restart as a separate copy.");
            if (!MediaMergePlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out var next, out var error))
                return Rejected(error);
            if (!current.CanContinueWith(next, out var why)) return Rejected(why);
            if (expiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            job.MediaPlanJson = next.ToJson();
            ChangeSourceUrlLocked(job, next.Tracks[0].Url);
            job.MediaExpiresAt = expiresAt;
            _credentials.Remove(id);
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
                (job.State, job.Reason, job.Message, job.RetryCount) = (JobState.Queued, "", "Source refreshed; continuing saved data.", 0);
            AddDuplicateCandidateLocked(job);
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0, "The refreshed source matches the saved tracks byte-for-byte by provider identity. Download continues.", 0);
    }
}
