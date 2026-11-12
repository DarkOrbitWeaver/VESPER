using System.Text;

namespace Vesper.Engine;

public enum AddStatus { Added, Duplicate, Invalid, DestinationUnavailable, AlreadySubmitted, Busy }
public enum QueueMove { Top, Up, Down, Bottom }

public sealed record AddRequest(
    string Url,
    string? DestinationFolder = null,
    string? FileName = null,
    bool StartNow = true,
    string? Referer = null,
    string? UserAgent = null,
    string? RequestId = null,
    string? ExpectedSha256 = null,
    string? PieceMapJson = null,
    JobKind Kind = JobKind.Http,
    JobPriority Priority = JobPriority.Normal,
    /// <summary>Cookie/Authorization values. Kept in memory only; never written to disk.</summary>
    IReadOnlyDictionary<string, string>? Secrets = null,
    string? MediaState = null,
    string? OriginalPageUrl = null,
    string? MediaProvider = null,
    string? MediaId = null,
    string? MediaQuality = null,
    string? MediaThumbnailUrl = null,
    DateTimeOffset? MediaExpiresAt = null,
    string? MediaPlanJson = null,
    string? MediaFormatId = null,
    MediaOutput Output = MediaOutput.Original,
    BrowserTransferProof? BrowserProof = null);

public sealed record AddResult(AddStatus Status, Guid JobId, string Message)
{
    public bool Ok => Status is AddStatus.Added or AddStatus.AlreadySubmitted;
}

public sealed record RemoveResult(bool Removed, string Message, string? RetainedPartialPath);
public sealed record DeleteResult(bool Deleted, string Message, string? Path);

public sealed partial class DownloadEngine
{
    // ---- adding ---------------------------------------------------------------------------------------------------

    public AddResult Add(AddRequest request)
    {
        AddResult result = AddCore(request);
        FlushOutbox();
        if (result.Status == AddStatus.Added) ScheduleNext();
        return result;
    }

