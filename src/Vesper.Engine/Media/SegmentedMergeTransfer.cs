using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>Downloads separate static HLS or DASH renditions into the merge job, then remuxes validated samples.</summary>
internal sealed class SegmentedMergeTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    private const long MaxPlaylistBytes = 4 * 1024 * 1024;
    private const int MaxInitializationBytes = 8 * 1024 * 1024;
    private const long MaxPartBytes = 64L * 1024 * 1024;
    private DownloadJob Job => session.Job;

    private sealed record Part(string Url, long? Offset, long? Length, HlsKey? Key, long Sequence,
        bool Initialization, double? DurationSeconds = null, double? StartSeconds = null);
    private sealed record PreparedTrack(MergeTrack Track, IReadOnlyList<Part> Parts, bool Fragmented,
        string SourceFingerprint, double? ActualStartSeconds = null, double? ActualEndSeconds = null);
    private sealed record Cursor(int Version, string Fingerprint, string Phase, int Track = 0, int Next = 0,
        long Offset = 0, string PrefixHash = "", int[]? CompletedParts = null, long[]? PartLengths = null,
        string?[]? SourceFingerprints = null, long MuxLength = 0, string? MuxSha256 = null,
        string[]? KeyFingerprints = null);

    internal async Task<RunOutcome> RunAsync(MediaMergePlan plan, CancellationToken ct)
    {
        if (!plan.IsSegmented) return RunOutcome.NeedsAttention("merge-plan", "The segmented merge plan is empty.");
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out string folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");

        string fingerprint = plan.Fingerprint;
        Cursor? cursor = ReadCursor();
        if (Job.MediaState is not null && cursor is null) return Stop("The saved segmented merge cursor is invalid.");
        if (cursor is { Phase: "muxed" }) return await FinalizeMergedAsync(cursor, fingerprint, ct).ConfigureAwait(false);
        if (cursor is not null && (cursor.Version != 1 || cursor.Fingerprint != fingerprint || cursor.Phase != "download"))
            return Stop("The saved data belongs to a different rendition plan. Restart as a separate copy.");

        PreparedTrack[] tracks;
        try
        {
            tracks = new PreparedTrack[plan.Tracks.Count];
            for (int i = 0; i < tracks.Length; i++)
                tracks[i] = await PrepareTrackAsync(plan.Tracks[i], plan.ClipRange, ct).ConfigureAwait(false);
        }
        catch (MediaContainerException ex) { return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Saved data was kept."); }

        if (tracks.Sum(track => (long)track.Parts.Count) > 100_000)
            return Stop("The selected renditions exceed the supported segment limit.");
        if (cursor is not null && !CursorMatches(cursor, fingerprint, tracks))
            return Stop("A selected playlist or fragment plan changed. Saved data was kept.");

        RecoverInterruptedSwap();
        TryDelete(MergeTransfer.MergingPath(Job));
        TryDelete(Job.PartialPath + ".samples");
        PartialFile file = session.OpenFile();
        long offset = Job.Extents.ContiguousFromZero();
        if (Job.Extents.TotalBytes != offset || file.Length < offset
            || (cursor is null && offset != 0) || (cursor is not null && cursor.Offset != offset))
            return Stop("The saved cursor does not match committed rendition bytes.");
        if (file.Length > offset) file.Truncate(offset);

        int[] completed = cursor?.CompletedParts is { } prior ? (int[])prior.Clone() : new int[tracks.Length];
        var lengths = cursor?.PartLengths is { } savedLengths ? new List<long>(savedLengths) : new List<long>();
        if (!LengthsMatch(lengths, offset))
            return Stop("The saved rendition lengths do not match committed bytes.");

        int trackIndex = cursor?.Track ?? 0;
        int next = cursor?.Next ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (offset > 0)
        {
            MediaPartialIntegrity.HashPrefix(file, offset, hash, ct);
            if (!Convert.ToHexString(hash.GetCurrentHash()).Equals(cursor!.PrefixHash, StringComparison.Ordinal))
                return Stop("The committed rendition prefix failed SHA-256 validation. Saved data was kept.");
        }

        using var keys = new HlsKeyCache(cursor?.KeyFingerprints);
        if (!keys.Valid) return Stop("The saved HLS key identities are invalid.");
        try
        {
            session.SetPhase(JobState.Downloading);
            for (int ti = trackIndex; ti < tracks.Length; ti++)
            {
                PreparedTrack prepared = tracks[ti];
                int firstPart = ti == trackIndex ? next : 0;
                if (completed[ti] != firstPart) return Stop("The saved segment cursor is inconsistent.");
                for (int pi = firstPart; pi < prepared.Parts.Count; pi++)
                {
                    ct.ThrowIfCancellationRequested();
                    Part part = prepared.Parts[pi];
                    long limit = part.Initialization ? MaxInitializationBytes : MaxPartBytes;
                    byte[] data = await FetchPartAsync(part.Url, part.Offset, part.Length, limit, ct).ConfigureAwait(false);
                    if (part.Key is { } key)
                    {
                        if (part.Initialization && key.Iv is null)
                            throw new RunnerStop(NeedsAttention("key", "Encrypted initialization requires an explicit AES IV."));
                        data = await DecryptAsync(data, key, part.Sequence, keys, ct).ConfigureAwait(false);
                    }
                    EnsureSpace(data.Length);
                    await session.Limiter.AcquireAsync(data.Length, ct).ConfigureAwait(false);
                    await engine.GlobalLimiter.AcquireAsync(data.Length, ct).ConfigureAwait(false);
                    await session.WriteAsync(data, offset, report: false, ct).ConfigureAwait(false);
                    hash.AppendData(data);
                    long start = offset;
                    offset = checked(offset + data.Length);
                    completed[ti] = pi + 1;
                    lengths.Add(data.LongLength);
                    int savedTrack = ti, savedNext = pi + 1;
                    if (savedNext == prepared.Parts.Count) { savedTrack++; savedNext = 0; }
                    var saved = new Cursor(1, fingerprint, "download", savedTrack, savedNext, offset,
                        Convert.ToHexString(hash.GetCurrentHash()), (int[])completed.Clone(), [.. lengths],
                        tracks.Select(track => (string?)track.SourceFingerprint).ToArray(),
                        KeyFingerprints: keys.Fingerprints.ToArray());
                    session.Report(new ByteRange(start, offset), JsonSerializer.Serialize(saved));
                    await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                    engine.UpdateJob(Job, job => job.Message = $"Track {ti + 1} of {tracks.Length}, segment {pi + 1} of {prepared.Parts.Count}");
                }
            }
        }
        catch (MediaContainerException ex)
        {
            return RunOutcome.NeedsAttention("invalid-container", ex.Message + " Saved data was kept.");
        }
        finally { keys.Clear(); }

        if (completed.Where((count, i) => count != tracks[i].Parts.Count).Any())
            return RunOutcome.NeedsAttention("incomplete", "A rendition is incomplete. Saved segments were kept; resume to continue.");
        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        engine.UpdateJob(Job, job => job.Message = "Merging tracks");
        return await MergeAsync(plan, fingerprint, tracks, completed, lengths, ct).ConfigureAwait(false);
    }

    private Cursor? ReadCursor()
    {
        if (string.IsNullOrEmpty(Job.MediaState)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Job.MediaState, new JsonSerializerOptions { MaxDepth = 16 }); }
        catch (JsonException) { return null; }
    }

    private async Task<PreparedTrack> PrepareTrackAsync(MergeTrack track, MediaTimeRange? clipRange, CancellationToken ct)
    {
        MergeMediaSource source = track.Source ?? throw new MediaContainerException("A rendition source is missing.");
        if (source.Protocol == "dash" && source.Dash is { } dash)
        {
            var dashParts = new List<Part>(dash.Fragments.Count + 1)
            {
                new(dash.Initialization.Url, dash.Initialization.Offset, dash.Initialization.Length, null, 0, true)
            };
            dashParts.AddRange(dash.Fragments.Select(fragment => new Part(fragment.Url, fragment.Offset, fragment.Length,
                null, 0, false, fragment.DurationSeconds, fragment.StartSeconds)));
            return SelectClip(new PreparedTrack(track, dashParts, Fragmented: true, dash.Fingerprint), clipRange);
        }
        if (source.Protocol != "hls" || source.PlaylistUrl is not { } playlistUrl)
            throw new MediaContainerException("The selected segmented source is unsupported.");

        (string text, Uri baseUri) = await FetchPlaylistAsync(playlistUrl, ct).ConfigureAwait(false);
        if (!HlsParser.IsPlaylist(text) || HlsParser.IsMaster(text))
            throw new MediaContainerException("A selected HLS rendition did not return a media playlist.");
        HlsMedia media = HlsParser.ParseMedia(text, baseUri);
        if (media.Unsupported is { } reason) throw new MediaContainerException(reason);
        if (!media.IsVod) throw new MediaContainerException("Separate live HLS renditions are not supported by this merge path.");
        if (media.Segments.Count == 0) throw new MediaContainerException("A selected HLS rendition contains no media segments.");
        if (media.Segments.Any(segment => segment.DiscontinuityBefore || segment.IsGap))
            throw new MediaContainerException("Discontinuous or missing HLS media cannot be merged safely.");
        if (media.Segments.Skip(1).Any(segment => !SameInitialization(media.Segments[0].Map, segment.Map)))
            throw new MediaContainerException("HLS initialization changes part-way through the rendition.");

        bool fragmented = media.Segments[0].Map is not null;
        if (fragmented && track.Container is not ("mp4" or "m4a") || !fragmented && track.Container != "ts")
            throw new MediaContainerException("The HLS media container differs from the selected rendition metadata.");

        var hlsParts = new List<Part>(media.Segments.Count + (fragmented ? 1 : 0));
        if (media.Segments[0].Map is { } map)
            hlsParts.Add(new Part(map.Uri, map.Offset, map.Length, map.Key, media.Segments[0].Sequence, Initialization: true));
        foreach (HlsSegment segment in media.Segments)
            hlsParts.Add(new Part(segment.Uri, segment.RangeOffset, segment.RangeLength, segment.Key,
                segment.Sequence, false, segment.Duration));
        byte[] fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(new { Base = baseUri.AbsoluteUri, media.Segments });
        string identity = Convert.ToHexString(SHA256.HashData(fingerprintBytes));
        return SelectClip(new PreparedTrack(track, hlsParts, fragmented, identity), clipRange);
    }

    private static PreparedTrack SelectClip(PreparedTrack track, MediaTimeRange? range)
    {
        if (range is null) return track;
        Part[] media = [.. track.Parts.Where(part => !part.Initialization)];
        double[] durations = media.Select(part => part.DurationSeconds ?? double.NaN).ToArray();
        double?[] starts = [.. media.Select(part => part.StartSeconds)];
        if (!range.TrySelect(durations, starts, out int first, out int count, out double actualStart, out double actualEnd))
            throw new MediaContainerException("The requested time range is outside the supported segment timeline.");
        var selected = media.Skip(first).Take(count);
        Part[] parts = track.Parts.FirstOrDefault(part => part.Initialization) is { } init
            ? [init, .. selected] : [.. selected];
        return track with { Parts = parts, ActualStartSeconds = actualStart, ActualEndSeconds = actualEnd };
    }

    private async Task<(string Text, Uri Base)> FetchPlaylistAsync(string url, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri requested, out string error))
            throw new RunnerStop(RunOutcome.NeedsAttention("bad-url", error));
        Uri finalUri = requested;
        byte[] bytes = await FetchPartAsync(url, null, null, MaxPlaylistBytes, ct, uri => finalUri = uri).ConfigureAwait(false);
        if (bytes.Length > MaxPlaylistBytes) throw new RunnerStop(NeedsAttention("too-large", "An HLS playlist is too large."));
        return (System.Text.Encoding.UTF8.GetString(bytes), finalUri);
    }

    private async Task<byte[]> FetchPartAsync(string url, long? offset, long? length, long limit, CancellationToken ct,
        Action<Uri>? finalUri = null)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error))
            throw new RunnerStop(RunOutcome.NeedsAttention("bad-url", error));
        if ((offset is null) != (length is null) || offset < 0 || length is <= 0 || length > limit
            || offset is long startOffset && length is long rangeLength && startOffset > long.MaxValue - rangeLength)
            throw new RunnerStop(NeedsAttention("invalid-range", "A rendition contains an invalid or oversized byte range."));
        long? end = offset is long start && length is long size ? checked(start + size - 1) : null;
        session.AddConnection();
        try
        {
            using HttpResponseMessage response = await http.SendAsync(uri, new RequestSpec(offset, end, Referer: Job.Referer,
                UserAgent: Job.UserAgent, CredentialOrigin: Job.SourceUrl), ct).ConfigureAwait(false);
            finalUri?.Invoke(response.RequestMessage?.RequestUri ?? uri);
            int status = (int)response.StatusCode;
            if (status is not (200 or 206)) throw StatusStop(status, response);
            if (!HttpFacts.IsIdentityEncoded(response) || HttpFacts.IsHtml(response))
                throw new RunnerStop(NeedsAttention("invalid-response", "The rendition server sent compressed data or an HTML page."));
            if (offset is long requested)
            {
                RangeCheck check = HttpFacts.CheckRangeResponse(response, requested, end);
                if (!check.IsValid || check.EndInclusive != end)
                    throw new RunnerStop(NeedsAttention("invalid-range", "The server returned a different byte range than requested."));
            }
            else if (status == 206)
                throw new RunnerStop(NeedsAttention("unexpected-range", "The rendition server returned only part of a full segment."));

            if (response.Content.Headers.ContentLength > limit)
                throw new RunnerStop(NeedsAttention("too-large", "A rendition segment exceeds the supported size limit."));
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(engine.Options.StallTimeout);
            try
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                using var result = new MemoryStream(response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
                    ? (int)response.Content.Headers.ContentLength.Value : 64 * 1024);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        int read = await body.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        if (result.Length + read > limit)
                            throw new RunnerStop(NeedsAttention("too-large", "A rendition segment exceeds the supported size limit."));
                        result.Write(buffer, 0, read);
                        stall.CancelAfter(engine.Options.StallTimeout);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                if (response.Content.Headers.ContentLength is long declared && result.Length != declared
                    || length is long expected && result.Length != expected)
                    throw new TransientNetworkException("A rendition object ended before its declared size.");
                if (result.TryGetBuffer(out ArraySegment<byte> complete) && complete.Array is { } array && complete.Count == array.Length)
                    return array;
                return result.ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TransientNetworkException("A rendition response stalled."); }
        }
        finally { session.RemoveConnection(); }
    }

    private async Task<byte[]> DecryptAsync(byte[] data, HlsKey key, long sequence, HlsKeyCache cache, CancellationToken ct)
    {
        if (key.Uri is null) throw new RunnerStop(NeedsAttention("key", "An encrypted HLS rendition has no key address."));
        byte[] material = await cache.GetAsync(key.Uri,
            (url, offset, length, limit, token) => FetchPartAsync(url, offset, length, limit, token), ct).ConfigureAwait(false);
        byte[] iv = key.Iv ?? SequenceIv(sequence);
        using Aes aes = Aes.Create();
        aes.Key = material;
        try { return aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
        catch (CryptographicException)
        { throw new RunnerStop(NeedsAttention("decrypt", "A rendition segment could not be decrypted with its supplied key.")); }
    }

    private async Task<RunOutcome> MergeAsync(MediaMergePlan plan, string fingerprint, PreparedTrack[] tracks,
        int[] completed, List<long> partLengths, CancellationToken ct)
    {
        string mergingPath = MergeTransfer.MergingPath(Job);
        string samplesPath = Job.PartialPath + ".samples";
        long length;
        string sha;
        session.CloseFile();
        try
        {
            using var source = new FileStream(Job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            if (source.Length != partLengths.Sum()) return Stop("The committed rendition file length changed.");
            using var samples = new FileStream(samplesPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.RandomAccess);
            var inputs = new List<(DemuxedTrack Track, Stream Source)>(tracks.Length);
            int partIndex = 0;
            long trackOffset = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                PreparedTrack prepared = tracks[i];
                if (completed[i] != prepared.Parts.Count) return Stop("A committed rendition is missing segments.");
                long[] sizes = partLengths.Skip(partIndex).Take(completed[i]).ToArray();
                partIndex += completed[i];
                long trackLength = sizes.Sum();
                TrackKind kind = prepared.Track.Role == "video" ? TrackKind.Video : TrackKind.Audio;
                List<DemuxedTrack> demuxed;
                Stream sampleSource;
                if (prepared.Fragmented)
                {
                    demuxed = DemuxFragmented(source, trackOffset, sizes, kind, prepared.Track.Codec, ct);
                    sampleSource = source;
                }
                else
                {
                    using var region = new RegionStream(source, trackOffset, trackLength);
                    demuxed = MpegTsDemuxer.Read(region, trackLength, samples, ct);
                    sampleSource = samples;
                }
                DemuxedTrack[] selectedTracks = [.. demuxed.Where(track => track.Kind == kind)];
                if (selectedTracks.Length == 0)
                    return RunOutcome.NeedsAttention(kind == TrackKind.Audio ? "missing-audio" : "missing-video",
                        $"The selected {prepared.Track.Role} rendition contains no {prepared.Track.Role} track. Saved data was kept.");
                if (selectedTracks.Length > 1)
                    throw new MediaContainerException($"The selected {prepared.Track.Role} rendition contains multiple {prepared.Track.Role} tracks.");
                DemuxedTrack selected = selectedTracks[0];
                if (prepared.Track.Codec is { } expected && !selected.Codec.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new MediaContainerException($"The selected {prepared.Track.Role} codec differs from the playlist metadata.");
                if (selected.Language == "und" && prepared.Track.Language is { Length: 3 } language)
                    selected.Language = language.ToLowerInvariant();
                inputs.Add((selected, sampleSource));
                trackOffset += trackLength;
            }
            samples.Flush(flushToDisk: true);
            if (inputs.Count == 2) ValidateTimeline(inputs);
            if (plan.ClipRange is not null)
            {
                double actualStart = tracks.Min(track => track.ActualStartSeconds ?? 0);
                double actualEnd = tracks.Max(track => track.ActualEndSeconds ?? 0);
                string range = MediaTimeRange.Describe(actualStart, actualEnd);
                engine.UpdateJob(Job, job =>
                {
                    string quality = job.MediaQuality ?? "Media";
                    int priorRange = quality.IndexOf(" · Segment-aligned ", StringComparison.Ordinal);
                    if (priorRange >= 0) quality = quality[..priorRange];
                    job.MediaQuality = quality + " · " + range;
                });
            }
            long outputPayloadBytes = inputs.Sum(input => input.Track.PayloadBytes);
            EnsureSpace(outputPayloadBytes + (1 << 20));
            string? container = MediaMerger.ChooseContainer(inputs.Select(input => input.Track).ToList(), plan.OutputContainer);
            if (container is null)
                return RunOutcome.NeedsAttention("unsupported-container",
                    $"The downloaded codecs ({string.Join(", ", inputs.Select(input => input.Track.Codec))}) cannot be stored in {plan.OutputContainer} without conversion. Saved data was kept.");
            MediaThumbnail? thumbnail = container == "mkv"
                ? await MediaThumbnail.FetchAsync(Job.MediaThumbnailUrl, http, engine.Options.AllowPrivateNetworks, ct).ConfigureAwait(false)
                : null;
            using (var output = new FileStream(mergingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                MergeResult merged = MediaMerger.Merge(inputs, container, output, ct, plan.Metadata, thumbnail);
                output.Flush(flushToDisk: true);
                length = output.Length;
                MediaMerger.Validate(output, length, inputs.Select(input => input.Track).ToList(), ct, merged.Metadata, merged.ThumbnailSha256);
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
        finally { TryDelete(samplesPath); }

        string tracksPath = MergeTransfer.TracksPath(Job);
        if (File.Exists(tracksPath)) return Stop("An older merge sidecar is in the way.");
        File.Move(Job.PartialPath, tracksPath);
        File.Move(mergingPath, Job.PartialPath);
        var extents = new ExtentSet();
        extents.Add(new ByteRange(0, length));
        var muxed = new Cursor(1, fingerprint, "muxed", MuxLength: length, MuxSha256: sha);
        DownloadJob committed = engine.CommitCheckpoint(Job, extents, JsonSerializer.Serialize(muxed));
        lock (session.Gate)
        {
            Job.Extents = extents;
            Job.Generation = committed.Generation;
            Job.MediaState = committed.MediaState;
        }
        engine.WriteManifest(committed);
        engine.UpdateJob(Job, job => { job.TotalBytes = length; job.Message = ""; });
        return await PublishAsync(ct).ConfigureAwait(false);
    }

    private static List<DemuxedTrack> DemuxFragmented(Stream source, long trackOffset, long[] partLengths,
        TrackKind requiredKind, string? expectedCodec, CancellationToken ct)
    {
        if (partLengths.Length < 2 || partLengths[0] is < 8 or > MaxInitializationBytes)
            throw new MediaContainerException("A fragmented rendition has no bounded initialization and media parts.");
        byte[] initialization = ReadAt(source, trackOffset, checked((int)partLengths[0]));
        FragmentedMp4 mp4 = FragmentedMp4.Open(initialization, captureSamples: true);
        if (requiredKind == TrackKind.Video && !mp4.HasVideo || requiredKind == TrackKind.Audio && !mp4.HasAudio)
            throw new MediaContainerException("A selected rendition initialization is missing its declared track.");
        if (expectedCodec is not null && !mp4.GetTracks().Any(track => track.Kind == requiredKind
            && track.Codec.Equals(expectedCodec, StringComparison.OrdinalIgnoreCase)))
            throw new MediaContainerException("A selected rendition initialization does not match its declared codec.");

        long partOffset = trackOffset + partLengths[0];
        for (int i = 1; i < partLengths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (partLengths[i] is <= 0 or > MaxPartBytes) throw new MediaContainerException("A fragmented media part exceeds its size limit.");
            byte[] fragment = ReadAt(source, partOffset, checked((int)partLengths[i]));
            mp4.ValidateFragment(fragment, partOffset);
            partOffset += partLengths[i];
        }
        mp4.Finish(requireAudio: requiredKind == TrackKind.Audio, requireVideo: requiredKind == TrackKind.Video);
        return mp4.GetTracks();
    }

    private static byte[] ReadAt(Stream stream, long offset, int length)
    {
        var bytes = new byte[length];
        stream.Position = offset;
        int read = 0;
        while (read < bytes.Length)
        {
            int n = stream.Read(bytes, read, bytes.Length - read);
            if (n == 0) throw new MediaContainerException("A saved rendition ended during a media part.");
            read += n;
        }
        return bytes;
    }

    private bool CursorMatches(Cursor cursor, string fingerprint, PreparedTrack[] tracks)
    {
        if (cursor.CompletedParts is not { } completed || completed.Length != tracks.Length
            || cursor.PartLengths is not { } lengths || cursor.SourceFingerprints is not { } fingerprints
            || fingerprints.Length != tracks.Length || cursor.Track < 0 || cursor.Track > tracks.Length || cursor.Next < 0
            || (cursor.Track == tracks.Length && cursor.Next != 0))
            return false;
        for (int i = 0; i < tracks.Length; i++)
        {
            if (fingerprints[i] != tracks[i].SourceFingerprint || completed[i] < 0 || completed[i] > tracks[i].Parts.Count)
                return false;
            int expected = i < cursor.Track ? tracks[i].Parts.Count : i == cursor.Track ? cursor.Next : 0;
            if (completed[i] != expected || (i == cursor.Track && cursor.Track < tracks.Length && cursor.Next > tracks[i].Parts.Count))
                return false;
        }
        return cursor.Fingerprint == fingerprint && lengths.Length == completed.Sum()
            && LengthsMatch(lengths, cursor.Offset) && cursor.PrefixHash.Length == 64
            && (cursor.Track == tracks.Length || cursor.Next < tracks[cursor.Track].Parts.Count);
    }

    private static bool LengthsMatch(IEnumerable<long> lengths, long expected)
    {
        long total = 0;
        foreach (long length in lengths)
        {
            if (length <= 0 || length > MaxPartBytes || total > long.MaxValue - length) return false;
            total += length;
        }
        return total == expected;
    }

    private static void ValidateTimeline(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs)
    {
        (double Start, double End) Range(DemuxedTrack track)
        {
            long first = long.MaxValue, last = long.MinValue;
            foreach (TrackSample sample in track.Samples)
            {
                long pts = checked(sample.Dts + sample.CtsOffset);
                first = Math.Min(first, pts);
                last = Math.Max(last, checked(pts + sample.Duration));
            }
            return (first / (double)track.Timescale, last / (double)track.Timescale);
        }

        var video = Range(inputs.Single(input => input.Track.Kind == TrackKind.Video).Track);
        var audio = Range(inputs.Single(input => input.Track.Kind == TrackKind.Audio).Track);
        double tolerance = Math.Max(1, Math.Max(video.End - video.Start, audio.End - audio.Start) * 0.01);
        if (Math.Abs(video.Start - audio.Start) > tolerance || Math.Abs(video.End - audio.End) > tolerance)
            throw new MediaContainerException($"The selected renditions have incompatible presentation timelines "
                + $"(video {video.Start:0.###}-{video.End:0.###}s, audio {audio.Start:0.###}-{audio.End:0.###}s).");
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

    private async Task<RunOutcome> FinalizeMergedAsync(Cursor cursor, string fingerprint, CancellationToken ct)
    {
        if (cursor.Version != 1 || cursor.Fingerprint != fingerprint || cursor.MuxLength <= 0 || cursor.MuxSha256 is not { Length: 64 })
            return Stop("The merged output cursor is incomplete.");
        if (!File.Exists(Job.PartialPath)) return Stop("The merged output is missing.");
        using (var stream = new FileStream(Job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            if (stream.Length < cursor.MuxLength) return Stop("The merged output is shorter than recorded.");
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(new RegionStream(stream, 0, cursor.MuxLength), ct).ConfigureAwait(false));
            if (actual != cursor.MuxSha256) return Stop("The merged output failed SHA-256 validation.");
        }
        if (Job.TotalBytes != cursor.MuxLength) engine.UpdateJob(Job, job => job.TotalBytes = cursor.MuxLength);
        return await PublishAsync(ct).ConfigureAwait(false);
    }

    private async Task<RunOutcome> PublishAsync(CancellationToken ct)
    {
        RunOutcome result = await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
        if (result.State == JobState.Completed && !MergeTransfer.DeleteSidecars(Job))
            engine.UpdateJob(Job, job => job.Message = "Saved. A temporary track file could not be removed: " + PathSafety.ForDisplay(MergeTransfer.TracksPath(job)));
        return result;
    }

    private void RecoverInterruptedSwap()
    {
        string tracksPath = MergeTransfer.TracksPath(Job);
        if (!File.Exists(tracksPath)) return;
        session.CloseFile();
        if (File.Exists(Job.PartialPath)) File.Delete(Job.PartialPath);
        File.Move(tracksPath, Job.PartialPath);
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.PartialPath));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space to download and merge. Saved data was kept."));
    }

    private RunnerStop StatusStop(int status, HttpResponseMessage response) => new(status switch
    {
        401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", "A selected rendition address expired or was refused. Saved segments were kept; refresh the source to continue."),
        408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The rendition server answered HTTP {status}.",
            HttpFacts.RetryAfter(response, DateTimeOffset.UtcNow)),
        _ => RunOutcome.NeedsAttention("http-status", $"The rendition server answered HTTP {status}. Saved data was kept.")
    });

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private static RunOutcome NeedsAttention(string reason, string message) => RunOutcome.NeedsAttention(reason, message + " Saved data was kept.");
    private static RunOutcome Stop(string reason) => NeedsAttention("merge-integrity", reason);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class HlsKeyCache : IDisposable
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _expected = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
        internal bool Valid { get; private set; } = true;
        internal IEnumerable<string> Fingerprints => _fingerprints.Select(pair => pair.Key + ":" + pair.Value);

        internal HlsKeyCache(IEnumerable<string>? saved)
        {
            foreach (string? item in saved ?? [])
            {
                string[] parts = item?.Split(':') ?? [];
                if (parts.Length != 2 || !IsSha256(parts[0]) || !IsSha256(parts[1]) || !_expected.TryAdd(parts[0], parts[1]))
                { Valid = false; return; }
            }
            foreach (var pair in _expected) _fingerprints[pair.Key] = pair.Value;
            if (_expected.Count > 32) Valid = false;
        }

        internal async Task<byte[]> GetAsync(string uri, Func<string, long?, long?, long, CancellationToken, Task<byte[]>> fetch, CancellationToken ct)
        {
            if (_keys.TryGetValue(uri, out byte[]? existing)) return existing;
            byte[] key = await fetch(uri, null, null, 64, ct).ConfigureAwait(false);
            if (key.Length != 16)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new MediaContainerException("The HLS rendition key is not a valid AES-128 key.");
            }
            string uriHash = Hash(Encoding.UTF8.GetBytes(uri));
            string keyHash = Hash(key);
            if (_expected.TryGetValue(uriHash, out string? expected) && expected != keyHash)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new RunnerStop(NeedsAttention("key-changed", "An HLS encryption key changed since committed segments were downloaded."));
            }
            if (_keys.Count >= 32)
            {
                string oldUri = _keys.Keys.First();
                CryptographicOperations.ZeroMemory(_keys[oldUri]);
                _keys.Remove(oldUri);
            }
            _fingerprints.Remove(uriHash);
            if (_fingerprints.Count >= 32) _fingerprints.Remove(_fingerprints.Keys.First());
            _fingerprints[uriHash] = keyHash;
            _keys.Add(uri, key);
            return key;
        }
        internal void Clear()
        {
            foreach (byte[] key in _keys.Values) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
        }
        public void Dispose() => Clear();

        private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    }
}
