using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Downloads each track into its own region of the job's partial file using bounded
/// (8 MiB) range requests through the normal durable checkpoint pipeline, so progress, pause, crash recovery and
/// per-chunk retry reuse the engine's existing transfer logic. Then demuxes and remuxes the committed regions into one
/// container (no transcoding), validates the output and records the swap before moving files. The downloaded tracks are kept
/// until the merged file is published, so a failed merge never destroys completed work.
/// </summary>
internal sealed class MergeTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    internal const long ChunkBytes = 8L * 1024 * 1024;
    private const int Workers = 3;
    private const int ChunkAttempts = 3;
    private DownloadJob Job => session.Job;

    internal sealed record Cursor(int Version, string Fingerprint, string Phase, long MuxLength = 0, string? MuxSha256 = null,
        long TracksLength = 0, string? TracksSha256 = null, string? MuxIdentity = null, string? TracksIdentity = null,
        string? SubtitleNotice = null);
    private sealed class Progress { public long Offset; }

    // The read handle denies external writes and remains open from verification
    // through publication. The digest belongs to this object, not merely its path.
    internal sealed record VerifiedOutput(FileStream File, string Digest, string? Identity) : IDisposable
    {
        public void Dispose() => File.Dispose();
    }

    internal static string TracksPath(DownloadJob job) => job.PartialPath + ".tracks";
    internal static string MergingPath(DownloadJob job) => job.PartialPath + ".merging";

    /// <summary>Removes merge sidecars. Returns false when a file could not be removed (the caller decides how to report it).</summary>
    internal static bool DeleteSidecars(DownloadJob job)
    {
        string tracks = TracksPath(job);
        try
        {
            if (PathExists(MergingPath(job))) return false;
            if (!PathExists(tracks)) return true;
            if (job.MediaState is null) return false;
            Cursor? cursor = JsonSerializer.Deserialize<Cursor>(job.MediaState);
            if (cursor is not { Phase: "muxed", TracksLength: > 0, TracksSha256: { Length: 64 } digest }) return false;
            string? identity = cursor.TracksIdentity;
            if (identity is not { Length: 64 } || !identity.All(Uri.IsHexDigit)) return false;

            using var handle = PathSafety.OpenTemporaryHandle(tracks, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.SequentialScan, allowDelete: true);
            PathSafety.RequireTemporaryIdentity(handle, cursor.TracksIdentity);
            using var file = new FileStream(handle, FileAccess.Read, 1 << 16);
            if (file.Length != cursor.TracksLength
                || !Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                return false;
            PathSafety.DeleteOpenTemporaryFile(handle, tracks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
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
        if (cursor?.Phase == "swapping") return await FinishSwapAsync(cursor, ct).ConfigureAwait(false);
        if (cursor?.Phase == "muxed") return await FinalizeMergedAsync(cursor, ct).ConfigureAwait(false);
        if (cursor is not null && cursor.Phase != "download") return Stop("The saved merge cursor has an unknown phase.");
        // A staging identity is saved before writing the output. Only that exact object can be discarded.
        if (PathExists(MergingPath(Job)) && cursor?.MuxIdentity is { } stageIdentity)
        {
            if (!TryDeleteSidecar(MergingPath(Job), stageIdentity, out _))
                return Stop("The unfinished merge output no longer belongs to this job.");
        }

        RestoreLegacyTracks(Job);
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
            EnsureSpace(work.Sum(w => w.End - w.Start) + (plan.ClipRange is null ? total : 0));
            await RunWorkersAsync(plan, work, state, ct).ConfigureAwait(false);
            await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        }
        if (!Job.Extents.Covers(0, total))
            return RunOutcome.NeedsAttention("incomplete", "Some track data is missing. Saved data was kept; resume to fetch the rest.");
        ct.ThrowIfCancellationRequested();
        return await MergeAsync(plan, fingerprint, ct).ConfigureAwait(false);
    }

    // download

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
            RequestCredentials? credentials = engine.CredentialsFor(Job.Id);
            using var response = await http.SendAsync(uri, new RequestSpec(from, toInclusive, IfRangeEtag: track.ResourceETag, Referer: Job.Referer,
                UserAgent: Job.UserAgent, Secrets: credentials?.Headers,
                CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl), ct).ConfigureAwait(false);
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
            if (track.ResourceETag is { } tag)
            {
                Uri final = response.RequestMessage?.RequestUri ?? uri;
                string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(final.AbsoluteUri)));
                if (response.Headers.ETag?.ToString() != tag || hash != track.ResourceUrlSha256
                    || response.Content.Headers.ContentRange?.Length != track.Size)
                    throw new RunnerStop(Stop("The selected media file changed after verification. Saved track data was kept."));
            }
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

    // merge

    private async Task<RunOutcome> MergeAsync(MediaMergePlan plan, string fingerprint, CancellationToken ct)
    {
        using var timing = ProcessingTiming.Measure(Job.Id, ProcessingPhase.Merge, Job.TotalBytes ?? 0);
        string mergingPath = MergingPath(Job);
        string? mergingIdentity = null;
        string? tracksIdentity = null;
        FileStream? source = null;
        bool swapCursorSaved = false;
        bool preserveStageForCrash = false;
        var subtitleLeases = new List<SegmentBufferLease>();
        var subtitleStreams = new List<MemoryStream>();
        var subtitleWarnings = new List<string>();
        try
        {
            if (PathExists(mergingPath))
                return Stop("An unfinished merge output has no saved swap proof. The file and downloaded tracks were kept.");

            engine.UpdateJob(Job, j => { j.State = JobState.Verifying; j.Reason = "prepare-media"; j.Message = "Preparing file"; });
            long length;
            string sha;
            long tracksLength;
            string tracksSha;
            session.CloseFile();
            try
            {
                source = PartialFile.OpenOwnedRead(Job, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
                {
                    if (source.Length < plan.TotalBytes) return Stop("The partial file is shorter than its committed tracks.");
                    var inputs = new List<(DemuxedTrack Track, Stream Source)>();
                    for (int i = 0; i < plan.Tracks.Count; i++)
                    {
                        var region = new RegionStream(source, plan.OffsetOf(i), plan.Tracks[i].Size);
                        List<DemuxedTrack> demuxed = MediaMerger.Demux(region, region.Length, ct);
                        if (plan.Tracks[i].Role == "muxed")
                        {
                            foreach (DemuxedTrack track in demuxed) inputs.Add((track, region));
                            continue;
                        }

                        TrackKind want = plan.Tracks[i].Role == "video" ? TrackKind.Video : TrackKind.Audio;
                        DemuxedTrack? selectedTrack = demuxed.FirstOrDefault(candidate => candidate.Kind == want);
                        if (selectedTrack is null)
                            return RunOutcome.NeedsAttention(want == TrackKind.Audio ? "missing-audio" : "missing-video",
                                $"The downloaded {plan.Tracks[i].Role} stream contains no {plan.Tracks[i].Role} track. Nothing was published; saved data was kept.");
                        if (plan.Tracks[i].Language is { Length: 3 } language && selectedTrack.Language == "und")
                            selectedTrack.Language = language;
                        inputs.Add((selectedTrack, region));
                    }

                    if (plan.RequireVideo && inputs.All(input => input.Track.Kind != TrackKind.Video)
                        || plan.RequireAudio && inputs.All(input => input.Track.Kind != TrackKind.Audio))
                        return RunOutcome.NeedsAttention("missing-media-track",
                            "The downloaded media is missing a selected audio or video track. Nothing was published; saved data was kept.");

                    string? container = MediaMerger.ChooseContainer(inputs.Select(input => input.Track).ToArray(), plan.OutputContainer);
                    if (container is null)
                        return RunOutcome.NeedsAttention("unsupported-container",
                            $"The downloaded codecs ({string.Join(", ", inputs.Select(input => input.Track.Codec))}) cannot be stored in {plan.OutputContainer} without conversion. Nothing was published; saved data was kept.");
                    if (plan.ClipRange is { } clip)
                        Mp4Clip.Apply(inputs.Select(input => input.Track).ToArray(), clip, ct);
                    EnsureSpace((plan.ClipRange is null ? plan.TotalBytes : inputs.Sum(input => input.Track.PayloadBytes)) + (1 << 20));
                    MediaThumbnail? thumbnail = container is "mp4" or "m4a" or "webm" or "mkv"
                        ? await MediaThumbnail.FetchAsync(Job.MediaThumbnailUrl, http, engine.Options.AllowPrivateNetworks, ct).ConfigureAwait(false)
                        : null;

                    if (plan.Subtitles.Count > 0)
                    {
                        int capacity = MediaMerger.AvailableTrackSlots(container, inputs.Count);
                        MergeSubtitle[] subtitlesToEmbed = plan.Subtitles.Take(capacity).ToArray();
                        int remaining = plan.Subtitles.Count - subtitlesToEmbed.Length;
                        if (remaining > 0)
                        {
                            string noun = remaining == 1 ? "subtitle remains" : "subtitles remain";
                            subtitleWarnings.Add($"The {container.ToUpperInvariant()} output supports at most {MediaMerger.MaxTrackCount} total tracks; {remaining} selected {noun} separate.");
                        }

                        if (subtitlesToEmbed.Length > 0)
                        {
                            long reservation = checked((long)subtitlesToEmbed.Length * TextSubtitleParser.MaxBytes * 2);
                            subtitleLeases.Add(await engine.AcquireSegmentBufferAsync(reservation, ct).ConfigureAwait(false));
                            double duration = inputs.Where(input => input.Track.Kind is TrackKind.Video or TrackKind.Audio)
                                .Select(input => input.Track.DurationSeconds).Where(seconds => seconds > 0)
                                .DefaultIfEmpty(0).Max();
                            double? mediaDuration = duration > 0 ? duration : null;
                            foreach (MergeSubtitle subtitle in subtitlesToEmbed)
                            {
                                DemuxedTrack? videoTrack = inputs.Select(input => input.Track)
                                    .FirstOrDefault(track => track.Kind == TrackKind.Video);
                                (ParsedSubtitle? parsed, string? notice) = await FetchSubtitleAsync(subtitle, mediaDuration,
                                    container, videoTrack?.Width ?? 0, videoTrack?.Height ?? 0, ct)
                                    .ConfigureAwait(false);
                                if (parsed is null)
                                {
                                    if (notice is { Length: > 0 }) subtitleWarnings.Add(notice);
                                    continue;
                                }
                                DemuxedTrack track = parsed.Track;
                                MemoryStream stream = parsed.Source;
                                subtitleStreams.Add(stream);
                                inputs.Add((track, stream));
                            }
                        }
                    }

                    var expected = inputs.Select(i => i.Track).ToList();
                    container = MediaMerger.ChooseContainer(expected, plan.OutputContainer);
                    if (container is null)
                        return RunOutcome.NeedsAttention("unsupported-container",
                            $"The downloaded codecs ({string.Join(", ", expected.Select(t => t.Codec))}) cannot be stored in {plan.OutputContainer} without conversion. Nothing was published; saved data was kept.");
                    using var output = CreateNewSidecar(mergingPath);
                    mergingIdentity = CaptureSidecarIdentity(output);
                    string staging = JsonSerializer.Serialize(new Cursor(1, fingerprint, "download", MuxIdentity: mergingIdentity));
                    engine.UpdateJob(Job, job => job.MediaState = staging);
                    MergeResult merged = MediaMerger.Merge(inputs, container, output, ct, plan.Metadata, thumbnail);
                    output.Flush(flushToDisk: true);
                    length = output.Length;
                    sha = await MergeTransfer.ValidateAndHashOutputAsync(Job.Id, output, length, inputs, merged, ct)
                        .ConfigureAwait(false);
                    tracksLength = source.Length;
                    source.Position = 0;
                    using (ProcessingTiming.Measure(Job.Id, ProcessingPhase.Hash, tracksLength))
                        tracksSha = Convert.ToHexString(await SHA256.HashDataAsync(source, ct).ConfigureAwait(false));
                    tracksIdentity = PathSafety.TemporaryFileIdentity(source.SafeFileHandle)
                        ?? throw new IOException("Stable temporary file identity is not supported on this platform.");
                    foreach (MemoryStream stream in subtitleStreams) stream.Dispose();
                    subtitleStreams.Clear();
                    foreach (SegmentBufferLease lease in subtitleLeases) lease.Dispose();
                    subtitleLeases.Clear();
                }
                engine.Failpoint("merge.afterStageWrite");
            }
            catch (MediaContainerException ex)
            {
                return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Nothing was published; saved data was kept.");
            }
            catch (IOException ex) when (ErrorClassifier.IsDiskFull(ex))
            {
                return RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space to merge. Saved data was kept.");
            }

            if (PathExists(TracksPath(Job))) return Stop("An older merge sidecar is in the way.");
            ct.ThrowIfCancellationRequested();
            string? subtitleNotice = subtitleWarnings.Count == 0 ? null
                : "Saved. Some selected subtitles were not embedded. "
                    + string.Join(" ", subtitleWarnings.Distinct(StringComparer.Ordinal));
            var swap = new Cursor(1, fingerprint, "swapping", length, sha, tracksLength, tracksSha,
                mergingIdentity, tracksIdentity, subtitleNotice);
            string swapState = JsonSerializer.Serialize(swap);
            try
            {
                engine.UpdateJob(Job, job => job.MediaState = swapState);
                swapCursorSaved = true;
            }
            catch
            {
                swapCursorSaved = string.Equals(Job.MediaState, swapState, StringComparison.Ordinal);
                throw;
            }
            engine.Failpoint("merge.beforeSwap");
            using var verifiedTracks = new VerifiedOutput(source!, tracksSha, tracksIdentity);
            return await FinishSwapAsync(swap, ct, verifiedTracks).ConfigureAwait(false);
        }
        catch (FailpointCrash)
        {
            preserveStageForCrash = true;
            throw;
        }
        finally
        {
            source?.Dispose();
            foreach (MemoryStream stream in subtitleStreams) stream.Dispose();
            foreach (SegmentBufferLease lease in subtitleLeases) lease.Dispose();
            if (!swapCursorSaved && !preserveStageForCrash && mergingIdentity is not null)
                TryDelete(mergingPath, mergingIdentity);
        }
    }

    private async Task<(ParsedSubtitle? Parsed, string? Notice)> FetchSubtitleAsync(MergeSubtitle subtitle,
        double? mediaDurationSeconds, string outputContainer, int videoWidth, int videoHeight, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(subtitle.Url, out Uri uri, out _))
            return (null, "A selected subtitle address was invalid; its separate subtitle download is available.");

        session.AddConnection();
        try
        {
            RequestCredentials? credentials = engine.CredentialsFor(Job.Id);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(uri, new RequestSpec(Referer: Job.Referer, UserAgent: Job.UserAgent,
                    Secrets: credentials?.Headers, CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return (null, "A selected subtitle could not be fetched in time; its separate subtitle download is available."); }
            catch (Exception ex) when (ex is TransientNetworkException or RedirectException or ProxyConfigurationException
                or BlockedAddressException or HttpRequestException)
            { return (null, "A selected subtitle could not be fetched safely; its separate subtitle download is available."); }

            using (response)
            {
            int status = (int)response.StatusCode;
            if (status != 200)
                return (null, "A selected subtitle address was refused; its separate subtitle download is available.");
            if (!HttpFacts.IsIdentityEncoded(response) || HttpFacts.IsHtml(response))
                return (null, "A selected subtitle response could not be embedded safely; its separate subtitle download is available.");
            if (response.Content.Headers.ContentLength is > TextSubtitleParser.MaxBytes)
                return (null, "A selected subtitle is larger than the 2 MiB embedding limit; its separate subtitle download is available.");

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(engine.Options.StallTimeout);
            await using Stream responseStream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            using var content = new MemoryStream(response.Content.Headers.ContentLength is long length
                ? checked((int)length) : 64 * 1024);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (true)
                {
                    int read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (content.Length + read > TextSubtitleParser.MaxBytes)
                        return (null, "A selected subtitle is larger than the 2 MiB embedding limit; its separate subtitle download is available.");
                    await session.Limiter.AcquireAsync(read, ct).ConfigureAwait(false);
                    await engine.GlobalLimiter.AcquireAsync(read, ct).ConfigureAwait(false);
                    await content.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    stall.CancelAfter(engine.Options.StallTimeout);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "A selected subtitle transfer stalled; its separate subtitle download is available.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            if (response.Content.Headers.ContentLength is long expectedLength && content.Length != expectedLength)
                return (null, "A selected subtitle response was incomplete; its separate subtitle download is available.");

            try
            {
                ParsedSubtitle parsed = TextSubtitleParser.Parse(content.GetBuffer().AsSpan(0, checked((int)content.Length)),
                    subtitle.Format, subtitle.Language, subtitle.Name ?? subtitle.Language, mediaDurationSeconds,
                    outputContainer, videoWidth, videoHeight);
                return (parsed, null);
            }
            catch (MediaContainerException)
            { return (null, "A selected subtitle uses content VESPER cannot embed safely; its separate subtitle download is available."); }
            }
        }
        finally { session.RemoveConnection(); }
    }

    private async Task<RunOutcome> FinishSwapAsync(Cursor cursor, CancellationToken ct, VerifiedOutput? verifiedTracks = null)
    {
        using VerifiedOutput verified = await FinishSwapAsync(engine, Job, cursor.MuxLength, cursor.MuxSha256, cursor.TracksLength,
            cursor.TracksSha256, cursor.MuxIdentity, cursor.TracksIdentity, ct, verifiedTracks)
            .ConfigureAwait(false);
        verifiedTracks?.Dispose(); // release the saved-track handle before sidecar cleanup
        var next = new ExtentSet();
        next.Add(new ByteRange(0, cursor.MuxLength));
        DownloadJob committed = engine.CommitCheckpoint(Job, next, JsonSerializer.Serialize(cursor with { Phase = "muxed" }),
            totalBytes: cursor.MuxLength, partialIdentity: verified.Identity);
        engine.WriteManifest(committed, verified.File.SafeFileHandle);
        return await PublishAsync(ct, cursor.SubtitleNotice, verified).ConfigureAwait(false);
    }

    private async Task<RunOutcome> FinalizeMergedAsync(Cursor cursor, CancellationToken ct)
    {
        if (cursor.MuxLength <= 0 || cursor.MuxSha256 is not { Length: 64 }) return Stop("The merge cursor is incomplete.");
        if (!File.Exists(Job.PartialPath)) return Stop("The merged file is missing.");
        using VerifiedOutput? verified = await OpenVerifiedMergedOutputAsync(engine, Job, cursor.MuxLength, cursor.MuxSha256, ct).ConfigureAwait(false);
        if (verified is null) return Stop("The merged file failed its saved validation.");
        return await PublishAsync(ct, cursor.SubtitleNotice, verified).ConfigureAwait(false);
    }

    private async Task<RunOutcome> PublishAsync(CancellationToken ct, string? subtitleNotice, VerifiedOutput verified)
    {
        RunOutcome outcome = await engine.FinalizeVerifiedMergeAsync(session, verified, ct).ConfigureAwait(false);
        if (outcome.State == JobState.Completed)
        {
            string? cleanupNotice = DeleteSidecars(Job) ? null
                : "A temporary track file could not be removed: " + PathSafety.ForDisplay(TracksPath(Job));
            string message = string.Join(" ", new[] { outcome.Message, subtitleNotice, cleanupNotice }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (message.Length > 0) outcome = outcome with { Message = message };
        }
        return outcome;
    }

    internal static void RestoreLegacyTracks(DownloadJob job)
    {
        string tracks = TracksPath(job);
        if (!PathExists(tracks)) return;
        if (PathExists(job.PartialPath) || job.Extents.IsEmpty)
            throw new RunnerStop(Stop("There are two possible files from an older merge and no saved proof of the swap."));
        using (var saved = PathSafety.OpenTemporaryHandle(tracks, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PathSafety.RequireTemporaryIdentity(saved, job.PartialIdentity);
            if (RandomAccess.GetLength(saved) < job.Extents.End)
                throw new RunnerStop(Stop("The saved tracks are shorter than the committed data."));
        }
        if (!PathSafety.TryMoveTemporaryFile(tracks, job.PartialPath, job.PartialIdentity, out string error))
            throw new RunnerStop(Stop("The saved tracks could not be restored safely: " + error));
    }

    internal static async Task<VerifiedOutput> FinishSwapAsync(DownloadEngine engine, DownloadJob job, long muxLength, string? muxSha,
        long tracksLength, string? tracksSha, string? muxIdentity, string? tracksIdentity, CancellationToken ct,
        VerifiedOutput? verifiedTracks = null)
    {
        string partial = job.PartialPath;
        string tracks = TracksPath(job);
        string stage = MergingPath(job);
        if (muxIdentity is not { Length: 64 } || !muxIdentity.All(Uri.IsHexDigit))
            throw new RunnerStop(Stop("The saved merge output has no file identity. The interrupted output and downloaded tracks were kept."));
        string? expectedTracksIdentity = tracksIdentity ?? job.PartialIdentity;
        if (expectedTracksIdentity is not { Length: 64 } || !expectedTracksIdentity.All(Uri.IsHexDigit)
            || tracksIdentity is not null && !string.Equals(tracksIdentity, job.PartialIdentity, StringComparison.Ordinal))
            throw new RunnerStop(Stop("The saved tracks have no matching file identity. The interrupted output and tracks were kept."));

        bool hasTracks = PathExists(tracks);
        bool hasStage = PathExists(stage);
        bool hasPartial = PathExists(partial);
        if ((!hasTracks && !(hasStage && hasPartial)) || (hasTracks && hasStage == hasPartial)
            || tracksLength < job.Extents.End || muxLength <= 0 || muxSha is not { Length: 64 })
            throw new RunnerStop(Stop("The files from the interrupted merge do not match the saved swap."));

        string tracksLocation = hasTracks ? tracks : partial;
        // On an uninterrupted merge the same read-only, write-denying handle
        // has remained open since hashing the source. A recovered swap has no
        // such proof and must rehash every saved byte as before.
        bool tracksMatch;
        if (verifiedTracks is not null && OperatingSystem.IsWindows())
        {
            tracksMatch = tracksSha is { Length: 64 } && verifiedTracks.Digest.Equals(tracksSha, StringComparison.OrdinalIgnoreCase)
                && verifiedTracks.Identity == expectedTracksIdentity
                && verifiedTracks.File.Length == tracksLength
                && PathSafety.TemporaryFileIdentity(verifiedTracks.File.SafeFileHandle) == expectedTracksIdentity
                && PathSafety.PathStillNamesFile(tracksLocation, verifiedTracks.File, tracksSha);
        }
        else
        {
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, tracksLength))
                tracksMatch = await MatchesAsync(tracksLocation, tracksLength, tracksSha, ct, expectedTracksIdentity).ConfigureAwait(false);
        }
        if (!tracksMatch)
            throw new RunnerStop(Stop("The files from the interrupted merge do not match the saved swap."));

        var outputHandle = PathSafety.OpenTemporaryHandle(hasStage ? stage : partial, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, FileOptions.SequentialScan, writeAttributes: !hasStage);
        FileStream? output = null;
        try
        {
            output = new FileStream(outputHandle, FileAccess.Read, 1 << 16);
            string? actualMuxIdentity = PathSafety.TemporaryFileIdentity(output.SafeFileHandle);
            bool movedWithChangedTimestamp = !hasStage && PathSafety.SameFileId(muxIdentity, actualMuxIdentity);
            if (actualMuxIdentity != muxIdentity && !movedWithChangedTimestamp)
                throw new RunnerStop(Stop("The merged output no longer has its saved file identity. Saved data was kept."));
            string actualDigest;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, output.Length))
                actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            if (output.Length != muxLength || !actualDigest.Equals(muxSha, StringComparison.OrdinalIgnoreCase))
                throw new RunnerStop(Stop("The output from the interrupted merge does not match the saved swap."));
            // Recovery of a rename completed before the timestamp could be restored.
            // The durable cursor, same file ID and exact output hash must all agree.
            if (actualMuxIdentity != muxIdentity)
                PathSafety.RestoreMovedTemporaryIdentity(output.SafeFileHandle, muxIdentity);

            ct.ThrowIfCancellationRequested();
            if (!hasTracks)
            {
                engine.Failpoint("merge.beforeTracksMove");
                if (!PathSafety.TryMoveTemporaryFile(partial, tracks, job.PartialIdentity, out string moveError))
                    throw new RunnerStop(Stop("The downloaded tracks could not be moved safely: " + moveError));
                engine.Failpoint("merge.afterTracksMove");
            }
            if (hasStage)
            {
                if (!PathSafety.TryMoveTemporaryFile(stage, partial, muxIdentity, out string moveError))
                    throw new RunnerStop(Stop("The merged output could not be moved safely: " + moveError));
                engine.Failpoint("merge.afterOutputMove");
            }
            if (!PathSafety.PathStillNamesFile(partial, output, muxSha))
                throw new RunnerStop(Stop("The merged output path changed during the swap."));
            return new VerifiedOutput(output, actualDigest, PathSafety.TemporaryFileIdentity(output.SafeFileHandle));
        }
        catch
        {
            if (output is null) outputHandle.Dispose();
            else output.Dispose();
            throw;
        }
    }

    // Older muxed rows have a saved output hash but no file ID. Bind the verified object
    // and retain its read handle so publication cannot race an external writer.
    internal static async Task<VerifiedOutput?> OpenVerifiedMergedOutputAsync(DownloadEngine engine, DownloadJob job,
        long length, string digest, CancellationToken ct)
    {
        var output = PathSafety.OpenTemporaryStream(job.PartialPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        bool accepted = false;
        try
        {
            if (output.Length != length) return null;
            string actual;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, length))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase)) return null;
            string? identity = PathSafety.TemporaryFileIdentity(output.SafeFileHandle);
            if (job.PartialIdentity is not null && job.PartialIdentity != identity) return null;
            if (job.PartialIdentity != identity || job.TotalBytes != length)
                engine.UpdateJob(job, pending => { pending.PartialIdentity = identity; pending.TotalBytes = length; });
            accepted = true;
            return new VerifiedOutput(output, actual, identity);
        }
        finally { if (!accepted) output.Dispose(); }
    }

    internal static async Task<string> ValidateAndHashOutputAsync(Guid jobId, Stream output, long length,
        IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, MergeResult merged, CancellationToken ct)
    {
        // The fused pass benefits the MP4 layout. Keep Matroska's existing
        // validation order, which measured equivalently on the same workload.
        if (merged.Container is "mp4" or "m4a")
        {
            using var verify = ProcessingTiming.Measure(jobId, ProcessingPhase.Verify, length);
            return MediaMerger.ValidateRemuxAndHash(output, length, inputs, ct, merged.Metadata, merged.ThumbnailSha256);
        }
        using (ProcessingTiming.Measure(jobId, ProcessingPhase.Verify, length))
            MediaMerger.ValidateRemux(output, length, inputs, ct, merged.Metadata, merged.ThumbnailSha256);
        output.Position = 0;
        using var hash = ProcessingTiming.Measure(jobId, ProcessingPhase.Hash, length);
        return Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
    }

    internal static async Task<bool> MatchesAsync(string path, long length, string? digest, CancellationToken ct,
        string? expectedIdentity = null)
    {
        if (length <= 0 || digest is not { Length: 64 }) return false;
        using var file = PathSafety.OpenTemporaryStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        if (expectedIdentity is not null)
        {
            try { PathSafety.RequireTemporaryIdentity(file.SafeFileHandle, expectedIdentity); }
            catch (IOException) { return false; }
        }
        return file.Length == length
            && Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }

    internal static FileStream CreateNewSidecar(string path, FileOptions options = FileOptions.None)
    {
        try
        {
            return PathSafety.OpenTemporaryStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1 << 20, options);
        }
        catch (IOException) when (PathExists(path))
        {
            throw new RunnerStop(Stop("An unclaimed merge sidecar already exists. Saved data and that file were kept."));
        }
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.PartialPath));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space to download and merge. Saved data was kept."));
    }

    internal static string CaptureSidecarIdentity(FileStream sidecar) =>
        PathSafety.TemporaryFileIdentity(sidecar.SafeFileHandle)
        ?? throw new IOException("Stable temporary file identity is not supported on this platform.");

    internal static bool TryDeleteSidecar(string path, string? expectedIdentity, out string error) =>
        PathSafety.TryDeleteTemporaryFile(path, expectedIdentity, out error);

    private static void TryDelete(string path, string? expectedIdentity) =>
        _ = TryDeleteSidecar(path, expectedIdentity, out _);

    private static RunOutcome Stop(string reason) => RunOutcome.NeedsAttention("merge-integrity", reason + " Saved data was kept.");
}
