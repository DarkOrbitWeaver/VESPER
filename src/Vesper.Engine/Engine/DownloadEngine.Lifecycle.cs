using System.Security.Cryptography;

namespace Vesper.Engine;

public sealed record VerifyResult(bool Ran, string? Sha256, bool? Matches, string Message);

public sealed partial class DownloadEngine
{
    public IReadOnlyList<string> ReadLog() => _store.ReadEvents();

    // ---- finalization: verify (only against a TRUSTED digest) and publish ------------------------------------------------

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
        file.Flush();
        if (!HttpFacts.IsHtmlFileName(job.FileName) && LooksLikeHtmlFile(file, total))
            return RunOutcome.NeedsAttention("html", "The server sent a web page instead of the selected file. Saved data was kept; nothing was published.");
        UpdateJob(job, j => { j.TotalBytes = total; j.State = JobState.Finalizing; j.Message = ""; ClearPendingPublication(j); });
        Failpoint("finalize.afterFlush");

        if (job.Kind == JobKind.Http && job.MediaProvider is not null)
        {
            string extension = Path.GetExtension(job.FileName).ToLowerInvariant();
            string type = job.ContentType?.ToLowerInvariant() ?? "";
            string? format = extension == ".webm" || type.Contains("webm") ? "webm"
                : extension is ".mp4" or ".m4a" or ".m4v" or ".mov" || type.Contains("mp4") ? "mp4" : null;
            if (format is not null)
            {
                try { MediaTrackProbe.RequireAudio(file, format, ct); }
                catch (MediaContainerException ex)
                { return RunOutcome.NeedsAttention("media-validation", ex.Message + " Saved data was kept."); }
            }
        }

