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
        && _jobs.TryGetValue(id, out DownloadJob? job) && job.BrowserProof is { } proof
        && (!proof.Adopted || proof.AdoptionTransferId == transferId) ? job : null;

    public JobSnapshot? BrowserTransferSnapshot(string transferId)
    {
        lock (_lock)
        {
            if (BrowserTransferLocked(transferId) is not { } job) return null;
            if (job.BrowserProof is { AdoptionReleased: true } && job.State != JobState.Completed
                && job.PendingFinalPath is null && job.State is not (JobState.Finalizing or JobState.Verifying or JobState.Converting))
            {
                if (!_sessions.ContainsKey(job.Id)) return null;
                // A released adoption keeps its data. Report stopping until its runner has actually exited.
                job = job.CopyForUiMutation();
                job.State = JobState.Cancelled;
            }
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

    private sealed record BrowserAdoption(string RequestId, BrowserTransferProof Proof);

    private sealed record PendingAdoption(Guid JobId, string ExpiredSource, string Url, RequestCredentials? Credentials,
        BrowserAdoption Binding, DateTime OfferedUtc)
    {
        public bool Ready { get; set; }
        public bool Accepting { get; set; }
    }

    // Fresh links Chrome offered but has not parked for yet. Guarded by _lock. An offer outlives the extension's
    // longest wait for a capture reply (24 s) and nothing more, so a forgotten one cannot be accepted late.
    private readonly Dictionary<string, PendingAdoption> _pendingAdoptions = [];
    private static readonly TimeSpan AdoptionWindow = TimeSpan.FromSeconds(30);
    private const int MaxPendingAdoptions = 128;

    /// <summary>
    /// Chrome started a download that may be the fresh address of a download waiting for a new link. Nothing is
    /// changed here. Exactly one waiting download must match the size and the strong validator Chrome received,
    /// and one ranged request must satisfy the same Replace Link rules a person would trigger by hand. The waiting
    /// download switches to the new address only in <see cref="AcceptBrowserTransferAsync"/>, once Chrome is parked.
    /// </summary>
    /// <returns>The waiting download that would continue, or null when Chrome's download is simply a new file.</returns>
    public async Task<Guid?> OfferFreshLinkAsync(AddRequest request, CancellationToken ct = default)
    {
        if (request is not { Kind: JobKind.Http, StartNow: true, RequestId: { Length: > 2 } requestId,
                BrowserProof: { IsValid: true, AwaitingConfirmation: true, Length: long length } proof }
            || !requestId.EndsWith(":0", StringComparison.Ordinal)
            || !UrlPolicy.TryParse(request.Url, out Uri fresh, out _)) return null;

        string transferId = requestId[..^2];
        if (!BridgeProtocol.IsRequestId(transferId)) return null;
        RequestCredentials? credentials = request.Secrets is { Count: > 0 }
            ? new RequestCredentials(new UriBuilder(fresh) { Path = "/", Query = "", Fragment = "", UserName = "", Password = "" }.Uri,
                new Dictionary<string, string>(request.Secrets))
            : null;
        PendingAdoption offer;
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string lapsed in _pendingAdoptions.Where(entry => now - entry.Value.OfferedUtc > AdoptionWindow)
                         .Select(entry => entry.Key).ToArray())
                _pendingAdoptions.Remove(lapsed);
            if (_pendingAdoptions.TryGetValue(transferId, out PendingAdoption? existing))
            {
                if (existing.Ready && existing.Url == request.Url && existing.Binding.Proof == proof)
                    return existing.JobId;
                throw new InvalidOperationException("A fresh-link offer is already being checked.");
            }
            DownloadJob[] waiting = [.. _jobs.Values.Where(job => WaitsForLinkLocked(job, fresh, length, proof))];
            if (waiting.Length != 1 || _pendingAdoptions.Count >= MaxPendingAdoptions
                || _pendingAdoptions.Values.Any(entry => entry.JobId == waiting[0].Id)) return null;
            offer = new PendingAdoption(waiting[0].Id, waiting[0].SourceUrl, request.Url, credentials,
                new BrowserAdoption(requestId, proof), now);
            // Reserve before awaiting network I/O so an abandon can revoke even an unfinished offer.
            _pendingAdoptions.Add(transferId, offer);
        }

        try
        {
            RescueResult verdict = await ReplaceLinkCoreAsync(offer.JobId, offer.Url, null, ct, offer.Credentials, null,
                offer.ExpiredSource, commit: false).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_pendingAdoptions.TryGetValue(transferId, out PendingAdoption? current)
                    || !ReferenceEquals(current, offer))
                    throw new OperationCanceledException("The browser withdrew the fresh-link offer.");
                if (!verdict.Accepted || DateTime.UtcNow - offer.OfferedUtc > AdoptionWindow)
                {
                    _pendingAdoptions.Remove(transferId);
                    return null;
                }
                offer.Ready = true;
            }
            Log(offer.JobId, "fresh-link-offered", UrlPolicy.Redact(fresh.AbsoluteUri));
            return offer.JobId;
        }
        catch
        {
            lock (_lock)
                if (_pendingAdoptions.TryGetValue(transferId, out PendingAdoption? current) && ReferenceEquals(current, offer))
                    _pendingAdoptions.Remove(transferId);
            throw;
        }
    }

    // Chrome's own response must already carry the size and the strong validator the saved bytes were fetched under.
    private bool WaitsForLinkLocked(DownloadJob job, Uri fresh, long length, BrowserTransferProof proof)
    {
        if (job is not { State: JobState.NeedsLink, Reason: "expired" or "forbidden", Kind: JobKind.Http, PendingFinalPath: null }
            || job.TotalBytes != length || job.Extents.TotalBytes == 0
            || _sessions.ContainsKey(job.Id) || _cancelling.Contains(job.Id) || IsCleanupPending(job)
            || !Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? expired)
            || !string.Equals(expired.Host, fresh.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        return job.Identity switch
        {
            IdentityLevel.StrongEtag => proof.ETag is not null && proof.ETag == job.ETag,
            IdentityLevel.LastModified => proof.UsesLastModified && proof.LastModifiedValue is { } stamp
                && job.LastModified is { } saved && stamp.ToUnixTimeSeconds() == saved.ToUnixTimeSeconds(),
            _ => false
        };
    }

    /// <summary>
    /// Chrome is parked. An ordinary transfer is confirmed; an offered fresh link is re-checked and committed now,
    /// through the Replace Link path, so a link that stopped qualifying leaves the waiting download untouched.
    /// </summary>
    public async Task<bool> AcceptBrowserTransferAsync(string transferId, CancellationToken ct = default)
    {
        PendingAdoption? offer;
        lock (_lock)
        {
            if (_pendingAdoptions.TryGetValue(transferId, out offer))
            {
                if (!offer.Ready || offer.Accepting || DateTime.UtcNow - offer.OfferedUtc > AdoptionWindow) return false;
                offer.Accepting = true;
            }
        }
        if (offer is null) return AcceptBrowserTransfer(transferId);
        try
        {
            RescueResult result = await ReplaceLinkCoreAsync(offer.JobId, offer.Url, null, ct, offer.Credentials, null,
                offer.ExpiredSource, adoption: offer.Binding).ConfigureAwait(false);
            if (result.Accepted) Log(offer.JobId, "fresh-link-adopted", result.Mode.ToString());
            return result.Accepted;
        }
        finally
        {
            lock (_lock)
                if (_pendingAdoptions.TryGetValue(transferId, out PendingAdoption? current) && ReferenceEquals(current, offer))
                    _pendingAdoptions.Remove(transferId);
        }
    }

    // Called with the engine lock held at the same commit that changes the saved job.
    private bool AdoptionStillAcceptedLocked(BrowserAdoption adoption, Guid jobId)
        => _pendingAdoptions.TryGetValue(adoption.RequestId[..^2], out PendingAdoption? offer)
            && ReferenceEquals(offer.Binding, adoption) && offer.JobId == jobId && offer.Accepting
            && DateTime.UtcNow - offer.OfferedUtc <= AdoptionWindow;

    // Chrome takes the file back. Keep the adoption binding until its runner stops, and
    // never let an earlier submission receipt control a later adoption of this same job.
    private bool ReleaseAdoption(string transferId)
    {
        lock (_lock)
        {
            if (_pendingAdoptions.Remove(transferId)) return true;
            if (BrowserTransferLocked(transferId) is not { BrowserProof.Adopted: true } job) return false;
            if (_cancelling.Contains(job.Id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || job.State is JobState.Completed or JobState.Finalizing or JobState.Verifying or JobState.Converting)
                return true;
            if (job.BrowserProof!.AdoptionReleased) return true;
            DownloadJob released = job.CopyForUpdate();
            released.BrowserProof = job.BrowserProof with { AdoptionReleased = true };
            if (job.State is JobState.Queued or JobState.WaitingForNetwork or JobState.WaitingForRetry)
            {
                released.State = JobState.Paused;
                released.NextRetryUtc = null;
                released.Message = "";
            }
            SaveLocked(released);
            job.ApplyPersistedState(released);
            Changed(job);
            if (_sessions.TryGetValue(job.Id, out JobSession? session)
                && session.Intent is not (StopIntent.Cancel or StopIntent.LiveStop or StopIntent.BrowserFallback or StopIntent.Shutdown))
            {
                session.Intent = StopIntent.Pause;
                session.Cts.Cancel();
            }
        }
        ScheduleNext();
        FlushOutbox();
        return true;
    }

    public bool AcceptBrowserTransfer(string transferId)
    {
        lock (_lock)
        {
            if (BrowserTransferLocked(transferId) is not { BrowserProof: { } proof } job) return false;
            if (proof.AdoptionReleased || IsCleanupPending(job) || _cancelling.Contains(job.Id)) return false;
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
        if (ReleaseAdoption(transferId)) return;
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
