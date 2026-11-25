using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Vesper.Engine;

public sealed record VerifyResult(bool Ran, string? Sha256, bool? Matches, string Message);

public sealed partial class DownloadEngine
{
    public IReadOnlyList<string> ReadLog() => _store.ReadEvents();

    // finalization: verify (only against a TRUSTED digest) and publish
    internal async Task<RunOutcome> FinalizeAsync(JobSession session, CancellationToken ct)
    {
        DownloadJob job = session.Job;
        if (session.BrowserApproval is { Task.IsCompletedSuccessfully: false }) return BrowserFallbackOutcome;
        PartialFile file = session.OpenFile();
        long total = job.TotalBytes ?? job.Extents.End;

        // Rule 7: never claim completion unless every byte is committed.
        if (!job.Extents.Covers(0, total))
            return RunOutcome.NeedsAttention("incomplete", "Some data is missing even though the transfer ended. Saved data was kept; resume to fetch the rest.");

        if (file.Length != total) file.Truncate(total);     // stale tail from an older, longer file never ships
        using (ProcessingTiming.Measure(job.Id, ProcessingPhase.DiskFlush, total)) file.Flush();
        if (!HttpFacts.IsHtmlFileName(job.FileName) && LooksLikeHtmlFile(file, total))
            return RunOutcome.NeedsAttention("html", "The server sent a web page instead of the selected file. Saved data was kept; nothing was published.");
        bool hasPreparedConversion = job.PendingConversionBytes is not null
            && job.PendingConversionDigest is { Length: 64 }
            && job.PendingConversionIdentity is { Length: 64 };
        UpdateJob(job, j =>
        {
            j.TotalBytes = total;
            j.State = JobState.Finalizing;
            j.Message = "";
            if (!hasPreparedConversion) ClearPendingPublication(j);
        });
        Failpoint("finalize.afterFlush");

        bool webmClip = DirectMediaPlan.TryWebmClip(job.MediaPlanJson, out var webmRange);
        if (job.Kind == JobKind.Http && job.MediaProvider is not null)
        {
            string extension = Path.GetExtension(job.FileName).ToLowerInvariant();
            string type = job.ContentType?.ToLowerInvariant() ?? "";
            string? format = extension == ".webm" || type.Contains("webm") ? "webm"
                : extension is ".mp4" or ".m4a" or ".m4v" or ".mov" || type.Contains("mp4") ? "mp4" : null;
            if (format == "webm" && job.Output == MediaOutput.Original && job.MediaProvider == "generic"
                && job.MediaPlanJson is not null && job.MediaPlanJson != DirectMediaPlan.AudioIfPresent && !webmClip)
                return RunOutcome.NeedsAttention("trim-failed", "Invalid WebM media plan. The original source was kept; nothing was published.");
            if (webmClip && format != "webm")
                return RunOutcome.NeedsAttention("trim-failed", "The selected WebM is not available. The source was kept; nothing was published.");
            if (format is not null)
            {
                using var probe = ProcessingTiming.Measure(job.Id, ProcessingPhase.Probe, total);
                bool allowSilentStandalone = job.Output == MediaOutput.Original
                    && string.Equals(job.MediaProvider, "generic", StringComparison.Ordinal)
                    && (string.Equals(job.MediaPlanJson, DirectMediaPlan.AudioIfPresent, StringComparison.Ordinal)
                        || webmClip);
                try
                {
                    if (webmClip) MediaTrackProbe.RequireSilentWebmHeader(file, ct);
                    else MediaTrackProbe.RequireAudio(file, format, ct, allowSilentStandalone, job.PartialIdentity);
                }
                catch (MediaContainerException ex)
                { return RunOutcome.NeedsAttention("media-validation", ex.Message + " Saved data was kept."); }
            }
        }

        bool verifyAtPublication = job.ExpectedSha256 is not null && job.Output == MediaOutput.Original
            && job.MediaMetadataJson is null;
        if (!verifyAtPublication && job.ExpectedSha256 is { } expected)
        {
            SetPhase(job, JobState.Verifying);
            string actual;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, total)) actual = HashFile(file, total, ct);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                session.CloseFile();
                string how = job.RescueCandidate ? " The replacement link may serve different content." : "";
                return new RunOutcome(JobState.Suspect, "hash-mismatch",
                    $"The file does not match the expected SHA-256, so it was not published.{how} The downloaded data was kept at {PathSafety.ForDisplay(job.PartialPath)}.");
            }
            UpdateJob(job, j => { j.Sha256Verified = true; j.State = JobState.Finalizing; });
        }

        ct.ThrowIfCancellationRequested();
        session.CloseFile();                                  // release the handle before renaming
        PreparedMediaFile? prepared;
        string? completionNotice = null;
        if (webmClip && job.Kind == JobKind.Http
            && job.Output == MediaOutput.Original && job.MediaProvider == "generic")
        {
            try { (prepared, completionNotice) = await PrepareWebmClipAsync(job, webmRange, ct).ConfigureAwait(false); }
            catch (MediaContainerException ex)
            { return RunOutcome.NeedsAttention("trim-failed", ex.Message + " The original source was kept; nothing was published."); }
            if (prepared is null) return RunOutcome.NeedsAttention("trim-failed", "The WebM clip could not be prepared. The original source was kept; nothing was published.");
        }
        else if (job.Output == MediaOutput.Original)
            (prepared, completionNotice) = await PrepareAudioMetadataAsync(job, ct).ConfigureAwait(false);
        else (prepared, completionNotice) = await ConvertMediaAsync(job, ct).ConfigureAwait(false);
        if (prepared is null && job.Kind == JobKind.Http && job.Output == MediaOutput.M4a
            && job.MediaPlanJson is not null)
            return RunOutcome.NeedsAttention("trim-failed", "The requested audio clip could not be prepared. The full source was kept; nothing was published.");
        RunOutcome outcome;
        try
        {
            if (prepared is not null) Failpoint("finalize.afterMediaPrepare");
            if (ct.IsCancellationRequested && prepared is not null)
            {
                prepared.Dispose();
                DeleteTemporaryFile(job, prepared.Path, prepared.Identity);
            }
            ct.ThrowIfCancellationRequested();
            outcome = await PublishAsync(job, prepared, completionNotice, ct, verifyAtPublication).ConfigureAwait(false);
        }
        finally { prepared?.Dispose(); }
        if (prepared is not null && job.PendingFinalPath is null && outcome.State != JobState.Completed)
            DeleteTemporaryFile(job, prepared.Path, prepared.Identity);
        return outcome;
    }

    internal async Task<RunOutcome> FinalizeVerifiedMergeAsync(JobSession session, MergeTransfer.VerifiedOutput verified, CancellationToken ct)
    {
        DownloadJob job = session.Job;
        // Conversion must reopen its source using the normal path. Only a finished
        // original merge can publish without a second full read of the same object.
        if (!OperatingSystem.IsWindows() || job.Kind != JobKind.Merge || job.Output != MediaOutput.Original
            || job.MediaMetadataJson is not null || job.ExpectedSha256 is not null)
        {
            verified.Dispose();
            return await FinalizeAsync(session, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        session.CloseFile();
        if (job.TotalBytes is not long total || total <= 0 || !job.Extents.Covers(0, total)
            || verified.File.Length != total || verified.Identity != job.PartialIdentity
            || verified.Identity is not { Length: 64 } || verified.Digest is not { Length: 64 })
            return RunOutcome.NeedsAttention("publication-file", "The verified merge output does not match its committed identity. Saved data was kept.");
        UpdateJob(job, j =>
        {
            j.State = JobState.Finalizing;
            j.Message = "";
            ClearPendingPublication(j);
        });
        Failpoint("finalize.afterFlush");
        return await PublishAsync(job, null, null, ct, verifiedMerge: verified).ConfigureAwait(false);
    }

    private static string HashFile(PartialFile file, long length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        while (offset < length)
        {
            ct.ThrowIfCancellationRequested();
            int read = file.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (read <= 0) throw new LocalStorageException("The partial file ended unexpectedly while verifying.", new EndOfStreamException());
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool LooksLikeHtmlFile(PartialFile file, long length)
    {
        if (length <= 0) return false;
        Span<byte> prefix = stackalloc byte[4096];
        int count = file.Read(prefix[..(int)Math.Min(prefix.Length, length)], 0);
        return HttpFacts.LooksLikeHtml(prefix[..count]);
    }

    /// <summary>
    /// Publishes with "create or fail" semantics: File.Move never overwrites. The chosen name is persisted BEFORE
    /// the rename so a crash between the rename and the database update can be reconciled on restart.
    /// </summary>
    private async Task<RunOutcome> PublishAsync(DownloadJob job, PreparedMediaFile? prepared, string? completionNotice, CancellationToken ct, bool verifyAtPublication = false, MergeTransfer.VerifiedOutput? verifiedMerge = null)
    {
        if (!PathSafety.TryResolveFolder(job.DestinationFolder, create: true, out _, out string problem))
            return RunOutcome.NeedsAttention("destination", problem + " The finished file is waiting at " + PathSafety.ForDisplay(job.PartialPath));

        using var timing = ProcessingTiming.Measure(job.Id, ProcessingPhase.Publish, prepared?.Bytes ?? job.TotalBytes ?? 0);
        string source = prepared?.Path ?? job.PartialPath;
        long? expectedLength = prepared?.Bytes ?? job.TotalBytes;
        if (!IsPublicationFile(job, source, expectedLength, staged: true))
            return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published is missing or changed.");

        const FileShare share = FileShare.Read | FileShare.Delete;
        const FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        FileStream? preparedProof = OperatingSystem.IsWindows() ? prepared?.VerifiedFile : null;
        FileStream file = verifiedMerge?.File ?? preparedProof ?? (prepared is null
            ? PartialFile.OpenOwnedRead(job, share, options)
            : PathSafety.OpenTemporaryStream(source, FileMode.Open, FileAccess.Read, share, 1 << 16, options));
        try
        {
            if (file.Length != expectedLength)
                return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published changed size.");
            if (prepared is not null)
            {
                try { PathSafety.RequireTemporaryIdentity(file.SafeFileHandle, prepared.Identity); }
                catch (IOException)
                {
                    return RunOutcome.NeedsAttention("publication-file", "The prepared file changed before it could be published.");
                }
            }
            if (verifyAtPublication) SetPhase(job, JobState.Verifying);
            string digest;
            if (verifiedMerge is not null) digest = verifiedMerge.Digest;
            else if (preparedProof is not null && !verifyAtPublication) digest = prepared!.Digest;
            else
            {
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, file.Length))
                    digest = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            }
            Debug.Assert(expectedLength is >= 0 && file.Length == expectedLength.Value,
                "The publication handle must retain the validated file length.");
            if (verifyAtPublication)
            {
                if (prepared is not null || !digest.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    string how = job.RescueCandidate ? " The replacement link may serve different content." : "";
                    return new RunOutcome(JobState.Suspect, "hash-mismatch",
                        $"The file does not match the expected SHA-256, so it was not published.{how} The downloaded data was kept at {PathSafety.ForDisplay(job.PartialPath)}.");
                }
                UpdateJob(job, j => { j.Sha256Verified = true; j.State = JobState.Finalizing; });
            }
            string? expectedDigest = prepared?.Digest ?? (job.Sha256Verified ? job.ExpectedSha256 : null);
            if (expectedDigest is not null && !digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
                return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published failed its saved validation.");

            string? sourceIdentity = TryGetPublishedFileIdentity(file.SafeFileHandle);
            string? expectedSourceIdentity = prepared?.Identity ?? job.PartialIdentity;
            if (OperatingSystem.IsWindows()
                && (sourceIdentity is null || expectedSourceIdentity is null
                    || !sourceIdentity.Equals(expectedSourceIdentity, StringComparison.Ordinal)))
                return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published no longer has its saved identity.");

            string fileName = job.Output == MediaOutput.Original ? job.FileName : prepared?.FileName ?? job.FileName;
            if (!PathSafety.PathStillNamesFile(source, file, digest))
                return RunOutcome.NeedsAttention("publication-pending", "The path to the file waiting to be published changed.");

            // Hold the verified stream without write sharing through publication.
            WriteMarkOfTheWeb(job, source, expectedSourceIdentity);

            for (int attempt = 0; attempt < 50; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                string target = FileNames.UniquePath(job.DestinationFolder, fileName);
                UpdateJob(job, j =>
                {
                    j.State = JobState.Finalizing;
                    j.PendingFinalPath = target;
                    j.PendingConversionBytes = prepared?.Bytes;
                    j.PendingConversionDigest = digest;
                    j.PendingConversionIdentity = prepared?.Identity;
                });
                Failpoint("finalize.beforeMove");
                if (!PathSafety.PathStillNamesFile(source, file, digest))
                    return RunOutcome.NeedsAttention("publication-pending", "The path to the file waiting to be published changed.");

                if (OperatingSystem.IsWindows())
                {
                    if (!PathSafety.TryPublishTemporaryFile(source, target, expectedSourceIdentity, out string moveError))
                    {
                        bool sourceStillMatches = PathSafety.PathStillNamesFile(source, file, digest);
                        if (File.Exists(target) && sourceStillMatches) continue;
                        if (!sourceStillMatches)
                            return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published changed before it could be moved.");
                        return RunOutcome.Retry("locked", "Another program (often antivirus) is using the file. VESPER will try again. " + moveError);
                    }
                }
                else
                {
                    try { File.Move(source, target, overwrite: false); }
                    catch (IOException) when (File.Exists(target) && IsPublicationFile(job, source, expectedLength, staged: true))
                    {
                        continue;   // someone created that name in the meantime: pick the next free one
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (!IsPublicationFile(job, source, expectedLength, staged: true))
                            return RunOutcome.NeedsAttention("publication-pending", "Could not confirm where the file was saved. Resume to check again.");

                        return RunOutcome.Retry("locked", "Another program (often antivirus) is using the file. VESPER will try again. " + ex.Message);
                    }
                }

                Failpoint("finalize.afterMove");
                if (!PathSafety.PathStillNamesFile(target, file, digest))
                    return RunOutcome.NeedsAttention("publication-pending", "The path to the saved file changed. Check it in File Explorer.");
                long publishedLength = file.Length;
                try
                {
                    // The transfer handle allows rename/delete. Reopen the final path with sharing
                    // that pins it until the completed row has been saved.
                    using var published = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
                        1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (published.Length != publishedLength || !IsPublicationFile(job, target, expectedLength))
                        return RunOutcome.NeedsAttention("publication-pending", "The path to the saved file changed. Check it in File Explorer.");

                    string? fileIdentity = TryGetPublishedFileIdentity(published.SafeFileHandle);
                    if (sourceIdentity is not null && fileIdentity is not null
                        && !sourceIdentity.Equals(fileIdentity, StringComparison.Ordinal))
                        return RunOutcome.NeedsAttention("publication-pending", "The path to the saved file changed. Check it in File Explorer.");
                    if (!PathSafety.PathStillNamesFile(target, published, digest))
                        return RunOutcome.NeedsAttention("publication-pending", "The path to the saved file changed. Check it in File Explorer.");

                    fileIdentity ??= sourceIdentity;
                    try
                    {
                        UpdateJob(job, j =>
                        {
                            MarkPublished(j, target, fileIdentity);
                            if (completionNotice is { Length: > 0 })
                            {
                                j.Reason = "media-metadata";
                                j.Message = completionNotice;
                            }
                        });
                    }
                    catch (LocalStorageException ex)
                    {
                        Log(job.Id, "publication-pending", ex.Message);
                        return RunOutcome.NeedsAttention("publication-pending", "The file was saved, but VESPER could not update the download record. Resume to finish checking it.");
                    }

                    FinishPublication(job);
                    Log(job.Id, "completed", job.FileName);
                    return job.Reason.Length == 0 ? RunOutcome.Done() : new RunOutcome(JobState.Completed, job.Reason, job.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return RunOutcome.NeedsAttention("publication-pending", "The saved file could not be pinned safely. Resume to verify where it was saved.");
                }
            }
            return RunOutcome.NeedsAttention("name", "No free file name could be found in the destination folder.");
        }
        finally { file.Dispose(); }
    }

    /// <summary>Marks the file as downloaded from the internet so Windows applies SmartScreen/Office protections.</summary>
    private void WriteMarkOfTheWeb(DownloadJob job, string path, string? expectedIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string directoryPath = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new IOException("The download folder is unavailable.");
            using SafeFileHandle directory = PathSafety.OpenTemporaryDirectoryHandle(directoryPath);
            // Pin the name while writing the alternate stream.
            using SafeFileHandle file = PathSafety.OpenTemporaryHandle(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            PathSafety.RequireTemporaryIdentity(file, expectedIdentity);
            string host = Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? source) ? source.GetLeftPart(UriPartial.Path) : "";
            string referrer = Uri.TryCreate(job.Referer, UriKind.Absolute, out Uri? r) ? r.GetLeftPart(UriPartial.Path) : "";
            File.WriteAllText(path + ":Zone.Identifier",
                $"[ZoneTransfer]\r\nZoneId=3\r\n{(referrer.Length > 0 ? $"ReferrerUrl={referrer}\r\n" : "")}HostUrl={host}\r\n");
            PathSafety.RequireTemporaryIdentity(file, expectedIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log(job.Id, "motw", "Origin marker could not be written on this volume: " + ex.Message);
        }
    }

    // restart recovery
    private void Recover()
    {
        var ownedPaths = _jobs.Values.Select(job => job.PartialPath)
            .Where(path => PathSafety.TryGetFullPath(path, out _, out _))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BrowserBodyReceiver.RecoverJournal(Options.DataDirectory, ownedPaths, _store.RecoveredFromCorruption);
        string[] manifestFolders = RecoveryFolders();
        foreach (string folder in manifestFolders)
            RecoveryManifest.RecoverInterruptedReplacements(folder,
                message => Log(null, "manifest-replacement", message));

        lock (_lock)
        {
            if (_store.RecoveredFromCorruption)
            {
                Log(null, "db-corrupt", $"Database was damaged and quarantined as {Path.GetFileName(_store.CorruptBackupPath)}.");
                ImportManifests(manifestFolders);
                _store.FinishQuarantineRecovery();
            }

            foreach (DownloadJob job in _jobs.Values.ToList())
            {
                if (IsCleanupPending(job)) continue;
                if (job.PendingFinalPath is not null || job.State == JobState.Finalizing)
                {
                    ReconcileFinalizing(job);
                    continue;
                }

                bool wasCandidate = IsDuplicateCandidate(job);
                string previousSourceUrl = job.SourceUrl;
                if (job.BrowserProof is { AdoptionReleased: true } && (job.State == JobState.Queued || JobStates.IsActive(job.State)))
                {
                    job.State = JobState.Paused;
                    job.NextRetryUtc = null;
                    job.Message = "";
                    SaveLocked(job);
                    continue;
                }
                if (job.BrowserProof is { AwaitingConfirmation: true } && (job.State == JobState.Queued || JobStates.IsActive(job.State)))
                {
                    job.State = JobState.Cancelled;
                    job.Reason = BrowserFallbackOutcome.Reason;
                    job.Message = BrowserFallbackOutcome.Message;
                    ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
                    SaveLocked(job);
                    continue;
                }
                switch (job.State)
                {
                    case JobState.Resolving or JobState.Downloading or JobState.WaitingForNetwork or JobState.WaitingForRetry
                        or JobState.Verifying or JobState.Converting:
                        job.State = JobState.Queued;
                        job.Message = "Resumed after VESPER restarted.";
                        job.NextRetryUtc = null;
                        SaveLocked(job);
                        break;
                    case JobState.Completed when job.PendingConversionBytes is not null && job.FinalPath is { } final:
                        using (FileStream? published = OpenPublicationFile(job, final, out bool matches))
                        {
                            if (matches && published is not null)
                            {
                                string? fileIdentity = TryGetPublishedFileIdentity(published.SafeFileHandle);
                                if (fileIdentity is not null && job.PublishedFileIdentity is null)
                                {
                                    DownloadJob saved = job.CopyForUpdate();
                                    saved.PublishedFileIdentity = fileIdentity;
                                    SaveRecoveredJob(job, saved);
                                }
                                FinishPublication(job, recovering: true);
                            }
                        }
                        break;
                }
                ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
            }
        }
    }

    // Called with the engine lock held, including when the user retries a failed completion write.
    private void ReconcileFinalizing(DownloadJob job)
    {
        string stage = job.PendingConversionBytes is null ? job.PartialPath : PublicationStagePath(job);
        string? target = job.PendingFinalPath;
        DownloadJob saved = job.CopyForUpdate();

        bool targetMatches = false;
        using FileStream? published = target is null ? null : OpenPublicationFile(job, target, out targetMatches);
        if (target is not null && targetMatches && PublicationPathIsMissing(job, stage, staged: true))
        {
            string? fileIdentity = published is null ? null : TryGetPublishedFileIdentity(published.SafeFileHandle);
            MarkPublished(saved, target, fileIdentity);
            SaveRecoveredJob(job, saved);
            FinishPublication(job, recovering: true);
            Log(job.Id, "reconciled", "Rename had completed before the interruption.");
            return;
        }

        long? expectedLength = job.PendingConversionBytes ?? job.TotalBytes;
        bool targetAvailable = target is null || PublicationPathIsMissing(job, target) || !targetMatches;
        bool stageMatches = false;
        if (targetAvailable)
        {
            if (target is null || PublicationDigest(job) is null)
                stageMatches = IsPublicationFile(job, stage, expectedLength, staged: true);
            else
            {
                using FileStream? stagedFile = OpenPublicationFile(job, stage, out stageMatches, staged: true);
            }
        }
        if (stageMatches)
        {
            saved.State = JobState.Queued;
            saved.Message = "Finishing after an interrupted publication.";
            saved.NextRetryUtc = null;
        }
        else
        {
            saved.State = JobState.NeedsAttention;
            saved.Reason = "finalize-lost";
            saved.Message = "VESPER could not verify which file was saved. Check the destination folder, then resume to check again.";
            saved.NextRetryUtc = null;
            ClearPendingPublication(saved);
        }
        SaveRecoveredJob(job, saved);
    }

    private void SaveRecoveredJob(DownloadJob job, DownloadJob saved)
    {
        SaveLocked(saved);
        bool wasCandidate = IsDuplicateCandidate(job);
        string previousSourceUrl = job.SourceUrl;
        job.ApplyPersistedState(saved);
        ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
    }

    private static bool IsPublicationPath(DownloadJob job, string path, bool staged)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(job.DestinationFolder)) return false;
        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationFolder));
            string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return parent is not null && (string.Equals(folder, parent, comparison) || staged && PartialFile.IsStagingFolder(parent, folder))
                && PathSafety.TryCheckUnlinkedDirectory(parent, out _);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsPublicationFile(DownloadJob job, string path, long? length = null, bool staged = false) =>
        IsPublicationPath(job, path, staged)
        && PathSafety.Inspect(path) is { Exists: true, IsReparsePoint: false, IsCloudPlaceholder: false, IsDirectory: false } file
        && (length is null || file.Length == length);

    private static bool SamePublicationPath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static bool PublicationPathIsMissing(DownloadJob job, string path, bool staged = false)
    {
        if (!IsPublicationPath(job, path, staged)) return false;
        try { File.GetAttributes(path); }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }

    private static string? PublicationDigest(DownloadJob job) =>
        job.PendingConversionDigest ?? (job.PendingConversionBytes is null && job.Sha256Verified ? job.ExpectedSha256 : null);

    private static string? TryGetPublishedFileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        try { return PathSafety.TemporaryFileIdentity(handle); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    // Keep the verified output open until the completion row commits.
    private static FileStream? OpenPublicationFile(DownloadJob job, string path, out bool matches, bool staged = false)
    {
        matches = false;
        string? digest = PublicationDigest(job);
        long? length = job.PendingConversionBytes ?? job.TotalBytes;
        if (digest is null || length is null || !IsPublicationFile(job, path, staged: staged)) return null;

        bool isConversionStage = job.PendingConversionBytes is not null
            && SamePublicationPath(path, PublicationStagePath(job));
        bool isPendingTarget = job.PendingFinalPath is { Length: > 0 } pendingPath
            && SamePublicationPath(path, pendingPath);
        bool isCompletedTarget = job.FinalPath is { Length: > 0 } finalPath
            && SamePublicationPath(path, finalPath);
        string? expectedIdentity = null;
        if (isConversionStage)
        {
            expectedIdentity = job.PendingConversionIdentity;
        }
        else if (isPendingTarget)
        {
            expectedIdentity = job.PendingConversionBytes is not null
                ? job.PendingConversionIdentity : job.PartialIdentity;
        }
        else if (isCompletedTarget)
        {
            expectedIdentity = job.PublishedFileIdentity
                ?? (job.PendingConversionBytes is not null ? job.PendingConversionIdentity : job.PartialIdentity);
        }
        if (OperatingSystem.IsWindows() && (isConversionStage || isPendingTarget || isCompletedTarget)
            && expectedIdentity is null) return null;

        FileStream? file = null;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (expectedIdentity is not null)
                PathSafety.RequireTemporaryIdentity(file.SafeFileHandle, expectedIdentity);
            if (IsPublicationFile(job, path, staged: staged))
            {
                matches = file.Length == length
                    && Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase);
                return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        file?.Dispose();
        return null;
    }

    private static void MarkPublished(DownloadJob job, string target, string? fileIdentity)
    {
        job.FinalPath = target;
        job.PublishedSha256 = PublicationDigest(job);
        job.PublishedFileIdentity = fileIdentity;
        job.PendingFinalPath = null;
        if (job.PendingConversionBytes is null) job.PendingConversionDigest = null;
        job.State = JobState.Completed;
        job.CompletedUtc ??= DateTime.UtcNow;
        job.RetryCount = 0;
        job.NextRetryUtc = null;
        job.UnverifiedResumeApproved = false;
        string keptReason = job.Reason is "unverified-length" ? job.Reason : "";
        string keptMessage = keptReason.Length > 0 ? job.Message : "";
        job.Reason = keptReason;
        job.Message = keptMessage;
        if (job.PendingConversionBytes is long bytes)
        {
            job.FileName = Path.GetFileName(target);
            job.TotalBytes = bytes;
            var extents = new ExtentSet();
            extents.Add(new ByteRange(0, bytes));
            job.Extents = extents;
            if (job.Output == MediaOutput.Original)
            {
                SetTaggedAudioFormat(job);
            }
            else
            {
                job.ContentType = job.Output switch
                {
                    MediaOutput.M4a => "audio/mp4",
                    MediaOutput.Opus => "audio/ogg",
                    MediaOutput.Wav => "audio/wav",
                    MediaOutput.Mp3 => "audio/mpeg",
                    _ => "video/mp4"
                };
                job.MediaQuality = job.Output switch
                {
                    MediaOutput.M4a => "Audio AAC",
                    MediaOutput.Opus => "Opus · copied",
                    MediaOutput.Wav => "PCM audio",
                    MediaOutput.Mp3 => "MP3 audio",
                    _ => "H.264 + AAC"
                };
            }
            job.Category = FileNames.CategoryFor(job.FileName);
        }
        else if (job.Output == MediaOutput.Mp3)
        {
            job.Reason = "saved-original";
            job.Message = $"Saved as {Path.GetExtension(job.FileName).TrimStart('.').ToUpperInvariant()}: an MP3 copy couldn't be created on this PC, so the original was saved unchanged.";
        }
        else if (job.Output != MediaOutput.Original)
        {
            job.Reason = "saved-original";
            job.Message = $"Saved as {Path.GetExtension(job.FileName).TrimStart('.').ToUpperInvariant()}: this PC can't convert this audio or video. "
                + "Installing Microsoft's free Web Media Extensions from the Store usually adds the missing decoder.";
        }
    }

    internal static void SetTaggedAudioFormat(DownloadJob job)
    {
        (string? mime, string? format) = Path.GetExtension(job.FileName).ToLowerInvariant() switch
        {
            ".mp3" => ("audio/mpeg", "MP3"),
            ".flac" => ("audio/flac", "FLAC"),
            ".opus" => ("audio/ogg", "Opus"),
            ".ogg" => ("audio/ogg", "Ogg audio"),
            _ => (job.ContentType, job.MediaQuality)
        };
        job.ContentType = mime;
        job.MediaQuality ??= format;
    }

    private void FinishPublication(DownloadJob job, bool recovering = false)
    {
        if (!RecoveryManifest.Delete(job))
            Log(job.Id, "publication-cleanup", "A recovery file was kept because it did not match this download.");
        if (recovering && job.Kind == JobKind.Merge && !MergeTransfer.DeleteSidecars(job))
            Log(job.Id, "publication-cleanup", "A merge sidecar was kept because it could not be verified or removed.");
        if (job.PendingConversionBytes is null || !DeleteTemporaryFile(job, job.PartialPath)) return;
        try
        {
            if (recovering)
            {
                DownloadJob saved = job.CopyForUpdate();
                ClearPendingPublication(saved);
                SaveRecoveredJob(job, saved);
            }
            else UpdateJob(job, ClearPendingPublication);
        }
        catch (LocalStorageException ex)
        {
            Log(job.Id, "publication-cleanup", ex.Message);
        }
    }

    private static void ClearPendingPublication(DownloadJob job)
    {
        job.PendingFinalPath = null;
        job.PendingConversionBytes = null;
        job.PendingConversionDigest = null;
        job.PendingConversionIdentity = null;
    }

    private string[] RecoveryFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Options.DefaultFolder };
        if (Directory.Exists(Options.DefaultFolder))
        {
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(Options.DefaultFolder))
                    if (!PartialFile.IsStagingFolder(sub, Options.DefaultFolder)) folders.Add(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Log(null, "manifest-replacement", "Recovery folders could not be fully scanned: " + ex.Message); }
        }
        foreach (DownloadJob job in _jobs.Values)
            if (PathSafety.TryGetFullPath(job.DestinationFolder, out string destination, out _)) folders.Add(destination);

        return folders.SelectMany(folder => new[] { folder, PartialFile.StagingFolder(folder) })
            .Where(folder => Directory.Exists(folder) && PathSafety.TryCheckUnlinkedDirectory(folder, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>After a database loss, partial files with a valid recovery manifest come back as "needs link" entries.</summary>
    private void ImportManifests(string[] folders)
    {
        long order = 0;
        foreach (string folder in folders)
        {
            foreach (string manifest in Directory.EnumerateFiles(folder, "*.vpart" + RecoveryManifest.Extension))
            {
                if (!RecoveryManifest.TryRecover(manifest, out DownloadJob job, out string reason))
                {
                    Log(null, "manifest-skipped", $"{Path.GetFileName(manifest)}: {reason}");
                    continue;
                }
                if (_jobs.ContainsKey(job.Id)) continue;
                // The database may have held a newer checksum obligation than this sidecar.
                // Require trusted evidence for manifest-only HTTP recovery when none was recorded.
                if (job.Kind == JobKind.Http && job.ExpectedSha256 is null && job.PieceMapJson is null)
                {
                    job.FinalResourceIdentity = null;
                    job.Identity = IdentityLevel.None;
                    job.UnverifiedResumeApproved = false;
                }
                job.QueueOrder = order++;
                SaveLocked(job);
                _jobs[job.Id] = job;
                AddDuplicateCandidateLocked(job);
                Log(job.Id, "manifest-imported", job.FileName);
            }
        }
    }

    // availability and verification
    public Task RefreshAvailabilityAsync() => Task.Run(RefreshAvailability);

    private void RefreshAvailability()
    {
        using var operation = EnterOperation();
        List<(Guid Id, string Path)> targets;
        lock (_lock)
            targets = [.. _jobs.Values.Where(j => j.State == JobState.Completed && !string.IsNullOrEmpty(j.FinalPath)).Select(j => (j.Id, j.FinalPath!))];

        foreach ((Guid id, string path) in targets)
        {
            bool missing = !PathSafety.Inspect(path).Exists;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(id, out DownloadJob? job) || job.FinalPath != path
                    || _completedFileActions.Contains(id) || missing == _missing.Contains(id)) continue;
                if (missing) _missing.Add(id); else _missing.Remove(id);
                Changed(job);
            }
        }
        FlushOutbox();
    }

    /// <summary>
    /// Reads the finished file once. Only a comparison with a TRUSTED expected digest proves anything;
    /// otherwise the digest is reported as information and the message says so.
    /// </summary>
    public async Task<VerifyResult> VerifyIntegrityAsync(Guid id, CancellationToken ct = default)
    {
        using var operation = EnterOperation(ct);
        ct = operation.Token;
        DownloadJob? job;
        lock (_lock) _jobs.TryGetValue(id, out job);
        if (job is null || job.State != JobState.Completed || string.IsNullOrEmpty(job.FinalPath))
            return new VerifyResult(false, null, null, "Only completed downloads can be verified.");

        FileCheck check = PathSafety.Inspect(job.FinalPath);
        if (!check.Exists) return new VerifyResult(false, null, null, "The file is missing.");

        string actual;
        try
        {
            await using var stream = new FileStream(job.FinalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VerifyResult(false, null, null, "The file could not be read: " + ex.Message);
        }

        if (job.ExpectedSha256 is null)
            return new VerifyResult(true, actual, null, "SHA-256: " + actual + ". No trusted checksum is known for this file, so this does not prove it is the intended content.");

        if (actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            UpdateIfExists(id, j => j.Sha256Verified = true);
            return new VerifyResult(true, actual, true, "The file matches the trusted SHA-256.");
        }

        UpdateIfExists(id, j => { j.State = JobState.Suspect; j.Reason = "hash-mismatch"; j.Message = "The file no longer matches the trusted SHA-256. It was kept."; });
        return new VerifyResult(true, actual, false, "The file does NOT match the trusted SHA-256. It was kept and marked suspect.");
    }

    /// <summary>Attaches a trusted checksum to an entry (typed by the user or taken from the publisher's page).</summary>
    public bool SetExpectedSha256(Guid id, string hex)
    {
        if (!TryNormalizeSha256(hex, out string normalized)) return false;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return false;
            if (job.PendingFinalPath is not null || job.State is JobState.Converting or JobState.Finalizing or JobState.Verifying)
                return false;
            if (job.FinalPath is null && (job.Kind == JobKind.Merge || job.Output != MediaOutput.Original
                || job.MediaMetadataJson is not null || DirectMediaPlan.TryWebmClip(job.MediaPlanJson, out _)))
                return false;
            DownloadJob saved = job.CopyForUpdate();
            saved.ExpectedSha256 = normalized;
            saved.Sha256Verified = false;
            SaveRecoveredJob(job, saved);
            if (job.FinalPath is null && !_sessions.ContainsKey(id) && job.Extents.TotalBytes > 0 && job.PartialIdentity is not null)
                WriteManifest(job);
            Changed(job);
        }
        FlushOutbox();
        return true;
    }
}
