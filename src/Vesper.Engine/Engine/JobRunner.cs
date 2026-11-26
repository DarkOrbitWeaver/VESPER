using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace Vesper.Engine;

/// <summary>
/// Executes one run of one HTTP job: validate the server's answer, move bytes to disk through the session's
/// checkpoint pipeline, and classify how the run ended. It never edits durable state directly.
/// </summary>
internal sealed class JobRunner(DownloadEngine engine, JobSession session, HttpTransport http)
{
    private const int BufferSize = 128 * 1024;
    private readonly EngineOptions _o = engine.Options;
    private DownloadJob Job => session.Job;
    private Uri _url = null!;
    private string? _runResourceIdentity;
    private PieceMap? _pieces;
    private bool _singleConnection;
    private bool _forceLinear;

    private sealed record FirstResult(HttpResponseMessage Response, long Start, bool Ranged, bool Replay, bool Decoded, long? Total);

    public async Task<RunOutcome> RunAsync()
    {
        CancellationToken ct = session.Cts.Token;
        try
        {
            if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out string folder, out string folderError)
                || !PathSafety.TryCheckUnlinkedDirectory(folder, out folderError))
                return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");

            return Job.Kind switch
            {
                JobKind.Hls => HlsWebVttPlan.TryParse(Job.MediaPlanJson, out _)
                    ? await new HlsWebVttTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false)
                    : await new HlsTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Dash => await new DashTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Merge => await new MergeTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Http => await TransferAsync(ct).ConfigureAwait(false),
                JobKind.BrowserStream => await FinishBrowserCopyAsync(ct).ConfigureAwait(false),
                _ => RunOutcome.NeedsAttention("unsupported-kind", "This download requires a newer engine. Saved data was kept.")
            };
        }
        catch (FailpointCrash) { throw; }
        catch (RunnerStop stop) { await SettleAsync(); return stop.Outcome; }
        catch (OperationCanceledException) when (session.Intent != StopIntent.None)
        {
            await SettleAsync();
            if (session.Intent == StopIntent.LiveStop)
            {
                if (!HlsTransfer.TryRequestLiveStop(Job.MediaState, out string stopState))
                    return RunOutcome.NeedsAttention("live-empty", "No complete live segment is available to keep.");
                engine.UpdateJob(Job, job => job.MediaState = stopState);
                return new RunOutcome(JobState.Queued, "live-stop", "Finishing the saved recording.");
            }
            return session.Intent switch
            {
                StopIntent.Pause => new RunOutcome(JobState.Paused, "", ""),
                StopIntent.Cancel => new RunOutcome(JobState.Cancelled, "cancelled", "Cancelling download."),
                StopIntent.BrowserFallback => DownloadEngine.BrowserFallbackOutcome,
                StopIntent.Window => new RunOutcome(JobState.Queued, "schedule", "Waiting for the scheduled download window."),
                StopIntent.MeteredNetwork => engine.MeteredNetworkStopOutcome(),
                _ => new RunOutcome(JobState.Queued, "shutdown", "Will resume when VESPER starts.")
            };
        }
        catch (LocalStorageException ex)
        {
            await SettleAsync();
            return ex.IsDiskFull
                ? RunOutcome.NeedsAttention("disk-full", "The disk is full. Free some space, then resume. Saved data was kept.")
                : RunOutcome.NeedsAttention("storage", ex.Message + " Saved data was kept.");
        }
        catch (Exception ex) when (ErrorClassifier.IsBlocked(ex))
        {
            await SettleAsync();
            return RunOutcome.NeedsAttention("blocked", ex.Message);
        }
        catch (ProxyConfigurationException ex)
        {
            return RunOutcome.NeedsAttention("proxy", ex.Message);
        }
        catch (RedirectException ex)
        {
            await SettleAsync();
            return RunOutcome.NeedsAttention("redirect", ex.Message);
        }
        catch (Exception ex) when (ErrorClassifier.IsTransient(ex))
        {
            await SettleAsync();
            return ErrorClassifier.IsNetworkDown(ex)
                ? RunOutcome.WaitNetwork("No network connection. Waiting for it to return.")
                : RunOutcome.Retry("network", "Connection problem: " + ErrorClassifier.Describe(ex));
        }
        catch (Exception ex)
        {
            await SettleAsync();   // an unexpected error is a bug: stop conservatively and keep the data
            return RunOutcome.NeedsAttention("unexpected", "Unexpected error: " + ex.Message);
        }
    }

    /// <summary>Makes whatever was written durable before the run ends, whatever the reason.</summary>
    private async Task SettleAsync()
    {
        try { await session.CheckpointAsync(force: true).ConfigureAwait(false); }
        catch (FailpointCrash) { throw; }
        catch (Exception ex) { engine.Log(Job.Id, "checkpoint-failed", ErrorClassifier.Describe(ex)); }
    }

    // main flow
    private async Task<RunOutcome> FinishBrowserCopyAsync(CancellationToken ct)
    {
        // This is a finite browser-supplied body, not a downloadable HTTP URL.
        // Neither restart nor a missing partial may turn it into a network replay.
        if (Job.TotalBytes is not long total || total <= 0 || !Job.Extents.Covers(0, total)
            || Job.ExpectedSha256 is not { Length: 64 })
            return RunOutcome.NeedsAttention("browser-copy-incomplete", "The browser copy is incomplete. Select the file again in your browser.");
        PartialFile file = session.OpenFile(create: false);
        if (file.Length != total)
            return RunOutcome.NeedsAttention("browser-copy-incomplete", "The browser copy changed on disk. Saved data was kept.");
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> TransferAsync(CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(Job.SourceUrl, out _url, out string urlError))
            throw Stop(RunOutcome.NeedsLink("bad-url", urlError));

        if (!Job.Extents.IsEmpty && !HttpTransport.IsCurrentResourceIdentity(Job.FinalResourceIdentity))
            throw Stop(RunOutcome.NeedsAttention("redirect-identity-missing",
                "This saved download does not include a verified redirect destination. Its existing bytes were kept. Use a trusted checksum or piece list to verify them, or start a fresh copy."));

        PartialFile file = session.OpenFile();
        ReconcileWithFile(file);
        if (Job.PieceMapJson is not null) LoadPieces(file, ct);

        SemaphoreSlim hostGate = engine.HostGate(session.Host);
        for (int pass = 0; pass < 2; pass++)
        {
            if (Job.TotalBytes is long known && Job.Extents.Covers(0, known)) return await FinishAsync(ct).ConfigureAwait(false);

            long start = Job.Extents.Gaps(Job.TotalBytes ?? long.MaxValue).FirstOrDefault().Start;
            await hostGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                FirstResult first = await OpenFirstAsync(start, ct).ConfigureAwait(false);
                using HttpResponseMessage response = first.Response;
                CheckBrowserResponse(first);
                CaptureMetadata(first);
                if (first.Total is long total) { EnsureSpace(file, total); file.EnsureLength(total); }
                await WaitForBrowserApprovalAsync(ct).ConfigureAwait(false);

                using (ProcessingTiming.Measure(Job.Id, ProcessingPhase.Transfer, first.Total ?? 0))
                {
                    if (first.Ranged) await RunRangedAsync(first, hostGate, ct).ConfigureAwait(false);
                    else await RunLinearAsync(first, file, ct).ConfigureAwait(false);
                }
            }
            catch (RangeProblemException problem) when (!_singleConnection)
            {
                // Segmentation is no longer defensible. Committed bytes are untouched; go on with one connection.
                _singleConnection = true;
                _forceLinear = problem.Code is "range-ignored" or "range-rejected";
                await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
                engine.Log(Job.Id, "single-connection", problem.Message);
                continue;
            }
            finally
            {
                hostGate.Release();
            }
            return await FinishAsync(ct).ConfigureAwait(false);
        }
        throw Stop(RunOutcome.NeedsAttention("bad-range", "The server keeps returning inconsistent ranges. Saved data was kept."));
    }

    private async Task<RunOutcome> FinishAsync(CancellationToken ct)
    {
        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    // The validator ladder for a parked Chrome download (see BrowserTransferProof). A strong ETag must match exactly.
    // A Last-Modified proof needs a response with no ETag at all, the identical date, a Date that keeps that date
    // strong, and the exact length. Every other fact Chrome observed must be reproduced before a single body byte
    // is read. Any difference stops this job with "browser-fallback" so the extension resumes Chrome's own copy.
    private void CheckBrowserResponse(FirstResult first)
    {
        // An adopted download resumes saved bytes under its own validators, which the resume path checks itself.
        if (Job.BrowserProof is not { Adopted: false } proof) return;
        HttpResponseMessage response = first.Response;
        string? etag = HttpFacts.StrongEtag(response);
        bool sameRepresentation = proof.UsesLastModified
            ? response.Headers.ETag is null && proof.LastModifiedValue is { } stamp
                && response.Headers.Date is { } served && served - stamp >= TimeSpan.FromSeconds(60)
            : proof.UsesAttachmentLength
                ? first.Ranged && first.Total == proof.Length
                    && response.Content.Headers.ContentDisposition?.DispositionType.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true
                : proof.UsesMetadata
                    ? proof.ETag is null || string.Equals(etag, proof.ETag, StringComparison.Ordinal)
                    : !string.IsNullOrEmpty(proof.ETag) && string.Equals(etag, proof.ETag, StringComparison.Ordinal);
        if (!sameRepresentation || first.Decoded
            || proof.Length is long length && first.Total != length
            || proof.UsesMetadata && first.Total is null
            || proof.ContentType is { } mime && !string.Equals(HttpFacts.ContentType(response), mime, StringComparison.OrdinalIgnoreCase)
            || proof.Attachment is bool attachment && HttpFacts.IsAttachment(response) != attachment
            || proof.FileName is { } name && !SameServedName(HttpFacts.SuggestedFileName(response), name)
            || proof.LastModifiedValue is { } modified && HttpFacts.LastModified(response) != modified
            || response.RequestMessage?.RequestUri is { } final && UrlPolicy.Normalize(final) != UrlPolicy.Normalize(_url))
            throw Stop(DownloadEngine.BrowserFallbackOutcome);
    }

    private static bool SameServedName(string? served, string observed) =>
        served is not null && string.Equals(FileNames.Sanitize(served), FileNames.Sanitize(observed), StringComparison.OrdinalIgnoreCase);

    private async Task WaitForBrowserApprovalAsync(CancellationToken ct)
    {
        if (session.BrowserApproval is not { } approval || approval.Task.IsCompletedSuccessfully) return;
        engine.UpdateJob(Job, j =>
        {
            if (j.BrowserProof is { AwaitingConfirmation: true })
            {
                j.Reason = "browser-ready";
                j.Message = "Waiting for Chrome to hand over this download.";
            }
        });
        try
        {
            await approval.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException) { }
        throw Stop(DownloadEngine.BrowserFallbackOutcome);
    }

    private void ReconcileWithFile(PartialFile file)
    {
        long length = file.Length;
        if (Job.Extents.End <= length) return;

        // The file is shorter than the database claims (external truncation or deletion). Uncertain bytes
        // never count as verified progress: keep only what the file can actually contain.
        ExtentSet clamped = Job.Extents.Clone();
        clamped.ClampTo(length);
        engine.Log(Job.Id, "reconcile", $"Partial file shorter than recorded ({length} < {Job.Extents.End}); extents clamped.");
        engine.UpdateJob(Job, j => j.Extents = clamped);
    }

    private void LoadPieces(PartialFile file, CancellationToken ct)
    {
        if (!PieceMap.TryParse(Job.PieceMapJson, out PieceMap map, out string error))
            throw Stop(RunOutcome.NeedsAttention("piece-map", "The piece-hash list is invalid: " + error));
        if (Job.TotalBytes is long total && total != map.TotalLength)
            throw Stop(RunOutcome.NeedsAttention("piece-map", "The piece-hash list describes a different file size."));

        // Keep whole, verified pieces only; anything else must be fetched again.
        ExtentSet verified = map.VerifyStored(file, Job.Extents, ct, out int invalid);
        if (verified.Serialize() != Job.Extents.Serialize())
        {
            engine.Log(Job.Id, "pieces", $"{invalid} stored piece(s) failed verification and will be fetched again.");
            engine.UpdateJob(Job, j => { j.Extents = verified; j.TotalBytes = map.TotalLength; });
        }
        _pieces = map;
    }

    // first request
    private RequestSpec BuildSpec(long? start, long? endInclusive)
    {
        RequestCredentials? credentials = engine.CredentialsFor(Job.Id);
        string? expectedResourceIdentity = _runResourceIdentity;
        if (expectedResourceIdentity is null && !Job.Extents.IsEmpty)
            expectedResourceIdentity = Job.FinalResourceIdentity;
        return new RequestSpec(start, endInclusive,
            IfRangeEtag: Job.Identity == IdentityLevel.StrongEtag ? Job.ETag : null,
            IfRangeDate: Job.Identity == IdentityLevel.LastModified ? Job.LastModified : null,
            Referer: Job.Referer, UserAgent: Job.UserAgent, Secrets: credentials?.Headers,
            CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl,
            ExpectedResourceIdentity: expectedResourceIdentity,
            HostCookies: credentials?.HostCookies,
            OriginHeader: credentials?.OriginHeader);
    }

    private async Task<FirstResult> OpenFirstAsync(long start, CancellationToken ct)
    {
        bool fresh = Job.Extents.IsEmpty;
        if (!fresh && Job.Identity == IdentityLevel.None && !Job.UnverifiedResumeApproved)
            throw Stop(NoValidatorStop());

        session.SetPhase(JobState.Resolving);
        long? rangeStart = Job.NonResumable || _forceLinear ? null : start;
        HttpResponseMessage response = await http.SendAsync(_url, BuildSpec(rangeStart, null), ct).ConfigureAwait(false);
        if (rangeStart is not null && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable
            && (fresh || Job.Identity == IdentityLevel.StrongEtag && _pieces is null
                && Job.TotalBytes is long known && (response.Content.Headers.ContentRange?.Length ?? known) == known))
        {
            // A server can stop accepting ranges between runs. The full response
            // still needs the same identity and all saved intervals must match.
            response.Dispose();
            response = await http.SendAsync(_url, BuildSpec(null, null), ct).ConfigureAwait(false);
        }
        try { return Interpret(response, start, fresh); }
        catch { response.Dispose(); throw; }
    }

    private FirstResult Interpret(HttpResponseMessage r, long start, bool fresh)
    {
        int code = (int)r.StatusCode;
        if (code == 206)
        {
            RangeCheck check = HttpFacts.CheckRangeResponse(r, start, null);
            if (!check.IsValid) throw Stop(RunOutcome.NeedsAttention("bad-range", check.Message + " Saved data was kept."));
            if (!HttpFacts.IsIdentityEncoded(r)) throw Stop(RunOutcome.NeedsAttention("encoding", "The server transformed the file (Content-Encoding), so partial data cannot be combined safely."));
            if (check.Total is null) throw Stop(RunOutcome.NeedsAttention("no-total", "The server did not say how large the file is."));
            RejectHtml(r);
            RequireSameRepresentation(r, check.Total, fresh);
            return new FirstResult(r, start, Ranged: true, Replay: false, Decoded: false, check.Total);
        }

        if (code == 200)
        {
            RejectHtml(r);
            if (!HttpFacts.IsIdentityEncoded(r) && r.Content.Headers.ContentEncoding.Count != 1)
                throw Stop(RunOutcome.NeedsAttention("encoding", "The server used multiple content encodings. The file was not published."));
            // A .tar.gz served with "Content-Encoding: gzip" is the file itself, not a transport encoding.
            // Decoding it would save a tar under a .tar.gz name. Chromium leaves such files alone, and so do we.
            bool decoded = !HttpFacts.IsIdentityEncoded(r) && !IsSelfCompressed(r, Job.FileName);
            long? length = decoded ? null : r.Content.Headers.ContentLength;
            if (fresh) return new FirstResult(r, 0, Ranged: false, Replay: false, decoded, length);

            // We asked to resume and received the whole body: the server ignores ranges or the file changed.
            RequireSameRepresentation(r, length, fresh: false);
            if ((Job.Extents.Ranges.Count != 1 || Job.Extents.Ranges[0].Start != 0)
                && (Job.Identity != IdentityLevel.StrongEtag || decoded || length is null || length != Job.TotalBytes
                    || _pieces is not null))
                throw Stop(RunOutcome.NeedsAttention("range-ignored", "The server no longer supports resuming and the saved data cannot be safely replayed. Saved data was kept."));
            return new FirstResult(r, 0, Ranged: false, Replay: true, decoded, length);
        }

        if (code == 416)
            throw Stop(RunOutcome.NeedsAttention("range-rejected", "The server rejected the requested range (the file may have changed or shrunk). Saved data was kept."));
        throw StatusStop(r);
    }

    private void RequireSameRepresentation(HttpResponseMessage r, long? total, bool fresh)
    {
        if (fresh) return;   // nothing stored yet; the identity is adopted from this response

        if (Job.TotalBytes is long known && total is long now && known != now)
            throw Changed($"size {Formatting.Bytes(known)} → {Formatting.Bytes(now)}");

        switch (Job.Identity)
        {
            case IdentityLevel.StrongEtag when HttpFacts.StrongEtag(r) != Job.ETag:
                throw Changed("different ETag");
            case IdentityLevel.LastModified when !HttpFacts.HasStrongLastModified(r) || !SameSecond(HttpFacts.LastModified(r), Job.LastModified):
                throw Changed("different modification time");
            case IdentityLevel.None when !Job.UnverifiedResumeApproved:
                throw Stop(NoValidatorStop());
        }
    }

    private static bool SameSecond(DateTimeOffset? a, DateTimeOffset? b) =>
        a is not null && b is not null && a.Value.ToUnixTimeSeconds() == b.Value.ToUnixTimeSeconds();

    private void RejectHtml(HttpResponseMessage r)
    {
        if (!HttpFacts.IsHtml(r) || HttpFacts.IsHtmlFileName(Job.FileName)
            || HttpFacts.IsHtmlFileName(HttpFacts.SuggestedFileName(r))) return;
        throw Stop(RunOutcome.NeedsAttention("html", "The server sent a web page instead of a file (a sign-in, error or captive-portal page). Nothing was saved from it."));
    }

    private void RejectHtmlBody(ReadOnlySpan<byte> prefix, HttpResponseMessage response)
    {
        if (HttpFacts.IsHtmlFileName(Job.FileName) || HttpFacts.IsHtmlFileName(HttpFacts.SuggestedFileName(response))) return;
        if (HttpFacts.LooksLikeHtml(prefix))
            throw Stop(RunOutcome.NeedsAttention("html", "The server sent a web page instead of the selected file. Nothing was published."));
    }

    private void CaptureMetadata(FirstResult first)
    {
        HttpResponseMessage r = first.Response;
        bool fresh = Job.Extents.IsEmpty;
        if (r.RequestMessage?.RequestUri is not { } finalUri)
            throw Stop(RunOutcome.NeedsAttention("redirect-identity-missing",
                "The server response did not identify the final file location. No new bytes were saved."));
        string finalResourceIdentity = HttpTransport.ResourceIdentity(finalUri);
        _runResourceIdentity = finalResourceIdentity;

        engine.UpdateJob(Job, j =>
        {
            if (fresh)
            {
                j.FinalResourceIdentity = finalResourceIdentity;
                j.ETag = HttpFacts.StrongEtag(r);
                j.LastModified = HttpFacts.LastModified(r);
                // If-Range dates require no ETag and a strong Last-Modified date (RFC 9110).
                bool strongDate = HttpFacts.HasStrongLastModified(r);
                j.Identity = j.ETag is not null ? IdentityLevel.StrongEtag
                    : j.LastModified is not null && strongDate ? IdentityLevel.LastModified : IdentityLevel.None;
                j.TotalBytes = first.Total;
                j.NonResumable = first.Decoded;
            }
            j.TotalBytes ??= first.Total;
            j.AcceptsRanges = first.Ranged || HttpFacts.AcceptsRanges(first.Response) || (j.AcceptsRanges && first.Replay);
            j.ContentType ??= HttpFacts.ContentType(r);
            if (!j.NameLocked && fresh)
            {
                string? suggestedName = HttpFacts.SuggestedFileName(r);
                Uri source = new(j.SourceUrl);
                string name = suggestedName is null ? FileNames.FromUrl(source)
                    : FileNames.Sanitize(suggestedName, j.FileName);
                name = FileNames.WithExtensionFor(name, j.ContentType);
                j.FileName = FileNames.ApplyTemplate(name, _o.FileNameTemplate, source);
                j.Category = FileNames.CategoryFor(j.FileName);
            }
            j.State = JobState.Downloading;
            j.Reason = "";
            j.Message = first.Ranged ? "" : first.Replay ? "This server cannot resume; re-reading and verifying saved data."
                : "This server does not support resuming.";
        });
    }

    private void EnsureSpace(PartialFile file, long total)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.DestinationFolder));
        if (root is null) return;
        try
        {
            long available = new DriveInfo(root).AvailableFreeSpace;
            long needed = Math.Max(0, total - file.Length) + _o.FreeSpaceReserve;
            if (available < needed)
                throw Stop(RunOutcome.NeedsAttention("disk-space",
                    $"Not enough free space on {root}: {Formatting.Bytes(needed)} needed, {Formatting.Bytes(available)} available. Saved data was kept."));
        }
        catch (ArgumentException) { /* network share or unusual path: DriveInfo cannot tell; writes will report a full disk */ }
    }

    // ranged transfer (sequential first, segmented when defensible)
    private sealed class RangeWorker
    {
        internal volatile bool Draining;
        internal Task Task = Task.CompletedTask;
    }

    private async Task RunRangedAsync(FirstResult first, SemaphoreSlim hostGate, CancellationToken ct)
    {
        long total = first.Total!.Value;
        bool pieceMode = _pieces is not null;
        if (_pieces is not null && _pieces.TotalLength != total)
            throw Stop(RunOutcome.NeedsAttention("piece-map", "The server's file size differs from the trusted piece list. Saved data was kept."));
        bool canSplit = _o.MaxConnectionsPerJob > 1 && !_singleConnection && !Job.NonResumable;
        bool segmented = canSplit && (pieceMode || (total >= _o.MinSegmentedSize && Job.Identity >= IdentityLevel.LastModified));
        bool recentlyThrottled = first.Response.RequestMessage?.RequestUri is { } origin && _o.Cooldown.WasRecentlyThrottled(origin);
        // A small file on a slow connection can take much longer than a large file on a fast one.
        // Permit one measured extra range only with a strong validator, never on a limited or throttled link.
        bool adaptiveSmall = canSplit && !segmented && !pieceMode && !recentlyThrottled
            && Job.Identity == IdentityLevel.StrongEtag && total >= ConnectionAdvisor.SmallFileMinimum
            && session.Limiter.BytesPerSecond == 0 && engine.GlobalLimiter.BytesPerSecond == 0;

        var board = new SegmentBoard(pieceMode ? long.MaxValue / 4 :
            adaptiveSmall ? Math.Min(_o.MinSplitBytes, ConnectionAdvisor.SmallSplitMinimum) : _o.MinSplitBytes,
            rateAware: segmented && !pieceMode,
            totalBytes: total);
        foreach (ByteRange gap in Job.Extents.Gaps(total)) AddSegments(board, gap, pieceMode);

        var advisor = new ConnectionAdvisor(segmented ? _o.MaxConnectionsPerJob : 1);
        if (segmented && recentlyThrottled)
            advisor.OnThrottled(_o.MaxConnectionsPerJob);
        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new List<RangeWorker>();
        RangeWorker? primaryWorker = null;

        async Task WorkerAsync(RangeWorker worker, bool primary, HttpResponseMessage? open, Segment? initial = null)
        {
            session.AddConnection();
            double lastWorkerRate = 0;
            try
            {
                while (!workersCts.IsCancellationRequested && !worker.Draining)
                {
                    Segment? segment = initial ?? board.Acquire(lastWorkerRate);
                    initial = null;
                    if (segment is null) break;
                    try
                    {
                        if (open is not null && segment.Next != first.Start) { open.Dispose(); open = null; }
                        HttpResponseMessage? use = open;
                        open = null;
                        await RunSegmentAsync(board, segment, use, pieceMode, segmented && !pieceMode, total, worker, workersCts.Token).ConfigureAwait(false);
                        if (segment.Rate > 0) lastWorkerRate = segment.Rate;
                    }
                    finally { board.Release(segment); }
                }
            }
            finally
            {
                if (initial is not null) board.Release(initial);
                open?.Dispose();
                session.RemoveConnection();
                if (!primary) hostGate.Release();
            }
        }

        void StartWorker(bool primary = false, HttpResponseMessage? response = null, Segment? initial = null)
        {
            var worker = new RangeWorker();
            worker.Task = Task.Run(() => WorkerAsync(worker, primary, response, initial), CancellationToken.None);
            workers.Add(worker);
            if (primary) primaryWorker = worker;
        }

        // Claim segment 0 for primary before spawning worker #2 so the already-open stream isn't raced off it.
        Segment? primarySegment = board.Acquire();
        StartWorker(primary: true, first.Response, primarySegment);
        int target = 1;
        if (primarySegment is not null && segmented && Job.Identity == IdentityLevel.StrongEtag && !pieceMode && !recentlyThrottled
            && _o.MaxConnectionsPerJob >= 2 && session.Limiter.BytesPerSecond == 0 && engine.GlobalLimiter.BytesPerSecond == 0
            && board.HasAssignableWork && hostGate.Wait(0))
        {
            target = 2;
            StartWorker();
        }

        long lastChange = Stopwatch.GetTimestamp();
        long startingBytes = session.RunBytes;
        long sampleBytes = startingBytes;
        bool smallProbeStarted = false;
        try
        {
            while (true)
            {
                workers.RemoveAll(w => w.Task.IsCompletedSuccessfully);
                if (workers.Any(w => w.Task.IsFaulted || w.Task.IsCanceled)) break;
                if (workers.Count == 0)
                {
                    if (!board.HasWork || ct.IsCancellationRequested) break;
                    StartWorker(primary: true);
                    target = 1;
                    lastChange = Stopwatch.GetTimestamp();
                    sampleBytes = session.RunBytes;
                }

                if (segmented && board.HasWork)
                {
                    int active = workers.Count(w => !w.Task.IsCompleted && !w.Draining);
                    TimeSpan interval = Job.Identity == IdentityLevel.StrongEtag && !pieceMode
                        ? TimeSpan.FromMilliseconds(Math.Min(_o.RampInterval.TotalMilliseconds, 800)) : _o.RampInterval;
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(lastChange);
                    long bytes = session.RunBytes - sampleBytes;
                    double rate = elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;
                    bool limited = session.Limiter.BytesPerSecond > 0 || engine.GlobalLimiter.BytesPerSecond > 0;
                    if (limited) target = 1;
                    else if (active == target && elapsed >= interval && bytes > 0
                        && board.RemainingBytes / rate > 2 && bytes >= 64 * 1024)
                    {
                        target = advisor.Recommend(active, rate, elapsed, interval);
                        lastChange = Stopwatch.GetTimestamp();
                        sampleBytes = session.RunBytes;
                    }
                    // Drain only after a completed write (or a verified piece). Never cancel a claimed chunk.
                    foreach (var worker in workers.Where(w => !w.Task.IsCompleted && !w.Draining).Reverse().Take(Math.Max(0, active - target)))
                        worker.Draining = true;
                    int missing = target - workers.Count(w => !w.Task.IsCompleted);
                    for (int i = 0; i < missing && board.HasAssignableWork; i++)
                    {
                        if (primaryWorker!.Task.IsCompleted) StartWorker(primary: true);
                        else if (hostGate.Wait(0)) StartWorker();
                        else break;
                        lastChange = Stopwatch.GetTimestamp();
                        sampleBytes = session.RunBytes;
                    }
                }
                if (adaptiveSmall && (session.Limiter.BytesPerSecond > 0 || engine.GlobalLimiter.BytesPerSecond > 0))
                    foreach (var worker in workers.Where(w => !w.Task.IsCompleted && !w.Draining).Skip(1))
                        worker.Draining = true;
                if (adaptiveSmall && !smallProbeStarted
                    && session.Limiter.BytesPerSecond == 0 && engine.GlobalLimiter.BytesPerSecond == 0
                    && ConnectionAdvisor.WorthSmallFileSplit(session.RunBytes - startingBytes, board.RemainingBytes,
                        Stopwatch.GetElapsedTime(lastChange))
                    && hostGate.Wait(0))
                {
                    smallProbeStarted = true;
                    StartWorker();
                }
                int pollMs = adaptiveSmall && !smallProbeStarted ? 250 : 1000;
                await Task.WhenAny(Task.WhenAny(workers.Select(w => w.Task)), Task.Delay(pollMs, CancellationToken.None)).ConfigureAwait(false);
            }
        }
        finally
        {
            workersCts.Cancel();
        }

        Exception? error = null;
        foreach (RangeWorker worker in workers)
        {
            try { await worker.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* a sibling failed or the user paused */ }
            catch (Exception ex) { error = Prefer(error, ex); }
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        ct.ThrowIfCancellationRequested();
        if (board.HasWork) throw new TransientNetworkException("The transfer ended before all data arrived.");
    }

    private static Exception Prefer(Exception? current, Exception next) =>
        current is null || next is FailpointCrash || (next is RunnerStop && current is not FailpointCrash) ? next : current;

    private void AddSegments(SegmentBoard board, ByteRange gap, bool pieceMode)
    {
        if (!pieceMode) { board.Add(new Segment(gap.Start, gap.End)); return; }
        // Piece mode: one segment per trusted piece, so every piece can be verified as a unit.
        for (int i = (int)(gap.Start / _pieces!.PieceLength); i < _pieces.PieceCount; i++)
        {
            ByteRange piece = _pieces.RangeOf(i);
            if (piece.Start >= gap.End) break;
            if (piece.End > gap.Start) board.Add(new Segment(piece.Start, piece.End));
        }
    }

    private async Task RunSegmentAsync(SegmentBoard board, Segment segment, HttpResponseMessage? first, bool pieceMode, bool boundedWindows, long total, RangeWorker worker, CancellationToken ct)
    {
        HttpResponseMessage? response = first;
        if (response?.Content.Headers.ContentRange?.To is long last && last < long.MaxValue)
            segment.WireEnd = last + 1;
        int emptyReads = 0;
        int pieceFailures = 0;
        while (segment.Next < segment.End)
        {
            if (worker.Draining && !pieceMode) { response?.Dispose(); return; }
            long before = segment.Next;
            IncrementalHash? hash = pieceMode ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            if (pieceMode) { segment.Next = segment.Start; before = segment.Start; }
            try
            {
                if (response is null)
                {
                    long preferredWindow = segment.Rate >= 16 * 1024 * 1024 ? 16L * 1024 * 1024
                        : segment.Rate >= 4 * 1024 * 1024 ? 8L * 1024 * 1024
                        : 4L * 1024 * 1024;
                    long wireEnd = board.PlanWireWindowEnd(segment, preferredWindow, boundedWindows);
                    response = await OpenRangeAsync(segment.Next, wireEnd - 1, total, ct).ConfigureAwait(false);
                }
                await StreamSegmentAsync(board, segment, response, hash, report: !pieceMode, worker, ct).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
                response = null;
                if (pieceMode && segment.Next < segment.End) hash?.Dispose();
            }
            // If this segment finished cleanly, immediately extend into any contiguous unowned span to preserve locality!
            if (!pieceMode && segment.Next >= segment.End)
                _ = board.TryExtend(segment, total);

            if (pieceMode && segment.Next >= segment.End)
            {
                using (hash)
                {
                    int index = (int)(segment.Start / _pieces!.PieceLength);
                    if (_pieces.Matches(index, hash!.GetHashAndReset()))
                    {
                        session.Report(new ByteRange(segment.Start, segment.End));
                        await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                        break;
                    }
                }
                // Corrupt piece: never reported, so never committed. Fetch it again a few times, then stop.
                if (++pieceFailures >= 3)
                    throw Stop(RunOutcome.NeedsAttention("piece-mismatch", $"Piece {segment.Start / _pieces.PieceLength} keeps failing its trusted checksum. The source may serve different content."));
                segment.Next = segment.Start;
                continue;
            }

            if (segment.Next == before)
            {
                if (++emptyReads >= 3) throw new TransientNetworkException("The server keeps closing the connection without sending data.");
            }
            else emptyReads = 0;
        }
    }

    private async Task<HttpResponseMessage> OpenRangeAsync(long start, long end, long total, CancellationToken ct)
    {
        HttpResponseMessage response = await http.SendAsync(_url, BuildSpec(start, end), ct).ConfigureAwait(false);
        try
        {
            int code = (int)response.StatusCode;
            if (code == 206)
            {
                RangeCheck check = HttpFacts.CheckRangeResponse(response, start, end);
                if (!check.IsValid) throw new RangeProblemException(check.Code, check.Message);
                if (!HttpFacts.IsIdentityEncoded(response)) throw new RangeProblemException("encoding", "The server compressed a range response.");
                if (check.Total != total) throw Changed($"size {Formatting.Bytes(total)} → {(check.Total is long t ? Formatting.Bytes(t) : "unknown")}");
                RequireSameRepresentation(response, total, fresh: false);
                return response;
            }
            if (code == 200 && !_singleConnection && _pieces is null && Job.Identity == IdentityLevel.StrongEtag
                && HttpFacts.IsIdentityEncoded(response) && response.Content.Headers.ContentLength == total)
            {
                RequireSameRepresentation(response, total, fresh: false);
                throw new RangeProblemException("range-ignored", "Range support changed; checking a full response against saved bytes.");
            }
            if (code == 416 && !_singleConnection && _pieces is null && Job.Identity == IdentityLevel.StrongEtag)
                throw new RangeProblemException("range-rejected", "A range was rejected; checking a full response against saved bytes.");
            if (code == 200) throw Stop(RunOutcome.NeedsAttention("range-ignored", "The server stopped honouring range requests or the file changed. Saved data was kept."));
            if (code == 416) throw Stop(RunOutcome.NeedsAttention("range-rejected", "The server rejected a range request. Saved data was kept."));
            throw StatusStop(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task StreamSegmentAsync(SegmentBoard board, Segment segment, HttpResponseMessage response,
        IncrementalHash? hash, bool report, RangeWorker worker, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool checkedBody = segment.Next != 0;
            while (true)
            {
                if (report && worker.Draining) return;
                long remaining = board.Remaining(segment);
                if (remaining <= 0)
                {
                    if (!report || response.Content.Headers.ContentRange?.To is not long last
                        || last == long.MaxValue || !board.TryExtend(segment, last + 1)) return;
                    continue;
                }
                int read = await ReadAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stall, ct).ConfigureAwait(false);
                if (read == 0) return;                                    // server ended early; the caller reconnects

                int n = board.ClaimChunk(segment, read);                  // the span may have been split while we were reading
                if (n <= 0) return;
                if (!checkedBody)
                {
                    RejectHtmlBody(buffer.AsSpan(0, n), response);
                    checkedBody = true;
                }
                await session.Limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await session.WriteAsync(buffer.AsMemory(0, n), segment.Next, report, ct).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, n);
                board.Advance(segment, n);
                if (n < read) return; // discarded response bytes must never be reused
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // linear transfer: unknown length, server without ranges, or replay-and-verify
    private async Task RunLinearAsync(FirstResult first, PartialFile file, CancellationToken ct)
    {
        if (first.Replay && Job.Extents.TotalBytes != Job.Extents.ContiguousFromZero())
        {
            await ReplaySparseAsync(first, file, ct).ConfigureAwait(false);
            return;
        }
        long prefix = first.Replay ? Job.Extents.ContiguousFromZero() : 0;
        long offset = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] local = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using Stream raw = await first.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using Stream body = first.Decoded ? Decode(raw, first.Response) : raw;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool checkedBody = false;

            while (true)
            {
                int read = await ReadAsync(body, buffer, stall, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (!checkedBody)
                {
                    RejectHtmlBody(buffer.AsSpan(0, read), first.Response);
                    checkedBody = true;
                }
                int skip = 0;

                if (offset < prefix)
                {
                    // Replay: bytes we already hold must be identical to what the server sends now.
                    skip = (int)Math.Min(read, prefix - offset);
                    if (file.Read(local.AsSpan(0, skip), offset) != skip || !buffer.AsSpan(0, skip).SequenceEqual(local.AsSpan(0, skip)))
                        throw Changed("saved data differs from what the server sends now");
                    offset += skip;
                    if (skip == read) continue;
                }

                int n = read - skip;
                await session.Limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await session.WriteAsync(buffer.AsMemory(skip, n), offset, report: true, ct).ConfigureAwait(false);
                offset += n;
            }

            if (offset < prefix) throw Changed("the server now sends less data than was already saved");
            if (!first.Decoded && first.Total is long expected && offset != expected)
                throw new TransientNetworkException($"The connection closed after {Formatting.Bytes(offset)} of {Formatting.Bytes(expected)}.");

            if (Job.TotalBytes is null)
            {
                // Trust explicit HTTP framing when ranges are unavailable. If ranges exist, probe the
                // boundary as well: a cleanly framed but short response can still be a truncated file.
                bool framed = HttpFacts.HasCompleteBodyFraming(first.Response);
                if (!framed || Job.AcceptsRanges && !first.Decoded)
                    await VerifyUnknownLengthAsync(offset, ct).ConfigureAwait(false);
                engine.UpdateJob(Job, j => j.TotalBytes = offset);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(local);
        }
    }

    // A server can stop honoring ranges after parallel workers have written sparse extents.
    // Compare every saved interval; stage missing bytes without reporting them until the
    // whole identity-proven response has been checked. A crash or mismatch retains only
    // the old extents, never a partially verified mixture.
    private async Task ReplaySparseAsync(FirstResult first, PartialFile file, CancellationToken ct)
    {
        if (Job.Identity != IdentityLevel.StrongEtag || first.Decoded || first.Total is not long total
            || total != Job.TotalBytes || _pieces is not null)
            throw Stop(RunOutcome.NeedsAttention("range-ignored", "The saved ranges cannot be safely replayed."));
        var saved = Job.Extents.Clone();
        var ranges = saved.Ranges;
        int index = 0;
        long offset = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] local = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using Stream body = await first.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (offset < total)
            {
                int read = await ReadAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, total - offset)), stall, ct).ConfigureAwait(false);
                if (read == 0) throw new TransientNetworkException("The full replay ended early.");
                if (offset == 0) RejectHtmlBody(buffer.AsSpan(0, read), first.Response);
                await session.Limiter.AcquireAsync(read, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(read, ct).ConfigureAwait(false);
                int used = 0;
                while (used < read)
                {
                    while (index < ranges.Count && ranges[index].End <= offset) index++;
                    bool covered = index < ranges.Count && ranges[index].Start <= offset;
                    long boundary = index >= ranges.Count ? total : covered ? ranges[index].End : ranges[index].Start;
                    int n = (int)Math.Min(read - used, boundary - offset);
                    if (covered)
                    {
                        if (file.Read(local.AsSpan(0, n), offset) != n || !buffer.AsSpan(used, n).SequenceEqual(local.AsSpan(0, n)))
                            throw Changed("a saved range differs from the full response");
                    }
                    else await session.WriteAsync(buffer.AsMemory(used, n), offset, report: false, ct).ConfigureAwait(false);
                    offset += n;
                    used += n;
                }
            }
            if (await ReadAsync(body, buffer.AsMemory(0, 1), stall, ct).ConfigureAwait(false) != 0)
                throw Changed("the full response exceeds the known length");
            foreach (ByteRange gap in saved.Gaps(total)) session.Report(gap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(local);
        }
    }

    /// <summary>True when the saved file is already a compressed archive, so a Content-Encoding header describes the file.</summary>
    private static bool IsSelfCompressed(HttpResponseMessage response, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        string? type = HttpFacts.ContentType(response)?.ToLowerInvariant();
        return response.Content.Headers.ContentEncoding.SingleOrDefault()?.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => ext is ".gz" or ".tgz" || type is "application/gzip" or "application/x-gzip",
            "br" => ext == ".br",
            "deflate" => ext == ".zz",
            "zstd" => ext == ".zst" || type == "application/zstd",
            "bzip2" or "x-bzip2" => ext == ".bz2" || type == "application/x-bzip2",
            "xz" => ext == ".xz" || type == "application/x-xz",
            _ => false
        };
    }

    /// <summary>
    /// The body ended without a stated length. Ask for one byte past what we hold: a 416 whose range is
    /// exactly our offset, or a 206 whose total matches, proves the file is complete. A larger total means
    /// the connection closed early. When the server cannot answer, the file is kept but marked unverified.
    /// </summary>
    private async Task VerifyUnknownLengthAsync(long offset, CancellationToken ct)
    {
        if (!Job.AcceptsRanges || Job.NonResumable) throw UnverifiedLength();

        // Ask for one byte only. Requesting offset- could make the server send the entire missing tail.
        using HttpResponseMessage probe = await http.SendAsync(_url, BuildSpec(offset, offset), ct).ConfigureAwait(false);
        if (probe.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var range = probe.Content.Headers.ContentRange;
            if (range is not { Unit: "bytes", From: null, To: null, Length: long total })
                throw UnverifiedLength();

            RequireSameRepresentation(probe, total, fresh: false);
            if (total == offset) return;
            if (total > offset) throw TruncatedLength(offset, total);
            throw Changed($"the server now reports {Formatting.Bytes(total)} instead of the saved {Formatting.Bytes(offset)}");
        }

        if (probe.StatusCode == HttpStatusCode.PartialContent)
        {
            RangeCheck check = HttpFacts.CheckRangeResponse(probe, offset, offset);
            if (!check.IsValid || check.Total is not long total) throw UnverifiedLength();
            RequireSameRepresentation(probe, total, fresh: false);
            if (total > offset) throw TruncatedLength(offset, total);
            if (total < offset) throw Changed($"the server now reports {Formatting.Bytes(total)} instead of the saved {Formatting.Bytes(offset)}");
        }

        if (probe.StatusCode != HttpStatusCode.OK) throw StatusStop(probe);
        throw UnverifiedLength();
    }

    private static RunnerStop UnverifiedLength() => Stop(RunOutcome.NeedsAttention("unverified-length",
        "The server did not state the file size and VESPER could not confirm the last byte. Saved data was kept; nothing was published."));

    private static RunnerStop TruncatedLength(long offset, long total) => Stop(RunOutcome.NeedsAttention("truncated",
        $"The connection closed after {Formatting.Bytes(offset)}, but the server reports {Formatting.Bytes(total)}. Saved data was kept; nothing was published."));

    private static Stream Decode(Stream body, HttpResponseMessage response)
    {
        string encoding = response.Content.Headers.ContentEncoding.LastOrDefault()?.ToLowerInvariant() ?? "";
        return encoding switch
        {
            "gzip" or "x-gzip" => new GZipStream(body, CompressionMode.Decompress),
            "deflate" => new ZLibStream(body, CompressionMode.Decompress),
            "br" => new BrotliStream(body, CompressionMode.Decompress),
            _ => throw new RunnerStop(RunOutcome.NeedsAttention("encoding", $"The server used an unsupported content encoding ({encoding})."))
        };
    }

    // One stall timer per stream, re-armed before each read. A stall abandons the stream, so the timer never
    // needs resetting. (A linked CTS per 128 KiB read cost thousands of allocations a second on fast links.)
    private async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationTokenSource stall, CancellationToken ct)
    {
        stall.CancelAfter(_o.StallTimeout);
        try { return await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientNetworkException("The connection stalled.");
        }
    }

    // outcome helpers
    private static RunnerStop Stop(RunOutcome outcome) => new(outcome);

    private RunnerStop Changed(string detail) => Stop(RunOutcome.NeedsAttention("changed",
        $"The file on the server has changed ({detail}). Saved data was kept but cannot be safely continued from this link."));

    private static RunOutcome NoValidatorStop() => RunOutcome.NeedsAttention("no-validator",
        "This server gives no way to confirm the file is unchanged, so resuming could mix two versions. Saved data was kept.");

    private RunnerStop StatusStop(HttpResponseMessage r)
    {
        int code = (int)r.StatusCode;
        bool linkWorkedBefore = !Job.Extents.IsEmpty || Job.Identity != IdentityLevel.None;
        bool refreshableMedia = Job.BrowserProof is null && Job.OriginalPageUrl is not null
            && !string.IsNullOrWhiteSpace(Job.MediaProvider) && Job.MediaProvider != "generic"
            && !string.IsNullOrWhiteSpace(Job.MediaId) && !string.IsNullOrWhiteSpace(Job.MediaFormatId);
        RunOutcome outcome = code switch
        {
            401 or 407 => RunOutcome.NeedsAttention("auth", $"The server asks for a sign-in (HTTP {code}). Send this download again from your signed-in browser."),
            403 when linkWorkedBefore || refreshableMedia => RunOutcome.NeedsLink("forbidden", "The server refused the link (HTTP 403). Get a fresh link from the browser. Your download data was kept."),
            403 => RunOutcome.NeedsAttention("forbidden", "The server refused the request (HTTP 403). Try again from the browser where the link works."),
            404 or 410 when linkWorkedBefore || refreshableMedia => RunOutcome.NeedsLink("expired", $"The link no longer works (HTTP {code}). Saved data was kept."),
            404 or 410 => RunOutcome.Failed("not-found", $"The file was not found (HTTP {code})."),
            408 or 429 or 503 => RunOutcome.Retry("throttled", $"The server asked VESPER to wait (HTTP {code}).", HttpFacts.RetryAfter(r, DateTimeOffset.UtcNow)),
            >= 500 => RunOutcome.Retry("server-error", $"The server had a problem (HTTP {code}).", HttpFacts.RetryAfter(r, DateTimeOffset.UtcNow)),
            _ => RunOutcome.Failed("http-status", $"The server answered HTTP {code}.")
        };
        return Stop(outcome);
    }
}
