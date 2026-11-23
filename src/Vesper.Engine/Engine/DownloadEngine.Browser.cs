namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    /// <summary>Records a finished Chrome file at its saved location without copying or changing it.</summary>
    public AddResult RegisterBrowserDownload(BrowserCompletedFile file)
    {
        if (file.Profile is not { Length: >= 8 and <= 36 }
            || !file.Profile.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            || !BridgeProtocol.IsRequestId(file.DownloadId)
            || !file.DownloadId.StartsWith("c-" + file.Profile + "-", StringComparison.Ordinal)
            || file.Hosts is not { Count: > 0 })
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Invalid browser download id.");
        if (!PathSafety.TryGetBrowserDownload(file, out string path, out string? identity))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "The Chrome file is outside the approved download folders or has changed.");

        string submission = "chrome-managed:" + file.DownloadId;
        AddResult result;
        lock (_lock)
        {
            if (_store.TryGetSubmission(submission, out Guid accepted) && _jobs.ContainsKey(accepted))
                return new AddResult(AddStatus.AlreadySubmitted, accepted, "This Chrome download is already in VESPER.");

            string? origin = file.Hosts.Count > 0
                ? file.Hosts[0].Contains(':') ? "https://[" + file.Hosts[0] + "]/" : "https://" + file.Hosts[0] + "/"
                : null;
            string name = Path.GetFileName(path);
            var job = new DownloadJob
            {
                Kind = JobKind.BrowserFile,
                SourceUrl = origin ?? "https://download.invalid/",
                OriginalPageUrl = origin,
                FileName = name,
                NameLocked = true,
                FinalPath = path,
                PublishedFileIdentity = identity,
                DestinationFolder = Path.GetDirectoryName(path)!,
                Category = FileNames.CategoryFor(name),
                TotalBytes = file.Length,
                State = JobState.Completed,
                CompletedUtc = DateTime.UtcNow,
                QueueOrder = _jobs.Count == 0 ? 0 : _jobs.Values.Min(existing => existing.QueueOrder) - 1
            };
            if (file.Length > 0) job.Extents.Add(new ByteRange(0, file.Length));
            SaveLocked(job, submission);
            _jobs[job.Id] = job;
            Changed(job);
            Log(job.Id, "chrome-managed", "Chrome completed the enrolled download.");
            result = new AddResult(AddStatus.Added, job.Id, "Added the file saved by Chrome.");
        }
        FlushOutbox();
        return result;
    }

    internal static RunOutcome BrowserFallbackOutcome => new(JobState.Cancelled, "browser-fallback", "Chrome can continue this download.");

    private DownloadJob? BrowserTransferLocked(string transferId) =>
        BridgeProtocol.IsRequestId(transferId) && _store.TryGetSubmission(transferId + ":0", out Guid id)
        && _jobs.TryGetValue(id, out DownloadJob? job) && job.BrowserProof is not null ? job : null;

    public JobSnapshot? BrowserTransferSnapshot(string transferId)
    {
        lock (_lock)
        {
            if (BrowserTransferLocked(transferId) is not { } job) return null;
            if (job.PendingFinalPath is not null && job.State != JobState.Finalizing)
            {
                // The file may already be on disk. Keep Chrome parked until publication is reconciled.
                job = job.CopyForUiMutation();
                job.State = JobState.Finalizing;
            }
            return SnapshotLocked(job);
        }
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
            if (IsCleanupPending(job) || _cancelling.Contains(job.Id)) return false;
            if (job.PendingFinalPath is not null) return !proof.AwaitingConfirmation;
            if (job.State is not (JobState.Queued or JobState.Resolving or JobState.Downloading or JobState.WaitingForRetry
                or JobState.WaitingForNetwork or JobState.Paused or JobState.Finalizing or JobState.Completed)) return false;
            if (!proof.AwaitingConfirmation) return true;
            if (job.State != JobState.Downloading || job.Reason != "browser-ready") return false;
            DownloadJob accepted = job.CopyForUpdate();
            accepted.BrowserProof = proof with { AwaitingConfirmation = false };
            if (accepted.Reason == "browser-ready") { accepted.Reason = ""; accepted.Message = ""; }
            SaveLocked(accepted);
            job.ApplyPersistedState(accepted);
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
            if (BrowserTransferLocked(transferId) is not { } job || _cancelling.Contains(job.Id)
                || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State is JobState.Completed or JobState.Finalizing or JobState.Verifying or JobState.Converting)
                return;
            bool wasCandidate = IsDuplicateCandidate(job);
            string previousSourceUrl = job.SourceUrl;
            DownloadJob abandoned = job.CopyForUpdate();
            abandoned.State = JobState.Cancelled;
            abandoned.Reason = BrowserFallbackOutcome.Reason;
            abandoned.Message = BrowserFallbackOutcome.Message;
            abandoned.NextRetryUtc = null;
            SaveLocked(abandoned);
            job.ApplyPersistedState(abandoned);
            ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
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