    private AddResult AddCore(AddRequest request)
    {
        if (request.RequestId is { Length: > 0 } requestId && _store.TryGetSubmission(requestId, out Guid earlier))
        {
            lock (_lock)
                if (_jobs.ContainsKey(earlier)) return new AddResult(AddStatus.AlreadySubmitted, earlier, "This request was already accepted.");
        }

        if (!UrlPolicy.TryParse(request.Url, out Uri uri, out string urlError))
            return new AddResult(AddStatus.Invalid, Guid.Empty, urlError);
        if (MediaPageLinks.TryGetSite(uri, out string mediaSite))
            return new AddResult(AddStatus.Invalid, Guid.Empty,
                $"This is a {mediaSite} video page, not a file link. Use Add Download > Find video to discover supported media formats.");
        try { UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return new AddResult(AddStatus.Invalid, Guid.Empty, ex.Message); }

        if (!Enum.IsDefined(request.Kind) || request.Kind == JobKind.BrowserFile)
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Unsupported job kind.");
        if (request.BrowserProof is { } proof && (request.Kind != JobKind.Http || !proof.IsValid
            || !proof.AwaitingConfirmation || request.Output != MediaOutput.Original || request.RequestId is null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Invalid browser handoff.");
        if (!Enum.IsDefined(request.Output) || request.Output != MediaOutput.Original &&
            (request.Kind is not (JobKind.Http or JobKind.Merge) || request.MediaProvider is not { Length: > 0 and <= 80 }
             || !string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.PieceMapJson is not null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Compatible output requires a discovered media download without a file checksum.");
        if (request.Kind == JobKind.Dash && (!DashDownloadPlan.TryParse(request.MediaPlanJson, Options.AllowPrivateNetworks, out _, out string planError)
            || request.Secrets is { Count: > 0 }))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "A valid clear DASH plan without persisted credentials is required.");
        if (request.Kind == JobKind.Merge && (!MediaMergePlan.TryParse(request.MediaPlanJson, Options.AllowPrivateNetworks, out _, out _)
            || request.Secrets is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.PieceMapJson is not null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "A valid media merge plan without credentials or file checksums is required.");

        string? expected = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) && !TryNormalizeSha256(request.ExpectedSha256, out expected))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "A SHA-256 checksum has 64 hexadecimal characters.");
        if (request.PieceMapJson is not null && !PieceMap.TryParse(request.PieceMapJson, out _, out string pieceError))
            return new AddResult(AddStatus.Invalid, Guid.Empty, pieceError);

        // Provenance is display-only; never use it to bypass request URL safety checks.
        string? originalPage = null;
        if (request.OriginalPageUrl is { Length: > 0 and <= 4096 } page &&
            UrlPolicy.TryParse(page, out Uri parsedPage, out _))
            originalPage = parsedPage.AbsoluteUri;

        string name = request.FileName is { Length: > 0 } ? FileNames.Sanitize(request.FileName) : FileNames.FromUrl(uri);
        string category = FileNames.CategoryFor(name);
        string baseFolder = request.DestinationFolder ?? Options.DefaultFolder;
        string folderText = request.DestinationFolder is null && Options.CategorySubfolders ? Path.Combine(baseFolder, category) : baseFolder;
        if (!PathSafety.TryResolveFolder(folderText, create: true, out string folder, out string folderError))
            return new AddResult(AddStatus.DestinationUnavailable, Guid.Empty, folderError);

        lock (_lock)
        {
            // Recheck inside the coordinator lock: simultaneous browser messages
            // carrying the same ID must not create two different jobs.
            if (request.RequestId is { Length: > 0 } submissionId && _store.TryGetSubmission(submissionId, out Guid accepted)
                && _jobs.ContainsKey(accepted))
                return new AddResult(AddStatus.AlreadySubmitted, accepted, "This request was already accepted.");

            string normalized = UrlPolicy.Normalize(uri);
            DownloadJob? existing = _jobs.Values.FirstOrDefault(j => j.Kind != JobKind.BrowserFile
                && j.State is not (JobState.Completed or JobState.Cancelled)
                && Uri.TryCreate(j.SourceUrl, UriKind.Absolute, out Uri? other) && UrlPolicy.Normalize(other) == normalized
                && j.Output == request.Output
                && (request.Kind is not (JobKind.Dash or JobKind.Merge) || j.MediaPlanJson == request.MediaPlanJson));
            if (existing is not null)
                return new AddResult(AddStatus.Duplicate, existing.Id, $"This link is already in your list ({existing.FileName}).");

            var id = Guid.NewGuid();
            string partialName = FileNames.Sanitize(name) + "." + id.ToString("N")[..8] + ".vpart";
            long order = _jobs.Count == 0 ? 0 : request.StartNow ? _jobs.Values.Min(j => j.QueueOrder) - 1 : _jobs.Values.Max(j => j.QueueOrder) + 1;

            var job = new DownloadJob
            {
                Id = id, Kind = request.Kind, SourceUrl = uri.AbsoluteUri, FileName = name, NameLocked = request.FileName is { Length: > 0 },
                DestinationFolder = folder, PartialPath = Path.Combine(folder, partialName), State = JobState.Queued,
                Priority = request.Priority, QueueOrder = order, Category = category, Referer = request.Referer, UserAgent = request.UserAgent,
                ExpectedSha256 = expected, PieceMapJson = request.PieceMapJson, MediaState = request.MediaState,
                OriginalPageUrl = originalPage,
                MediaProvider = request.MediaProvider is { Length: <= 80 } ? request.MediaProvider : null,
                MediaId = request.MediaId is { Length: <= 256 } ? request.MediaId : null,
                MediaFormatId = request.MediaFormatId is { Length: <= 256 } ? request.MediaFormatId : null,
                MediaQuality = request.MediaQuality is { Length: <= 180 } ? request.MediaQuality : null,
                MediaThumbnailUrl = request.MediaThumbnailUrl is { Length: <= 4096 } ? request.MediaThumbnailUrl : null,
                MediaExpiresAt = request.MediaExpiresAt,
                Output = request.Output,
                BrowserProof = request.BrowserProof,
                MediaPlanJson = request.Kind is JobKind.Dash or JobKind.Merge ? request.MediaPlanJson : null,
                TotalBytes = request.PieceMapJson is not null && PieceMap.TryParse(request.PieceMapJson, out PieceMap map, out _) ? map.TotalLength : null
            };
            SaveLocked(job, request.RequestId); // Durably commit before exposing the job in memory.
            _jobs[id] = job;
            if (request.Secrets is { Count: > 0 })
            {
                Uri origin = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "", UserName = "", Password = "" }.Uri;
                _credentials[id] = new RequestCredentials(origin, new Dictionary<string, string>(request.Secrets));
            }
            // "Download" goes to the front of the queue and starts at once if a slot is free; otherwise it waits its turn.
            // Only the explicit "Start now" command may exceed the concurrency limit.
            Changed(job);
            Log(id, "added", UrlPolicy.Redact(job.SourceUrl));
            return new AddResult(AddStatus.Added, id, "");
        }
    }

