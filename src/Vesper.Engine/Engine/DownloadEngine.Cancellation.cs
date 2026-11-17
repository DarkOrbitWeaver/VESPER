namespace Vesper.Engine;

public sealed record CancelDownloadResult(bool Cancelled, string Message);

public sealed partial class DownloadEngine
{
    // A cancel is one operation, not Pause + RemoveFromList: no runner or queue action
    // may start using this job while its files are being discarded.
    private readonly HashSet<Guid> _cancelling = [];

    public async Task<CancelDownloadResult> CancelDownloadAsync(Guid id)
    {
        Task? running;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || job.Kind == JobKind.BrowserFile)
                return new(false, "This download is no longer available.");
            if (_cancelling.Contains(id))
                return new(false, "This download is already being cancelled.");
            if (job.State is JobState.Completed or JobState.Verifying or JobState.Finalizing or JobState.Converting
                || job.FinalPath is not null || job.PendingFinalPath is not null)
                return new(false, "This download has already reached file publication. Nothing was deleted.");

            if (_sessions.TryGetValue(id, out JobSession? session))
            {
                session.Intent = StopIntent.Cancel;
                session.Cts.Cancel();
                running = session.RunTask;
            }
            else
            {
                // Persist the cancellation before touching files. If the app crashes during
                // cleanup, recovery never silently resumes the discarded download.
                PersistUiMutationLocked(job, pending =>
                {
                    pending.State = JobState.Cancelled;
                    pending.NextRetryUtc = null;
                    pending.Reason = "cancelled";
                    pending.Message = "Cancelling download.";
                });
                Changed(job);
                running = null;
            }
            _cancelling.Add(id);
        }

        try
        {
            if (running is not null) await running.ConfigureAwait(false);

            CancelDownloadResult result;
            bool removed = false;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(id, out DownloadJob? job))
                    return new(false, "This download no longer exists.");
                if (_sessions.ContainsKey(id) || job.State is JobState.Completed or JobState.Verifying
                    or JobState.Finalizing or JobState.Converting || job.FinalPath is not null
                    || job.PendingFinalPath is not null)
                    return new(false, "The download reached file publication before cancellation. Nothing was deleted.");

                if (!TryRemoveCancelledFiles(job, out string error))
                {
                    string message = "Could not remove download data: " + error;
                    PersistUiMutationLocked(job, pending =>
                    {
                        pending.State = JobState.Cancelled;
                        pending.NextRetryUtc = null;
                        pending.Reason = "cancel-cleanup";
                        pending.Message = message;
                    });
                    Changed(job);
                    result = new(false, job.Message);
                }
                else
                {
                    _store.Delete(id);
                    RemoveDuplicateCandidateLocked(job, job.SourceUrl);
                    _jobs.Remove(id);
                    _credentials.Remove(id);
                    _missing.Remove(id);
                    _forced.Remove(id);
                    Log(id, "cancelled", "The unfinished download and its owned temporary files were removed.");
                    removed = true;
                    result = new(true, "Download cancelled.");
                }
            }

            // Deliver any older state notifications before the removal notification, so
            // a delayed queued snapshot cannot bring the cancelled row back to the UI.
            FlushOutbox();
            if (removed)
            {
                JobRemoved?.Invoke(id);
                ScheduleNext();
            }
            return result;
        }
        finally
        {
            lock (_lock) _cancelling.Remove(id);
        }
    }

    private static bool TryRemoveCancelledFiles(DownloadJob job, out string error)
    {
        error = "";
        string partial, folder;
        try
        {
            partial = Path.GetFullPath(job.PartialPath);
            folder = Path.GetFullPath(job.DestinationFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The recorded download path is invalid.";
            return false;
        }

        string suffix = "." + job.Id.ToString("N")[..8] + ".vpart";
        string name = Path.GetFileName(partial);
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || name.Length <= suffix.Length
            || !string.Equals(Path.GetDirectoryName(partial), folder, StringComparison.OrdinalIgnoreCase))
        {
            error = "The temporary file no longer matches this download.";
            return false;
        }

        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                error = "The download directory is a link; no files were deleted.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The download directory is unavailable.";
            return false;
        }

        // Exact files produced by this job. Never scan a directory or delete by wildcard.
        // Include all generated tag extensions: a renamed download may use another one.
        string[] files =
        [
            partial, RecoveryManifest.PathFor(partial), RecoveryManifest.PathFor(partial) + ".tmp",
            MergeTransfer.TracksPath(job), MergeTransfer.MergingPath(job), partial + ".samples",
            partial + ".convert.mp4", partial + ".convert.opus", partial + ".convert.wav",
            partial + ".convert-audio.mp4", partial + ".convert-tagged.mp4",
            partial + ".tagged.mp3", partial + ".tagged.flac", partial + ".tagged.ogg", partial + ".tagged.opus"
        ];

        // First reject every known unsafe path before deleting even the first partial.
        // During deletion recheck each path again to limit symlink/placeholder races.
        string[] owned = [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (string file in owned)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                        || PathSafety.Inspect(file).IsCloudPlaceholder)
                    {
                        error = "A temporary path was replaced by a directory, link or cloud placeholder.";
                        return false;
                    }
                    if (pass == 1) File.Delete(file);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = pass == 0 ? "A temporary path could not be checked: " + ex.Message
                        : "A temporary file could not be removed: " + ex.Message;
                    return false;
                }
            }
        }

        return true;
    }
}
