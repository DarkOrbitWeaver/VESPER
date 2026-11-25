using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Vesper.Engine;

/// <summary>
/// Auxiliary recovery record stored next to the partial file. The database is authoritative; this
/// only lets a job be re-created if the database is lost. It is written AFTER a checkpoint committed,
/// so everything it claims was already flushed. It deliberately omits query strings (signed tokens).
/// </summary>
internal static class RecoveryManifest
{
    public const string Extension = ".vmeta";
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    private const string PreviousSuffix = ".previous";

    private sealed record Body(int V, Guid JobId, long Generation, string Url, string FileName, long? Total,
        string Extents, string? ETag, string? LastModified, int Identity, string Destination, int Kind = 0,
        string? MediaMetadataJson = null, string? MediaThumbnailUrl = null, string? PartialIdentity = null,
        string? FinalResourceIdentity = null, string? ManifestIdentity = null,
        string? ExpectedSha256 = null, string? PieceMapJson = null, bool RescueCandidate = false,
        bool UnverifiedResumeApproved = false, int RescueMode = 0, bool IntegrityUnavailable = false);

    private sealed record Envelope(string? Check, string? Body);
    private sealed record MergeSwapProof(int Version, string Fingerprint, string Phase, long MuxLength,
        string? MuxSha256, long TracksLength, string? TracksSha256, string? MuxIdentity, string? TracksIdentity);

    public static string PathFor(string partialPath) => partialPath + Extension;