        if (job.ExpectedSha256 is { } expected)
        {
            SetPhase(job, JobState.Verifying);
            string actual = HashFile(file, total, ct);
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
        if (job.Output == MediaOutput.Original)
            (prepared, completionNotice) = await PrepareAudioMetadataAsync(job, ct).ConfigureAwait(false);
        else (prepared, completionNotice) = await ConvertMediaAsync(job, ct).ConfigureAwait(false);
        if (prepared is not null) Failpoint("finalize.afterMediaPrepare");
        if (ct.IsCancellationRequested && prepared is not null) DeleteTemporaryFile(job, prepared.Path);
        ct.ThrowIfCancellationRequested();
        RunOutcome outcome = await PublishAsync(job, prepared, completionNotice, ct).ConfigureAwait(false);
        if (prepared is not null && job.PendingFinalPath is null && outcome.State != JobState.Completed)
            DeleteTemporaryFile(job, prepared.Path);
        return outcome;
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
    private async Task<RunOutcome> PublishAsync(DownloadJob job, PreparedMediaFile? prepared, string? completionNotice, CancellationToken ct)
    {
        if (!PathSafety.TryResolveFolder(job.DestinationFolder, create: true, out _, out string problem))
            return RunOutcome.NeedsAttention("destination", problem + " The finished file is waiting at " + PathSafety.ForDisplay(job.PartialPath));

        string source = prepared?.Path ?? job.PartialPath;
        long? expectedLength = prepared?.Bytes ?? job.TotalBytes;
        if (!IsPublicationFile(job, source, expectedLength))
            return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published is missing or changed.");

        string? digest = prepared?.Digest ?? (job.Sha256Verified ? job.ExpectedSha256 : null);
        if (digest is null)
        {
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != expectedLength)
                return RunOutcome.NeedsAttention("publication-file", "The file waiting to be published changed size.");
            digest = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        }

        string fileName = job.Output == MediaOutput.Original ? job.FileName : prepared?.FileName ?? job.FileName;
        WriteMarkOfTheWeb(job, source);

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
            });
            Failpoint("finalize.beforeMove");
            try
            {
                File.Move(source, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target) && IsPublicationFile(job, source, expectedLength))
            {
                continue;   // someone created that name in the meantime: pick the next free one
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!IsPublicationFile(job, source, expectedLength))
                    return RunOutcome.NeedsAttention("publication-pending", "Could not confirm where the file was saved. Resume to check publication again.");

                UpdateJob(job, ClearPendingPublication);
                return RunOutcome.Retry("locked", "Another program (often antivirus) is using the file. VESPER will try again. " + ex.Message);
            }

            Failpoint("finalize.afterMove");
            try
            {
                UpdateJob(job, j =>
                {
                    MarkPublished(j, target);
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
        if (IsPublicationFile(job, source, expectedLength))
            UpdateJob(job, ClearPendingPublication);
        return RunOutcome.NeedsAttention("name", "No free file name could be found in the destination folder.");
    }

    /// <summary>Marks the file as downloaded from the internet so Windows applies SmartScreen/Office protections.</summary>
    private void WriteMarkOfTheWeb(DownloadJob job, string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string host = Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? source) ? source.GetLeftPart(UriPartial.Path) : "";
            string referrer = Uri.TryCreate(job.Referer, UriKind.Absolute, out Uri? r) ? r.GetLeftPart(UriPartial.Path) : "";
            File.WriteAllText(path + ":Zone.Identifier",
                $"[ZoneTransfer]\r\nZoneId=3\r\n{(referrer.Length > 0 ? $"ReferrerUrl={referrer}\r\n" : "")}HostUrl={host}\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log(job.Id, "motw", "Origin marker could not be written on this volume: " + ex.Message);
        }
    }

    // ---- restart recovery ---------------------------------------------------------------------------------------------

    private void Recover()
    {
        lock (_lock)
        {
            if (_store.RecoveredFromCorruption)
            {
                Log(null, "db-corrupt", $"Database was damaged and quarantined as {Path.GetFileName(_store.CorruptBackupPath)}.");
                ImportManifests();
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
                            if (matches) FinishPublication(job, recovering: true);
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
        if (target is not null && targetMatches && PublicationPathIsMissing(job, stage))
        {
            MarkPublished(saved, target);
            SaveRecoveredJob(job, saved);
            FinishPublication(job, recovering: true);
            Log(job.Id, "reconciled", "Rename had completed before the interruption.");
            return;
        }

        long? expectedLength = job.PendingConversionBytes ?? job.TotalBytes;
        bool targetAvailable = target is null || PublicationPathIsMissing(job, target) || (published is not null && !targetMatches);
        bool stageMatches = false;
        if (targetAvailable)
        {
            if (target is null || PublicationDigest(job) is null)
                stageMatches = IsPublicationFile(job, stage, expectedLength);
            else
            {
                using FileStream? stagedFile = OpenPublicationFile(job, stage, out stageMatches);
            }
        }
        if (stageMatches)
        {
            saved.State = JobState.Queued;
            saved.Message = "Finishing after an interrupted publication.";
            saved.NextRetryUtc = null;
            ClearPendingPublication(saved);
        }
        else
        {
            saved.State = JobState.NeedsAttention;
            saved.Reason = "finalize-lost";
            saved.Message = "VESPER could not verify which file was saved. Check the destination folder, then resume to check again.";
            saved.NextRetryUtc = null;
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

    private static bool IsPublicationPath(DownloadJob job, string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(job.DestinationFolder)) return false;
        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationFolder));
            string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(folder, parent, comparison) && PathSafety.TryCheckUnlinkedDirectory(folder, out _);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsPublicationFile(DownloadJob job, string path, long? length = null) =>
        IsPublicationPath(job, path)
        && PathSafety.Inspect(path) is { Exists: true, IsReparsePoint: false, IsCloudPlaceholder: false, IsDirectory: false } file
        && (length is null || file.Length == length);

    private static bool PublicationPathIsMissing(DownloadJob job, string path)
    {
        if (!IsPublicationPath(job, path)) return false;
        try { File.GetAttributes(path); }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }

    private static string? PublicationDigest(DownloadJob job) =>
        job.PendingConversionDigest ?? (job.PendingConversionBytes is null && job.Sha256Verified ? job.ExpectedSha256 : null);

    // Keep the verified output open until the completion row commits.
    private static FileStream? OpenPublicationFile(DownloadJob job, string path, out bool matches)
    {
        matches = false;
        string? digest = PublicationDigest(job);
        long? length = job.PendingConversionBytes ?? job.TotalBytes;
        if (digest is null || length is null || !IsPublicationFile(job, path)) return null;

        FileStream? file = null;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (IsPublicationFile(job, path))
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

    private static void MarkPublished(DownloadJob job, string target)
    {
        job.FinalPath = target;
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
            if (job.Output == MediaOutput.Original && string.Equals(Path.GetExtension(target), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                job.ContentType = "audio/mpeg";
                job.MediaQuality ??= "MP3";
            }
            else
            {
                job.ContentType = job.Output switch { MediaOutput.M4a => "audio/mp4", MediaOutput.Opus => "audio/ogg", MediaOutput.Wav => "audio/wav", _ => "video/mp4" };
                job.MediaQuality = job.Output switch { MediaOutput.M4a => "Audio AAC", MediaOutput.Opus => "Opus · copied", MediaOutput.Wav => "PCM audio", _ => "H.264 + AAC" };
            }
            job.Category = FileNames.CategoryFor(job.FileName);
        }
        else if (job.Output != MediaOutput.Original)
        {
            job.Reason = "saved-original";
            job.Message = $"Saved as {Path.GetExtension(job.FileName).TrimStart('.').ToUpperInvariant()}: this PC can't convert this audio or video. "
                + "Installing Microsoft's free Web Media Extensions from the Store usually adds the missing decoder.";
        }
    }

    private void FinishPublication(DownloadJob job, bool recovering = false)
    {
        RecoveryManifest.Delete(job.PartialPath);
        if (job.PendingConversionBytes is null || !DeleteTemporaryFile(job, job.PartialPath)) return;
        try
        {
            if (recovering)
            {
                MergeTransfer.DeleteSidecars(job);
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
    }

    /// <summary>After a database loss, partial files with a valid recovery manifest come back as "needs link" entries.</summary>
    private void ImportManifests()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Options.DefaultFolder };
        if (Directory.Exists(Options.DefaultFolder))
        {
            try { foreach (string sub in Directory.EnumerateDirectories(Options.DefaultFolder)) folders.Add(sub); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable subfolders are skipped */ }
        }

        long order = 0;
        foreach (string folder in folders.Where(Directory.Exists))
        {
            foreach (string manifest in Directory.EnumerateFiles(folder, "*.vpart" + RecoveryManifest.Extension))
            {
                if (!RecoveryManifest.TryRecover(manifest, out DownloadJob job, out string reason))
                {
                    Log(null, "manifest-skipped", $"{Path.GetFileName(manifest)}: {reason}");
                    continue;
                }
                if (_jobs.ContainsKey(job.Id)) continue;
                job.QueueOrder = order++;
                SaveLocked(job);
                _jobs[job.Id] = job;
                AddDuplicateCandidateLocked(job);
                Log(job.Id, "manifest-imported", job.FileName);
            }
        }
    }

    // ---- availability and verification -----------------------------------------------------------------------------------

    public Task RefreshAvailabilityAsync() => Task.Run(RefreshAvailability);

    private void RefreshAvailability()
    {
        List<(Guid Id, string Path)> targets;
        lock (_lock)
            targets = [.. _jobs.Values.Where(j => j.State == JobState.Completed && !string.IsNullOrEmpty(j.FinalPath)).Select(j => (j.Id, j.FinalPath!))];

        foreach ((Guid id, string path) in targets)
        {
            bool missing = !PathSafety.Inspect(path).Exists;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(id, out DownloadJob? job) || missing == _missing.Contains(id)) continue;
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
            DownloadJob saved = job.CopyForUpdate();
            saved.ExpectedSha256 = normalized;
            saved.Sha256Verified = false;
            SaveRecoveredJob(job, saved);
            Changed(job);
        }
        FlushOutbox();
        return true;
    }
}
