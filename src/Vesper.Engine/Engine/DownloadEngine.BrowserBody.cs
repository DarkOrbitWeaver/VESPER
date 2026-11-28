using System.Text.Json;

namespace Vesper.Engine;

public sealed record BrowserBodyTrackSpec(string Role, string Container, long? Length = null, string? Codec = null, string? Language = null);

/// <summary>Commands for the browser-byte receiver, supporting single-stream, unknown-length streaming, and dual-track native remuxing.</summary>
public sealed record BrowserBodyRequest(
    string Operation,
    string TransferId,
    string? Origin = null,
    string? FileName = null,
    long? Length = null,
    long? Offset = null,
    byte[]? Bytes = null,
    string? Digest = null,
    bool Session = false,
    bool VerifyMp4 = false,
    bool Automatic = true,
    int TrackIndex = 0,
    IReadOnlyList<BrowserBodyTrackSpec>? Tracks = null,
    string? PageUrl = null,
    string? OutputContainer = null,
    bool VerifyWebm = false);

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
                && _jobs.TryGetValue(id, out DownloadJob? accepted)
                && accepted.Kind is JobKind.BrowserStream or JobKind.Merge)
            {
                if (request.Operation == "status" && !string.Equals(request.Origin, accepted.SourceUrl, StringComparison.Ordinal))
                    return BridgeResponse.Fail(requestId, "missing", "No matching browser receipt.");
                // A remux can have a different published length. The receipt
                // acknowledges the browser's input, not the output container.
                long? received = accepted.TotalBytes;
                if (accepted.Kind == JobKind.Merge)
                {
                    received = MediaMergePlan.TryParse(accepted.MediaPlanJson, Options.AllowPrivateNetworks, out var plan, out _)
                        && plan.Provider == "browser" && plan.MediaId == request.TransferId
                        ? plan.Tracks.Sum(track => track.Size) : null;
                }
                return BridgeResponse.Success(requestId, data: new { accepted = true, jobId = id, offset = received,
                    state = accepted.State.ToString() });
            }
            if (request.Operation == "status")
                return BridgeResponse.Fail(requestId, "missing", "No durable browser receipt was found.");
            receiver = _browserBodies ??= new BrowserBodyReceiver(Options.DataDirectory, BrowserBodyDestination);
        }

        BrowserBodyReceiver.Reply reply;
        switch (request.Operation)
        {
            case "begin" when request.Origin is not null && request.FileName is not null:
                reply = await receiver.BeginAsync(
                    request.TransferId, request.Origin, request.FileName, request.Length, ct,
                    request.Session, request.VerifyMp4, request.Tracks, request.PageUrl, request.OutputContainer, request.VerifyWebm)
                    .ConfigureAwait(false);
                if (reply.Code == "storage")
                    return BridgeResponse.Fail(requestId, "storage", "Not enough free space for the browser file, or its folder is unavailable.");
                break;
            case "append" when request.Offset is long offset && request.Bytes is not null && request.Digest is not null:
                reply = await receiver.AppendAsync(request.TransferId, offset, request.Bytes, request.Digest, ct, request.TrackIndex).ConfigureAwait(false);
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
            return BridgeResponse.Fail(requestId, reply.Code, "The browser returned an invalid or incomplete media container. No file was accepted.");
        if (!reply.Ok)
            return BridgeResponse.Fail(requestId, reply.Code, "The browser stream could not be accepted. No completed file was claimed.");
        return BridgeResponse.Success(requestId, data: new { offset = reply.Offset, accepted = reply.JobId is not null, jobId = reply.JobId,
            mediaCheck = request.Operation == "begin" ? request.VerifyMp4 ? "mp4" : request.VerifyWebm ? "webm" : null : null });
    }

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
            var staging = new DriveInfo(Path.GetPathRoot(PartialsFolder)!);
            if (!staging.IsReady || staging.AvailableFreeSpace - Math.Max(0, Options.FreeSpaceReserve) < length) return null;
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            return drive.IsReady && drive.AvailableFreeSpace - Math.Max(0, Options.FreeSpaceReserve) >= length ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private AddResult AcceptBrowserBody(string transferId, BrowserBodyReceiver.Transfer body, string digest)
    {
        if (!TryNormalizeSha256(digest, out string expected))
            return new(AddStatus.Invalid, Guid.Empty, "Invalid browser copy integrity proof.");
        string category = FileNames.CategoryFor(body.Name);
        Guid id = body.JobId;
        DownloadJob job;

        // Retained WebM is also a playback reconstruction, not a byte-exact
        // original file. Route every declared track through the verified remux.
        if (body.Tracks is { Count: > 0 } specs)
        {
            string outContainer = body.OutputContainer ?? Path.GetExtension(body.Name).TrimStart('.').ToLowerInvariant();
            if (outContainer is not ("mp4" or "m4a" or "webm" or "mkv")) outContainer = "mp4";
            string mergedName = Path.ChangeExtension(body.Name, outContainer);
            var mergeTracks = new List<MergeTrack>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                BrowserBodyTrackSpec spec = specs[i];
                mergeTracks.Add(new MergeTrack(
                    specs.Count == 1 && spec.Role == "video" ? "muxed" : spec.Role,
                    $"browser-track-{i}",
                    body.Origin,
                    body.TrackLengths[i],
                    spec.Container,
                    spec.Codec,
                    $"browser-sha256:{expected}:{i}",
                    spec.Language is { Length: 3 } lang ? lang : null));
            }
            var mergePlan = new MediaMergePlan
            {
                Version = mergeTracks.Any(t => t.Role == "muxed") ? 3 : 1,
                Provider = "browser",
                MediaId = transferId,
                OutputContainer = outContainer,
                RequireVideo = specs.Any(t => t.Role is "video" or "muxed"),
                RequireAudio = specs.Any(t => t.Role is "audio" or "muxed"),
                Tracks = mergeTracks
            };
            string cursorState = JsonSerializer.Serialize(new MergeTransfer.Cursor(1, mergePlan.Fingerprint, "download"));
            job = new DownloadJob
            {
                Id = id,
                Kind = JobKind.Merge,
                SourceUrl = body.Origin,
                OriginalPageUrl = body.Origin,
                MediaProvider = "browser",
                MediaId = transferId,
                MediaQuality = "Browser authenticated stream",
                FileName = mergedName,
                NameLocked = true,
                DestinationFolder = body.Folder,
                PartialPath = body.File.Path,
                PartialIdentity = body.Identity,
                TotalBytes = body.Offset,
                MediaPlanJson = mergePlan.ToJson(),
                MediaState = cursorState,
                State = JobState.Queued,
                Category = FileNames.CategoryFor(mergedName),
                NonResumable = true
            };
        }
        else
        {
            job = new DownloadJob
            {
                Id = id,
                Kind = JobKind.BrowserStream,
                SourceUrl = body.Origin,
                OriginalPageUrl = body.Origin,
                FileName = body.Name,
                NameLocked = true,
                DestinationFolder = body.Folder,
                PartialPath = body.File.Path,
                PartialIdentity = body.Identity,
                TotalBytes = body.Offset,
                ExpectedSha256 = expected,
                State = JobState.Queued,
                Category = category,
                NonResumable = true
            };
        }

        job.Extents.Add(new ByteRange(0, body.Offset));
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
                body.Claimed = true;
                throw;
            }
            body.Claimed = true;
            body.File.MarkIdentitySaved();
            WriteManifest(job, body.File.Handle);
            body.File.Dispose();
            _jobs[id] = job;
            Changed(job);
        }
        FlushOutbox();
        ScheduleNext();
        return new(AddStatus.Added, id, "The browser stream was accepted for verification and publication.");
    }
}
