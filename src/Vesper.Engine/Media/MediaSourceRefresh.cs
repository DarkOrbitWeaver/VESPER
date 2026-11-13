namespace Vesper.Engine;

/// <summary>A newly resolved plain-file URL, not permission to append a different representation.</summary>
public sealed record ResolvedMediaSource(string Provider, string MediaId, string FormatId, string Url,
    DateTimeOffset? ExpiresAt = null);

public sealed partial class DownloadEngine
{
    /// <summary>
    /// Explicit source re-resolution for HTTP media. Requires exact provider/media/format identities, then delegates
    /// byte identity to Rescue (strong same-resource validators or a trusted full/piece digest). A matching format ID
    /// alone is never proof that bytes match. Streaming refresh needs plan-level verification and is refused here.
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
        if (liveState is not null)
            return await RefreshLiveHlsSourceAsync(id, replacement, liveState, ct).ConfigureAwait(false);
        var result = await ReplaceLinkAsync(id, replacement.Url, ct: ct).ConfigureAwait(false);
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

    private async Task<RescueResult> RefreshLiveHlsSourceAsync(Guid id, ResolvedMediaSource replacement,
        string savedState, CancellationToken ct)
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
            try { (nextState, error) = await HlsTransfer.PrepareLiveRefreshAsync(this, session, _http, savedState, replacement.Url, ct).ConfigureAwait(false); }
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
