namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    internal static RunOutcome BrowserFallbackOutcome => new(JobState.Cancelled, "browser-fallback", "Chrome can continue this download.");

    private DownloadJob? BrowserTransferLocked(string transferId) =>
        BridgeProtocol.IsRequestId(transferId) && _store.TryGetSubmission(transferId + ":0", out Guid id)
        && _jobs.TryGetValue(id, out DownloadJob? job) && job.BrowserProof is not null ? job : null;

    public JobSnapshot? BrowserTransferSnapshot(string transferId)
    {
        lock (_lock) return BrowserTransferLocked(transferId) is { } job ? SnapshotLocked(job) : null;
    }

    public bool BrowserTransferIsRunning(string transferId)
    {
        lock (_lock) return BrowserTransferLocked(transferId) is { } job && _sessions.ContainsKey(job.Id);
    }

    public bool AcceptBrowserTransfer(string transferId)
    {
        lock (_lock)
        {
            if (BrowserTransferLocked(transferId) is not { BrowserProof: { } proof } job) return false;
            if (job.State is not (JobState.Queued or JobState.Resolving or JobState.Downloading or JobState.WaitingForRetry
                or JobState.WaitingForNetwork or JobState.Paused or JobState.Finalizing or JobState.Completed)) return false;
            if (!proof.AwaitingConfirmation) return true;
            var previousStatus = (job.Reason, job.Message);
            job.BrowserProof = proof with { AwaitingConfirmation = false };
            if (job.Reason == "browser-ready") { job.Reason = ""; job.Message = ""; }
            try { SaveLocked(job); }
            catch
            {
                job.BrowserProof = proof;
                (job.Reason, job.Message) = previousStatus;
                throw;
            }
            Changed(job);
            _sessions.GetValueOrDefault(job.Id)?.BrowserApproval?.TrySetResult();
        }
        FlushOutbox();
        return true;
    }

    public void AbandonBrowserTransfer(string transferId)
    {
        lock (_lock)
        {
            if (BrowserTransferLocked(transferId) is not { } job || job.State is JobState.Completed or JobState.Finalizing) return;
            bool wasCandidate = IsDuplicateCandidate(job);
            string previousSourceUrl = job.SourceUrl;
            job.State = JobState.Cancelled;
            ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
            job.Reason = BrowserFallbackOutcome.Reason;
            job.Message = BrowserFallbackOutcome.Message;
            job.NextRetryUtc = null;
            SaveLocked(job);
            Changed(job);
            if (_sessions.TryGetValue(job.Id, out JobSession? session))
            {
                session.Intent = StopIntent.BrowserFallback;
                session.Cts.Cancel();
            }
        }
        ScheduleNext();
        FlushOutbox();
    }
}
