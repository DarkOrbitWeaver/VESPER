using System.Buffers;
using System.Security.Cryptography;

namespace Vesper.Engine;

/// <summary>Commands for the opt-in browser-byte receiver. No browser-provided paths or credentials.</summary>
public sealed record BrowserBodyRequest(string Operation, string TransferId, string? Origin = null,
    string? FileName = null, long? Length = null, long? Offset = null, byte[]? Bytes = null, string? Digest = null, bool Session = false);

public sealed partial class DownloadEngine
{
    private BrowserBodyReceiver? _browserBodies;
    private const string BrowserBodySubmission = "browser-body:";

    public async Task<BridgeResponse> ReceiveBrowserBodyAsync(string requestId, BrowserBodyRequest request, CancellationToken ct)
    {
        if (!BridgeProtocol.IsRequestId(request.TransferId))
            return BridgeResponse.Fail(requestId, "invalid", "Invalid browser transfer.");
        BrowserBodyReceiver receiver;
        lock (_lock)
        {
            if (_disposed) return BridgeResponse.Fail(requestId, "closed", "VESPER is closing.");
            if (_store.TryGetSubmission(BrowserBodySubmission + request.TransferId, out Guid id)
                && _jobs.TryGetValue(id, out DownloadJob? accepted) && accepted.Kind == JobKind.BrowserStream)
            {
                if (request.Operation == "status" && !string.Equals(request.Origin, accepted.SourceUrl, StringComparison.Ordinal))
                    return BridgeResponse.Fail(requestId, "missing", "No matching browser receipt.");
                return BridgeResponse.Success(requestId, data: new { accepted = true, jobId = id, offset = accepted.TotalBytes,
                    state = accepted.State.ToString() });
            }
            // Status is read-only and cannot open a receiver or restart a transfer.
            if (request.Operation == "status")
                return BridgeResponse.Fail(requestId, "missing", "No durable browser receipt was found.");
            receiver = _browserBodies ??= new BrowserBodyReceiver(Options.DataDirectory);
        }

        BrowserBodyReceiver.Reply reply;
        switch (request.Operation)
        {
            case "begin" when request.Origin is not null && request.FileName is not null && request.Length is long length:
                if (length is < 1 or > BrowserBodyReceiver.MaxBytes || !CanStageBrowserBody(request.FileName, length))
                    return BridgeResponse.Fail(requestId, "storage", "Not enough free space for the browser file and verified destination copy.");
                reply = await receiver.BeginAsync(request.TransferId, request.Origin, request.FileName, length, ct, request.Session)
                    .ConfigureAwait(false);
                break;
            case "append" when request.Offset is long offset && request.Bytes is not null && request.Digest is not null:
                reply = await receiver.AppendAsync(request.TransferId, offset, request.Bytes, request.Digest, ct).ConfigureAwait(false);
                break;
            case "finish":
                reply = await receiver.FinishAsync(request.TransferId,
                    (origin, name, size, digest, body, token) =>
                        AcceptBrowserBodyAsync(request.TransferId, origin, name, size, digest, body, token), ct).ConfigureAwait(false);
                break;
            case "abort":
                await receiver.AbortAsync(request.TransferId, ct).ConfigureAwait(false);
                return BridgeResponse.Success(requestId);
            default:
                return BridgeResponse.Fail(requestId, "invalid", "Invalid browser transfer operation.");
        }
        if (!reply.Ok)
            return BridgeResponse.Fail(requestId, reply.Code, "The browser copy could not be accepted. No completed file was claimed.");
        return BridgeResponse.Success(requestId, data: new { offset = reply.Offset, accepted = reply.JobId is not null, jobId = reply.JobId });
    }

