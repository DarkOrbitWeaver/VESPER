namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    /// <summary>Rebase a standalone DASH job after verifying the exact committed fragment prefix.</summary>
    public async Task<RescueResult> RefreshDashPlanAsync(Guid id, ResolvedMediaSource replacement,
        string replacementPlanJson, CancellationToken ct = default, string? expectedExpiredSource = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!UrlPolicy.TryParse(replacement.Url, out Uri url, out string urlError)) return Rejected(urlError);
        try { UrlPolicy.EnsureAllowed(url, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return Rejected(ex.Message); }
        if (!TryCreateReplacementCredentials(replacement, out RequestCredentials? credentials, out string credentialError))
            return Rejected(credentialError);

        DownloadJob original, snapshot;
        ExtentSet savedExtents;
        DashDownloadPlan current, next;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Dash) return Rejected("Only DASH jobs accept a replacement DASH plan.");
            if (!MatchesAutomaticRefreshLocked(job, expectedExpiredSource)) return Rejected(AutomaticRefreshChanged);
            if (_cancelling.Contains(id) || _sessions.ContainsKey(id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State is not (JobState.Queued or JobState.Paused or JobState.WaitingForNetwork or JobState.WaitingForRetry
                    or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed))
                return Rejected("The DASH job is not in a resumable state. Saved data was kept.");
            if (string.IsNullOrWhiteSpace(job.MediaProvider) || string.IsNullOrWhiteSpace(job.MediaId)
                || string.IsNullOrWhiteSpace(job.MediaFormatId) || job.MediaProvider != replacement.Provider
                || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                return Rejected("The refreshed DASH media identity differs. Saved data was kept.");
            if (!DashDownloadPlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out current, out _))
                return Rejected("The saved DASH plan is invalid. Saved data was kept.");
            if (!DashDownloadPlan.TryParse(replacementPlanJson, Options.AllowPrivateNetworks, out next, out string error))
                return Rejected(error);
            next = next with { ClipRange = current.ClipRange };
            if (current.Provider != replacement.Provider || current.MediaId != replacement.MediaId
                || current.RepresentationId != replacement.FormatId || !CanRebaseDashPlan(current, next))
                return Rejected("The refreshed DASH representation or timeline differs. Saved data was kept.");
            if (next.ClipRange is not null && next.Fragments.Any(fragment => fragment.DurationSeconds is null))
                return Rejected("The refreshed DASH timeline cannot reproduce the selected clip. Saved data was kept.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            original = job;
            snapshot = job.CopyForUpdate();
            savedExtents = job.Extents;
        }

        string? cursor;
        try
        {
            using var session = new JobSession(this, snapshot);
            var transfer = new DashTransfer(this, session, _http, credentials, useStoredCredentials: false);
            (bool valid, cursor, string error) = await transfer.VerifyRefreshAsync(current, next, ct).ConfigureAwait(false);
            if (!valid) return Rejected(error + " Saved data was kept.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RunnerStop stop) { return Rejected(stop.Outcome.Message); }
        catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex)
            || ex is RedirectException or IOException or UnauthorizedAccessException or MediaContainerException)
        { return Rejected("The refreshed DASH source could not be verified: " + ErrorClassifier.Describe(ex) + ". Saved data was kept."); }

        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, original)
                || _cancelling.Contains(id) || _sessions.ContainsKey(id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State != snapshot.State || job.SourceUrl != snapshot.SourceUrl || job.MediaState != snapshot.MediaState
                || job.MediaPlanJson != snapshot.MediaPlanJson || !ReferenceEquals(job.Extents, savedExtents)
                || !MatchesAutomaticRefreshLocked(job, expectedExpiredSource))
                return Rejected("The DASH job changed while its replacement was being checked. Saved data was kept.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The refreshed source expired before it could be installed.");
            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, updated.SourceUrl);
            updated.SourceUrl = replacement.Url;
            updated.MediaPlanJson = next.ToJson();
            updated.MediaState = cursor;
            updated.MediaExpiresAt = replacement.ExpiresAt;
            if (job.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.WaitingForRetry)
            {
                (updated.State, updated.Reason, updated.Message, updated.RetryCount) =
                    (JobState.Queued, "", "Source refreshed; saved fragments were verified.", 0);
                updated.NextRetryUtc = null;
            }
            ct.ThrowIfCancellationRequested();
            SaveRefreshedJobLocked(job, updated);
            if (credentials is not null) _credentials[id] = credentials;
            else _credentials.Remove(id);
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, RescueMode.R0, "The refreshed DASH source matches the saved fragments byte-for-byte.",
            snapshot.Extents.TotalBytes);
    }

    private static bool CanRebaseDashPlan(DashDownloadPlan current, DashDownloadPlan next) =>
        current.Version == next.Version && current.Provider == next.Provider && current.MediaId == next.MediaId
        && current.RepresentationId == next.RepresentationId && current.RequireAudio == next.RequireAudio
        && current.RequireVideo == next.RequireVideo && current.ClipRange == next.ClipRange
        && SameFragmentShape(current.Initialization, next.Initialization)
        && current.Fragments.Count == next.Fragments.Count
        && !current.Fragments.Where((fragment, index) => !SameFragmentShape(fragment, next.Fragments[index])).Any();
}