    internal static bool TryNormalizeSha256(string text, out string normalized)
    {
        normalized = text.Trim().Replace(" ", "").ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    // ---- run control ------------------------------------------------------------------------------------------------

    public void Pause(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return;
            if (_sessions.TryGetValue(id, out JobSession? session)) { session.Intent = StopIntent.Pause; session.Cts.Cancel(); return; }
            if (job.State is JobState.Queued or JobState.WaitingForNetwork or JobState.WaitingForRetry)
            {
                job.State = JobState.Paused;
                job.NextRetryUtc = null;
                job.Message = "";
                SaveLocked(job);
                Changed(job);
            }
        }
        FlushOutbox();
    }

    public void Resume(Guid id) => Requeue(id, startNow: false);

    public void StartNow(Guid id) => Requeue(id, startNow: true);

    private void Requeue(Guid id, bool startNow)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || _sessions.ContainsKey(id)) return;
            if (job.BrowserProof is { AwaitingConfirmation: true }
                || job.State is JobState.Completed or JobState.Suspect or JobState.Verifying or JobState.Finalizing or JobState.Converting) return;
            job.State = JobState.Queued;
            job.NextRetryUtc = null;
            job.RetryCount = 0;
            job.Message = "";
            job.Reason = "";
            if (startNow) { job.QueueOrder = _jobs.Values.Min(j => j.QueueOrder) - 1; _forced.Add(id); }
            SaveLocked(job);
            Changed(job);
        }
        ScheduleNext();
    }

    public void PauseAll()
    {
        foreach (JobSnapshot s in Snapshots().Where(s => s.State is not (JobState.Completed or JobState.Paused or JobState.Suspect
            or JobState.Failed or JobState.Cancelled or JobState.NeedsLink or JobState.NeedsAttention)))
            Pause(s.Id);
    }

    public void ResumeAll()
    {
        foreach (JobSnapshot s in Snapshots().Where(s => s.State == JobState.Paused)) Resume(s.Id);
    }

    /// <summary>The user explicitly accepts resuming from a server that offers no way to check the file is unchanged.</summary>
    public void ApproveUnverifiedResume(Guid id)
    {
        UpdateIfIdle(id, j => j.UnverifiedResumeApproved = true);
        Log(id, "unverified-resume", "User approved resuming without a validator.");
        Resume(id);
    }

    // ---- per-job settings ------------------------------------------------------------------------------------------------

    public void SetPriority(Guid id, JobPriority priority)
    {
        UpdateIfExists(id, j => j.Priority = priority);
        ScheduleNext();
    }

    public void SetSpeedLimit(Guid id, long bytesPerSecond)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return;
            job.SpeedLimitBytes = Math.Max(0, bytesPerSecond);
            _sessions.GetValueOrDefault(id)?.Limiter.SetRate(job.SpeedLimitBytes);
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
    }

    /// <summary>Call after changing <see cref="Options"/>. Applies limits and re-evaluates the queue; running transfers are not disturbed.</summary>
    public void SettingsChanged()
    {
        GlobalLimiter.SetRate(Options.GlobalLimitBytesPerSecond);
        ScheduleNext();
    }

    public void MoveInQueue(Guid id, QueueMove move)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return;
            List<DownloadJob> order = [.. _jobs.Values.Where(j => j.State != JobState.Completed).OrderBy(j => j.QueueOrder)];
            int index = order.IndexOf(job);
            if (index < 0) return;
            switch (move)
            {
                case QueueMove.Top: job.QueueOrder = order[0].QueueOrder - 1; break;
                case QueueMove.Bottom: job.QueueOrder = order[^1].QueueOrder + 1; break;
                case QueueMove.Up when index > 0: (job.QueueOrder, order[index - 1].QueueOrder) = (order[index - 1].QueueOrder, job.QueueOrder); SaveLocked(order[index - 1]); break;
                case QueueMove.Down when index < order.Count - 1: (job.QueueOrder, order[index + 1].QueueOrder) = (order[index + 1].QueueOrder, job.QueueOrder); SaveLocked(order[index + 1]); break;
                default: return;
            }
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
    }

    /// <summary>
    /// Renames the entry. For unfinished downloads this is the name the file will be published under;
    /// for completed ones it only changes the record, never the file on disk.
    /// </summary>
    public void Rename(Guid id, string name) =>
        UpdateIfExists(id, j => { j.FileName = FileNames.Sanitize(name, j.FileName); j.NameLocked = true; });

    // ---- remove / delete / discard — three distinct, explicit operations ---------------------------------------------------

    /// <summary>Removes the record only. A partial file and its recovery file stay on disk and are reported to the caller.</summary>
    public RemoveResult RemoveFromList(Guid id)
    {
        RemoveResult result;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return new RemoveResult(false, "That entry no longer exists.", null);
            if (_sessions.ContainsKey(id)) return new RemoveResult(false, "Pause the download before removing it.", null);

            string? retained = job.State != JobState.Completed && File.Exists(job.PartialPath) ? job.PartialPath : null;
            _store.Delete(id);
            _jobs.Remove(id);
            _credentials.Remove(id);
            _missing.Remove(id);
            _forced.Remove(id);
            Log(id, "removed", "Entry removed from list; no files deleted.");
            result = new RemoveResult(true, retained is null ? "Removed from the list. No files were touched."
                : "Removed from the list. The partially downloaded file was kept.", retained);
        }
        JobRemoved?.Invoke(id);
        ArmWakeTimerLocked();
        return result;
    }

    /// <summary>
    /// Deletes the published file after re-validating it. It refuses links and cloud placeholders, and files whose
    /// size no longer matches what VESPER wrote (the user may have replaced it). Uses <paramref name="remove"/>
    /// (for example the Recycle Bin) when supplied.
    /// </summary>
    public DeleteResult DeleteDownloadedFile(Guid id, Action<string>? remove = null)
    {
        DownloadJob? job;
        lock (_lock) _jobs.TryGetValue(id, out job);
        if (job is null || job.State != JobState.Completed || string.IsNullOrEmpty(job.FinalPath))
            return new DeleteResult(false, "There is no completed file to delete for this entry.", null);
        if (job.Kind == JobKind.BrowserFile)
            return new DeleteResult(false, "Manage this file from Chrome or File Explorer.", job.FinalPath);

        string path = job.FinalPath;
        FileCheck check = PathSafety.Inspect(path);
        if (!check.Exists) return new DeleteResult(false, "The file is already gone. Nothing was deleted.", path);
        if (check.IsDirectory) return new DeleteResult(false, "The recorded path is now a folder. Nothing was deleted.", path);
        if (check.IsReparsePoint || check.IsCloudPlaceholder)
            return new DeleteResult(false, "The file is a link or cloud placeholder. VESPER will not delete it; remove it in File Explorer.", path);
        if (job.TotalBytes is long expected && check.Length != expected)
            return new DeleteResult(false, "The file's size changed after VESPER saved it, so it may no longer be the downloaded file. Nothing was deleted.", path);

        try { (remove ?? File.Delete)(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeleteResult(false, "The file could not be deleted: " + ex.Message, path);
        }
        UpdateIfExists(id, _ => { }, markMissing: true);
        Log(id, "file-deleted", "Downloaded file deleted by user.");
        return new DeleteResult(true, "The file was deleted.", path);
    }

    /// <summary>
    /// Explicitly throws away the partial data of an unfinished job and queues it from byte zero.
    /// The only way partial data is ever deleted; callers must confirm with the user first.
    /// </summary>
    public bool RestartFromScratch(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || _sessions.ContainsKey(id) || job.State == JobState.Completed) return false;
            try
            {
                if (File.Exists(job.PartialPath)) File.Delete(job.PartialPath);
                if (!MergeTransfer.DeleteSidecars(job)) throw new IOException("A merge track file could not be deleted.");
                if (job.Output != MediaOutput.Original
                    && (!DeleteTemporaryFile(job, ConversionPath(job)) || !DeleteTemporaryFile(job, ConversionAudioPath(job))))
                    throw new IOException("A temporary conversion file could not be deleted.");
                RecoveryManifest.Delete(job.PartialPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                job.Message = "The partial file could not be deleted: " + ex.Message;
                SaveLocked(job);
                Changed(job);
                return false;
            }
            job.Extents = new ExtentSet();
            job.TotalBytes = job.PieceMapJson is not null && PieceMap.TryParse(job.PieceMapJson, out PieceMap map, out _) ? map.TotalLength : null;
            (job.ETag, job.LastModified, job.Identity, job.AcceptsRanges, job.NonResumable) = (null, null, IdentityLevel.None, false, false);
            (job.State, job.Reason, job.Message, job.RetryCount, job.MediaState, job.RescueCandidate) = (JobState.Queued, "", "", 0, null, false);
            ClearPendingPublication(job);
            SaveLocked(job);
            Log(id, "restart", "Partial data discarded at the user's request.");
            Changed(job);
        }
        ScheduleNext();
        return true;
    }

    /// <summary>For a completed entry whose file moved: point the record at the file the user located, if it plausibly matches.</summary>
    public DeleteResult LocateFile(Guid id, string path)
    {
        FileCheck check = PathSafety.Inspect(path);
        DownloadJob? job;
        lock (_lock) _jobs.TryGetValue(id, out job);
        if (job is null || job.State != JobState.Completed) return new DeleteResult(false, "This entry is not a completed download.", null);
        if (!check.Exists || check.IsDirectory) return new DeleteResult(false, "That file does not exist.", path);
        if (job.TotalBytes is long expected && check.Length != expected)
            return new DeleteResult(false, $"That file is {Formatting.Bytes(check.Length)}, but the download was {Formatting.Bytes(expected)}. It is probably a different file.", path);

        UpdateIfExists(id, j => j.FinalPath = Path.GetFullPath(path), markMissing: false);
        return new DeleteResult(true, "The entry now points to that file.", path);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private void UpdateIfExists(Guid id, Action<DownloadJob> mutate, bool? markMissing = null)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return;
            mutate(job);
            if (markMissing is true) _missing.Add(id);
            if (markMissing is false) _missing.Remove(id);
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
    }

    private void UpdateIfIdle(Guid id, Action<DownloadJob> mutate)
    {
        lock (_lock)
            if (_sessions.ContainsKey(id)) return;
        UpdateIfExists(id, mutate);
    }

    private void ArmWakeTimerLocked()
    {
        lock (_lock) ArmWakeTimer();
    }
}

/// <summary>Redaction for anything that may be exported or shown in diagnostics.</summary>
public static class Diagnostics
{
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (string token in text.Split(' '))
        {
            string part = token;
            if (part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || part.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                part = UrlPolicy.Redact(part);
            builder.Append(part).Append(' ');
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string scrubbed = builder.ToString().TrimEnd();
        return profile.Length > 3 ? scrubbed.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : scrubbed;
    }

    /// <summary>Writes a privacy-aware log: links lose their query strings, the profile path is abbreviated.</summary>
    public static void ExportLog(DownloadEngine engine, string path)
    {
        IReadOnlyList<string> lines = engine.ReadLog();
        File.WriteAllLines(path, lines.Select(Scrub));
    }
}
