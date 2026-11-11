using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
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
    private PieceMap? _pieces;
    private bool _singleConnection;

    private sealed record FirstResult(HttpResponseMessage Response, long Start, bool Ranged, bool Replay, bool Decoded, long? Total);

    public async Task<RunOutcome> RunAsync()
    {
        CancellationToken ct = session.Cts.Token;
        try
        {
            return Job.Kind switch
            {
                JobKind.Hls => await new HlsTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Dash => await new DashTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Merge => await new MergeTransfer(engine, session, http).RunAsync(ct).ConfigureAwait(false),
                JobKind.Http => await TransferAsync(ct).ConfigureAwait(false),
                _ => RunOutcome.NeedsAttention("unsupported-kind", "This download requires a newer engine. Saved data was kept.")
            };
        }
        catch (FailpointCrash) { throw; }
        catch (RunnerStop stop) { await SettleAsync(); return stop.Outcome; }
        catch (OperationCanceledException) when (session.Intent != StopIntent.None)
        {
            await SettleAsync();
            return session.Intent switch
            {
                StopIntent.Pause => new RunOutcome(JobState.Paused, "", ""),
                StopIntent.Window => new RunOutcome(JobState.Queued, "schedule", "Waiting for the scheduled download window."),
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

    // ---- main flow ---------------------------------------------------------------------------------------------

    private async Task<RunOutcome> TransferAsync(CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(Job.SourceUrl, out _url, out string urlError))
            throw Stop(RunOutcome.NeedsLink("bad-url", urlError));

        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out string folderError))
            throw Stop(RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept."));

        PartialFile file = session.OpenFile();
        ReconcileWithFile(file);
        if (Job.PieceMapJson is not null) LoadPieces(file, ct);

        for (int pass = 0; pass < 2; pass++)
        {
            if (Job.TotalBytes is long known && Job.Extents.Covers(0, known)) return await FinishAsync(ct).ConfigureAwait(false);

            long start = Job.Extents.Gaps(Job.TotalBytes ?? long.MaxValue).FirstOrDefault().Start;
            FirstResult first = await OpenFirstAsync(start, ct).ConfigureAwait(false);
            try
            {
                CaptureMetadata(first);
                if (first.Total is long total) { EnsureSpace(file, total); file.EnsureLength(total); }

                if (first.Ranged) await RunRangedAsync(first, ct).ConfigureAwait(false);
                else await RunLinearAsync(first, file, ct).ConfigureAwait(false);
            }
            catch (RangeProblemException problem) when (!_singleConnection)
            {
                // Segmentation is no longer defensible. Committed bytes are untouched; go on with one connection.
                _singleConnection = true;
                engine.Log(Job.Id, "single-connection", problem.Message);
                continue;
            }
            finally
            {
                first.Response.Dispose();
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

    // ---- first request -----------------------------------------------------------------------------------------

    private RequestSpec BuildSpec(long? start, long? endInclusive) => new(
        start, endInclusive,
        IfRangeEtag: Job.Identity == IdentityLevel.StrongEtag ? Job.ETag : null,
        IfRangeDate: Job.Identity == IdentityLevel.LastModified ? Job.LastModified : null,
        Referer: Job.Referer, UserAgent: Job.UserAgent, Secrets: engine.SecretsFor(Job.Id));

    private async Task<FirstResult> OpenFirstAsync(long start, CancellationToken ct)
    {
        bool fresh = Job.Extents.IsEmpty;
        if (!fresh && Job.Identity == IdentityLevel.None && !Job.UnverifiedResumeApproved)
            throw Stop(NoValidatorStop());

        session.SetPhase(JobState.Resolving);
        long? rangeStart = Job.NonResumable ? null : start;
        HttpResponseMessage response = await http.SendAsync(_url, BuildSpec(rangeStart, null), ct).ConfigureAwait(false);
        if (fresh && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // An empty file cannot satisfy "bytes=0-". Ask again without a range before giving up.
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
            bool decoded = !HttpFacts.IsIdentityEncoded(r);
            long? length = decoded ? null : r.Content.Headers.ContentLength;
            if (fresh) return new FirstResult(r, 0, Ranged: false, Replay: false, decoded, length);

            // We asked to resume and received the whole body: the server ignores ranges or the file changed.
            RequireSameRepresentation(r, length, fresh: false);
            if (Job.Extents.Ranges.Count != 1 || Job.Extents.Ranges[0].Start != 0)
                throw Stop(RunOutcome.NeedsAttention("range-ignored", "The server no longer supports resuming and the saved data is not a single block. Saved data was kept."));
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
            case IdentityLevel.LastModified when !SameSecond(HttpFacts.LastModified(r), Job.LastModified):
                throw Changed("different modification time");
            case IdentityLevel.None when !Job.UnverifiedResumeApproved:
                throw Stop(NoValidatorStop());
        }
    }

    private static bool SameSecond(DateTimeOffset? a, DateTimeOffset? b) =>
        a is not null && b is not null && a.Value.ToUnixTimeSeconds() == b.Value.ToUnixTimeSeconds();

    private void RejectHtml(HttpResponseMessage r)
    {
        if (!HttpFacts.IsHtml(r) || HttpFacts.IsAttachment(r)) return;
        string ext = Path.GetExtension(Job.FileName).ToLowerInvariant();
        if (ext is ".html" or ".htm" or ".xhtml" or ".php" or ".asp" or ".aspx" || Job.ContentType is "text/html") return;
        throw Stop(RunOutcome.NeedsAttention("html", "The server sent a web page instead of a file (a sign-in, error or captive-portal page). Nothing was saved from it."));
    }

    private void CaptureMetadata(FirstResult first)
    {
        HttpResponseMessage r = first.Response;
        bool fresh = Job.Extents.IsEmpty;
        engine.UpdateJob(Job, j =>
        {
            if (fresh)
            {
                j.ETag = HttpFacts.StrongEtag(r);
                j.LastModified = HttpFacts.LastModified(r);
                j.Identity = j.ETag is not null ? IdentityLevel.StrongEtag
                    : j.LastModified is not null ? IdentityLevel.LastModified : IdentityLevel.None;
                j.TotalBytes = first.Total;
                j.NonResumable = first.Decoded;
            }
            j.TotalBytes ??= first.Total;
            j.AcceptsRanges = first.Ranged || (j.AcceptsRanges && first.Replay);
            j.ContentType ??= HttpFacts.ContentType(r);
            if (!j.NameLocked && fresh)
            {
                string name = FileNames.Sanitize(HttpFacts.SuggestedFileName(r), j.FileName);
                j.FileName = FileNames.WithExtensionFor(name, j.ContentType);
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

    // ---- ranged transfer (sequential first, segmented when defensible) --------------------------------------------------

    private async Task RunRangedAsync(FirstResult first, CancellationToken ct)
    {
        long total = first.Total!.Value;
        bool pieceMode = _pieces is not null;
        if (_pieces is not null && _pieces.TotalLength != total)
            throw Stop(RunOutcome.NeedsAttention("piece-map", "The server's file size differs from the trusted piece list. Saved data was kept."));
        bool segmented = _o.MaxConnectionsPerJob > 1 && !_singleConnection && !Job.NonResumable
            && (pieceMode || (total >= _o.MinSegmentedSize && Job.Identity >= IdentityLevel.LastModified));

        var board = new SegmentBoard(pieceMode ? long.MaxValue / 4 : _o.MinSplitBytes);
        foreach (ByteRange gap in Job.Extents.Gaps(total)) AddSegments(board, gap, pieceMode);

        var advisor = new ConnectionAdvisor(segmented ? _o.MaxConnectionsPerJob : 1);
        using var workersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new List<Task>();
        HttpResponseMessage? firstResponse = first.Response;
        SemaphoreSlim hostGate = engine.HostGate(session.Host);

        async Task WorkerAsync(bool takesFirst)
        {
            session.AddConnection();
            HttpResponseMessage? open = takesFirst ? first.Response : null;
            try
            {
                while (!workersCts.IsCancellationRequested)
                {
                    Segment? segment = board.Acquire();
                    if (segment is null) break;
                    try
                    {
                        if (open is not null && segment.Next != first.Start) { open.Dispose(); open = null; }
                        HttpResponseMessage? use = open;
                        open = null;
                        await RunSegmentAsync(board, segment, use, pieceMode, total, workersCts.Token).ConfigureAwait(false);
                    }
                    finally { board.Release(segment); }
                }
            }
            finally
            {
                open?.Dispose();
                session.RemoveConnection();
                hostGate.Release();
            }
        }

        // The first connection may wait for a free host slot; extra ones are only opened if a slot is free right now.
        await hostGate.WaitAsync(ct).ConfigureAwait(false);
        firstResponse = null;   // ownership passes to the first worker
        workers.Add(Task.Run(() => WorkerAsync(takesFirst: true), CancellationToken.None));

        long lastChange = Stopwatch.GetTimestamp();
        int target = 1;
        try
        {
            while (true)
            {
                workers.RemoveAll(t => t.IsCompletedSuccessfully);
                if (workers.Count == 0 || workers.Any(t => t.IsFaulted || t.IsCanceled)) break;

                if (segmented && board.HasWork)
                {
                    int recommended = advisor.Recommend(session.Connections, session.SpeedBytesPerSecond,
                        Stopwatch.GetElapsedTime(lastChange), _o.RampInterval);
                    if (recommended != target) { target = recommended; lastChange = Stopwatch.GetTimestamp(); }
                    while (workers.Count(t => !t.IsCompleted) < target && hostGate.Wait(0))
                        workers.Add(Task.Run(() => WorkerAsync(takesFirst: false), CancellationToken.None));
                }
                await Task.WhenAny(Task.WhenAny(workers), Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            }
        }
        finally
        {
            workersCts.Cancel();
            firstResponse?.Dispose();
        }

        Exception? error = null;
        foreach (Task worker in workers)
        {
            try { await worker.ConfigureAwait(false); }
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

    private async Task RunSegmentAsync(SegmentBoard board, Segment segment, HttpResponseMessage? first, bool pieceMode, long total, CancellationToken ct)
    {
        HttpResponseMessage? response = first;
        int emptyReads = 0;
        int pieceFailures = 0;
        while (segment.Next < segment.End)
        {
            long before = segment.Next;
            IncrementalHash? hash = pieceMode ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            if (pieceMode) { segment.Next = segment.Start; before = segment.Start; }
            try
            {
                response ??= await OpenRangeAsync(segment, total, ct).ConfigureAwait(false);
                await StreamSegmentAsync(board, segment, response, hash, report: !pieceMode, ct).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
                response = null;
                if (pieceMode && segment.Next < segment.End) hash?.Dispose();
            }

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

    private async Task<HttpResponseMessage> OpenRangeAsync(Segment segment, long total, CancellationToken ct)
    {
        long start = segment.Next;
        long end = segment.End - 1;
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
        IncrementalHash? hash, bool report, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (true)
            {
                long remaining = board.Remaining(segment);
                if (remaining <= 0) return;
                int read = await ReadAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (read == 0) return;                                    // server ended early; the caller reconnects

                int n = board.ClaimChunk(segment, read);                  // the span may have been split while we were reading
                if (n <= 0) return;
                await session.Limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(n, ct).ConfigureAwait(false);
                await session.WriteAsync(buffer.AsMemory(0, n), segment.Next, report, ct).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, n);
                board.Advance(segment, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- linear transfer: unknown length, server without ranges, or replay-and-verify ----------------------------------

    private async Task RunLinearAsync(FirstResult first, PartialFile file, CancellationToken ct)
    {
        long prefix = first.Replay ? Job.Extents.ContiguousFromZero() : 0;
        long offset = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] local = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using Stream raw = await first.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using Stream body = first.Decoded ? Decode(raw, first.Response) : raw;

            while (true)
            {
                int read = await ReadAsync(body, buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
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

            if (Job.TotalBytes is null) engine.UpdateJob(Job, j => j.TotalBytes = offset);   // unknown length: the stream's end defines it
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(local);
        }
    }

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

    private async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_o.StallTimeout);
        try { return await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientNetworkException("The connection stalled.");
        }
    }

    // ---- outcome helpers -------------------------------------------------------------------------------------------------

    private static RunnerStop Stop(RunOutcome outcome) => new(outcome);

    private RunnerStop Changed(string detail) => Stop(RunOutcome.NeedsAttention("changed",
        $"The file on the server has changed ({detail}). Saved data was kept but cannot be safely continued from this link."));

    private static RunOutcome NoValidatorStop() => RunOutcome.NeedsAttention("no-validator",
        "This server gives no way to confirm the file is unchanged, so resuming could mix two versions. Saved data was kept.");

    private RunnerStop StatusStop(HttpResponseMessage r)
    {
        int code = (int)r.StatusCode;
        bool linkWorkedBefore = !Job.Extents.IsEmpty || Job.Identity != IdentityLevel.None;
        RunOutcome outcome = code switch
        {
            401 or 407 => RunOutcome.NeedsAttention("auth", $"The server asks for a sign-in (HTTP {code}). Send this download again from your signed-in browser."),
            403 when linkWorkedBefore => RunOutcome.NeedsLink("expired", "The server refused the link (HTTP 403). It has probably expired."),
            403 => RunOutcome.NeedsAttention("forbidden", "The server refused access (HTTP 403). The link may need a signed-in browser session."),
            404 or 410 when linkWorkedBefore => RunOutcome.NeedsLink("expired", $"The link no longer works (HTTP {code}). Saved data was kept."),
            404 or 410 => RunOutcome.Failed("not-found", $"The file was not found (HTTP {code})."),
            408 or 429 or 503 => RunOutcome.Retry("throttled", $"The server asked VESPER to wait (HTTP {code}).", HttpFacts.RetryAfter(r, DateTimeOffset.UtcNow)),
            >= 500 => RunOutcome.Retry("server-error", $"The server had a problem (HTTP {code}).", HttpFacts.RetryAfter(r, DateTimeOffset.UtcNow)),
            _ => RunOutcome.Failed("http-status", $"The server answered HTTP {code}.")
        };
        return Stop(outcome);
    }
}
