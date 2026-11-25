namespace Vesper.Engine;

/// <summary>Commands for the opt-in browser-byte receiver. No browser-provided paths or credentials.</summary>
public sealed record BrowserBodyRequest(string Operation, string TransferId, string? Origin = null,
    string? FileName = null, long? Length = null, long? Offset = null, byte[]? Bytes = null, string? Digest = null, bool Session = false, bool VerifyMp4 = false);

public sealed partial class DownloadEngine
{
    private BrowserBodyReceiver? _browserBodies;
    private const string BrowserBodySubmission = "browser-body:";

    public async Task<BridgeResponse> ReceiveBrowserBodyAsync(string requestId, BrowserBodyRequest request, CancellationToken ct)
    {
        using var operation = EnterOperation(ct);
        ct = operation.Token;
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
            receiver = _browserBodies ??= new BrowserBodyReceiver(Options.DataDirectory, BrowserBodyDestination);
        }

        BrowserBodyReceiver.Reply reply;
        switch (request.Operation)
        {
            case "begin" when request.Origin is not null && request.FileName is not null && request.Length is long length:
                reply = await receiver.BeginAsync(request.TransferId, request.Origin, request.FileName, length, ct, request.Session, request.VerifyMp4)
                    .ConfigureAwait(false);
                if (reply.Code == "storage")
                    return BridgeResponse.Fail(requestId, "storage", "Not enough free space for the browser file, or its folder is unavailable.");
                break;
            case "append" when request.Offset is long offset && request.Bytes is not null && request.Digest is not null:
                reply = await receiver.AppendAsync(request.TransferId, offset, request.Bytes, request.Digest, ct).ConfigureAwait(false);
                break;
            case "finish":
                reply = await receiver.FinishAsync(request.TransferId,
                    (body, digest, token) => Task.FromResult(AcceptBrowserBody(request.TransferId, body, digest)), ct).ConfigureAwait(false);
                break;
            case "abort":
                await receiver.AbortAsync(request.TransferId, ct).ConfigureAwait(false);
                return BridgeResponse.Success(requestId);
            default:
                return BridgeResponse.Fail(requestId, "invalid", "Invalid browser transfer operation.");
        }
        if (reply.Code == "invalid-media")
            return BridgeResponse.Fail(requestId, reply.Code, "The browser returned an invalid or incomplete MP4. No file was accepted.");
        if (!reply.Ok)
            return BridgeResponse.Fail(requestId, reply.Code, "The browser copy could not be accepted. No completed file was claimed.");
        return BridgeResponse.Success(requestId, data: new { offset = reply.Offset, accepted = reply.JobId is not null, jobId = reply.JobId,
            mediaCheck = request.Operation == "begin" && request.VerifyMp4 ? "mp4" : null });
    }

    // The same destination rules as an ordinary download, checked once before any byte is written.
    // Bytes land in this folder's staging area, so only one copy of the file ever needs space.
    private string? BrowserBodyDestination(string name, long length)
    {
        string category = FileNames.CategoryFor(FileNames.Sanitize(name));
        string destination = Options.CategorySubfolders ? Path.Combine(Options.DefaultFolder, category) : Options.DefaultFolder;
        if (!PathSafety.TryResolveFolder(destination, create: true, out string folder, out _)
            || !PathSafety.TryCheckUnlinkedDirectory(folder, out _))
            return null;
        folder = Path.TrimEndingDirectorySeparator(folder);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            return drive.IsReady && drive.AvailableFreeSpace - Math.Max(0, Options.FreeSpaceReserve) >= length ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // The partial is already complete and hashed. Only a byte-exact copy becomes a durable engine job,
    // which then verifies the SHA-256 from disk and publishes through the ordinary owned-partial path.
    private AddResult AcceptBrowserBody(string transferId, BrowserBodyReceiver.Transfer body, string digest)
    {
        if (!TryNormalizeSha256(digest, out string expected))
            return new(AddStatus.Invalid, Guid.Empty, "Invalid browser copy integrity proof.");
        string category = FileNames.CategoryFor(body.Name);
        Guid id = Guid.NewGuid();
        var job = new DownloadJob
        {
            Id = id, Kind = JobKind.BrowserStream, SourceUrl = body.Origin, OriginalPageUrl = body.Origin,
            FileName = body.Name, NameLocked = true, DestinationFolder = body.Folder, PartialPath = body.File.Path,
            PartialIdentity = body.Identity, TotalBytes = body.Length, ExpectedSha256 = expected,
            State = JobState.Queued, Category = category, NonResumable = true
        };
        job.Extents.Add(new ByteRange(0, body.Length));
        lock (_lock)
        {
            if (_disposed) return new(AddStatus.Busy, Guid.Empty, "VESPER is closing.");
            string submission = BrowserBodySubmission + transferId;
            if (_store.TryGetSubmission(submission, out Guid prior) && _jobs.ContainsKey(prior))
                return new(AddStatus.AlreadySubmitted, prior, "The browser copy was already accepted.");
            if (_jobs.Count != 0 && _jobs.Values.Min(x => x.QueueOrder) == long.MinValue)
                RebaseQueueOrdersLocked();
            job.QueueOrder = _jobs.Count == 0 ? 0 : _jobs.Values.Min(x => x.QueueOrder) - 1;
            try { SaveLocked(job, submission); }
            catch
            {
                // The outcome is uncertain: durable state may own this partial, so it is never deleted.
                body.Claimed = true;
                throw;
            }
            body.Claimed = true;
            body.File.MarkIdentitySaved();
            WriteManifest(job, body.File.Handle);
            // Do not expose a queued job while its transfer handle is still held.
            body.File.Dispose();
            _jobs[id] = job;
            Changed(job);
        }
        FlushOutbox();
        ScheduleNext();
        return new(AddStatus.Added, id, "The browser copy was accepted for verification and publication.");
    }
}
