using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    BrowserTransferProof? BrowserProof = null,
    string? MediaMetadataJson = null,
    bool InitiallyPaused = false);

public sealed record AddResult(AddStatus Status, Guid JobId, string Message, bool StartDeferred = false)
{
    public bool Ok => Status is AddStatus.Added or AddStatus.AlreadySubmitted;
}

public sealed record RemoveResult(bool Removed, string Message, string? RetainedPartialPath);
public sealed record DeleteResult(bool Deleted, string Message, string? Path);
public sealed record OpenCompletedFileResult(CompletedFileOpenLease? Lease, string Message);

/// <summary>Keeps the verified completed file at its saved path through the shell handoff.</summary>
public sealed class CompletedFileOpenLease : IDisposable
{
    private readonly FileStream _file;
    private Action? _release;

    internal CompletedFileOpenLease(FileStream file, Action release)
    {
        _file = file;
        _release = release;
    }

    public void Dispose()
    {
        try { _file.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}

public sealed partial class DownloadEngine
{
    private readonly HashSet<Guid> _completedFileActions = [];

    // adding

    public AddResult Add(AddRequest request)
    {
        AddResult result;
        try { result = AddCore(request); }
        finally { FlushOutbox(); }
        if (result.Status == AddStatus.Added)
        {
            try { ScheduleNext(); }
            catch (LocalStorageException ex)
            {
                bool startDeferred;
                lock (_lock)
                {
                    startDeferred = _jobs.TryGetValue(result.JobId, out DownloadJob? job)
                        && job.State == JobState.Queued && !_sessions.ContainsKey(result.JobId);
                    if (!_disposed && !_crashed)
                    {
                        Log(startDeferred ? result.JobId : null,
                            startDeferred ? "add-start-failed" : "add-scheduler-failed", ex.Message);
                        RetrySchedulingSoonLocked();
                    }
                }
                FlushOutbox();
                if (startDeferred)
                {
                    return result with
                    {
                        Message = "Added to the queue. Starting will retry when storage is available.",
                        StartDeferred = true
                    };
                }
            }
        }
        return result;
    }

    /// <summary>Read-only receipt for a durable, already accepted submission. Never starts another job.</summary>
    public Guid? FindAcceptedSubmission(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256) return null;
        lock (_lock)
            return _store.TryGetSubmission(requestId, out Guid id) && _jobs.ContainsKey(id) ? id : null;
    }

    private AddResult AddCore(AddRequest request)
    {
        using var operation = EnterOperation();
        if (request.RequestId is { Length: > 0 } requestId && _store.TryGetSubmission(requestId, out Guid earlier))
        {
            lock (_lock)
                if (_jobs.ContainsKey(earlier)) return new AddResult(AddStatus.AlreadySubmitted, earlier, "This request was already accepted.");
        }

        if (!UrlPolicy.TryParse(request.Url, out Uri uri, out string urlError))
            return new AddResult(AddStatus.Invalid, Guid.Empty, urlError);
        if (MediaPageLinks.TryGetSite(uri, out string mediaSite))
            return new AddResult(AddStatus.Invalid, Guid.Empty,
                $"This is a {mediaSite} media page. In Add download, choose Find media.");
        try { UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return new AddResult(AddStatus.Invalid, Guid.Empty, ex.Message); }

        if (!Enum.IsDefined(request.Kind) || request.Kind is JobKind.BrowserFile or JobKind.BrowserStream)
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Unsupported job kind.");
        if (request.BrowserProof is { } proof && (request.Kind != JobKind.Http || !proof.IsValid
            || !proof.AwaitingConfirmation || request.Output != MediaOutput.Original || request.RequestId is null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Invalid browser handoff.");
        if (!Enum.IsDefined(request.Output) || request.Output != MediaOutput.Original &&
            (request.Kind is not (JobKind.Http or JobKind.Merge) || request.MediaProvider is not { Length: > 0 and <= 80 }
             || !string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.PieceMapJson is not null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Compatible output requires a discovered media download without a file checksum.");
        if (request.Kind == JobKind.Dash && !DashDownloadPlan.TryParse(request.MediaPlanJson, Options.AllowPrivateNetworks, out _, out _))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "A valid clear DASH plan is required.");
        RequestCredentials? mergeCredentials = null;
        if (request.Kind == JobKind.Merge)
        {
            if (!MediaMergePlan.TryParse(request.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan plan, out string mergeError))
                return new AddResult(AddStatus.Invalid, Guid.Empty, mergeError);
            if (!plan.IsSegmented && plan.ClipRange is not null && request.Output != MediaOutput.Original)
                return new AddResult(AddStatus.Invalid, Guid.Empty, "Local MP4 trimming keeps the selected MP4 tracks as they are.");
            if (request.Output == MediaOutput.Mp3 && plan.OutputRange is not null)
                return new AddResult(AddStatus.Invalid, Guid.Empty, "MP3 output does not support audio clips.");
            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.PieceMapJson is not null)
                return new AddResult(AddStatus.Invalid, Guid.Empty, "A media merge plan cannot use a file checksum or piece map.");
            if (request.Secrets is { Count: > 0 }
                && !TryCreateMergeCredentials(plan, uri, request.Secrets, out mergeCredentials, out string credentialError, allowSegmented: true))
                return new AddResult(AddStatus.Invalid, Guid.Empty, credentialError);
        }

        bool webmClipPlan = DirectMediaPlan.TryWebmClip(request.MediaPlanJson, out _);
        if (webmClipPlan &&
            (request.Kind != JobKind.Http || request.Output != MediaOutput.Original || request.MediaProvider != "generic"
            || !string.Equals(Path.GetExtension(request.FileName ?? uri.AbsolutePath), ".webm", StringComparison.OrdinalIgnoreCase)
            || request.ExpectedSha256 is not null || request.PieceMapJson is not null || request.BrowserProof is not null
            || request.MediaMetadataJson is not null))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "WebM trimming requires an original standalone WebM download.");
        if (request.Kind == JobKind.Http && request.Output == MediaOutput.Original && request.MediaProvider == "generic"
            && string.Equals(Path.GetExtension(request.FileName ?? uri.AbsolutePath), ".webm", StringComparison.OrdinalIgnoreCase)
            && request.MediaPlanJson is not null && request.MediaPlanJson != DirectMediaPlan.AudioIfPresent && !webmClipPlan)
            return new AddResult(AddStatus.Invalid, Guid.Empty, "Invalid standalone WebM media plan.");
        bool requestedAudioIfPresentPlan = string.Equals(request.MediaPlanJson,
            DirectMediaPlan.AudioIfPresent, StringComparison.Ordinal);
        bool audioIfPresentPlan = requestedAudioIfPresentPlan && request.Kind == JobKind.Http
            && request.Output == MediaOutput.Original
            && string.Equals(request.MediaProvider, "generic", StringComparison.Ordinal);
        if (requestedAudioIfPresentPlan && !audioIfPresentPlan)
            return new AddResult(AddStatus.Invalid, Guid.Empty, "The audio-optional plan requires an original generic media download.");
        if (request.Kind == JobKind.Http && request.Output == MediaOutput.Wav && request.MediaPlanJson is not null
            && !MediaTimeRange.TryParse(request.MediaPlanJson, out _))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "The requested WAV range is invalid.");
        if (request.Kind == JobKind.Http && request.Output == MediaOutput.M4a && request.MediaPlanJson is not null
            && !MediaTimeRange.TryParse(request.MediaPlanJson, out _))
            return new AddResult(AddStatus.Invalid, Guid.Empty, "The requested M4A range is invalid.");
        if (request.Output == MediaOutput.Mp3 && request.Kind == JobKind.Http && request.MediaPlanJson is not null)
            return new AddResult(AddStatus.Invalid, Guid.Empty, "MP3 output does not support audio clips.");
        MediaMergeMetadata? mediaMetadata = null;
        if (request.MediaMetadataJson is not null)
        {
            // An original file is tagged in its own format. A converted file becomes M4A/MP4, which always takes tags.
            bool canCarryTags = request.Output != MediaOutput.Original
                || MediaMergePlan.SupportsAudioMetadataFile(request.FileName ?? uri.AbsolutePath);
            if (request.Kind != JobKind.Http || request.MediaProvider is not { Length: > 0 and <= 80 }
                || request.BrowserProof is not null || request.Secrets is { Count: > 0 }
                || request.ExpectedSha256 is not null || request.PieceMapJson is not null || request.MediaPlanJson is not null && request.Output != MediaOutput.Wav
                || !canCarryTags
                || !MediaMergePlan.TryParseMetadata(request.MediaMetadataJson, out mediaMetadata)
                || (mediaMetadata is not null && !MediaMergePlan.HasAudioTagValues(mediaMetadata)
                    && string.IsNullOrWhiteSpace(request.MediaThumbnailUrl)))
                return new AddResult(AddStatus.Invalid, Guid.Empty, "Embedded audio metadata requires a discovered MP3, FLAC, Ogg or Opus download, or converted M4A/MP3/MP4 output, without credentials or a file checksum.");
        }

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

        string suggestedName = request.FileName is { Length: > 0 } ? FileNames.Sanitize(request.FileName) : FileNames.FromUrl(uri);
        string name = FileNames.ApplyTemplate(suggestedName, Options.FileNameTemplate, uri);
        string category = FileNames.CategoryFor(name);
        string baseFolder = request.DestinationFolder ?? Options.DefaultFolder;
        string folderText = request.DestinationFolder is null && Options.CategorySubfolders ? Path.Combine(baseFolder, category) : baseFolder;
        if (!PathSafety.TryResolveFolder(folderText, create: true, out string folder, out string folderError)
            || !PathSafety.TryCheckUnlinkedDirectory(folder, out folderError))
            return new AddResult(AddStatus.DestinationUnavailable, Guid.Empty, folderError);
        folder = Path.TrimEndingDirectorySeparator(folder);

        lock (_lock)
        {
            // Recheck inside the coordinator lock: simultaneous browser messages
            // carrying the same ID must not create two different jobs.
            if (request.RequestId is { Length: > 0 } submissionId && _store.TryGetSubmission(submissionId, out Guid accepted)
                && _jobs.ContainsKey(accepted))
                return new AddResult(AddStatus.AlreadySubmitted, accepted, "This request was already accepted.");

            string normalized = UrlPolicy.Normalize(uri);
            var duplicateKey = new DuplicateKey(normalized, request.Output);
            DownloadJob? existing = null;
            if (_duplicateCandidates.TryGetValue(duplicateKey, out List<DownloadJob>? candidates))
            {
                for (int i = 0; i < candidates.Count;)
                {
                    DownloadJob candidate = candidates[i];
                    if (candidate.State is JobState.Completed or JobState.Cancelled)
                    {
                        candidates.RemoveAt(i);
                        continue;
                    }

                    if (existing is null && SameRequestPlan(candidate, request))
                        existing = candidate;
                    i++;
                }

                if (candidates.Count == 0) _duplicateCandidates.Remove(duplicateKey);
            }
            if (existing is not null)
                return new AddResult(AddStatus.Duplicate, existing.Id, $"This link is already in your list ({existing.FileName}).");

            if (_jobs.Count > 0)
            {
                long boundary = request.StartNow
                    ? _jobs.Values.Min(j => j.QueueOrder)
                    : _jobs.Values.Max(j => j.QueueOrder);
                if (request.StartNow ? boundary == long.MinValue : boundary == long.MaxValue)
                    RebaseQueueOrdersLocked();
            }

            var id = Guid.NewGuid();
            string partialName = FileNames.Sanitize(name) + "." + id.ToString("N")[..8] + ".vpart";
            string stagingFolder = PartialFile.PrepareStagingFolder(folder);
            long order = _jobs.Count == 0 ? 0 : request.StartNow ? _jobs.Values.Min(j => j.QueueOrder) - 1 : _jobs.Values.Max(j => j.QueueOrder) + 1;

            var job = new DownloadJob
            {
                Id = id, Kind = request.Kind, SourceUrl = uri.AbsoluteUri, FileName = name, NameLocked = request.FileName is { Length: > 0 },
                DestinationFolder = folder, PartialPath = Path.Combine(stagingFolder, partialName),
                State = request.InitiallyPaused ? JobState.Paused : JobState.Queued,
                Priority = request.Priority, QueueOrder = order, Category = category, Referer = request.Referer, UserAgent = request.UserAgent,
                ExpectedSha256 = expected, PieceMapJson = request.PieceMapJson, MediaState = request.MediaState,
                OriginalPageUrl = originalPage,
                MediaProvider = request.MediaProvider is { Length: <= 80 } ? request.MediaProvider : null,
                MediaId = request.MediaId is { Length: <= 256 } ? request.MediaId : null,
                MediaFormatId = request.MediaFormatId is { Length: <= 256 } ? request.MediaFormatId : null,
                MediaQuality = request.MediaQuality is { Length: <= 180 } ? request.MediaQuality : null,
                MediaThumbnailUrl = request.MediaThumbnailUrl is { Length: <= 4096 } ? request.MediaThumbnailUrl : null,
                MediaMetadataJson = mediaMetadata is null ? null : JsonSerializer.Serialize(mediaMetadata),
                MediaExpiresAt = request.MediaExpiresAt,
                Output = request.Output,
                BrowserProof = request.BrowserProof,
                MediaPlanJson = request.Kind is JobKind.Hls or JobKind.Dash or JobKind.Merge || request.Output is MediaOutput.Wav or MediaOutput.M4a || audioIfPresentPlan || webmClipPlan
                    ? request.MediaPlanJson : null,
                TotalBytes = request.PieceMapJson is not null && PieceMap.TryParse(request.PieceMapJson, out PieceMap map, out _) ? map.TotalLength : null
            };
            SaveLocked(job, request.RequestId); // Durably commit before exposing the job in memory.
            _jobs[id] = job;
            AddDuplicateCandidateLocked(job);
            if (request.Kind == JobKind.Merge)
            {
                if (mergeCredentials is not null) _credentials[id] = mergeCredentials;
            }
            else if (request.Secrets is { Count: > 0 })
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

    private bool SameRequestPlan(DownloadJob existing, AddRequest request)
    {
        if (existing.Kind != request.Kind || existing.Output != request.Output) return false;
        if (request.Kind == JobKind.Merge)
            return MediaMergePlan.TryParse(existing.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan previous, out _)
                && MediaMergePlan.TryParse(request.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan next, out _)
                && previous.Fingerprint == next.Fingerprint;

        if (request.Kind == JobKind.Http && request.Output is (MediaOutput.Wav or MediaOutput.M4a))
            return SameTimeRange(existing.MediaPlanJson, request.MediaPlanJson);

        bool previousWebmClip = DirectMediaPlan.TryWebmClip(existing.MediaPlanJson, out MediaTimeRange previousRange);
        bool requestedWebmClip = DirectMediaPlan.TryWebmClip(request.MediaPlanJson, out MediaTimeRange requestedRange);
        if (previousWebmClip || requestedWebmClip)
            return previousWebmClip && requestedWebmClip && previousRange == requestedRange;

        return existing.MediaPlanJson == request.MediaPlanJson;
    }

    private static bool SameTimeRange(string? previousJson, string? requestedJson)
    {
        if (previousJson is null || requestedJson is null) return previousJson == requestedJson;
        return MediaTimeRange.TryParse(previousJson, out MediaTimeRange previous)
            && MediaTimeRange.TryParse(requestedJson, out MediaTimeRange requested)
            && previous == requested;
    }

    // run control

    public void Pause(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || _cancelling.Contains(id) || IsCleanupPending(job)) return;
            if (_sessions.TryGetValue(id, out JobSession? session))
            {
                if (session.Intent is StopIntent.Cancel or StopIntent.LiveStop or StopIntent.BrowserFallback or StopIntent.Shutdown) return;
                session.Intent = StopIntent.Pause;
                session.Cts.Cancel();
                return;
            }
            if (job.State is JobState.Queued or JobState.WaitingForNetwork or JobState.WaitingForRetry)
            {
                PersistUiMutationLocked(job, static pending =>
                {
                    pending.State = JobState.Paused;
                    pending.NextRetryUtc = null;
                    pending.Message = "";
                });
                Changed(job);
            }
        }
        FlushOutbox();
    }

    /// <summary>Stops an HLS live capture at its last committed segment and keeps the playable recording.</summary>
    public bool StopAndKeepLive(Guid id)
    {
        bool queued = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)
                || job.State is JobState.Completed or JobState.Finalizing or JobState.Verifying or JobState.Converting or JobState.Suspect
                || _cancelling.Contains(id) || IsCleanupPending(job) || job.PendingFinalPath is not null || job.Kind != JobKind.Hls
                || !HlsTransfer.TryRequestLiveStop(job.MediaState, out string stopState))
                return false;

