using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Native merge job. Phase 1 downloads every track into its own region of the job's partial file using bounded
/// (8 MiB) range requests through the normal durable checkpoint pipeline, so progress, pause, crash recovery and
/// per-chunk retry all reuse the engine's guarantees. Phase 2 demuxes the committed regions, remuxes them into one
/// container (no transcoding), re-validates the output and swaps it in atomically. The downloaded tracks are kept
/// until the merged file is published, so a failed merge never destroys completed work.
/// </summary>
internal sealed class MergeTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    internal const long ChunkBytes = 8L * 1024 * 1024;
    private const int Workers = 3;
    private const int ChunkAttempts = 3;
    private DownloadJob Job => session.Job;

    internal sealed record Cursor(int Version, string Fingerprint, string Phase, long MuxLength = 0, string? MuxSha256 = null);
    private sealed class Progress { public long Offset; }

    internal static string TracksPath(DownloadJob job) => job.PartialPath + ".tracks";
    internal static string MergingPath(DownloadJob job) => job.PartialPath + ".merging";

    /// <summary>Removes merge sidecars. Returns false when a file could not be removed (the caller decides how to report it).</summary>
    internal static bool DeleteSidecars(DownloadJob job)
    {
        bool ok = true;
        foreach (string path in new[] { TracksPath(job), MergingPath(job) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        }
        return ok;
    }

    internal async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!MediaMergePlan.TryParse(Job.MediaPlanJson, engine.Options.AllowPrivateNetworks, out var plan, out var error))
            return RunOutcome.NeedsAttention("merge-plan", error);
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out var folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");
        if (plan.IsSegmented)
            return await new SegmentedMergeTransfer(engine, session, http).RunAsync(plan, ct).ConfigureAwait(false);
        string fingerprint = plan.Fingerprint;
        Cursor? cursor = null;
        if (Job.MediaState is not null)
        {
            try { cursor = JsonSerializer.Deserialize<Cursor>(Job.MediaState, new JsonSerializerOptions { MaxDepth = 8 }); }
            catch (JsonException) { return Stop("The saved merge cursor is invalid."); }
            if (cursor is null || cursor.Version != 1 || cursor.Fingerprint != fingerprint)
                return Stop("The saved data belongs to a different representation. Restart as a separate copy.");
        }
        if (cursor?.Phase == "muxed") return await FinalizeMergedAsync(cursor, ct).ConfigureAwait(false);
        if (cursor is not null && cursor.Phase != "download") return Stop("The saved merge cursor has an unknown phase.");

        RecoverInterruptedSwap();
        long total = plan.TotalBytes;
        if (Job.Extents.End > total) return Stop("Committed bytes extend past the planned tracks.");
        if (Job.TotalBytes != total) engine.UpdateJob(Job, j => j.TotalBytes = total);
        string state = JsonSerializer.Serialize(new Cursor(1, fingerprint, "download"));

        session.OpenFile();
        session.SetPhase(JobState.Downloading);
        var work = new Queue<(int Track, long Start, long End)>();
        for (int i = 0; i < plan.Tracks.Count; i++)
        {
            long trackStart = plan.OffsetOf(i), trackEnd = trackStart + plan.Tracks[i].Size;
            foreach (ByteRange gap in Job.Extents.Gaps(total))
            {
                long s = Math.Max(gap.Start, trackStart), e = Math.Min(gap.End, trackEnd);
                for (long p = s; p < e; p += ChunkBytes) work.Enqueue((i, p, Math.Min(e, p + ChunkBytes)));
            }
        }
        if (work.Count > 0)
        {
            EnsureSpace(work.Sum(w => w.End - w.Start) + total);
            await RunWorkersAsync(plan, work, state, ct).ConfigureAwait(false);
            await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        }
        if (!Job.Extents.Covers(0, total))
            return RunOutcome.NeedsAttention("incomplete", "Some track data is missing. Saved data was kept; resume to fetch the rest.");
        ct.ThrowIfCancellationRequested();
        return await MergeAsync(plan, fingerprint, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ download

    private async Task RunWorkersAsync(MediaMergePlan plan, Queue<(int Track, long Start, long End)> work, string state, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new object();
        Exception? failure = null;

        async Task Worker()
        {
            while (true)
            {
                (int Track, long Start, long End) item;
                lock (gate)
                {
                    if (work.Count == 0 || failure is not null) return;
                    item = work.Dequeue();
                }
                try { await FetchChunkAsync(plan, item.Track, item.Start, item.End, state, stop.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (gate) failure ??= ex;
                    stop.Cancel();
                    return;
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(Workers, work.Count)).Select(_ => Worker())).ConfigureAwait(false);
        if (failure is not null)
        {
            ct.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task FetchChunkAsync(MediaMergePlan plan, int index, long start, long end, string state, CancellationToken ct)
    {
        var progress = new Progress { Offset = start };
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await FetchRangeAsync(plan, index, progress, end, state, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < ChunkAttempts && !ct.IsCancellationRequested && ex is not RunnerStop && ErrorClassifier.IsTransient(ex))
            {
                // Per-chunk retry: only this chunk's remaining bytes are fetched again; other tracks continue.
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchRangeAsync(MediaMergePlan plan, int index, Progress progress, long end, string state, CancellationToken ct)
    {
        MergeTrack track = plan.Tracks[index];
        long trackBase = plan.OffsetOf(index);
        if (!UrlPolicy.TryParse(track.Url, out var uri, out _)) throw new RunnerStop(Stop("A track URL is invalid."));
        long from = progress.Offset - trackBase, toInclusive = end - trackBase - 1;
        session.AddConnection();
        try
        {
            using var response = await http.SendAsync(uri, new RequestSpec(from, toInclusive, Referer: Job.Referer,
                UserAgent: Job.UserAgent, CredentialOrigin: Job.SourceUrl), ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status != 206) throw new RunnerStop(status switch
            {
                200 => Stop("The media server ignored the byte-range request."),
                401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", "The media address expired or was refused. Saved track data was kept; refresh the source to continue."),
                416 => Stop("The media server reports a different size than planned."),
                408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The media server answered HTTP {status}.",
                    HttpFacts.RetryAfter(response, DateTimeOffset.UtcNow)),
                _ => RunOutcome.NeedsAttention("http-status", $"The media server answered HTTP {status}.")
            });
            if (!HttpFacts.IsIdentityEncoded(response) || HttpFacts.IsHtml(response))
                throw new RunnerStop(Stop("The media server sent compressed data or an HTML page."));
            var check = HttpFacts.CheckRangeResponse(response, from, toInclusive);
            if (!check.IsValid || check.EndInclusive != toInclusive)
                throw new RunnerStop(Stop("The media server returned a different byte range than requested."));
            if (response.Content.Headers.ContentRange?.Length is long full && full != track.Size)
                throw new RunnerStop(Stop("The media server reports a different file size than planned; the representation changed."));

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(engine.Options.StallTimeout);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                while (progress.Offset < end)
                {
                    int want = (int)Math.Min(buffer.Length, end - progress.Offset);
                    int n = await stream.ReadAsync(buffer.AsMemory(0, want), stall.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    await session.Limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                    await engine.GlobalLimiter.AcquireAsync(n, ct).ConfigureAwait(false);
                    await session.WriteAsync(buffer.AsMemory(0, n), progress.Offset, report: false, ct).ConfigureAwait(false);
                    session.Report(new ByteRange(progress.Offset, progress.Offset + n), state);
                    progress.Offset += n;
                    await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                    stall.CancelAfter(engine.Options.StallTimeout);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransientNetworkException("The media transfer stalled.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (progress.Offset != end) throw new TransientNetworkException("The media server closed the connection early.");
        }
        finally { session.RemoveConnection(); }
    }

    // ------------------------------------------------------------------------------------------------ merge

    private async Task<RunOutcome> MergeAsync(MediaMergePlan plan, string fingerprint, CancellationToken ct)
    {
        engine.UpdateJob(Job, j => { j.State = JobState.Verifying; j.Reason = "prepare-media"; j.Message = "Preparing file"; });
        string mergingPath = MergingPath(Job);
        long length;
        string sha;
        session.CloseFile();
        try
        {
            using (var source = new FileStream(Job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess))
            {
                if (source.Length < plan.TotalBytes) return Stop("The partial file is shorter than its committed tracks.");
                var inputs = new List<(DemuxedTrack Track, Stream Source)>();
                for (int i = 0; i < plan.Tracks.Count; i++)
                {
                    var region = new RegionStream(source, plan.OffsetOf(i), plan.Tracks[i].Size);
                    TrackKind want = plan.Tracks[i].Role == "video" ? TrackKind.Video : TrackKind.Audio;
                    DemuxedTrack? t = MediaMerger.Demux(region, region.Length, ct).FirstOrDefault(x => x.Kind == want);
                    if (t is null)
                        return RunOutcome.NeedsAttention(want == TrackKind.Audio ? "missing-audio" : "missing-video",
                            $"The downloaded {plan.Tracks[i].Role} stream contains no {plan.Tracks[i].Role} track. Nothing was published; saved data was kept.");
                    if (plan.Tracks[i].Language is { Length: 3 } lang && t.Language == "und") t.Language = lang;
                    inputs.Add((t, region));
                }
                var expected = inputs.Select(i => i.Track).ToList();
                string? container = MediaMerger.ChooseContainer(expected, plan.OutputContainer);
                if (container is null)
                    return RunOutcome.NeedsAttention("unsupported-container",
                        $"The downloaded codecs ({string.Join(", ", expected.Select(t => t.Codec))}) cannot be stored in {plan.OutputContainer} without conversion. Nothing was published; saved data was kept.");
                EnsureSpace(plan.TotalBytes + (1 << 20));
                MediaThumbnail? thumbnail = container == "mkv"
                    ? await MediaThumbnail.FetchAsync(Job.MediaThumbnailUrl, http, engine.Options.AllowPrivateNetworks, ct).ConfigureAwait(false)
                    : null;
                using var output = new FileStream(mergingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
                MergeResult merged = MediaMerger.Merge(inputs, container, output, ct, plan.Metadata, thumbnail);
                output.Flush(flushToDisk: true);
                length = output.Length;
                MediaMerger.Validate(output, length, expected, ct, merged.Metadata, merged.ThumbnailSha256);
                output.Position = 0;
                sha = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            }
        }
        catch (MediaContainerException ex)
        {
            TryDelete(mergingPath);
            return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Nothing was published; saved data was kept.");
        }
        catch (IOException ex) when (ErrorClassifier.IsDiskFull(ex))
        {
            TryDelete(mergingPath);
            return RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space to merge. Saved data was kept.");
        }

        // Swap: the downloaded tracks stay on disk under a sidecar name until the merged file is published.
        string tracksPath = TracksPath(Job);
        if (File.Exists(tracksPath)) return Stop("An older merge sidecar is in the way.");
        File.Move(Job.PartialPath, tracksPath);
        File.Move(mergingPath, Job.PartialPath);
        var next = new ExtentSet();
        next.Add(new ByteRange(0, length));
        DownloadJob committed = engine.CommitCheckpoint(Job, next, JsonSerializer.Serialize(new Cursor(1, fingerprint, "muxed", length, sha)));
        lock (session.Gate)
        {
            Job.Extents = next;
            Job.Generation = committed.Generation;
            Job.MediaState = committed.MediaState;
        }
        engine.WriteManifest(committed);
        engine.UpdateJob(Job, j => { j.TotalBytes = length; j.Message = ""; });
        return await PublishAsync(ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> FinalizeMergedAsync(Cursor cursor, CancellationToken ct)
    {
        if (cursor.MuxLength <= 0 || cursor.MuxSha256 is not { Length: 64 }) return Stop("The merge cursor is incomplete.");
        if (!File.Exists(Job.PartialPath)) return Stop("The merged file is missing.");
        using (var fs = new FileStream(Job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
        {
            if (fs.Length < cursor.MuxLength) return Stop("The merged file is shorter than recorded.");
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(new RegionStream(fs, 0, cursor.MuxLength), ct).ConfigureAwait(false));
            if (actual != cursor.MuxSha256) return Stop("The merged file failed SHA-256 validation.");
        }
        if (Job.TotalBytes != cursor.MuxLength) engine.UpdateJob(Job, j => j.TotalBytes = cursor.MuxLength);
        return await PublishAsync(ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> PublishAsync(CancellationToken ct)
    {
        RunOutcome outcome = await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
        if (outcome.State == JobState.Completed && !DeleteSidecars(Job))
            engine.UpdateJob(Job, j => j.Message = "Saved. A temporary track file could not be removed: " + PathSafety.ForDisplay(TracksPath(j)));
        return outcome;
    }

    /// <summary>A crash between the two renames leaves the tracks under the sidecar name; the merge output was never committed.</summary>
    private void RecoverInterruptedSwap()
    {
        string tracks = TracksPath(Job);
        if (!File.Exists(tracks)) return;
        session.CloseFile();
        if (File.Exists(Job.PartialPath)) File.Delete(Job.PartialPath);
        File.Move(tracks, Job.PartialPath);
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.PartialPath));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space to download and merge. Saved data was kept."));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unpublished temp file; next run overwrites it */ }
    }

    private static RunOutcome Stop(string reason) => RunOutcome.NeedsAttention("merge-integrity", reason + " Saved data was kept.");
}
