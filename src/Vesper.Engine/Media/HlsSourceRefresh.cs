namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    private async Task<RescueResult> RefreshVodHlsSourceAsync(Guid id, ResolvedMediaSource replacement,
        RequestCredentials? credentials, CancellationToken ct, string? expectedExpiredSource)
    {
        if (!UrlPolicy.TryParse(replacement.Url, out Uri url, out string error)) return Rejected(error);
        try { UrlPolicy.EnsureAllowed(url, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return Rejected(ex.Message); }

        DownloadJob original, snapshot;
        ExtentSet savedExtents;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Hls || HlsTransfer.IsLiveState(job.MediaState)
                || !MatchesAutomaticRefreshLocked(job, expectedExpiredSource)
                || _cancelling.Contains(id) || _sessions.ContainsKey(id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                    or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("The HLS job changed or is not in a resumable state. Saved data was kept.");
            if (job.MediaProvider != replacement.Provider || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                return Rejected("The refreshed HLS media identity differs. Saved data was kept.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            original = job;
            snapshot = job.CopyForUpdate();
            savedExtents = job.Extents;
        }

        string? cursor;
        try
        {
            using var session = new JobSession(this, snapshot);
            var transfer = new HlsTransfer(this, session, _http, credentials, useStoredCredentials: false);
            (bool valid, cursor, string? reason) = await transfer.VerifyVodRefreshAsync(replacement.Url, ct).ConfigureAwait(false);
            if (!valid) return Rejected((reason ?? "The refreshed HLS playlist could not be verified.") + " Saved data was kept.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RunnerStop stop) { return Rejected(stop.Outcome.Message); }
        catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex)
            || ex is RedirectException or IOException or UnauthorizedAccessException or MediaContainerException)
        { return Rejected("The refreshed HLS source could not be verified: " + ErrorClassifier.Describe(ex) + ". Saved data was kept."); }

        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, original)
                || _cancelling.Contains(id) || _sessions.ContainsKey(id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State != snapshot.State || job.SourceUrl != snapshot.SourceUrl || job.MediaState != snapshot.MediaState
                || job.MediaPlanJson != snapshot.MediaPlanJson || !ReferenceEquals(job.Extents, savedExtents)
                || !MatchesAutomaticRefreshLocked(job, expectedExpiredSource))
                return Rejected("The HLS job changed while its replacement was being checked. Saved data was kept.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The refreshed source expired before it could be installed.");
            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.SourceUrl = replacement.Url;
            updated.MediaState = cursor;
            updated.MediaExpiresAt = replacement.ExpiresAt;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
            {
                (updated.State, updated.Reason, updated.Message, updated.RetryCount) =
                    (JobState.Queued, "", "Source refreshed; saved segments were verified.", 0);
                updated.NextRetryUtc = null;
            }
            ct.ThrowIfCancellationRequested();
            SaveRefreshedJobLocked(job, updated);
            if (credentials is not null) _credentials[id] = credentials;
            else _credentials.Remove(id);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0, "The refreshed HLS playlist can continue the saved segments.",
            snapshot.Extents.TotalBytes);
    }
}
