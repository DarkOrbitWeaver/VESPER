namespace Vesper.Engine;

// WebM does not have MP4's edit-list mechanism. Copy a contiguous frame
// interval beginning at a decodable keyframe; report the actual boundaries.
internal static class WebmClip
{
    internal static (double Start, double End) Apply(DemuxedTrack track, MediaTimeRange range, CancellationToken ct)
    {
        if (!range.IsValid || track is not { Kind: TrackKind.Video, SourceContainer: "webm", Codec: "vp8" or "vp9",
                Timescale: > 0 } || track.Samples.Count == 0)
            throw new MediaContainerException("This WebM cannot be trimmed without re-encoding.");
        var samples = track.Samples;
        long origin = samples[0].Dts;
        if (origin < 0) throw new MediaContainerException("The WebM has an unsupported timeline.");
        double from = range.StartSeconds * track.Timescale, to = range.EndSeconds * track.Timescale;
        int first = -1, last = -1;
        long previous = -1, end = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sample = samples[i];
            if (sample.Dts < origin || sample.Dts <= previous || sample.Duration <= 0
                || sample.CtsOffset != 0 || sample.Lacing != 0 || sample.Dts > long.MaxValue - sample.Duration)
                throw new MediaContainerException("The WebM has an unsupported frame timeline.");
            previous = sample.Dts;
            long relative = sample.Dts - origin;
            end = relative + sample.Duration;
            if (sample.Key && relative <= from) first = i;
            if (relative < to) last = i;
        }
        if (first < 0 || last < first || to > end + 1)
            throw new MediaContainerException("The selected range has no complete decodable WebM clip.");
        // Do not silently return the whole original for a requested partial cut.
        if (first == 0 && last == samples.Count - 1 && (from > 0 || to < end - 1))
            throw new MediaContainerException("This range would keep every frame. Choose a wider cut or another format.");
        long baseTime = samples[first].Dts;
        double actualStart = (baseTime - origin) / (double)track.Timescale;
        double actualEnd = (samples[last].Dts - origin + samples[last].Duration) / (double)track.Timescale;
        var selected = new List<TrackSample>(last - first + 1);
        for (int i = first; i <= last; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sample = samples[i];
            sample.Dts -= baseTime;
            selected.Add(sample);
        }
        track.Samples = selected;
        return (actualStart, actualEnd);
    }
}

public sealed partial class DownloadEngine
{
    private async Task<(PreparedMediaFile? File, string? Notice)> PrepareWebmClipAsync(
        DownloadJob job, MediaTimeRange range, CancellationToken ct)
    {
        SetPhase(job, JobState.Converting);
        string stage = PublicationStagePath(job);
        string? identity = null;
        bool ready = false, permit = false;
        try
        {
            using var source = PartialFile.OpenOwnedRead(job);
            List<DemuxedTrack> tracks;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Probe, source.Length))
                tracks = WebmDemuxer.Read(source, source.Length, ct);
            if (tracks.Count != 1 || tracks[0].Kind != TrackKind.Video)
                throw new MediaContainerException("The selected silent WebM contains unexpected tracks.");
            var actual = WebmClip.Apply(tracks[0], range, ct);
            string notice = "Keyframe-aligned clip: " + TimeSpan.FromSeconds(actual.Start).ToString(@"hh\:mm\:ss\.fff")
                + "–" + TimeSpan.FromSeconds(actual.End).ToString(@"hh\:mm\:ss\.fff") + ".";
            if (TryReusePreparedAudioMetadata(job, stage) is { } existing)
            {
                ready = true;
                return (existing, notice);
            }
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.CopyQueue))
                await _remuxSlots.WaitAsync(ct).ConfigureAwait(false);
            permit = true;
            using var output = CreateOwnedStage(stage, FileShare.None, out identity);
            var inputs = new List<(DemuxedTrack Track, Stream Source)> { (tracks[0], source) };
            MergeResult merged;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Remux, source.Length))
                merged = MediaMerger.Merge(inputs, "webm", output, ct);
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.DiskFlush, output.Length))
                output.Flush(flushToDisk: true);
            long length = output.Length;
            output.Dispose();
            FileStream verified = OpenPreparedRead(stage, identity);
            try
            {
                if (verified.Length != length) throw new IOException("The clip stage changed size.");
                string digest = await MergeTransfer.ValidateAndHashOutputAsync(job.Id, verified, length, inputs, merged, ct)
                    .ConfigureAwait(false);
                var prepared = new PreparedMediaFile(length, digest, stage, job.FileName, identity, verified);
                PersistPreparedMedia(job, prepared);
                ready = true;
                return (prepared, notice);
            }
            finally { if (!ready) verified.Dispose(); }
        }
        finally
        {
            if (!ready && identity is not null) DeleteTemporaryFile(job, stage, identity);
            if (permit) _remuxSlots.Release();
        }
    }
}