            if (_sessions.TryGetValue(id, out JobSession? session))
            {
                if (session.Intent != StopIntent.None) return false;
                // Do not cancel the runner or mark a stop intent until SQLite accepts it.
                PersistUiMutationLocked(job, pending =>
                {
                    pending.MediaState = stopState;
                    pending.Message = "Finishing the recording";
                });
                session.Intent = StopIntent.LiveStop;
                Changed(job);
                session.Cts.Cancel();
            }
            else
            {
                PersistUiMutationLocked(job, pending =>
                {
                    pending.MediaState = stopState;
                    pending.State = JobState.Queued;
                    pending.NextRetryUtc = null;
                    pending.Message = "Finishing the recording";
                });
                AddDuplicateCandidateLocked(job);
                Changed(job);
                queued = true;
            }
        }
        if (queued) ScheduleNext();
        FlushOutbox();
        return true;
    }
    public void Resume(Guid id) => Requeue(id, startNow: false);

    public void StartNow(Guid id) => Requeue(id, startNow: true);

    private void Requeue(Guid id, bool startNow)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || _sessions.ContainsKey(id) || _cancelling.Contains(id)) return;
            if (job.BrowserProof is { AwaitingConfirmation: true } || IsCleanupPending(job)
                || job.Reason == "recovery-integrity-missing") return;

            if (job.PendingFinalPath is not null)
            {
                ReconcileFinalizing(job);
            }
            else
            {
                if (job.State is JobState.Completed or JobState.Suspect or JobState.Verifying or JobState.Finalizing or JobState.Converting) return;
                if (startNow && _jobs.Values.Min(j => j.QueueOrder) == long.MinValue)
                    RebaseQueueOrdersLocked();
                long queueOrder = startNow ? _jobs.Values.Min(j => j.QueueOrder) - 1 : job.QueueOrder;
                PersistUiMutationLocked(job, pending =>
                {
                    pending.State = JobState.Queued;
                    pending.NextRetryUtc = null;
                    pending.RetryCount = 0;
                    pending.Message = "";
                    pending.Reason = "";
                    if (pending.BrowserProof is { AdoptionReleased: true }) pending.BrowserProof = null;
                    if (startNow) pending.QueueOrder = queueOrder;
                });
                AddDuplicateCandidateLocked(job);
            }
            if (startNow && job.State == JobState.Queued) _forced.Add(id);
            Changed(job);
        }
        ScheduleNext();
    }

    // Leave room at both ends so adding or starting a job cannot wrap a signed queue order.
    // The store commits the whole rebase before any live job is changed.
    private void NormalizeQueueOrdersOnStartup()
    {
        lock (_lock)
        {
            DownloadJob[] ordered = [.. _jobs.Values.OrderBy(j => j.QueueOrder)
                .ThenBy(j => j.CreatedUtc).ThenBy(j => j.Id)];
            if (ordered.Length < 2) return;

            bool duplicateOrders = ordered.Select(j => j.QueueOrder).Distinct().Count() != ordered.Length;
            bool atBoundary = ordered[0].QueueOrder == long.MinValue || ordered[^1].QueueOrder == long.MaxValue;
            if (!duplicateOrders && !atBoundary) return;

            RebaseQueueOrdersLocked(publishChanges: false);
        }
    }

    private void RebaseQueueOrdersLocked(bool publishChanges = true)
    {
        DownloadJob[] ordered = [.. _jobs.Values.OrderBy(j => j.QueueOrder)
            .ThenBy(j => j.CreatedUtc).ThenBy(j => j.Id)];
        var changes = new (Guid JobId, long Order)[ordered.Length];
        for (int i = 0; i < ordered.Length; i++) changes[i] = (ordered[i].Id, i);

        try { _store.SaveQueueOrders(changes); }
        catch (SqliteException ex)
        {
            throw new LocalStorageException("The queue order could not be saved: " + ex.Message, ex);
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            ordered[i].QueueOrder = changes[i].Order;
            if (publishChanges) Changed(ordered[i]);
        }
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

    // per-job settings

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
            long limit = Math.Max(0, bytesPerSecond);
            PersistUiMutationLocked(job, pending => pending.SpeedLimitBytes = limit);
            _sessions.GetValueOrDefault(id)?.Limiter.SetRate(limit);
            Changed(job);
        }
        FlushOutbox();
    }

    /// <summary>Call after changing <see cref="Options"/>. Applies limits and re-evaluates the queue; network policy may checkpoint running work.</summary>
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

            DownloadJob? neighbor = null;
            long position;
            switch (move)
            {
                case QueueMove.Top:
                    if (order[0].QueueOrder == long.MinValue)
                        throw new InvalidOperationException("The queue cannot move further toward the top.");
                    position = order[0].QueueOrder - 1;
                    break;
                case QueueMove.Bottom:
                    if (order[^1].QueueOrder == long.MaxValue)
                        throw new InvalidOperationException("The queue cannot move further toward the bottom.");
                    position = order[^1].QueueOrder + 1;
                    break;
                case QueueMove.Up when index > 0:
                    neighbor = order[index - 1];
                    position = neighbor.QueueOrder;
                    break;
                case QueueMove.Down when index < order.Count - 1:
                    neighbor = order[index + 1];
                    position = neighbor.QueueOrder;
                    break;
                default: return;
            }

            long neighborPosition = job.QueueOrder;
            try
            {
                // The neighbor is saved first to expose and roll back a refused second write
                // in the same SQLite transaction, never as a separately committed row.
                if (neighbor is null)
                    _store.SaveQueueOrders([(job.Id, position)]);
                else
                    _store.SaveQueueOrders([(neighbor.Id, neighborPosition), (job.Id, position)]);
            }
            catch (SqliteException ex)
            {
                throw new LocalStorageException("The queue order could not be saved: " + ex.Message, ex);
            }

            if (neighbor is not null)
            {
                neighbor.QueueOrder = neighborPosition;
                Changed(neighbor);
            }
            job.QueueOrder = position;
            Changed(job);
        }
        FlushOutbox();
    }

    /// <summary>
    /// Renames the entry. For unfinished downloads this is the name the file will be published under;
    /// for completed ones it only changes the record, never the file on disk.
    /// </summary>
    public void Rename(Guid id, string name) => UpdateIfExists(id, job =>
    {
        string sanitized = FileNames.Sanitize(name, job.FileName);
        string sourceExtension = Path.GetExtension(job.FileName);
        bool sourceIsTaggedAudio = job.MediaProvider is not null && MediaMergePlan.SupportsAudioMetadataFile(job.FileName);
        if (sourceIsTaggedAudio && !string.Equals(Path.GetExtension(sanitized), sourceExtension, StringComparison.OrdinalIgnoreCase))
        {
            sanitized = Path.ChangeExtension(sanitized, sourceExtension);
            job.Message = $"This discovered audio uses {sourceExtension}, so its original extension was kept.";
        }
        job.FileName = sanitized;
        job.NameLocked = true;
    });

    // remove / delete / discard — three distinct, explicit operations

    /// <summary>Removes the record only. A partial file and its recovery file stay on disk and are reported to the caller.</summary>
    public RemoveResult RemoveFromList(Guid id)
    {
        RemoveResult result;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return new RemoveResult(false, "That entry no longer exists.", null);
            if (_sessions.ContainsKey(id) || _cancelling.Contains(id)) return new RemoveResult(false, "This download is still stopping.", null);
            if (_completedFileActions.Contains(id)) return new RemoveResult(false, "Wait for the file action to finish.", null);
            if (IsCleanupPending(job)) return new RemoveResult(false, "Finish cleaning up this download before removing it.", null);
            if (job.PendingFinalPath is not null) return new RemoveResult(false, "This download is still being saved.", null);

            string? retained = job.State != JobState.Completed && File.Exists(job.PartialPath) ? job.PartialPath : null;
            _store.Delete(id);
            RemoveDuplicateCandidateLocked(job, job.SourceUrl);
            _jobs.Remove(id);
            _mediaDeliveryHistory.Forget(id);
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

    public Task<DeleteResult> DeleteDownloadedFileAsync(Guid id, Action<string>? remove = null, string? confirmedPath = null) =>
        ChangeCompletedFileAsync(id, locatePath: null, remove, confirmedPath);

    /// <summary>
    /// Explicitly discards only this job's unfinished data. Persist a non-running recovery
    /// state BEFORE deleting anything, so a crash or failed SQLite save cannot resume a
    /// now-missing partial under stale extents. The user must confirm this action first.
    /// </summary>
    public bool RestartFromScratch(Guid id)
    {
        bool restarted = false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || _sessions.ContainsKey(id)
                || job.Kind == JobKind.BrowserStream || _cancelling.Contains(id) || !CanDiscardPartial(job))
                return false;

            DownloadJob preparing = job.CopyForUpdate();
            preparing.State = JobState.NeedsAttention;
            preparing.Reason = RestartCleanup;
            preparing.Message = "Restart is unfinished. Choose Start over to retry, or Cancel download to remove the entry.";
            preparing.NextRetryUtc = null;
            SaveLocked(preparing); // If this fails, no file has been touched.
            job.ApplyPersistedState(preparing);

            // The queued job is now safely parked even if deleting files or the next
            // database update fails, or Windows terminates the process mid-cleanup.
            if (TryRemoveCancelledFiles(job, out string error))
            {
                DownloadJob reset = job.CopyForUpdate();
                reset.Extents = new ExtentSet();
                reset.PartialIdentity = null;
                reset.FinalResourceIdentity = null;
                reset.TotalBytes = reset.PieceMapJson is not null
                    && PieceMap.TryParse(reset.PieceMapJson, out PieceMap map, out _) ? map.TotalLength : null;
                (reset.ETag, reset.LastModified, reset.Identity, reset.AcceptsRanges, reset.NonResumable) =
                    (null, null, IdentityLevel.None, false, false);
                (reset.State, reset.Reason, reset.Message, reset.RetryCount, reset.MediaState, reset.RescueCandidate) =
                    (JobState.Queued, "", "", 0, null, false);
                reset.Sha256Verified = false;
                reset.UnverifiedResumeApproved = false;
                reset.NextRetryUtc = null;
                ClearPendingPublication(reset);
                try
                {
                    SaveLocked(reset);
                    job.ApplyPersistedState(reset);
                    AddDuplicateCandidateLocked(job);
                    Log(id, "restart", "Partial data discarded at the user's request.");
                    restarted = true;
                }
                catch (LocalStorageException ex)
                {
                    // The persisted restart-cleanup marker is still authoritative.
                    // On restart, a missing partial must never be resumed using old extents.
                    Log(id, "restart-pending", ex.Message);
                }
            }
            else
            {
                Log(id, "restart-pending", error);
                PersistUiMutationLocked(job, pending => pending.Message = "Could not remove download data: " + error);
            }
            Changed(job);
        }
        FlushOutbox();
        if (restarted) ScheduleNext();
        return restarted;
    }

    public Task<DeleteResult> LocateFileAsync(Guid id, string path) =>
        string.IsNullOrWhiteSpace(path)
            ? Task.FromResult(new DeleteResult(false, "Choose the file to locate.", path))
            : ChangeCompletedFileAsync(id, path, remove: null, confirmedPath: null);

    public async Task<OpenCompletedFileResult> OpenCompletedFileAsync(Guid id, string? confirmedPath,
        CancellationToken ct = default)
    {
        using var operation = EnterOperation(ct);
        ct = operation.Token;
        string path;
        string digest;
        string? savedIdentity;
        long length;
        lock (_lock)
        {
            if (_disposed) return new(null, "VESPER is closing.");
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || job.State != JobState.Completed || job.FinalPath is null)
                return new(null, "This entry has no completed file to open.");
            if (job.Kind == JobKind.BrowserFile)
                return new(null, "This file is owned by Chrome. Open it from Chrome downloads.");
            if (!SameCompletedPath(job.FinalPath, confirmedPath))
                return new(null, "The file location changed. Open the action again to confirm the current file.");
            if (job.PublishedSha256 is not { Length: 64 } proof || job.TotalBytes is not long bytes || bytes < 0)
                return new(null, "VESPER has no saved file proof for this entry. Manage it in File Explorer.");
            if (!_completedFileActions.Add(id))
                return new(null, "Another file action is in progress for this entry.");

            path = job.FinalPath;
            digest = proof;
            savedIdentity = job.PublishedFileIdentity;
            length = bytes;
        }

        FileStream? file = null;
        bool keepAction = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(path))
                return new(null, "The saved file path is not valid.");
            path = Path.GetFullPath(path);
            if (!IsSafeCompletedPath(path, length))
                return new(null, "The saved file is missing, changed size, or uses an unsafe path.");

            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != length)
                return new(null, "The saved file no longer matches the completed download.");

            string? currentIdentity = TryGetPublishedFileIdentity(file.SafeFileHandle);
            if (OperatingSystem.IsWindows() && currentIdentity is null)
                return new(null, "VESPER could not verify this file's saved Windows identity. Check it in File Explorer.");

            if (savedIdentity is not null)
            {
                if (currentIdentity is null)
                    return new(null, "VESPER could not verify the file's saved identity. Check it in File Explorer.");
                if (!string.Equals(savedIdentity, currentIdentity, StringComparison.Ordinal))
                    return new(null, "A different file now occupies the saved path. The entry was kept unchanged.");
            }
            else
            {
                file.Position = 0;
                string currentDigest = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
                if (!currentDigest.Equals(digest, StringComparison.OrdinalIgnoreCase))
                    return new(null, "The saved file does not match the completed download. The entry was kept unchanged.");
                file.Position = 0;

                if (currentIdentity is not null)
                {
                    lock (_lock)
                    {
                        if (_disposed || !_jobs.TryGetValue(id, out DownloadJob? currentJob)
                            || currentJob.State != JobState.Completed || !SameCompletedPath(currentJob.FinalPath, path))
                            return new(null, "The completed entry changed while VESPER checked the file.");
                        if (currentJob.PublishedFileIdentity is not null
                            && !string.Equals(currentJob.PublishedFileIdentity, currentIdentity, StringComparison.Ordinal))
                            return new(null, "A different file now occupies the saved path. The entry was kept unchanged.");

                        PersistUiMutationLocked(currentJob, pending => pending.PublishedFileIdentity = currentIdentity);
                        Changed(currentJob);
                    }
                    FlushOutbox();
                }
            }

            if (!IsSafeCompletedPath(path, length) || !PathSafety.PathStillNamesFile(path, file, digest))
                return new(null, "VESPER could not confirm the saved file path. Check it in File Explorer.");

            lock (_lock)
            {
                if (_disposed || !_jobs.TryGetValue(id, out DownloadJob? currentJob)
                    || currentJob.State != JobState.Completed || !SameCompletedPath(currentJob.FinalPath, path)
                    || !_completedFileActions.Contains(id))
                    return new(null, "The completed entry changed while VESPER checked the file.");
            }

            var lease = new CompletedFileOpenLease(file, () => ReleaseCompletedFileAction(id));
            file = null;
            keepAction = true;
            return new(lease, "");
        }
        catch (OperationCanceledException)
        {
            return new(null, "Opening the completed file was cancelled.");
        }
        catch (LocalStorageException ex)
        {
            return new(null, "VESPER could not save the file identity: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return new(null, "VESPER could not safely open this file: " + ex.Message);
        }
        finally
        {
            file?.Dispose();
            if (!keepAction) ReleaseCompletedFileAction(id);
        }
    }

    private async Task<DeleteResult> ChangeCompletedFileAsync(Guid id, string? locatePath, Action<string>? remove, string? confirmedPath)
    {
        using var operation = EnterOperation();
        bool deleting = locatePath is null;
        string path;
        string digest;
        long length;
        lock (_lock)
        {
            if (_disposed) return new(false, "VESPER is closing.", null);
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || job.State != JobState.Completed || job.FinalPath is null)
                return new(false, "This entry has no completed file.", null);
            if (job.Kind == JobKind.BrowserFile)
                return new(false, "Manage this file from Chrome or File Explorer.", job.FinalPath);
            if (confirmedPath is not null && confirmedPath != job.FinalPath)
                return new(false, "The file location changed. Open the action again to confirm the new path.", job.FinalPath);
            path = locatePath ?? job.FinalPath;
            if (job.PublishedSha256 is not { Length: 64 } proof || job.TotalBytes is not long bytes)
                return new(false, "VESPER has no saved checksum for this entry. Manage the file in File Explorer.", path);
            digest = proof;
            length = bytes;
            if (!_completedFileActions.Add(id))
                return new(false, "Another file action is in progress for this entry.", path);
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return new(false, "Choose a file with a full path.", path);
            path = Path.GetFullPath(path);
            if (!IsSafeCompletedPath(path, length))
                return new(false, "The file is missing, changed size, or uses an unsafe path.", path);

            FileShare share = deleting ? FileShare.Read | FileShare.Delete : FileShare.Read;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, share, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != length
                || !Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                return new(false, "The file does not match the saved download.", path);

            lock (_lock)
            {
                if (_disposed) return new(false, "VESPER is closing.", path);
            }
            Failpoint("completed-file.afterHash");
            if (!IsSafeCompletedPath(path, length) || !PathSafety.PathStillNamesFile(path, file, digest))
                return new(false, "VESPER could not confirm the file path. Check it in File Explorer.", path);

            if (!deleting)
            {
                string? fileIdentity = TryGetPublishedFileIdentity(file.SafeFileHandle);
                lock (_lock)
                {
                    if (_disposed) return new(false, "VESPER is closing.", path);
                    DownloadJob job = _jobs[id];
                    PersistUiMutationLocked(job, pending =>
                    {
                        pending.FinalPath = path;
                        pending.PublishedFileIdentity = fileIdentity;
                    });
                    _missing.Remove(id);
                    Changed(job);
                }
                FlushOutbox();
                return new(true, "The entry now points to that file.", path);
            }

            (remove ?? File.Delete)(path);
            file.Dispose();
            if (File.Exists(path))
                return new(false, "The file is still at the saved path. Check it in File Explorer.", path);
            lock (_lock)
            {
                if (!_disposed)
                {
                    _missing.Add(id);
                    Changed(_jobs[id]);
                    Log(id, "file-deleted", "Downloaded file deleted by user.");
                }
            }
            FlushOutbox();
            return new(true, "The file was deleted.", path);
        }
        catch (OperationCanceledException) { return new(false, "The file action was cancelled.", path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, "The file action could not finish: " + ex.Message, path);
        }
        finally
        {
            lock (_lock) _completedFileActions.Remove(id);
        }
    }

    private static bool IsSafeCompletedPath(string path, long length) =>
        !Path.GetFileName(path).Contains(':')
        && Path.GetDirectoryName(path) is { } folder && PathSafety.TryCheckUnlinkedDirectory(folder, out _)
        && PathSafety.Inspect(path) is { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false } file
        && file.Length == length;

    private static bool SameCompletedPath(string? savedPath, string? confirmedPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath) || string.IsNullOrWhiteSpace(confirmedPath)) return false;
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(savedPath), Path.GetFullPath(confirmedPath), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void ReleaseCompletedFileAction(Guid id)
    {
        lock (_lock) _completedFileActions.Remove(id);
    }

    // helpers

    /// <summary>
    /// A UI edit must reach SQLite before it becomes visible to an active session or the UI.
    /// This is deliberately limited to pure, repeatable scalar edits: each modifier runs once
    /// against an unpublished candidate, then against the SAME live job after a successful save.
    /// Never use it for file I/O, mutable extents, external side effects or network commands.
    /// </summary>
    private void PersistUiMutationLocked(DownloadJob job, Action<DownloadJob> mutate)
    {
        DownloadJob proposed = job.CopyForUiMutation();
        mutate(proposed);
        if (!ReferenceEquals(proposed.Extents, job.Extents))
            throw new InvalidOperationException("Only durable checkpoints may replace download extents.");
        SaveLocked(proposed);
        mutate(job); // Existing JobSession references must still point to this same live object.
    }

    private void UpdateIfExists(Guid id, Action<DownloadJob> mutate, bool idleOnly = false)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)
                || idleOnly && (_sessions.ContainsKey(id) || _cancelling.Contains(id) || IsCleanupPending(job) || job.PendingFinalPath is not null)) return;
            bool wasCandidate = IsDuplicateCandidate(job);
            string previousSourceUrl = job.SourceUrl;
            PersistUiMutationLocked(job, mutate);
            ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
            Changed(job);
        }
        FlushOutbox();
    }

    private void UpdateIfIdle(Guid id, Action<DownloadJob> mutate) =>
        UpdateIfExists(id, mutate, idleOnly: true);

    private void ArmWakeTimerLocked()
    {
        lock (_lock) ArmWakeTimer();
    }
}

