namespace Vesper.Engine;

internal static class Mp4Clip
{
    internal static void Apply(IReadOnlyList<DemuxedTrack> tracks, MediaTimeRange range, CancellationToken ct)
    {
        bool audioOnly = tracks is [{ Kind: TrackKind.Audio, Codec: "aac" }];
        bool audioAndVideo = tracks.Count == 2
            && tracks.Count(track => track is { Kind: TrackKind.Video, Codec: "h264" }) == 1
            && tracks.Count(track => track is { Kind: TrackKind.Audio, Codec: "aac" }) == 1;
        if (!range.IsValid || !audioOnly && !audioAndVideo)
            throw new MediaContainerException("This MP4 clip needs one AAC audio track, optionally with one H.264 video track.");

        long from = TimeSpan.FromSeconds(range.StartSeconds).Ticks;
        long to = TimeSpan.FromSeconds(range.EndSeconds).Ticks;
        if (to <= from)
            throw new MediaContainerException("The selected time range is too short.");

        try
        {
            foreach (DemuxedTrack track in tracks)
                TrimTrack(track, from, to, ct);
        }
        catch (OverflowException)
        {
            throw new MediaContainerException("A source MP4 timestamp is too large for the selected range.");
        }
    }

    private static void TrimTrack(DemuxedTrack track, long from, long to, CancellationToken ct)
    {
        const uint movieScale = (uint)TimeSpan.TicksPerSecond;
        if (track.SourceContainer != "mp4" || track.Timescale == 0 || track.Samples.Count == 0)
            throw new MediaContainerException("The selected MP4 track has no usable timeline.");
        if (track.Kind == TrackKind.Audio && (track.CodecPrivate is not { Length: >= 2 } config || config[0] >> 3 != 2))
            throw new MediaContainerException("The selected MP4 audio isn't AAC-LC and can't be trimmed by copying its packets.");

        long origin = track.EditMediaTime;
        if (track.EditList is { } edits)
        {
            if (edits.Count != 1 || edits[0] is not { MediaRateInteger: 1, MediaRateFraction: 0, MediaTime: >= 0 } edit
                || track.EditMovieTimescale == 0 || (Int128)to * track.EditMovieTimescale > (Int128)edit.SegmentDuration * movieScale)
                throw new MediaContainerException("The selected range doesn't fit the source MP4 edit.");
            origin = edit.MediaTime;
        }

        long start = checked(origin + (long)((Int128)from * track.Timescale / movieScale));
        long end = checked(origin + (long)(((Int128)to * track.Timescale + movieScale - 1) / movieScale));
        int first = -1, last = -1;
        long earliest = long.MaxValue, latest = long.MinValue;
        List<TrackSample> samples = track.Samples;
        for (int i = 0; i < samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            TrackSample sample = samples[i];
            long pts = checked(sample.Dts + sample.CtsOffset);
            long next = checked(pts + sample.Duration);
            if (pts >= end || next <= start || sample.Duration <= 0) continue;
            if (first < 0) first = i;
            last = i;
            earliest = Math.Min(earliest, pts);
            latest = Math.Max(latest, next);
        }
        if (first < 0 || earliest > start || latest < end)
            throw new MediaContainerException("The selected range extends outside one of the MP4 tracks.");

        // Keep video from the preceding sync sample, and one AAC packet for decoder overlap.
        if (track.Kind == TrackKind.Video)
        {
            while (first > 0 && (!samples[first].Key || samples[first].Dts > start)) first--;
            if (!samples[first].Key)
                throw new MediaContainerException("The clip has no preceding keyframe.");
        }
        else if (first > 0) first--;

        long decodeStart = samples[first].Dts;
        if (decodeStart > start)
            throw new MediaContainerException("The clip has no usable decoding start.");
        var selected = new List<TrackSample>(last - first + 1);
        for (int i = first; i <= last; i++)
        {
            ct.ThrowIfCancellationRequested();
            TrackSample sample = samples[i];
            sample.Dts -= decodeStart;
            selected.Add(sample);
        }

        track.Samples = selected;
        track.EditMediaTime = start - decodeStart;
        track.EditMovieTimescale = movieScale;
        track.EditList = [new Mp4EditListEntry((ulong)(to - from), track.EditMediaTime, 1, 0)];
    }
}
