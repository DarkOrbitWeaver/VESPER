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
            job.State = JobState.Cancelled;
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

    /// <summary>Adds a reference to an existing Chrome download. VESPER never copies, replaces, or deletes it.</summary>
    public AddResult RegisterBrowserDownload(string requestId, BrowserCompletedFile file, string? transferId = null)
    {
        if (!BridgeProtocol.IsRequestId(requestId))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Invalid browser download id.");
        if (!PathSafety.TryGetBrowserDownload(file, Options.DefaultFolder, Options.BrowserDownloadsFolder, out string path))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "The Chrome file is outside the download folders or has changed.");
        string submission = "chrome-completed:" + requestId;
        AddResult result;
        lock (_lock)
        {
            if (_store.TryGetSubmission(submission, out Guid accepted) && _jobs.ContainsKey(accepted))
                return new AddResult(AddStatus.AlreadySubmitted, accepted, "This Chrome download is already in the list.");
            DownloadJob? prior = transferId is not null ? BrowserTransferLocked(transferId) : null;
            if (prior is { State: JobState.Completed } && !_missing.Contains(prior.Id))
            {
                SaveLocked(prior, submission);
                return new AddResult(AddStatus.AlreadySubmitted, prior.Id, "VESPER already saved this download.");
            }
            if (prior is not null && (_sessions.ContainsKey(prior.Id) || JobStates.IsActive(prior.State)))
                return new AddResult(AddStatus.Busy, Guid.Empty, "VESPER is releasing the previous transfer.");

            // Replace an unused handoff entry. An interrupted transfer with retained bytes stays available for recovery.
            bool replace = prior is { State: JobState.Cancelled, Extents.IsEmpty: true } && RemoveEmptyBrowserPartial(prior);
            string origin = UrlPolicy.TryParse(file.Origin, out Uri uri, out _) ? uri.GetLeftPart(UriPartial.Authority) + "/" : "";
            string name = Path.GetFileName(path);
            var job = new DownloadJob
            {
                Id = replace ? prior!.Id : Guid.NewGuid(), Kind = JobKind.BrowserFile,
                SourceUrl = origin, OriginalPageUrl = origin.Length == 0 ? null : origin,
                FileName = name, NameLocked = true, FinalPath = path, DestinationFolder = Path.GetDirectoryName(path)!,
                Category = FileNames.CategoryFor(name), TotalBytes = file.Length, State = JobState.Completed,
                CompletedUtc = DateTime.UtcNow, QueueOrder = replace ? prior!.QueueOrder : (_jobs.Count == 0 ? 0 : _jobs.Values.Min(j => j.QueueOrder) - 1)
            };
            job.Extents.Add(new ByteRange(0, file.Length));
            SaveLocked(job, submission);
            _jobs[job.Id] = job;
            _credentials.Remove(job.Id);
            _missing.Remove(job.Id);
            Changed(job);
            Log(job.Id, "chrome-completed", "Chrome completed the download.");
            result = new AddResult(AddStatus.Added, job.Id, "Added the file saved by Chrome.");
        }
        FlushOutbox();
        return result;
    }

    private bool RemoveEmptyBrowserPartial(DownloadJob job)
    {
        if (job.PartialPath.Length == 0 || !File.Exists(job.PartialPath)) return true;
        if (!PathSafety.IsInside(job.DestinationFolder, job.PartialPath) || !job.PartialPath.EndsWith(".vpart", StringComparison.OrdinalIgnoreCase))
            return false;
        return DeleteTemporaryFile(job, job.PartialPath);
    }
}