/// <summary>Redaction for anything that may be exported or shown in diagnostics.</summary>
public static class Diagnostics
{
    private static readonly Regex UrlPattern = new("""https?://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HeaderPattern = new("""\b(?<name>Authorization|Proxy-Authorization|Cookie|Set-Cookie)\s*["']?\s*[:=]\s*["']?[^\r\n]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignmentPattern = new("""(?<key>\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?(?:id|token)|api[_-]?key|client[_-]?secret|password|secret|signature|sig|token)\b\s*["']?\s*[:=]\s*["']?)(?<value>[^&;\s,"'<>]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string scrubbed = HeaderPattern.Replace(text, match => $"{match.Groups["name"].Value}: [redacted]");
        scrubbed = SecretAssignmentPattern.Replace(scrubbed, match => $"{match.Groups["key"].Value}[redacted]");
        scrubbed = UrlPattern.Replace(scrubbed, RedactUrl);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 3 ? scrubbed.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : scrubbed;
    }

    private static string RedactUrl(Match match)
    {
        string value = match.Value;
        int end = value.Length;
        while (end > 0 && value[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}') end--;
        return end == 0 ? value : UrlPolicy.Redact(value[..end]) + value[end..];
    }

    /// <summary>Writes a privacy-aware log: links lose their query strings, the profile path is abbreviated.</summary>
    public static void ExportLog(DownloadEngine engine, string path)
    {
        IReadOnlyList<string> lines = engine.ReadLog();
        File.WriteAllLines(path, lines.Select(Scrub));
    }

    /// <summary>Writes a local support archive with system metadata and the scrubbed event log only.</summary>
    public static void ExportBundle(DownloadEngine engine, string path, string version)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string target = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(target) ?? throw new IOException("The diagnostics destination has no parent folder.");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The diagnostics destination folder does not exist.");
        string temporary = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                string[] system =
                [
                    $"VESPER version: {version}",
                    $"Operating system: {RuntimeInformation.OSDescription}",
                    $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
                    $".NET runtime: {Environment.Version}",
                    $"Exported UTC: {DateTimeOffset.UtcNow:O}"
                ];
                AddText(archive, "system.txt", string.Join(Environment.NewLine, system));
                AddText(archive, "events.txt", string.Join(Environment.NewLine, engine.ReadLog().Select(Scrub)));
                AddText(archive, "contents.txt", "This archive contains system details and the scrubbed event log only. It excludes download files, settings, databases, cookies and authorization headers. Event details may include filenames; review before sharing.");
            }
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void AddText(ZipArchive archive, string name, string value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(value);
    }
}
