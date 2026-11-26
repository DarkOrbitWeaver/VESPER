using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    private sealed record MergeSwapProof(int Version, string Fingerprint, string Phase, long MuxLength,
        string? MuxSha256, long TracksLength, string? TracksSha256, string? MuxIdentity, string? TracksIdentity);
    private sealed record RecoveryManifestEnvelope(string? Check, string? Body);
    private sealed record RecoveryManifestBody(int V, Guid JobId, long Generation, string? Url, string? FileName, long? Total,
        string? Extents, string? ETag, string? LastModified, int Identity, string? Destination, int Kind = 0,
        string? MediaMetadataJson = null, string? MediaThumbnailUrl = null, string? PartialIdentity = null,
        string? FinalResourceIdentity = null);

    public async Task<CancelDownloadResult> CancelDownloadAsync(Guid id)
    {
        using var operation = EnterOperation();
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
            string message = $"Cancellation is unfinished. {error} Resolve the ownership or access issue, then choose Cancel download to retry.";
            try { PersistUiMutationLocked(job, pending => pending.Message = message); }
            catch (LocalStorageException ex) { Log(job.Id, CancelCleanup, ex.Message); }
            return new(false, $"Could not safely remove download data: {error} Resolve the ownership or access issue, then choose Cancel download to retry.");
        }

        try { _store.Delete(job.Id); }
        catch (SqliteException ex)
        {
            Log(job.Id, CancelCleanup, ex.Message);
            return new(false, "The temporary files were removed. Choose Cancel download to retry removing the entry.");
        }

        _jobs.Remove(job.Id);
        _mediaDeliveryHistory.Forget(job.Id);
        _credentials.Remove(job.Id);
        _missing.Remove(job.Id);
        Log(job.Id, "cancelled", "The unfinished download and its owned temporary files were removed.");
        return new(true, "Download cancelled.");
    }

    private bool TryRemoveCancelledFiles(DownloadJob job, out string error)
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
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationFolder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The recorded download path is invalid.";
            return false;
        }

        string suffix = "." + job.Id.ToString("N")[..8] + ".vpart";
        string name = Path.GetFileName(partial);
        string partialFolder = Path.GetDirectoryName(partial)!;
        bool staged = PartialFile.IsStagingFolder(partialFolder, folder);
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || name.Length <= suffix.Length
            || (!staged && !string.Equals(partialFolder, folder, StringComparison.OrdinalIgnoreCase)))
        {
            error = "The temporary file no longer matches this download.";
            return false;
        }

        // These paths can be deleted only when the current job has saved proof for them.
        // Unknown stages are left in place and keep the cancellation entry actionable.
        string[] unprovenStages =
        [
            RecoveryManifest.PathFor(partial) + ".tmp", RecoveryManifest.PathFor(partial) + ".previous", partial + ".samples",
            partial + ".convert.mp4", partial + ".convert.opus", partial + ".convert.wav", partial + ".convert.mp3",
            partial + ".convert-audio.mp4", partial + ".convert-tagged.mp4", partial + ".convert-tagged.mp3",
            partial + ".tagged.mp3", partial + ".tagged.flac", partial + ".tagged.ogg", partial + ".tagged.opus"
        ];

        string manifestPath = RecoveryManifest.PathFor(partial);
        string tracksPath = MergeTransfer.TracksPath(job);
        string mergingPath = MergeTransfer.MergingPath(job);
        MergeSwapProof? swap = ReadMergeSwapProof(job);
        var opened = new List<(string Path, SafeFileHandle Handle)>();
        try
        {
            if (staged)
            {
                if (!PathSafety.TryCheckUnlinkedDirectory(folder, out error)) return false;
                try { File.GetAttributes(partialFolder); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
            }
            if (!PathSafety.TryCheckUnlinkedDirectory(partialFolder, out error)) return false;

            string rollbackPattern = $".vesper-rollback-{job.Id:N}-*";
            string? rollback = Directory.EnumerateFiles(partialFolder, rollbackPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (rollback is not null)
            {
                error = "A conversion backup has no saved ownership proof. Move it out of the download folder: "
                    + PathSafety.ForDisplay(rollback);
                return false;
            }

            foreach (string file in unprovenStages)
            {
                try
                {
                    using SafeFileHandle stage = PathSafety.OpenTemporaryHandle(file, FileMode.Open, FileAccess.Read,
                        FileShare.Read, allowDelete: true);
                    error = "A temporary stage has no saved ownership proof. Move it out of the download folder: "
                        + PathSafety.ForDisplay(file);
                    return false;
                }
                catch (FileNotFoundException) { }
            }

            foreach (string file in new[] { partial, manifestPath, tracksPath, mergingPath })
            {
                try
                {
                    SafeFileHandle handle = PathSafety.OpenTemporaryHandle(file, FileMode.Open, FileAccess.Read,
                        FileShare.Read, allowDelete: true);
                    opened.Add((file, handle));
                }
                catch (FileNotFoundException) { }
            }

            SafeFileHandle? partialHandle = OpenedHandle(opened, partial);
            SafeFileHandle? manifestHandle = OpenedHandle(opened, manifestPath);
            SafeFileHandle? tracksHandle = OpenedHandle(opened, tracksPath);
            SafeFileHandle? mergingHandle = OpenedHandle(opened, mergingPath);

            if (partialHandle is not null)
            {
                if (swap is { Phase: "muxed" } muxed)
                    PathSafety.RequireTemporaryIdentity(partialHandle, muxed.MuxIdentity);
                else if (swap is { Phase: "swapping" } swapping && tracksHandle is null)
                    RequireOneTemporaryIdentity(partialHandle, swapping.TracksIdentity, swapping.MuxIdentity);
                else if (swap is { Phase: "swapping" } swappingWithTracks)
                    PathSafety.RequireTemporaryIdentity(partialHandle, swappingWithTracks.MuxIdentity);
                else
                    PathSafety.RequireTemporaryIdentity(partialHandle, job.PartialIdentity);
            }
            if (manifestHandle is not null
                && !RecoveryManifestBelongsToJob(manifestHandle, manifestPath, partial, folder, job, swap))
                throw new IOException("The recovery file has no matching job identity.");
            if (tracksHandle is not null)
            {
                if (swap is null)
                    throw new IOException("The saved tracks have no ownership proof.");
                PathSafety.RequireTemporaryIdentity(tracksHandle, swap.TracksIdentity);
                if (!MatchesSavedContent(tracksHandle, swap.TracksLength, swap.TracksSha256!))
                    throw new IOException("The saved tracks no longer match the merge cursor.");
            }
            if (mergingHandle is not null)
            {
                if (swap is not { Phase: "swapping" })
                    throw new IOException("The merge output has no saved swapping proof.");
                PathSafety.RequireTemporaryIdentity(mergingHandle, swap.MuxIdentity);
                if (!MatchesSavedContent(mergingHandle, swap.MuxLength, swap.MuxSha256!))
                    throw new IOException("The merge output no longer matches the swap cursor.");
            }

            // Open every file before deleting any. On Windows, deletion uses those same handles.
            foreach (var (file, handle) in opened.Where(item => !string.Equals(item.Path, partial, StringComparison.OrdinalIgnoreCase)))
                PathSafety.DeleteOpenTemporaryFile(handle, file);
            foreach (var (file, handle) in opened.Where(item => string.Equals(item.Path, partial, StringComparison.OrdinalIgnoreCase)))
            {
                Failpoint("cancel.beforePartialDelete");
                PathSafety.DeleteOpenTemporaryFile(handle, file);
            }
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

    private MergeSwapProof? ReadMergeSwapProof(DownloadJob job)
    {
        if (job.Kind != JobKind.Merge || job.MediaState is not { Length: > 0 } state
            || !MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan plan, out _))
            return null;

        MergeSwapProof? cursor;
        try { cursor = JsonSerializer.Deserialize<MergeSwapProof>(state, new JsonSerializerOptions { MaxDepth = 8 }); }
        catch (JsonException) { return null; }

        if (cursor is null || cursor.Phase is not ("swapping" or "muxed") || cursor.Fingerprint != plan.Fingerprint
            || (plan.IsSegmented ? cursor.Version is not (1 or 2) : cursor.Version != 1)
            || cursor.MuxLength <= 0 || !IsSha256(cursor.MuxSha256) || !IsSha256(cursor.MuxIdentity)
            || cursor.TracksLength <= 0 || !IsSha256(cursor.TracksSha256))
            return null;

        string? tracksIdentity = cursor.TracksIdentity;
        if (cursor.Phase == "swapping")
        {
            tracksIdentity ??= job.PartialIdentity; // Older swapping cursors predate TracksIdentity.
            if (tracksIdentity is not { Length: 64 } || !tracksIdentity.All(Uri.IsHexDigit)
                || !string.Equals(tracksIdentity, job.PartialIdentity, StringComparison.Ordinal))
                return null;
        }
        else if (tracksIdentity is not { Length: 64 } || !tracksIdentity.All(Uri.IsHexDigit)
            || !string.Equals(job.PartialIdentity, cursor.MuxIdentity, StringComparison.Ordinal))
        {
            return null;
        }

        return cursor with { TracksIdentity = tracksIdentity };
    }

    private static SafeFileHandle? OpenedHandle(List<(string Path, SafeFileHandle Handle)> opened, string path)
    {
        foreach (var item in opened)
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) return item.Handle;
        return null;
    }

    private static bool RecoveryManifestBelongsToJob(SafeFileHandle handle, string path, string partial, string folder,
        DownloadJob job, MergeSwapProof? swap)
    {
        if (RecoveryManifest.TryRecover(path, out DownloadJob recovered, out _))
        {
            return recovered.Id == job.Id && recovered.Kind == job.Kind
                && string.Equals(Path.GetFullPath(recovered.PartialPath), partial, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFullPath(recovered.DestinationFolder), folder, StringComparison.OrdinalIgnoreCase)
                && ManifestPartialIdentityMatches(recovered.PartialIdentity, job, swap)
                && recovered.Generation <= job.Generation;
        }

        // A crash can leave the previous manifest behind after a merge checkpoint commits.
        // Its checksum and saved track identity still prove that it belongs to this job.
        long length = RandomAccess.GetLength(handle);
        if (length is <= 0 or > RecoveryManifest.MaxManifestBytes) return false;
        byte[] bytes = new byte[(int)length];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
            if (read <= 0) return false;
            total += read;
        }

        try
        {
            string manifestJson = Encoding.UTF8.GetString(bytes);
            if (manifestJson.StartsWith('\uFEFF')) manifestJson = manifestJson[1..];
            RecoveryManifestEnvelope? envelope = JsonSerializer.Deserialize<RecoveryManifestEnvelope>(manifestJson);
            if (envelope?.Body is not { Length: > 0 } bodyJson || envelope.Check is not { Length: > 0 } check
                || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyJson))).Equals(check, StringComparison.Ordinal))
                return false;

            RecoveryManifestBody? body = JsonSerializer.Deserialize<RecoveryManifestBody>(bodyJson);
            if (body is null || body.V is not (1 or 2) || body.JobId != job.Id || body.Kind != (int)job.Kind
                || body.Generation < 0 || body.Generation > job.Generation || body.Total is < 0
                || body.FileName is not { Length: > 0 } || body.Url is not { Length: > 0 }
                || !UrlPolicy.TryParse(body.Url, out _, out _) || body.Extents is null
                || !ExtentSet.TryParse(body.Extents, out _) || !Enum.IsDefined((IdentityLevel)body.Identity)
                || body.FinalResourceIdentity is not null && !HttpTransport.IsResourceIdentity(body.FinalResourceIdentity))
                return false;

            return ManifestPartialIdentityMatches(body.PartialIdentity, job, swap)
                && body.Destination is { Length: > 0 } destination
                && string.Equals(Path.GetFullPath(destination), folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ManifestPartialIdentityMatches(string? manifestIdentity, DownloadJob job, MergeSwapProof? swap)
    {
        if (!OperatingSystem.IsWindows())
            return string.Equals(manifestIdentity, job.PartialIdentity, StringComparison.Ordinal);
        if (job.PartialIdentity is null) return false;
        if (string.Equals(manifestIdentity, job.PartialIdentity, StringComparison.Ordinal)) return true;

        return swap is { Phase: "muxed" } muxed
            && string.Equals(job.PartialIdentity, muxed.MuxIdentity, StringComparison.Ordinal)
            && string.Equals(manifestIdentity, muxed.TracksIdentity, StringComparison.Ordinal);
    }

    private static bool MatchesSavedContent(SafeFileHandle handle, long length, string expectedSha256)
    {
        if (length <= 0 || RandomAccess.GetLength(handle) != length) return false;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (read <= 0) return false;
            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireOneTemporaryIdentity(SafeFileHandle handle, params string?[] expectedIdentities)
    {
        if (!OperatingSystem.IsWindows()) return;
        string? actual = PathSafety.TemporaryFileIdentity(handle);
        if (actual is null || !expectedIdentities.Any(expected =>
                expected is not null && string.Equals(expected, actual, StringComparison.Ordinal)))
            throw new IOException("The partial path no longer contains a file saved by the merge cursor.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
