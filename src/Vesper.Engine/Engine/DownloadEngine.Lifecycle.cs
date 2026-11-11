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
        PartialFile file = session.OpenFile();
        long total = job.TotalBytes ?? job.Extents.End;

        // Rule 7: never claim completion unless every byte is committed.
        if (!job.Extents.Covers(0, total))
            return RunOutcome.NeedsAttention("incomplete", "Some data is missing even though the transfer ended. Saved data was kept; resume to fetch the rest.");

        if (file.Length != total) file.Truncate(total);     // stale tail from an older, longer file never ships
        file.Flush();
        UpdateJob(job, j => { j.TotalBytes = total; j.State = JobState.Finalizing; j.Message = ""; });
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
        return Publish(job);
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

    /// <summary>
    /// Publishes with "create or fail" semantics: File.Move never overwrites. The chosen name is persisted BEFORE
    /// the rename so a crash between the rename and the database update can be reconciled on restart.
    /// </summary>
    private RunOutcome Publish(DownloadJob job)
    {
        if (!PathSafety.TryResolveFolder(job.DestinationFolder, create: true, out _, out string problem))
            return RunOutcome.NeedsAttention("destination", problem + " The finished file is waiting at " + PathSafety.ForDisplay(job.PartialPath));

        WriteMarkOfTheWeb(job);

        for (int attempt = 0; attempt < 50; attempt++)
        {
            string target = FileNames.UniquePath(job.DestinationFolder, job.FileName);
            UpdateJob(job, j => j.PendingFinalPath = target);
            Failpoint("finalize.beforeMove");
            try
            {
                File.Move(job.PartialPath, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                continue;   // someone created that name in the meantime: pick the next free one
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RunOutcome.Retry("locked", "Another program (often antivirus) is using the file. VESPER will try again. " + ex.Message);
            }

            Failpoint("finalize.afterMove");
            UpdateJob(job, j =>
            {
                j.FinalPath = target;
                j.PendingFinalPath = null;
                j.State = JobState.Completed;
                j.CompletedUtc = DateTime.UtcNow;
                j.Reason = "";
                j.Message = "";
                j.RetryCount = 0;
                j.UnverifiedResumeApproved = false;
            });
            RecoveryManifest.Delete(job.PartialPath);
            Log(job.Id, "completed", job.FileName);
            return RunOutcome.Done();
        }
        return RunOutcome.NeedsAttention("name", "No free file name could be found in the destination folder.");
    }

    /// <summary>Marks the file as downloaded from the internet so Windows applies SmartScreen/Office protections.</summary>
    private void WriteMarkOfTheWeb(DownloadJob job)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string host = Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? source) ? source.GetLeftPart(UriPartial.Path) : "";
            string referrer = Uri.TryCreate(job.Referer, UriKind.Absolute, out Uri? r) ? r.GetLeftPart(UriPartial.Path) : "";
            File.WriteAllText(job.PartialPath + ":Zone.Identifier",
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
            }

            foreach (DownloadJob job in _jobs.Values.ToList())
            {
                switch (job.State)
                {
                    case JobState.Resolving or JobState.Downloading or JobState.WaitingForNetwork or JobState.WaitingForRetry or JobState.Verifying:
                        job.State = JobState.Queued;
                        job.Message = "Resumed after VESPER restarted.";
                        job.NextRetryUtc = null;
                        SaveLocked(job);
                        break;
                    case JobState.Finalizing:
                        ReconcileFinalizing(job);
                        break;
                }
            }
        }
    }

    /// <summary>VESPER died while publishing. Decide, from what is on disk, whether the rename happened.</summary>
    private void ReconcileFinalizing(DownloadJob job)
    {
        if (File.Exists(job.PartialPath))
        {
            // The rename did not happen; the runner will verify and publish again.
            job.State = JobState.Queued;
            job.Message = "Finishing after restart.";
        }
        else if (job.PendingFinalPath is { } published && PathSafety.Inspect(published) is { Exists: true } found
            && (job.TotalBytes is null || found.Length == job.TotalBytes))
        {
            job.FinalPath = published;
            job.PendingFinalPath = null;
            job.State = JobState.Completed;
            job.CompletedUtc ??= DateTime.UtcNow;
            job.Message = "";
            Log(job.Id, "reconciled", "Rename had completed before the interruption.");
        }
        else
        {
            job.State = JobState.NeedsAttention;
            job.Reason = "finalize-lost";
            job.Message = "VESPER was interrupted while finishing this file and cannot find it. Check the destination folder, or use Locate file.";
        }
        SaveLocked(job);
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
                _jobs[job.Id] = job;
                SaveLocked(job);
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
        UpdateIfExists(id, j => { j.ExpectedSha256 = normalized; j.Sha256Verified = false; });
        return true;
    }
}