    public static void Write(DownloadJob job, SafeFileHandle? livePartialHandle = null)
    {
        if (job.PartialPath.Length == 0) return;
        if (livePartialHandle is not null)
            PathSafety.RequireTemporaryIdentity(livePartialHandle, job.PartialIdentity);

        string path = PathFor(job.PartialPath);
        string temp = path + ".tmp";
        string? tempIdentity = null;
        try
        {
            using SafeFileHandle handle = PathSafety.OpenTemporaryHandle(temp, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, allowDelete: true);
            using (var file = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false))
            {
                try
                {
                    tempIdentity = PathSafety.TemporaryFileIdentity(file.SafeFileHandle);
                    var body = new Body(2, job.Id, job.Generation, UrlPolicy.Redact(job.SourceUrl), job.FileName, job.TotalBytes,
                        job.Extents.Serialize(), job.ETag, job.LastModified?.ToString("O"), (int)job.Identity, job.DestinationFolder,
                        (int)job.Kind, job.MediaMetadataJson, ResourcePath(job.MediaThumbnailUrl), job.PartialIdentity,
                        job.FinalResourceIdentity, tempIdentity, job.ExpectedSha256, job.PieceMapJson,
                        job.RescueCandidate, job.UnverifiedResumeApproved, (int)job.RescueMode);
                    string bodyJson = JsonSerializer.Serialize(body);
                    string json = JsonSerializer.Serialize(new Envelope(Hash(bodyJson), bodyJson));
                    if (Encoding.UTF8.GetByteCount(json) + 3 > MaxManifestBytes)
                    {
                        // Keep the sidecar readable but never import an incomplete integrity obligation.
                        body = body with { PieceMapJson = null, IntegrityUnavailable = true };
                        bodyJson = JsonSerializer.Serialize(body);
                        json = JsonSerializer.Serialize(new Envelope(Hash(bodyJson), bodyJson));
                        if (Encoding.UTF8.GetByteCount(json) + 3 > MaxManifestBytes)
                            throw new IOException("The recovery record exceeds its size limit.");
                    }
                    using (var writer = new StreamWriter(file, Encoding.UTF8, 4096, leaveOpen: true))
                    {
                        writer.Write(json);
                        writer.Flush();
                    }
                    file.Flush(flushToDisk: true);
                }
                catch
                {
                    try { PathSafety.DeleteOpenTemporaryFile(file.SafeFileHandle, temp); }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
                    throw;
                }
            }

            InstallManifest(temp, path, job, tempIdentity, livePartialHandle);
            tempIdentity = null;
        }
        catch
        {
            if (tempIdentity is not null)
                _ = PathSafety.TryDeleteTemporaryFile(temp, tempIdentity, out _);
            throw;
        }
    }

    private static void InstallManifest(string temp, string path, DownloadJob job, string? tempIdentity,
        SafeFileHandle? livePartialHandle)
    {
        RequireFileIdentity(temp, tempIdentity, "The recovery stage changed before it could be installed.");
        bool hasPrior;
        string? priorIdentity = null;
        try
        {
            using SafeFileHandle priorHandle = PathSafety.OpenTemporaryHandle(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            priorIdentity = PathSafety.TemporaryFileIdentity(priorHandle);
            if (!TryReadBody(priorHandle, out Body priorBody)
                || !ManifestMatchesJob(priorBody, path, job)
                || (!TryRecover(path, out DownloadJob prior, out _, publishedPath: null, publishedIdentity: null,
                    livePartialHandle: livePartialHandle, manifestHandle: priorHandle)
                    && !MatchesMuxedMergeIdentity(job, priorBody.PartialIdentity)))
                throw new IOException("An existing recovery file could not be verified and was left unchanged.");
            hasPrior = true;
        }
        catch (FileNotFoundException)
        {
            hasPrior = false;
        }

        if (hasPrior)
        {
            RequireFileIdentity(path, priorIdentity, "The existing recovery file changed before replacement.");
            RequireFileIdentity(temp, tempIdentity, "The recovery stage changed before it could be installed.");
            string previous = path + PreviousSuffix;
            if (File.Exists(previous) || Directory.Exists(previous))
                throw new IOException("A previous recovery replacement is still waiting to be recovered.");

            if (OperatingSystem.IsWindows())
            {
                if (!PathSafety.TryMoveTemporaryFile(path, previous, priorIdentity, out string moveError))
                    throw new IOException("The existing recovery file could not be staged safely: " + moveError);
            }
            else
            {
                File.Move(path, previous, overwrite: false);
            }

            try
            {
                MoveManifestStage(temp, path, tempIdentity);
                using SafeFileHandle installed = VerifyInstalledManifest(path, job, tempIdentity);
                if (OperatingSystem.IsWindows())
                    _ = PathSafety.TryDeleteTemporaryFile(previous, priorIdentity, out _);
                else
                {
                    try { File.Delete(previous); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception installError) when (installError is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            if (!PathSafety.TryMoveTemporaryFile(previous, path, priorIdentity, out string restoreError))
                                throw new IOException(restoreError);
                        }
                        else File.Move(previous, path, overwrite: false);
                    }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException("The previous recovery file could not be restored after an interrupted replacement.",
                            new AggregateException(installError, restoreError));
                    }
                }
                throw;
            }

            if (OperatingSystem.IsWindows())
                _ = PathSafety.TryDeleteTemporaryFile(previous, priorIdentity, out _);
            else
            {
                try { File.Delete(previous); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        else
        {
            MoveManifestStage(temp, path, tempIdentity);
            using SafeFileHandle installed = VerifyInstalledManifest(path, job, tempIdentity);
        }
    }

    private static void MoveManifestStage(string temp, string path, string? tempIdentity)
    {
        RequireFileIdentity(temp, tempIdentity, "The recovery stage changed before it could be installed.");
        if (OperatingSystem.IsWindows())
        {
            if (tempIdentity is null)
                throw new IOException("The recovery stage has no stable file identity.");
            if (!PathSafety.TryMoveTemporaryFile(temp, path, tempIdentity, out string error))
                throw new IOException(error);
        }
        else File.Move(temp, path, overwrite: false);
    }

    private static SafeFileHandle VerifyInstalledManifest(string path, DownloadJob job, string? expectedIdentity)
    {
        SafeFileHandle handle = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (expectedIdentity is not null
                && !string.Equals(expectedIdentity, PathSafety.TemporaryFileIdentity(handle), StringComparison.Ordinal))
                throw new IOException("The installed recovery file changed before it could be verified.");
            if (!TryReadBody(handle, out Body body) || !ManifestMatchesJob(body, path, job))
                throw new IOException("The installed recovery file does not match this download.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequireFileIdentity(string path, string? expected, string message)
    {
        if (expected is null) return;
        using SafeFileHandle file = PathSafety.OpenTemporaryHandle(path, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        if (!string.Equals(expected, PathSafety.TemporaryFileIdentity(file), StringComparison.Ordinal))
            throw new IOException(message);
    }

    public static bool Delete(DownloadJob job)
    {
        string manifestPath = PathFor(job.PartialPath);
        try
        {
            using SafeFileHandle manifestHandle = PathSafety.OpenTemporaryHandle(manifestPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, allowDelete: true);
            if (!TryReadBody(manifestHandle, out Body body)
                || !ManifestMatchesJob(body, manifestPath, job)
                || (!PartialStillMatches(job, body) && !PublishedFileStillMatches(job)))
                return false;

            PathSafety.DeleteOpenTemporaryFile(manifestHandle, manifestPath);
            DeletePreviousForJob(manifestPath, job);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PartialStillMatches(DownloadJob job, Body body)
    {
        if (job.PartialIdentity is null || body.PartialIdentity is null) return false;

        bool manifestOwnsPartial = string.Equals(body.PartialIdentity, job.PartialIdentity, StringComparison.Ordinal)
            || MatchesMuxedMergeIdentity(job, body.PartialIdentity);
        if (!manifestOwnsPartial) return false;

        try
        {
            using SafeFileHandle partial = PathSafety.OpenTemporaryHandle(job.PartialPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = RandomAccess.GetLength(partial);
            if (body.Total is long bytes && length < bytes) return false;
            return string.Equals(job.PartialIdentity, PathSafety.TemporaryFileIdentity(partial), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PublishedFileStillMatches(DownloadJob job)
    {
        if (job.FinalPath is not { Length: > 0 } finalPath
            || job.PublishedFileIdentity is not { Length: 64 } publishedIdentity)
            return false;

        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(finalPath));
            if (folder is null || !SamePath(folder, Path.TrimEndingDirectorySeparator(job.DestinationFolder))) return false;

            using SafeFileHandle published = PathSafety.OpenTemporaryHandle(finalPath, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return string.Equals(publishedIdentity, PathSafety.TemporaryFileIdentity(published), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryGetPartialPath(string manifestPath, out string partial)
    {
        partial = "";
        if (manifestPath.EndsWith(Extension + PreviousSuffix, StringComparison.OrdinalIgnoreCase))
            partial = manifestPath[..^(Extension.Length + PreviousSuffix.Length)];
        else if (manifestPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            partial = manifestPath[..^Extension.Length];
        return partial.Length > 0;
    }

    internal static void RecoverInterruptedReplacements(string folder, Action<string>? report = null,
        Action<string>? beforeRestore = null)
    {
        string[] backups;
        try { backups = Directory.GetFiles(folder, "*.vpart" + Extension + PreviousSuffix, SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke("A previous recovery file could not be inspected: " + ex.Message);
            return;
        }

        foreach (string backup in backups)
        {
            string primary = backup[..^PreviousSuffix.Length];
            if (!TryGetPartialPath(primary, out _)) continue;

            if (File.Exists(primary) || Directory.Exists(primary))
            {
                try
                {
                    using SafeFileHandle currentHandle = PathSafety.OpenTemporaryHandle(primary, FileMode.Open,
                        FileAccess.Read, FileShare.Read);
                    if (!TryRecover(primary, out DownloadJob current, out _, publishedPath: null, publishedIdentity: null,
                        manifestHandle: currentHandle)) continue;

                    using SafeFileHandle previousHandle = PathSafety.OpenTemporaryHandle(backup, FileMode.Open,
                        FileAccess.Read, FileShare.Read, allowDelete: true);
                    if (TryRecover(backup, out DownloadJob previous, out _, publishedPath: null, publishedIdentity: null,
                        manifestHandle: previousHandle)
                        && current.Id == previous.Id && (current.Kind == previous.Kind
                            || current.Generation > previous.Generation && SwitchableKind(current.Kind) && SwitchableKind(previous.Kind)
                                && current.PartialIdentity is { Length: 64 })
                        && SamePath(current.PartialPath, previous.PartialPath)
                        && current.Generation >= previous.Generation
                        && string.Equals(current.PartialIdentity, previous.PartialIdentity, StringComparison.Ordinal))
                        PathSafety.DeleteOpenTemporaryFile(previousHandle, backup);
                }
                catch (FileNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { report?.Invoke("A previous recovery file was kept because it could not be checked: " + ex.Message); }
                continue;
            }

            try
            {
                // Keep this handle through the move. The file ID used by the rename must
                // come from the same manifest bytes that passed recovery validation.
                using SafeFileHandle previousHandle = PathSafety.OpenTemporaryHandle(backup, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (!TryRecover(backup, out _, out string problem, publishedPath: null, publishedIdentity: null,
                    manifestHandle: previousHandle))
                {
                    report?.Invoke("A previous recovery file was kept because it could not be verified: " + problem);
                    continue;
                }

                string? identity = PathSafety.TemporaryFileIdentity(previousHandle);
                if (OperatingSystem.IsWindows() && identity is null)
                {
                    report?.Invoke("A previous recovery file was kept because it has no stable file identity.");
                    continue;
                }

                beforeRestore?.Invoke(backup);
                if (OperatingSystem.IsWindows())
                {
                    if (!PathSafety.TryMoveTemporaryFile(backup, primary, identity, out string error))
                        report?.Invoke("A previous recovery file was kept because it could not be restored: " + error);
                }
                else File.Move(backup, primary, overwrite: false);
            }
            catch (FileNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report?.Invoke("A previous recovery file was kept because it could not be restored: " + ex.Message);
            }
        }
    }

    private static void DeletePreviousForJob(string manifestPath, DownloadJob job)
    {
        string previous = manifestPath + PreviousSuffix;
        try
        {
            using SafeFileHandle handle = PathSafety.OpenTemporaryHandle(previous, FileMode.Open,
                FileAccess.Read, FileShare.Read, allowDelete: true);
            if (TryReadBody(handle, out Body body)
                && ManifestMatchesJob(body, manifestPath, job))
                PathSafety.DeleteOpenTemporaryFile(handle, previous);
        }
        catch (FileNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static bool TryReadBody(SafeFileHandle handle, out Body body)
    {
        body = null!;
        try
        {
            long length = RandomAccess.GetLength(handle);
            if (length is <= 0 or > MaxManifestBytes) return false;
            byte[] bytes = new byte[(int)length];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
                if (read <= 0) return false;
                total += read;
            }

            string json = Encoding.UTF8.GetString(bytes);
            if (json.StartsWith('\uFEFF')) json = json[1..];
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json);
            if (envelope?.Body is not { Length: > 0 } bodyJson || envelope.Check is not { Length: > 0 } check
                || !Hash(bodyJson).Equals(check, StringComparison.Ordinal))
                return false;

            body = JsonSerializer.Deserialize<Body>(bodyJson)!;
            return body is not null && ManifestIdentityMatches(body, handle);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SwitchableKind(JobKind kind) => kind is JobKind.Http or JobKind.Dash or JobKind.Hls;

    private static bool ManifestMatchesJob(Body body, string manifestPath, DownloadJob job)
    {
        if (body.V is not (1 or 2) || body.JobId != job.Id
            || (body.Kind != (int)job.Kind && !(body.Generation < job.Generation
                && SwitchableKind((JobKind)body.Kind) && SwitchableKind(job.Kind)
                && job.PartialIdentity is { Length: 64 } && body.PartialIdentity == job.PartialIdentity))
            || body.Generation < 0 || body.Generation > job.Generation || body.Total is < 0
            || body.Url is not { Length: > 0 } || !UrlPolicy.TryParse(body.Url, out _, out _)
            || body.Extents is null || !ExtentSet.TryParse(body.Extents, out _)
            || !Enum.IsDefined((IdentityLevel)body.Identity)
            || body.FinalResourceIdentity is not null && !HttpTransport.IsResourceIdentity(body.FinalResourceIdentity))
            return false;

        string partial = manifestPath[..^Extension.Length];
        string folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
        string jobDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationFolder));
        if (!SamePath(partial, job.PartialPath)
            || (!SamePath(folder, jobDestination) && !PartialFile.IsStagingFolder(folder, jobDestination))
            || body.Destination is not { Length: > 0 } destination
            || !SamePath(Path.TrimEndingDirectorySeparator(destination), jobDestination))
            return false;

        if (string.Equals(body.PartialIdentity, job.PartialIdentity, StringComparison.Ordinal)) return true;
        return MatchesMuxedMergeIdentity(job, body.PartialIdentity);
    }

    private static bool ManifestIdentityMatches(Body body, SafeFileHandle handle)
    {
        if (body.ManifestIdentity is null) return true;
        return string.Equals(body.ManifestIdentity, PathSafety.TemporaryFileIdentity(handle), StringComparison.Ordinal);
    }

    private static bool MatchesMuxedMergeIdentity(DownloadJob job, string? manifestIdentity)
    {
        if (job.Kind != JobKind.Merge || manifestIdentity is not { Length: 64 }
            || job.MediaState is not { Length: > 0 } state
            || !MediaMergePlan.TryParse(job.MediaPlanJson, allowPrivate: true, out MediaMergePlan plan, out _))
            return false;

        MergeSwapProof? proof;
        try { proof = JsonSerializer.Deserialize<MergeSwapProof>(state, new JsonSerializerOptions { MaxDepth = 8 }); }
        catch (JsonException) { return false; }

        return proof is { Phase: "muxed", MuxLength: > 0, TracksLength: > 0 }
            && proof.Fingerprint == plan.Fingerprint
            && (plan.IsSegmented ? proof.Version is 1 or 2 : proof.Version == 1)
            && IsSha256(proof.MuxSha256) && IsSha256(proof.TracksSha256)
            && IsSha256(proof.MuxIdentity) && IsSha256(proof.TracksIdentity)
            && string.Equals(job.PartialIdentity, proof.MuxIdentity, StringComparison.Ordinal)
            && (string.Equals(manifestIdentity, proof.MuxIdentity, StringComparison.Ordinal)
                || string.Equals(manifestIdentity, proof.TracksIdentity, StringComparison.Ordinal));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Returns a job in NeedsLink state: the manifest knows the identity but not the secret link.</summary>
    public static bool TryRecover(string manifestPath, out DownloadJob job, out string problem)
        => TryRecover(manifestPath, out job, out problem, publishedPath: null, publishedIdentity: null,
            livePartialHandle: null);

    private static bool TryRecover(string manifestPath, out DownloadJob job, out string problem,
        string? publishedPath, string? publishedIdentity, SafeFileHandle? livePartialHandle = null,
        SafeFileHandle? manifestHandle = null)
    {
        job = null!;
        problem = "";
        SafeFileHandle? ownedManifestHandle = null;
        try
        {
            if (!TryGetPartialPath(manifestPath, out string partial))
            { problem = "invalid recovery file name"; return false; }
            if (manifestHandle is null)
                manifestHandle = ownedManifestHandle = PathSafety.OpenTemporaryHandle(manifestPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
            SafeFileHandle manifestSource = manifestHandle;
            long manifestLength = RandomAccess.GetLength(manifestSource);
            if (manifestLength > MaxManifestBytes)
            { problem = "recovery file is too large"; return false; }
            byte[] manifestBytes = new byte[(int)manifestLength];
            int manifestTotal = 0;
            while (manifestTotal < manifestBytes.Length)
            {
                int read = RandomAccess.Read(manifestSource, manifestBytes.AsSpan(manifestTotal), manifestTotal);
                if (read <= 0) { problem = "malformed recovery file"; return false; }
                manifestTotal += read;
            }
            string manifestJson = Encoding.UTF8.GetString(manifestBytes);
            if (manifestJson.StartsWith('\uFEFF')) manifestJson = manifestJson[1..];
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(manifestJson);
            if (envelope?.Body is not { Length: > 0 } bodyJson || envelope.Check is not { Length: > 0 } check)
            { problem = "malformed recovery file"; return false; }
            if (Hash(bodyJson) != check) { problem = "checksum mismatch"; return false; }
            Body? body = JsonSerializer.Deserialize<Body>(bodyJson);
            if (body is null || body.V is not (1 or 2)) { problem = "unsupported version"; return false; }
            if (!ManifestIdentityMatches(body, manifestSource))
            { problem = "recovery file identity mismatch"; return false; }
            if (body.JobId == Guid.Empty) { problem = "invalid recovery job id"; return false; }
            if (body.Generation < 0) { problem = "invalid recovery generation"; return false; }
            if (body.Total is < 0) { problem = "invalid recovery file length"; return false; }
            if (!Enum.IsDefined((IdentityLevel)body.Identity)) { problem = "invalid recovery identity"; return false; }
            if (!Enum.IsDefined((JobKind)body.Kind)) { problem = "invalid recovery job kind"; return false; }
            if (body.V == 2 && (!Enum.IsDefined((RescueMode)body.RescueMode)
                || body.ExpectedSha256 is not null && !IsSha256(body.ExpectedSha256)
                || body.PieceMapJson is not null && (!PieceMap.TryParse(body.PieceMapJson, out PieceMap pieceMap, out _)
                    || pieceMap.TotalLength != body.Total)
                || body.RescueCandidate && body.ExpectedSha256 is null
                || body.RescueMode == (int)RescueMode.R1b && body.PieceMapJson is null && !body.IntegrityUnavailable))
            { problem = "invalid recovery integrity obligations"; return false; }
            if (body.FinalResourceIdentity is not null && !HttpTransport.IsResourceIdentity(body.FinalResourceIdentity))
            { problem = "invalid recovery resource identity"; return false; }
            if (body.Url is not { Length: > 0 } || !UrlPolicy.TryParse(body.Url, out _, out _))
            { problem = "invalid recovery source URL"; return false; }
            if (body.Extents is null || !ExtentSet.TryParse(body.Extents, out ExtentSet extents))
            { problem = "unreadable extents"; return false; }

            // The checksum is unkeyed, so a planted manifest could name another folder or file.
            // Only import one that describes the partial sitting next to it.
            string here = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
            if (string.IsNullOrWhiteSpace(body.Destination))
            { problem = "the recovery file names an invalid folder"; return false; }
            string destination;
            try { destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(body.Destination)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { problem = "the recovery file names an invalid folder"; return false; }
            if (!string.Equals(destination, here, StringComparison.OrdinalIgnoreCase)
                && !PartialFile.IsStagingFolder(here, destination))
            { problem = "the recovery file names a different folder"; return false; }

            SafeFileHandle saved;
            SafeFileHandle? savedOwner = null;
            bool savedAtPublishedPath = false;
            if (livePartialHandle is not null)
            {
                saved = livePartialHandle;
            }
            else
            {
                try
                {
                    savedOwner = PathSafety.OpenTemporaryHandle(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
                    saved = savedOwner;
                }
                catch (FileNotFoundException) when (publishedPath is not null && publishedIdentity is not null)
                {
                    string published = Path.GetFullPath(publishedPath);
                    string? publishedDirectory = Path.GetDirectoryName(published);
                    if (!string.Equals(publishedDirectory, destination, StringComparison.OrdinalIgnoreCase)
                        || body.PartialIdentity is null
                        || !string.Equals(body.PartialIdentity, publishedIdentity, StringComparison.Ordinal))
                    { problem = "the recovery file does not match the published file"; return false; }

                    savedOwner = PathSafety.OpenTemporaryHandle(published, FileMode.Open, FileAccess.Read, FileShare.Read);
                    saved = savedOwner;
                    savedAtPublishedPath = true;
                }
            }

            using (savedOwner)
            {
                if (body.PartialIdentity is not null)
                    PathSafety.RequireTemporaryIdentity(saved, body.PartialIdentity);
                if (savedAtPublishedPath
                    && !string.Equals(body.PartialIdentity, publishedIdentity, StringComparison.Ordinal))
                { problem = "the recovery file does not match the published file"; return false; }
                long length = RandomAccess.GetLength(saved);

                string fileName = FileNames.Sanitize(body.FileName);
                extents.ClampTo(length);   // never claim bytes the file cannot contain
                string? thumbnailUrl = UrlPolicy.TryParse(body.MediaThumbnailUrl, out Uri thumbnail, out _)
                    ? thumbnail.GetLeftPart(UriPartial.Path) : null;
                string? mediaMetadataJson = MediaMergePlan.SupportsAudioMetadataFile(body.FileName)
                    && MediaMergePlan.TryParseMetadata(body.MediaMetadataJson, out MediaMergeMetadata? metadata)
                    && (MediaMergePlan.HasAudioTagValues(metadata!) || thumbnailUrl is not null)
                    ? JsonSerializer.Serialize(metadata) : null;

                job = new DownloadJob
                {
                    Id = body.JobId, Kind = (JobKind)body.Kind,
                    SourceUrl = UrlPolicy.Redact(body.Url), FileName = fileName, NameLocked = true,
                    DestinationFolder = destination, PartialPath = partial, PartialIdentity = body.PartialIdentity, TotalBytes = body.Total, Extents = extents,
                    FinalResourceIdentity = body.V == 2 && !body.IntegrityUnavailable ? body.FinalResourceIdentity : null,
                    ExpectedSha256 = body.V == 2 ? body.ExpectedSha256 : null,
                    PieceMapJson = body.V == 2 ? body.PieceMapJson : null,
                    RescueCandidate = body.V == 2 && body.RescueCandidate,
                    UnverifiedResumeApproved = body.V == 2 && !body.IntegrityUnavailable && body.UnverifiedResumeApproved,
                    RescueMode = body.V == 2 ? (RescueMode)body.RescueMode : RescueMode.None,
                    State = body.Kind == (int)JobKind.Http && !body.IntegrityUnavailable ? JobState.NeedsLink : JobState.NeedsAttention,
                    Reason = body.IntegrityUnavailable ? "recovery-integrity-missing" : "restored",
                    Message = body.IntegrityUnavailable ? "The saved piece-checksum map exceeded the recovery file limit. Provide the original trusted checksums before continuing. Saved data was kept."
                        : body.Kind == (int)JobKind.Http ? "Restored from a recovery file. Provide the download link and trusted verification evidence to continue."
                        : "Streaming partial restored without a private transfer plan. Resolve a new copy; saved fragments were kept and must not be treated as an ordinary HTTP file.",
                    ETag = body.ETag, Identity = body.V == 2 && !body.IntegrityUnavailable ? (IdentityLevel)body.Identity : IdentityLevel.None, AcceptsRanges = true, Generation = body.Generation,
                    MediaMetadataJson = mediaMetadataJson,
                    MediaThumbnailUrl = thumbnailUrl,
                    LastModified = DateTimeOffset.TryParse(body.LastModified, out DateTimeOffset lm) ? lm : null
                };
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
        finally { ownedManifestHandle?.Dispose(); }
    }

    private static string? ResourcePath(string? address) => UrlPolicy.TryParse(address, out Uri uri, out _)
        ? uri.GetLeftPart(UriPartial.Path) : null;

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
