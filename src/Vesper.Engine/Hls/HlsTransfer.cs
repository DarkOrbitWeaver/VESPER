using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Downloads a non-DRM VOD HLS playlist into one file. Each segment is fetched whole, optionally decrypted
/// (AES-128 with an openly served key), appended at a known offset and then reported to the same checkpoint
/// pipeline as HTTP ranges, together with the next-segment cursor. Resume therefore restarts at a segment boundary.
/// Output is MPEG-TS (concatenated TS is valid) or fragmented MP4 (init segment + fragments). Separate audio
/// renditions would need a muxer and are refused rather than faked.
/// </summary>
internal sealed class HlsTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    private const long MaxPlaylistBytes = 4 * 1024 * 1024;
    private const long MaxSegmentBytes = 64L * 1024 * 1024; // Bound buffering until streaming decryption is available.
    private const string LiveMode = "live-hls";
    private sealed record Cursor(string Playlist, int Next, string? PlanHash = null, long Offset = 0,
        string? PrefixHash = null, int InitLength = 0, Mp4Progress? Progress = null,
        string? Mode = null, long? NextSequence = null, long? LastDiscontinuitySequence = null,
        string? Container = null, string? MapIdentity = null, string? SourceIdentity = null, int TargetDuration = 0,
        double CapturedSeconds = 0, string[]? KeyFingerprints = null, bool StopRequested = false,
        bool HadGaps = false, string? GapMessage = null);
    private DownloadJob Job => session.Job;

    internal static bool IsLiveState(string? json)
    {
        Cursor? cursor = ParseCursor(json);
        return cursor is { Mode: LiveMode, Offset: > 0 };
    }

    internal static bool TryRequestLiveStop(string? json, out string updated)
    {
        Cursor? cursor = ParseCursor(json);
        if (cursor is not { Mode: LiveMode, Offset: > 0 }) { updated = ""; return false; }
        updated = JsonSerializer.Serialize(cursor with { StopRequested = true });
        return true;
    }

    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out string folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");
        MediaTimeRange? clipRange = null;
        if (Job.MediaPlanJson is not null)
        {
            if (!MediaTimeRange.TryParse(Job.MediaPlanJson, out MediaTimeRange parsedRange))
                return RunOutcome.NeedsAttention("clip-range", "The saved HLS time range is invalid. Saved data was kept.");
            clipRange = parsedRange;
        }
        Cursor? cursor = ReadCursor();
        if (Job.MediaState is not null && cursor is null)
            return RunOutcome.NeedsAttention("cursor", "The saved streaming cursor is invalid. Saved data was kept.");
        if (cursor is { Mode: LiveMode, StopRequested: true })
            return await FinalizeStoppedLiveAsync(cursor, ct).ConfigureAwait(false);
        if (cursor is not null && cursor.Mode is not null and not LiveMode)
            return RunOutcome.NeedsAttention("cursor", "The saved HLS recording mode is unsupported. Saved data was kept.");
        string playlistUrl = cursor?.Playlist ?? Job.SourceUrl;
        (string text, Uri baseUri) = await FetchTextAsync(playlistUrl, ct).ConfigureAwait(false);
        if (!HlsParser.IsPlaylist(text)) return RunOutcome.NeedsAttention("not-playlist", "The address did not return an HLS playlist.");
        if (HlsParser.IsMaster(text))
        {
            string[] sessionKeys = text.Split('\n').Select(line => line.Trim())
                .Where(line => line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)).ToArray();
            if (sessionKeys.Length > 0)
            {
                string reasonCode = sessionKeys.Any(HlsParser.IsDrmSessionKey) ? "drm" : "unsupported";
                return RunOutcome.NeedsAttention(reasonCode, "Master session keys are not supported.");
            }
            IReadOnlyList<HlsVariant> usable = [.. HlsParser.ParseMaster(text, baseUri).Where(v => !v.NeedsSeparateAudio)];
            if (usable.Count == 0)
                return RunOutcome.NeedsAttention("separate-audio", "Every quality keeps audio separate; VESPER does not include a verified muxer for this combination.");
            HlsVariant best = usable.OrderByDescending(v => v.Bandwidth).First();
            (text, baseUri) = await FetchTextAsync(best.Uri, ct).ConfigureAwait(false);
        }
        playlistUrl = baseUri.AbsoluteUri;
        HlsMedia media = HlsParser.ParseMedia(text, baseUri, allowTimelineChanges: true);
        if (media.Unsupported is { } reason) return RunOutcome.NeedsAttention(media.UnsupportedReasonCode, reason);
        if (clipRange is not null && !media.IsVod)
            return RunOutcome.NeedsAttention("clip-live", "Time-range extraction is available for completed HLS playlists, not live recordings.");
        if (cursor?.Mode == LiveMode || !media.IsVod)
            return await RunLiveAsync(playlistUrl, media, cursor, ct).ConfigureAwait(false);
        if (media.Segments.Count == 0) return RunOutcome.Failed("empty", "The playlist contains no media segments.");
        if (media.Segments.Any(segment => segment.DiscontinuityBefore || segment.IsGap))
            return RunOutcome.NeedsAttention("unsupported", "Discontinuous or missing media requires processing that is not supported.");
        if (media.Segments.Skip(1).Any(segment => !SameInitialization(media.Segments[0].Map, segment.Map)))
            return RunOutcome.NeedsAttention("unsupported", "Initialization URI, byte range or encryption changes part-way. A remux is required; saved data was kept.");
        byte[] planBytes = clipRange is null ? JsonSerializer.SerializeToUtf8Bytes(media.Segments)
            : JsonSerializer.SerializeToUtf8Bytes(new { media.Segments, Clip = clipRange });
        string planHash = Convert.ToHexString(SHA256.HashData(planBytes));
        IReadOnlyList<HlsSegment> segments = media.Segments;
        double actualStart = 0, actualEnd = 0;
        if (clipRange is not null)
        {
            double[] durations = [.. media.Segments.Select(segment => segment.Duration)];
            if (!clipRange.TrySelect(durations, out int first, out int count, out actualStart, out actualEnd))
                return RunOutcome.NeedsAttention("clip-range", "The requested HLS time range is outside the available segment timeline.");
            segments = media.Segments.Skip(first).Take(count).ToArray();
            string range = MediaTimeRange.Describe(actualStart, actualEnd);
            engine.UpdateJob(Job, job =>
            {
                string quality = job.MediaQuality ?? "HLS";
                int priorRange = quality.IndexOf(" · Segment-aligned ", StringComparison.Ordinal);
                if (priorRange >= 0) quality = quality[..priorRange];
                job.MediaQuality = quality + " · " + range;
            });
        }
        int next = cursor?.Next ?? 0;
        long offset = Job.Extents.ContiguousFromZero();
        if (next < 0 || next > segments.Count || (next == 0) != (offset == 0) || Job.Extents.TotalBytes != offset)
            return RunOutcome.NeedsAttention("cursor", "Saved progress no longer matches the playlist. Saved data was kept.");
        bool fragmented = media.Segments[0].Map is not null;
        SetOutputName(fragmented ? ".mp4" : ".ts");
        PartialFile file = session.OpenFile();
        if (file.Length < offset) return RunOutcome.NeedsAttention("cursor", "The partial file is shorter than committed progress. Saved data was kept.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FragmentedMp4? mp4 = null;
        TransportStreamValidator? transport = fragmented ? null : new();
        int initLength = 0;
        var keys = new Dictionary<string, byte[]>();
        try
        {
            if (offset > 0)
            {
                if (cursor?.PlanHash != planHash || cursor.Offset != offset || cursor.PrefixHash is null)
                    return RunOutcome.NeedsAttention("source-changed", "The playlist changed, or this legacy cursor has no verified manifest identity. Restart is required; saved data was kept.");
                MediaPartialIntegrity.HashPrefix(file, offset, hash, ct, transport);
                if (Convert.ToHexString(hash.GetCurrentHash()) != cursor.PrefixHash)
                    return RunOutcome.NeedsAttention("integrity", "The committed HLS prefix failed SHA-256 validation. Saved data was kept.");
                initLength = cursor.InitLength;
                if (fragmented)
                {
                    if (cursor.Progress is null) throw new MediaContainerException("Saved MP4 track state is missing.");
                    mp4 = FragmentedMp4.Open(MediaPartialIntegrity.ReadInitialization(file, initLength, ct));
                    mp4.Restore(cursor.Progress);
                }
            }
            session.SetPhase(JobState.Downloading);
            DateTime lastMessage = DateTime.MinValue;
            for (int i = next; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                HlsSegment segment = segments[i];
                byte[]? initialization = null;
                if (i == 0 && segment.Map is { } map)
                {
                    initialization = await FetchSegmentAsync(map.Uri, map.Offset, map.Length, ct).ConfigureAwait(false);
                    if (map.Key is { } mapKey)
                    {
                        if (mapKey.Iv is null) throw new MediaContainerException("Encrypted initialization requires an explicit IV.");
                        initialization = await DecryptAsync(initialization, mapKey, segment.Sequence, keys, ct).ConfigureAwait(false);
                    }
                    if (initialization.Length > 8 * 1024 * 1024) throw new MediaContainerException("Initialization exceeds the supported size limit.");
                    mp4 = FragmentedMp4.Open(initialization);
                    initLength = initialization.Length;
                }
                byte[] data = await FetchSegmentAsync(segment.Uri, segment.RangeOffset, segment.RangeLength, ct).ConfigureAwait(false);
                if (segment.Key is { } key) data = await DecryptAsync(data, key, segment.Sequence, keys, ct).ConfigureAwait(false);
                if (fragmented) mp4!.ValidateFragment(data);
                else transport!.Validate(data);
                int totalLength = checked((initialization?.Length ?? 0) + data.Length);
                string? root = Path.GetPathRoot(Job.PartialPath);
                if (root is not null && new DriveInfo(root) is { IsReady: true } drive
                    && drive.AvailableFreeSpace < totalLength + engine.Options.FreeSpaceReserve)
                    throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "Not enough free disk space. Saved data was kept."));
                await session.Limiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
                if (initialization is not null)
                {
                    await session.WriteAsync(initialization, offset, report: false, ct).ConfigureAwait(false);
                    hash.AppendData(initialization);
                }
                await session.WriteAsync(data, offset + (initialization?.Length ?? 0), report: false, ct).ConfigureAwait(false);
                hash.AppendData(data);
                long end = checked(offset + totalLength);
                session.Report(new ByteRange(offset, end), JsonSerializer.Serialize(new Cursor(playlistUrl, i + 1,
                    planHash, end, Convert.ToHexString(hash.GetCurrentHash()), initLength, mp4?.Snapshot())));
                offset = end;
                await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastMessage > TimeSpan.FromSeconds(1))
                {
                    lastMessage = DateTime.UtcNow;
                    engine.UpdateJob(Job, j => j.Message = $"Segment {i + 1} of {segments.Count}");
                }
            }
            mp4?.Finish(requireAudio: Job.MediaProvider is not null, requireVideo: false);
            transport?.Finish(requireAudio: Job.MediaProvider is not null);
        }
        catch (MediaContainerException ex)
        { return RunOutcome.NeedsAttention("invalid-container", ex.Message + " No output was published; saved data was kept."); }
        finally { foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key); }
        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        engine.UpdateJob(Job, j => { j.TotalBytes = offset; j.Message = ""; });
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> RunLiveAsync(string playlistUrl, HlsMedia media, Cursor? cursor, CancellationToken ct)
    {
        if (media.TargetDuration <= 0 || media.PlaylistType == "VOD" && !media.IsVod)
            return RunOutcome.NeedsAttention("live-playlist", "The live playlist has no usable target duration or is marked VOD without ENDLIST.");
        if (media.Segments.Count > 0 && media.Segments.Skip(1)
            .Any(segment => !SameInitialization(media.Segments[0].Map, segment.Map)))
            return RunOutcome.NeedsAttention("live-layout", "The live playlist changes initialization data inside its current window. Saved data was kept.");

        long offset = Job.Extents.ContiguousFromZero();
        if (Job.Extents.TotalBytes != offset || (cursor is null && offset != 0)
            || (cursor is not null && cursor.Offset != offset))
            return RunOutcome.NeedsAttention("cursor", "Saved live progress does not match its committed segment cursor. Saved data was kept.");

        string container = media.Segments.Count > 0
            ? media.Segments[0].Map is null ? "ts" : "mp4"
            : cursor?.Container ?? "ts";
        string mapIdentity = media.Segments.Count > 0
            ? MapIdentity(media.Segments[0].Map)
            : cursor?.MapIdentity ?? "";
        string planHash = LivePlanHash(playlistUrl, container, mapIdentity, media.TargetDuration);
        PartialFile file = session.OpenFile();
        if (file.Length < offset)
            return RunOutcome.NeedsAttention("cursor", "The live recording file is shorter than its committed cursor. Saved data was kept.");
        if (file.Length > offset) file.Truncate(offset);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TransportStreamValidator? transport = container == "ts" ? new() : null;
        FragmentedMp4? mp4 = null;
        int initLength = cursor?.InitLength ?? 0;
        long nextSequence;
        long? lastDiscontinuity = cursor?.LastDiscontinuitySequence;
        double capturedSeconds = cursor?.CapturedSeconds ?? 0;

        if (cursor is null)
        {
            nextSequence = media.Segments.Count > 0 ? media.Segments[0].Sequence : media.MediaSequence;
            cursor = new Cursor(playlistUrl, 0, planHash, 0, EmptyHash(), Mode: LiveMode,
                NextSequence: nextSequence, Container: container, MapIdentity: mapIdentity, SourceIdentity: LiveSourceIdentity(Job),
                TargetDuration: media.TargetDuration, KeyFingerprints: []);
            engine.UpdateJob(Job, job =>
            {
                job.MediaState = JsonSerializer.Serialize(cursor);
                job.TotalBytes = null;
                job.Message = "Recording live";
            });
        }
        else
        {
            if (cursor.Mode != LiveMode || cursor.Playlist != playlistUrl || cursor.PlanHash != planHash
                || cursor.Container != container || cursor.MapIdentity != mapIdentity
                || cursor.SourceIdentity != LiveSourceIdentity(Job)
                || cursor.TargetDuration != media.TargetDuration || cursor.NextSequence is not long expected
                || expected < 0 || cursor.PrefixHash is not { Length: 64 }
                || !double.IsFinite(capturedSeconds) || capturedSeconds < 0)
                return RunOutcome.NeedsAttention("source-changed", "The live playlist identity or timeline changed. Saved data was kept.");
            nextSequence = expected;
        }

        if (cursor.KeyFingerprints is { Length: > 32 })
            return RunOutcome.NeedsAttention("key-state", "The saved live key history is too large or invalid. Saved data was kept.");
        if (!TryReadKeyFingerprints(cursor.KeyFingerprints, out Dictionary<string, string> keyFingerprints))
            return RunOutcome.NeedsAttention("key-state", "The saved live key identities are invalid. Saved data was kept.");

        if (offset > 0)
        {
            MediaPartialIntegrity.HashPrefix(file, offset, hash, ct, transport);
            if (Convert.ToHexString(hash.GetCurrentHash()) != cursor.PrefixHash)
                return RunOutcome.NeedsAttention("integrity", "The committed live prefix failed SHA-256 validation. Saved data was kept.");
            if (container == "mp4")
            {
                if (initLength is < 8 or > 8 * 1024 * 1024 || cursor.Progress is null || offset <= initLength)
                    return RunOutcome.NeedsAttention("cursor", "The saved live MP4 track state is incomplete. Saved data was kept.");
                mp4 = FragmentedMp4.Open(MediaPartialIntegrity.ReadInitialization(file, initLength, ct));
                mp4.Restore(cursor.Progress);
            }
        }
        else if (cursor.PrefixHash != EmptyHash() || cursor.InitLength != 0 || cursor.Progress is not null)
            return RunOutcome.NeedsAttention("cursor", "The empty live cursor contains unexpected media state. Saved data was kept.");

        SetOutputName(container == "ts" ? ".ts" : ".mp4");
        session.SetPhase(JobState.Downloading);
        engine.UpdateJob(Job, job => { job.TotalBytes = null; job.Message = RecordingMessage(capturedSeconds); });
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                HlsMap? windowMap = media.Segments.Count == 0 ? null : media.Segments[0].Map;
                if (media.Segments.Count > 0 && media.Segments.Skip(1)
                    .Any(segment => !SameInitialization(windowMap, segment.Map)))
                {
                    cursor = MarkLiveGap(cursor, "The live stream changed its initialization data.");
                    return RunOutcome.NeedsAttention("live-discontinuity", cursor.GapMessage + " Saved data was kept.");
                }
                if (media.Segments.Count > 0 && MapIdentity(windowMap) != cursor.MapIdentity)
                {
                    cursor = MarkLiveGap(cursor, "The live stream changed its initialization data.");
                    return RunOutcome.NeedsAttention("live-discontinuity", cursor.GapMessage + " Saved data was kept.");
                }

                foreach (HlsSegment segment in media.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    if (segment.Sequence < nextSequence) continue;
                    if (segment.Sequence > nextSequence)
                    {
                        cursor = MarkLiveGap(cursor, $"The live window dropped segment {nextSequence} before it could be saved.");
                        return RunOutcome.NeedsAttention("live-gap", cursor.GapMessage + " Saved data was kept. Stop and keep can save the playable portion.");
                    }
                    if (segment.IsGap || segment.DiscontinuityBefore
                        || lastDiscontinuity is long previous && segment.DiscontinuitySequence != previous)
                    {
                        string message = segment.IsGap
                            ? $"The source marked live segment {segment.Sequence} as missing."
                            : $"The live stream changed timeline at segment {segment.Sequence}.";
                        cursor = MarkLiveGap(cursor, message);
                        return RunOutcome.NeedsAttention(segment.IsGap ? "live-gap" : "live-discontinuity",
                            cursor.GapMessage + " Saved data was kept. Stop and keep can save the playable portion.");
                    }
                    if (segment.Map is not null && MapIdentity(segment.Map) != cursor.MapIdentity
                        || segment.Map is null && cursor.Container == "mp4")
                    {
                        cursor = MarkLiveGap(cursor, $"The live stream changed initialization data at segment {segment.Sequence}.");
                        return RunOutcome.NeedsAttention("live-discontinuity", cursor.GapMessage + " Saved data was kept.");
                    }
                    if (segment.Sequence == long.MaxValue)
                        return RunOutcome.NeedsAttention("live-sequence", "The live media sequence number overflowed. Saved data was kept.");

                    byte[]? initialization = null;
                    if (offset == 0 && cursor.Container == "mp4")
                    {
                        if (segment.Map is not { } map)
                            return RunOutcome.NeedsAttention("live-layout", "The live MP4 playlist has no initialization segment.");
                        initialization = await FetchSegmentAsync(map.Uri, map.Offset, map.Length, ct).ConfigureAwait(false);
                        if (map.Key is { } mapKey)
                        {
                            if (mapKey.Iv is null) return RunOutcome.NeedsAttention("key", "Encrypted initialization requires an explicit AES IV.");
                            initialization = await DecryptLiveAsync(initialization, mapKey, segment.Sequence, keys, keyFingerprints, ct).ConfigureAwait(false);
                        }
                        if (initialization.Length is < 8 or > 8 * 1024 * 1024)
                            throw new MediaContainerException("The live initialization segment is empty or too large.");
                        mp4 = FragmentedMp4.Open(initialization);
                    }

                    byte[] data = await FetchSegmentAsync(segment.Uri, segment.RangeOffset, segment.RangeLength, ct).ConfigureAwait(false);
                    if (segment.Key is { } key)
                        data = await DecryptLiveAsync(data, key, segment.Sequence, keys, keyFingerprints, ct).ConfigureAwait(false);
                    if (cursor.Container == "ts") transport!.Validate(data);
                    else mp4!.ValidateFragment(data);

                    int partLength = checked((initialization?.Length ?? 0) + data.Length);
                    EnsureLiveSpace(partLength);
                    await session.Limiter.AcquireAsync(partLength, ct).ConfigureAwait(false);
                    await engine.GlobalLimiter.AcquireAsync(partLength, ct).ConfigureAwait(false);
                    long start = offset;
                    if (initialization is not null)
                    {
                        initLength = initialization.Length;
                        await session.WriteAsync(initialization, offset, report: false, ct).ConfigureAwait(false);
                        hash.AppendData(initialization);
                    }
                    await session.WriteAsync(data, offset + (initialization?.Length ?? 0), report: false, ct).ConfigureAwait(false);
                    hash.AppendData(data);
                    offset = checked(offset + partLength);
                    nextSequence = segment.Sequence + 1;
                    lastDiscontinuity = segment.DiscontinuitySequence;
                    capturedSeconds += segment.Duration;
                    if (!double.IsFinite(capturedSeconds)) throw new MediaContainerException("The live recording duration overflowed.");
                    cursor = new Cursor(playlistUrl, 0, planHash, offset, Convert.ToHexString(hash.GetCurrentHash()),
                        initLength, mp4?.Snapshot(), Mode: LiveMode, NextSequence: nextSequence,
                        LastDiscontinuitySequence: lastDiscontinuity, Container: container, MapIdentity: mapIdentity,
                        SourceIdentity: cursor.SourceIdentity,
                        TargetDuration: media.TargetDuration, CapturedSeconds: capturedSeconds,
                        KeyFingerprints: FormatKeyFingerprints(keyFingerprints));
                    session.Report(new ByteRange(start, offset), JsonSerializer.Serialize(cursor));
                    await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
                    engine.UpdateJob(Job, job => { job.TotalBytes = null; job.Message = RecordingMessage(capturedSeconds); });
                }

                if (media.IsVod)
                    return await FinishLiveCaptureAsync(cursor, transport, mp4, userStopped: false, ct).ConfigureAwait(false);

                await Task.Delay(PollDelay(media.TargetDuration), ct).ConfigureAwait(false);
                (string text, Uri finalUri) = await FetchTextAsync(playlistUrl, ct).ConfigureAwait(false);
                if (finalUri.AbsoluteUri != playlistUrl || HlsParser.IsMaster(text))
                    return RunOutcome.NeedsAttention("source-changed", "The live playlist address or type changed. Saved data was kept.");
                media = HlsParser.ParseMedia(text, finalUri, allowTimelineChanges: true);
                if (media.Unsupported is { } reason)
                    return RunOutcome.NeedsAttention(media.UnsupportedReasonCode, reason + " Saved data was kept.");
                if (media.TargetDuration != cursor.TargetDuration || media.TargetDuration <= 0
                    || media.PlaylistType == "VOD" && !media.IsVod)
                    return RunOutcome.NeedsAttention("live-playlist", "The live playlist timing or type changed unexpectedly. Saved data was kept.");
            }
        }
        catch (OperationCanceledException) when (session.Intent == StopIntent.LiveStop)
        {
            await session.CheckpointAsync(force: true, CancellationToken.None).ConfigureAwait(false);
            Cursor latest = ParseCursor(Job.MediaState) ?? cursor;
            latest = latest with { StopRequested = true };
            engine.UpdateJob(Job, job => job.MediaState = JsonSerializer.Serialize(latest));
            return await FinalizeStoppedLiveAsync(latest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MediaContainerException ex)
        {
            return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Saved data was kept.");
        }
        finally
        {
            foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<RunOutcome> FinalizeStoppedLiveAsync(Cursor cursor, CancellationToken ct)
    {
        long offset = Job.Extents.ContiguousFromZero();
        if (offset <= 0 || Job.Extents.TotalBytes != offset || cursor.Offset != offset
            || cursor.Mode != LiveMode || cursor.PrefixHash is not { Length: 64 })
            return RunOutcome.NeedsAttention("live-empty", "No complete live segment is available to keep.");
        if (cursor.Container is not ("ts" or "mp4") || cursor.NextSequence is null or < 0
            || !double.IsFinite(cursor.CapturedSeconds) || cursor.CapturedSeconds < 0)
            return RunOutcome.NeedsAttention("cursor", "The saved live recording cursor is invalid. Saved data was kept.");

        PartialFile file = session.OpenFile();
        if (file.Length < offset) return RunOutcome.NeedsAttention("cursor", "The live recording is shorter than its committed cursor. Saved data was kept.");
        if (file.Length > offset) file.Truncate(offset);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TransportStreamValidator? transport = cursor.Container == "ts" ? new() : null;
        MediaPartialIntegrity.HashPrefix(file, offset, hash, ct, transport);
        if (Convert.ToHexString(hash.GetCurrentHash()) != cursor.PrefixHash)
            return RunOutcome.NeedsAttention("integrity", "The committed live prefix failed SHA-256 validation. Saved data was kept.");

        FragmentedMp4? mp4 = null;
        if (cursor.Container == "mp4")
        {
            if (cursor.InitLength is < 8 or > 8 * 1024 * 1024 || cursor.Progress is null || offset <= cursor.InitLength)
                return RunOutcome.NeedsAttention("cursor", "The saved live MP4 track state is incomplete. Saved data was kept.");
            mp4 = FragmentedMp4.Open(MediaPartialIntegrity.ReadInitialization(file, cursor.InitLength, ct));
            mp4.Restore(cursor.Progress);
        }
        return await FinishLiveCaptureAsync(cursor, transport, mp4, userStopped: true, ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> FinishLiveCaptureAsync(Cursor cursor, TransportStreamValidator? transport,
        FragmentedMp4? mp4, bool userStopped, CancellationToken ct)
    {
        if (cursor.Offset <= 0) return RunOutcome.NeedsAttention("live-empty", "No complete live segment is available to keep.");
        try
        {
            if (cursor.Container == "ts") transport?.Finish(requireAudio: Job.MediaProvider is not null);
            else mp4?.Finish(requireAudio: Job.MediaProvider is not null, requireVideo: false);
        }
        catch (MediaContainerException ex)
        { return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Saved data was kept."); }

        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        engine.UpdateJob(Job, job => { job.TotalBytes = cursor.Offset; job.Message = ""; });
        RunOutcome outcome = await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
        if (outcome.State != JobState.Completed) return outcome;
        if (cursor.HadGaps)
            return outcome with { Reason = "live-gaps", Message = cursor.GapMessage ?? "One or more live segments were unavailable." };
        return userStopped ? outcome with { Reason = "live-stopped", Message = "Recording stopped and saved." } : outcome;
    }

    private Cursor MarkLiveGap(Cursor cursor, string message)
    {
        Cursor updated = cursor with { HadGaps = true, GapMessage = message };
        engine.UpdateJob(Job, job => { job.MediaState = JsonSerializer.Serialize(updated); job.Message = message; });
        return updated;
    }

    internal static async Task<(string? MediaState, string? Error)> PrepareLiveRefreshAsync(
        DownloadEngine engine, JobSession session, HttpTransport http, string savedState, string replacementUrl,
        CancellationToken ct)
    {
        Cursor? cursor = ParseCursor(savedState);
        DownloadJob job = session.Job;
        if (cursor is not { Mode: LiveMode, Offset: > 0, Container: "ts", NextSequence: not null })
            return (null, "Only saved MPEG-TS live recordings can continue from a refreshed link.");
        if (cursor.SourceIdentity is null || cursor.SourceIdentity != LiveSourceIdentity(job))
            return (null, "This live recording has no stable provider, media and format identity. Saved data was kept.");
        if (!UrlPolicy.TryParse(replacementUrl, out Uri replacement, out string urlError)) return (null, urlError);
        try { UrlPolicy.EnsureAllowed(replacement, engine.Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return (null, ex.Message); }

        var transfer = new HlsTransfer(engine, session, http);
        string playlistUrl;
        HlsMedia media;
        try
        {
            (string text, Uri baseUri) = await transfer.FetchTextAsync(replacement.AbsoluteUri, ct).ConfigureAwait(false);
            if (!HlsParser.IsPlaylist(text) || HlsParser.IsMaster(text))
                return (null, "The refreshed link must resolve directly to the selected HLS media playlist.");
            media = HlsParser.ParseMedia(text, baseUri, allowTimelineChanges: true);
            if (media.Unsupported is { } unsupported) return (null, unsupported);
            if (media.TargetDuration != cursor.TargetDuration || media.TargetDuration <= 0
                || media.PlaylistType == "VOD" && !media.IsVod)
                return (null, "The refreshed live playlist has a different timing or playlist type.");
            if (media.Segments.Any(segment => segment.Map is not null))
                return (null, "The refreshed link changed this recording from MPEG-TS to fragmented MP4.");
            if (media.Segments.Count > 0 && media.Segments.Skip(1)
                .Any(segment => !SameInitialization(media.Segments[0].Map, segment.Map)))
                return (null, "The refreshed live playlist changes its track layout.");
            playlistUrl = baseUri.AbsoluteUri;
        }
        catch (RunnerStop stop) { return (null, stop.Outcome.Message); }

        long expected = cursor.NextSequence!.Value;
        if (media.Segments.Count == 0)
        {
            if (media.MediaSequence > expected)
                return (null, $"The refreshed live window has already dropped segment {expected}.");
        }
        else
        {
            if (media.Segments[0].Sequence > expected)
                return (null, $"The refreshed live window has already dropped segment {expected}.");
            HlsSegment? next = media.Segments.FirstOrDefault(segment => segment.Sequence == expected);
            if (next is { } segment && (segment.IsGap || segment.DiscontinuityBefore
                || cursor.LastDiscontinuitySequence is long prior && segment.DiscontinuitySequence != prior))
                return (null, $"The refreshed playlist cannot continue safely at segment {expected}.");
        }

        Cursor updated = cursor with
        {
            Playlist = playlistUrl,
            PlanHash = LivePlanHash(playlistUrl, "ts", "", media.TargetDuration),
            StopRequested = false
        };
        return (JsonSerializer.Serialize(updated), null);
    }

    private async Task<byte[]> DecryptLiveAsync(byte[] data, HlsKey key, long sequence,
        Dictionary<string, byte[]> keys, Dictionary<string, string> fingerprints, CancellationToken ct)
    {
        if (key.Uri is null || !UrlPolicy.TryParse(key.Uri, out Uri keyUri, out _))
            throw new RunnerStop(RunOutcome.NeedsAttention("key", "The live AES-128 key address is invalid."));
        if (!keys.TryGetValue(key.Uri, out byte[]? material))
        {
            using HttpResponseMessage response = await http.SendAsync(keyUri, Spec(null, null), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusStop(response);
            material = await ReadBoundedAsync(response, 64, ct).ConfigureAwait(false);
            if (material.Length != 16)
            {
                CryptographicOperations.ZeroMemory(material);
                throw new MediaContainerException("The live stream key is not a valid AES-128 key.");
            }
            string uriHash = HashText(key.Uri), keyHash = Convert.ToHexString(SHA256.HashData(material));
            if (fingerprints.TryGetValue(uriHash, out string? expected) && expected != keyHash)
            {
                CryptographicOperations.ZeroMemory(material);
                throw new RunnerStop(RunOutcome.NeedsAttention("key-changed", "A live HLS key changed since the last durable segment."));
            }
            if (keys.Count >= 32)
            {
                string oldUri = keys.Keys.First();
                CryptographicOperations.ZeroMemory(keys[oldUri]);
                keys.Remove(oldUri);
            }
            fingerprints.Remove(uriHash);
            if (fingerprints.Count >= 32) fingerprints.Remove(fingerprints.Keys.First());
            fingerprints[uriHash] = keyHash;
            keys.Add(key.Uri, material);
        }

        byte[] iv = key.Iv ?? SequenceIv(sequence);
        using Aes aes = Aes.Create();
        aes.Key = material;
        try { return aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
        catch (CryptographicException)
        { throw new RunnerStop(RunOutcome.NeedsAttention("decrypt", "A live segment could not be decrypted with its supplied key.")); }
    }

    private static bool TryReadKeyFingerprints(IEnumerable<string>? values, out Dictionary<string, string> fingerprints)
    {
        fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string? item in values ?? [])
        {
            string[] parts = item?.Split(':') ?? [];
            if (parts.Length != 2 || !IsHash(parts[0]) || !IsHash(parts[1]) || !fingerprints.TryAdd(parts[0], parts[1]))
                return false;
        }
        return fingerprints.Count <= 32;
    }

    private static string[] FormatKeyFingerprints(Dictionary<string, string> fingerprints) =>
        [.. fingerprints.Select(pair => pair.Key + ":" + pair.Value)];

    private static string MapIdentity(HlsMap? map)
    {
        if (map is null) return "";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { map.Uri, map.Length, map.Offset, KeyUri = map.Key?.Uri, Iv = map.Key?.Iv });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string LivePlanHash(string playlist, string container, string mapIdentity, int targetDuration) =>
        HashText($"{LiveMode}|{playlist}|{container}|{mapIdentity}|{targetDuration}");

    private static string? LiveSourceIdentity(DownloadJob job) =>
        string.IsNullOrWhiteSpace(job.MediaProvider) || string.IsNullOrWhiteSpace(job.MediaId)
            || string.IsNullOrWhiteSpace(job.MediaFormatId)
            ? null
            : HashText($"{job.MediaProvider}|{job.MediaId}|{job.MediaFormatId}");

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string EmptyHash() => Convert.ToHexString(SHA256.HashData([]));

    private static TimeSpan PollDelay(int targetDuration) => TimeSpan.FromSeconds(Math.Max(0.5, targetDuration / 2d));

    private static string RecordingMessage(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds));
        string time = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
        return "Recording live · " + time;
    }

    private void EnsureLiveSpace(long additional)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.PartialPath));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "Not enough free space to continue recording. Saved data was kept."));
    }

    private Cursor? ReadCursor()
    {
        if (string.IsNullOrEmpty(Job.MediaState)) return null;
        return ParseCursor(Job.MediaState);
    }

    private static Cursor? ParseCursor(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(json, new JsonSerializerOptions { MaxDepth = 16 }); }
        catch (JsonException) { return null; }
    }

    private void SetOutputName(string extension)
    {
        if (Job.NameLocked && Path.HasExtension(Job.FileName)) return;
        string stem = Path.GetFileNameWithoutExtension(Job.FileName);
        if (stem.Length == 0) stem = "video";
        engine.UpdateJob(Job, j => { j.FileName = stem + extension; j.Category = "Video"; });
    }

    private async Task<(string Text, Uri Base)> FetchTextAsync(string url, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error)) throw new RunnerStop(RunOutcome.NeedsLink("bad-url", error));
        using HttpResponseMessage response = await http.SendAsync(uri, Spec(null, null), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw StatusStop(response);
        byte[] body = await ReadBoundedAsync(response, MaxPlaylistBytes, ct).ConfigureAwait(false);
        return (System.Text.Encoding.UTF8.GetString(body), response.RequestMessage?.RequestUri ?? uri);
    }

    private async Task<byte[]> FetchSegmentAsync(string url, long? offset, long? length, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error))
            throw new RunnerStop(RunOutcome.NeedsAttention("bad-url", error));
        if (offset is not null && (length is not long count || count <= 0 || offset.Value < 0 || count > MaxSegmentBytes || offset.Value > long.MaxValue - count))
            throw new RunnerStop(RunOutcome.NeedsAttention("invalid-range", "The playlist contains an invalid or oversized byte range."));
        long? end = offset is long start && length is long size ? start + size - 1 : null;
        using HttpResponseMessage response = await http.SendAsync(uri, Spec(offset, end), ct).ConfigureAwait(false);
        int code = (int)response.StatusCode;
        if (code is not (200 or 206)) throw StatusStop(response);
        if (offset is long requestedStart)
        {
            RangeCheck check = HttpFacts.CheckRangeResponse(response, requestedStart, end);
            if (!check.IsValid || check.EndInclusive != end)
                throw new RunnerStop(RunOutcome.NeedsAttention("invalid-range", "The server returned a different byte range than the playlist requested. Saved data was kept."));
            if (!HttpFacts.IsIdentityEncoded(response))
                throw new RunnerStop(RunOutcome.NeedsAttention("encoded-range", "The server compressed a byte-range response; it cannot be assembled safely."));
        }
        else if (code == 206)
            throw new RunnerStop(RunOutcome.NeedsAttention("unexpected-range", "The server unexpectedly returned only part of a full segment."));
        if (HlsLooksLikeHtml(response)) throw new RunnerStop(RunOutcome.NeedsAttention("html", "The server sent a web page instead of video data."));
        byte[] bytes = await ReadBoundedAsync(response, MaxSegmentBytes, ct).ConfigureAwait(false);
        if (length is long expected && bytes.LongLength != expected)
            throw new TransientNetworkException("The HLS byte-range response was incomplete.");
        return bytes;
    }

    private static bool HlsLooksLikeHtml(HttpResponseMessage r) => HttpFacts.IsHtml(r);

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(engine.Options.StallTimeout);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            long? knownLength = response.Content.Headers.ContentLength;
            using var buffer = new MemoryStream(knownLength is > 0 ? (int)knownLength.Value : 64 * 1024);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(engine.Options.StallTimeout);
                if (buffer.Length + read > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
                buffer.Write(chunk, 0, read);
            }
            if (response.Content.Headers.ContentLength is long declared && buffer.Length != declared)
                throw new TransientNetworkException("A segment ended early.");
            if (buffer.TryGetBuffer(out var complete) && complete.Array is { } array && complete.Count == array.Length)
                return array; // common Content-Length path avoids another full-segment allocation
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TransientNetworkException("The HLS response stalled."); }
    }

    private async Task<byte[]> DecryptAsync(byte[] data, HlsKey key, long sequence, Dictionary<string, byte[]> cache, CancellationToken ct)
    {
        if (key.Uri is null) throw new RunnerStop(RunOutcome.NeedsAttention("key", "An encrypted stream has no key address."));
        if (!cache.TryGetValue(key.Uri, out byte[]? material))
        {
            if (!UrlPolicy.TryParse(key.Uri, out Uri keyUri, out _))
                throw new RunnerStop(RunOutcome.NeedsAttention("key", "The HLS key address is invalid or not permitted."));
            using HttpResponseMessage response = await http.SendAsync(keyUri, Spec(null, null), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusStop(response);
            material = await ReadBoundedAsync(response, 64, ct).ConfigureAwait(false);
            if (material.Length != 16) throw new RunnerStop(RunOutcome.NeedsAttention("key", "The stream's key is not a valid AES-128 key."));
            if (cache.Count >= 32)
            {
                string old = cache.Keys.First(); CryptographicOperations.ZeroMemory(cache[old]); cache.Remove(old);
            }
            cache[key.Uri] = material;
        }

        byte[] iv = key.Iv ?? SequenceIv(sequence);
        using Aes aes = Aes.Create();
        aes.Key = material;
        try { return aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
        catch (CryptographicException) { throw new RunnerStop(RunOutcome.NeedsAttention("decrypt", "A segment could not be decrypted with the supplied key.")); }
    }

    private static bool SameInitialization(HlsMap? left, HlsMap? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Uri != right.Uri || left.Length != right.Length || left.Offset != right.Offset) return false;
        if (left.Key is null || right.Key is null) return left.Key is null && right.Key is null;
        if (left.Key.Uri != right.Key.Uri) return false;
        if (left.Key.Iv is null || right.Key.Iv is null) return left.Key.Iv is null && right.Key.Iv is null;
        return left.Key.Iv.AsSpan().SequenceEqual(right.Key.Iv);
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private RequestSpec Spec(long? start, long? end)
    {
        RequestCredentials? credentials = engine.CredentialsFor(Job.Id);
        return new RequestSpec(start, end, Referer: Job.Referer, UserAgent: Job.UserAgent,
            Secrets: credentials?.Headers, CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl);
    }

    private static RunnerStop StatusStop(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        return new RunnerStop(code switch
        {
            401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", $"The stream address no longer works (HTTP {code}). Saved data was kept."),
            408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The server had a problem (HTTP {code}).",
                HttpFacts.RetryAfter(response, DateTimeOffset.UtcNow)),
            _ => RunOutcome.Failed("http-status", $"The server answered HTTP {code}.")
        });
    }
}

