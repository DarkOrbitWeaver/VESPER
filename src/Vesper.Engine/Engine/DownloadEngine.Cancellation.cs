using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Vesper.Engine;

public sealed record CancelDownloadResult(bool Cancelled, string Message);

public sealed partial class DownloadEngine
{
    private const string CancelCleanup = "cancel-cleanup";
    private const string RestartCleanup = "restart-cleanup";

    // A cancel is one operation, not Pause + RemoveFromList: no runner or queue action
    // may start using this job while its files are being discarded.
    private readonly HashSet<Guid> _cancelling = [];

    internal static bool IsCleanupPending(DownloadJob job) => IsCleanupPending(job.State, job.Reason);

    internal static bool IsCleanupPending(JobState state, string reason) =>
        reason is CancelCleanup or RestartCleanup || state == JobState.Cancelled && reason == "cancelled";

    private static bool CanDiscardPartial(DownloadJob job) =>
        job.Kind != JobKind.BrowserFile
        && job.State is not (JobState.Completed or JobState.Verifying or JobState.Finalizing or JobState.Converting)
        && job.FinalPath is null && job.PendingFinalPath is null;

    public async Task<CancelDownloadResult> CancelDownloadAsync(Guid id)
    {
        bool cancelling = false;
        try
        {
            Task? running = null;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(id, out DownloadJob? job) || job.Kind == JobKind.BrowserFile)
                    return new(false, "This download is no longer available.");
                if (!CanDiscardPartial(job))
                    return new(false, "This download has already reached file publication. Nothing was deleted.");
                if (!_cancelling.Add(id))
                    return new(false, "This download is already being cancelled.");
                cancelling = true;

                if (_sessions.TryGetValue(id, out JobSession? session))
                {
                    session.Intent = StopIntent.Cancel;
                    session.Cts.Cancel();
                    running = session.RunTask;
                }
            }

            if (running is not null) await running.ConfigureAwait(false);

            CancelDownloadResult result;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(id, out DownloadJob? job))
                    return new(false, "This download no longer exists.");
                if (_sessions.ContainsKey(id) || !CanDiscardPartial(job))
                    return new(false, "The download reached file publication before cancellation. Nothing was deleted.");

                result = FinishCancellationLocked(job);
                if (!result.Cancelled) Changed(job);
            }

            // Deliver any older state notifications before the removal notification, so
            // a delayed queued snapshot cannot bring the cancelled row back to the UI.
            FlushOutbox();
            if (result.Cancelled)
            {
                JobRemoved?.Invoke(id);
                ScheduleNext();
            }
            return result;
        }
        finally
        {
            if (cancelling)
            {
                lock (_lock) _cancelling.Remove(id);
            }
        }
    }

    private CancelDownloadResult FinishCancellationLocked(DownloadJob job)
    {
        // A settled runner may have failed to save its outcome. Require our own cleanup commit before deleting bytes.
        PersistUiMutationLocked(job, static pending =>
        {
            pending.State = JobState.Cancelled;
            pending.NextRetryUtc = null;
            pending.Reason = CancelCleanup;
            pending.Message = "Cancellation is unfinished. Choose Cancel download to retry cleanup.";
        });
        RemoveDuplicateCandidateLocked(job, job.SourceUrl);
        _forced.Remove(job.Id);

        if (!TryRemoveCancelledFiles(job, out string error))
        {
            Log(job.Id, CancelCleanup, error);
            return new(false, $"Could not remove download data: {error} Choose Cancel download to retry.");
        }

        try { _store.Delete(job.Id); }
        catch (SqliteException ex)
        {
            Log(job.Id, CancelCleanup, ex.Message);
            return new(false, "The temporary files were removed. Choose Cancel download to retry removing the entry.");
        }

        _jobs.Remove(job.Id);
        _credentials.Remove(job.Id);
        _missing.Remove(job.Id);
        Log(job.Id, "cancelled", "The unfinished download and its owned temporary files were removed.");
        return new(true, "Download cancelled.");
    }

    private static bool TryRemoveCancelledFiles(DownloadJob job, out string error)
    {
        error = "";
        string partial, folder;
        try
        {
            if (!Path.IsPathFullyQualified(job.PartialPath) || !Path.IsPathFullyQualified(job.DestinationFolder))
            {
                error = "The recorded download path is invalid.";
                return false;
            }
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

        var opened = new List<(string Path, SafeFileHandle Handle)>();
        try
        {
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    SafeFileHandle handle = PathSafety.OpenTemporaryHandle(file, FileMode.Open, FileAccess.Read,
                        FileShare.Read, allowDelete: true);
                    opened.Add((file, handle));
                }
                catch (FileNotFoundException) { }
            }

            // Open every file before deleting any. On Windows, deletion uses those same handles.
            foreach (var (file, handle) in opened)
                PathSafety.DeleteOpenTemporaryFile(handle, file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            foreach (var (_, handle) in opened) handle.Dispose();
        }
    }
}