    private bool CanStageBrowserBody(string name, long length)
    {
        string destination = Options.CategorySubfolders
            ? Path.Combine(Options.DefaultFolder, FileNames.CategoryFor(FileNames.Sanitize(name))) : Options.DefaultFolder;
        try
        {
            string? inboxRoot = Path.GetPathRoot(Path.GetFullPath(Options.DataDirectory));
            string? destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
            if (inboxRoot is null || destinationRoot is null) return false;
            var inbox = new DriveInfo(inboxRoot);
            var output = new DriveInfo(destinationRoot);
            long reserve = Math.Max(0, Options.FreeSpaceReserve);
            if (inboxRoot.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase))
                return inbox.IsReady && inbox.AvailableFreeSpace - reserve >= length * 2;
            return inbox.IsReady && output.IsReady && inbox.AvailableFreeSpace - reserve >= length
                && output.AvailableFreeSpace - reserve >= length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // The inbox is volatile and delete-on-close. Only a byte-exact complete copy
    // becomes a durable engine job, via the ordinary owned-partial publication path.
    private async Task<AddResult> AcceptBrowserBodyAsync(string transferId, string origin, string name,
        long length, string digest, Stream body, CancellationToken ct)
    {
        if (!TryNormalizeSha256(digest, out string expected))
            return new(AddStatus.Invalid, Guid.Empty, "Invalid browser copy integrity proof.");
        name = FileNames.Sanitize(name);
        string category = FileNames.CategoryFor(name);
        string destination = Options.CategorySubfolders ? Path.Combine(Options.DefaultFolder, category) : Options.DefaultFolder;
        if (!PathSafety.TryResolveFolder(destination, create: true, out string folder, out string error)
            || !PathSafety.TryCheckUnlinkedDirectory(folder, out error))
            return new(AddStatus.DestinationUnavailable, Guid.Empty, error);
        folder = Path.TrimEndingDirectorySeparator(folder);
        // Other downloads or applications can consume space during the transfer.
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            if (!drive.IsReady || drive.AvailableFreeSpace - Math.Max(0, Options.FreeSpaceReserve) < length)
                return new(AddStatus.DestinationUnavailable, Guid.Empty, "Not enough free space to publish the browser file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(AddStatus.DestinationUnavailable, Guid.Empty, "Cannot check free space at the destination."); }
        Guid id = Guid.NewGuid();
        string staging = PartialFile.PrepareStagingFolder(folder);
        string path = Path.Combine(staging, FileNames.Sanitize(name) + "." + id.ToString("N")[..8] + ".vpart");
        using PartialFile file = PartialFile.Open(path, FileMode.CreateNew, allowDelete: true);
        bool committed = false;
        bool saveAttempted = false;
        try
        {
            string? identity = file.CaptureIdentity();
            if (OperatingSystem.IsWindows() && identity is null)
                throw new IOException("Cannot establish the staged browser file identity.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long offset = 0;
                while (offset < length)
                {
                    int count = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct)
                        .ConfigureAwait(false);
                    if (count == 0) throw new IOException("The browser copy ended early.");
                    await file.WriteAsync(buffer.AsMemory(0, count), offset, ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    offset += count;
                }
                if (await body.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0
                    || !expected.Equals(Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The browser copy failed its transport integrity check.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

            file.Flush();
            ct.ThrowIfCancellationRequested();
            var job = new DownloadJob
            {
                Id = id, Kind = JobKind.BrowserStream, SourceUrl = origin, OriginalPageUrl = origin,
                FileName = name, NameLocked = true, DestinationFolder = folder, PartialPath = path,
                PartialIdentity = identity, TotalBytes = length, ExpectedSha256 = expected,
                State = JobState.Queued, Category = category, NonResumable = true
            };
            job.Extents.Add(new ByteRange(0, length));
            lock (_lock)
            {
                if (_disposed) return new(AddStatus.Busy, Guid.Empty, "VESPER is closing.");
                string submission = BrowserBodySubmission + transferId;
                if (_store.TryGetSubmission(submission, out Guid prior) && _jobs.ContainsKey(prior))
                    return new(AddStatus.AlreadySubmitted, prior, "The browser copy was already accepted.");
                if (_jobs.Count != 0 && _jobs.Values.Min(x => x.QueueOrder) == long.MinValue)
                    RebaseQueueOrdersLocked();
                job.QueueOrder = _jobs.Count == 0 ? 0 : _jobs.Values.Min(x => x.QueueOrder) - 1;
                saveAttempted = true;
                SaveLocked(job, submission);
                committed = true;
                file.MarkIdentitySaved();
                WriteManifest(job, file.Handle);
                // Do not expose a queued job while its transfer handle is still held.
                file.Dispose();
                _jobs[id] = job;
                Changed(job);
            }
        }
        finally
        {
            // If a persistence operation has an uncertain outcome, retain the
            // partial rather than delete something that durable state could own.
            if (!committed && !saveAttempted)
                file.DeleteUnclaimedNewFile();
        }
        file.Dispose();
        FlushOutbox();
        ScheduleNext();
        return new(AddStatus.Added, id, "The browser copy was accepted for verification and publication.");
    }
}