/// <summary>Describes a link for the browser's quality picker. One small request; nothing is saved.</summary>
public sealed record MediaProbeResult(string Kind, string Message, string? ContentType, long? ContentLength,
    double? DurationSeconds, IReadOnlyList<MediaVariant> Variants);

public sealed record MediaVariant(string Url, long Bandwidth, int Width, int Height, bool AudioOnly, string? Codecs);

public sealed partial class DownloadEngine
{
    public async Task<MediaProbeResult> ProbeMediaAsync(string url, CancellationToken ct = default)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error)) return new MediaProbeResult("invalid", error, null, null, null, []);
        try
        {
            UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks);
            bool playlist = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                return new MediaProbeResult("unsupported", "Use Find video to resolve a supported clear fragmented-MP4 DASH plan; raw manifests cannot be downloaded as files.", null, null, null, []);

            using HttpResponseMessage response = await _http.SendAsync(uri, new RequestSpec(playlist ? null : 0, playlist ? null : 0), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new MediaProbeResult("unreachable", $"The server answered HTTP {(int)response.StatusCode}.", null, null, null, []);

            string? type = HttpFacts.ContentType(response);
            if (playlist || type is "application/vnd.apple.mpegurl" or "application/x-mpegurl")
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(body, System.Text.Encoding.UTF8, true, 1 << 16, leaveOpen: true);
                var buffer = new char[(int)Math.Min(4 * 1024 * 1024, 1 << 20)];
                int n = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
                string text = new(buffer, 0, n);
                if (!HlsParser.IsPlaylist(text)) return new MediaProbeResult("unsupported", "That address is not a playlist VESPER understands.", type, null, null, []);
                if (HlsParser.IsMaster(text))
                {
                    var variants = HlsParser.ParseMaster(text, uri).Where(v => !v.NeedsSeparateAudio)
                        .Select(v => new MediaVariant(v.Uri, v.Bandwidth, v.Width, v.Height, v.AudioOnly, v.Codecs)).ToList();
                    return variants.Count == 0
                        ? new MediaProbeResult("unsupported", "Every quality keeps its audio in a separate track, which VESPER cannot join.", type, null, null, [])
                        : new MediaProbeResult("hls-master", "", type, null, null, variants);
                }
                HlsMedia media = HlsParser.ParseMedia(text, uri);
                if (media.Unsupported is { } why) return new MediaProbeResult("unsupported", why, type, null, null, []);
                if (!media.IsVod) return new MediaProbeResult("unsupported", "Live streams are not supported.", type, null, null, []);
                return new MediaProbeResult("hls-media", "", type, null, media.TotalSeconds, []);
            }

            long? length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            return new MediaProbeResult("direct", "", type, length, null, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException))
        {
            return new MediaProbeResult("unreachable", ErrorClassifier.Describe(ex), null, null, null, []);
        }
    }
}
