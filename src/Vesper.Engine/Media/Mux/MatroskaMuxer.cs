using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>
/// Writes WebM/Matroska from demuxed tracks (WebM or MP4 sources). Blocks are copied unchanged, ordered by decode
/// time, timestamped by presentation time (1 ms TimecodeScale), clustered at video keyframes and indexed with Cues.
/// </summary>
internal static class MatroskaMuxer
{
    private const int SeekHeadReserve = 160;
    private const long NanosecondsPerMillisecond = 1_000_000;

    internal sealed record TrackTiming(long[]? DecodeNanoseconds, long[] PresentationMilliseconds);
    internal sealed record Timeline(IReadOnlyList<TrackTiming> Tracks, long ShiftNanoseconds, long DurationMilliseconds);

    public static long Write(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, Stream output, bool webm,
        CancellationToken ct, MediaMergeMetadata? metadata = null, MediaThumbnail? thumbnail = null)
    {
        if (inputs.Count is < 1 or > MediaMerger.MaxTrackCount)
            throw new MediaContainerException($"A Matroska output needs between one and {MediaMerger.MaxTrackCount} tracks.");
        long origin = output.Position;

        Timeline timeline = BuildTimeline(inputs.Select(input => input.Track).ToArray(), ct);
        bool hasVideo = inputs.Any(i => i.Track.Kind == TrackKind.Video);

        // ---- EBML header + segment
        var hdr = new MemoryStream();
        UInt(hdr, 0x4286, 1); UInt(hdr, 0x42F7, 1); UInt(hdr, 0x42F2, 4); UInt(hdr, 0x42F3, 8);
        Str(hdr, 0x4282, webm ? "webm" : "matroska"); UInt(hdr, 0x4287, 4); UInt(hdr, 0x4285, 2);
        Master(output, WebmDemuxer.IdEbml, hdr.ToArray());
        Id(output, WebmDemuxer.IdSegment);
        long segSizePos = output.Position;
        Size8(output, 0);
        long segData = output.Position;
        long seekHeadPos = output.Position;
        Void(output, SeekHeadReserve);

        long infoPos = output.Position - segData;
        var info = new MemoryStream();
        UInt(info, 0x2AD7B1, 1_000_000);
        Str(info, 0x4D80, "VESPER");
        Str(info, 0x5741, "VESPER");
        if (metadata?.Title is { Length: > 0 } title) Str(info, 0x7BA9, title);
        Float(info, 0x4489, timeline.DurationMilliseconds);
        Master(output, WebmDemuxer.IdInfo, info.ToArray());

        long tracksPos = output.Position - segData;
        var tracks = new MemoryStream();
        for (int ti = 0; ti < inputs.Count; ti++) Master(tracks, 0xAE, TrackEntry(inputs[ti].Track, ti + 1, webm));
        Master(output, WebmDemuxer.IdTracks, tracks.ToArray());

        long tagsPos = -1;
        if (metadata is { } tagMetadata && HasMatroskaTags(tagMetadata))
        {
            tagsPos = output.Position - segData;
            Master(output, WebmDemuxer.IdTags, Tags(tagMetadata));
        }

        long chaptersPos = -1;
        if (metadata?.Chapters is { Count: > 0 } chapters)
        {
            chaptersPos = output.Position - segData;
            Master(output, WebmDemuxer.IdChapters, Chapters(chapters));
        }

        long attachmentsPos = -1;
        if (thumbnail is not null)
        {
            attachmentsPos = output.Position - segData;
            Master(output, WebmDemuxer.IdAttachments, Attachments(thumbnail));
        }

        // ---- clusters (k-way merge in decode order)
        var next = new int[inputs.Count];
        var cues = new List<(long TimeMs, int Track, long ClusterPos)>();
        long clusterSizePos = -1, clusterStartMs = 0, clusterBytes = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        Span<byte> tc = stackalloc byte[2];
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int pick = -1;
                for (int ti = 0; ti < inputs.Count; ti++)
                {
                    if (next[ti] >= inputs[ti].Track.Samples.Count) continue;
                    long decodeTime = timeline.Tracks[ti].DecodeNanoseconds![next[ti]];
                    if (pick < 0 || decodeTime < timeline.Tracks[pick].DecodeNanoseconds![next[pick]]) pick = ti;
                }
                if (pick < 0) break;
                var t = inputs[pick].Track;
                int sampleIndex = next[pick]++;
                var s = t.Samples[sampleIndex];
                TrackTiming trackTiming = timeline.Tracks[pick];
                long pts = trackTiming.PresentationMilliseconds[sampleIndex];
                long sampleDuration = SampleDurationMilliseconds(t, s, timeline.ShiftNanoseconds, pts);
                bool videoKey = t.Kind == TrackKind.Video && s.Key;
                long rel = pts - clusterStartMs;
                bool newCluster = clusterSizePos < 0
                    || rel > 30_000 || rel < -30_000
                    || (videoKey && rel >= 1_000)
                    || (!hasVideo && rel >= 2_000)
                    || (clusterBytes > 16 << 20 && (videoKey || !hasVideo || rel >= 5_000));
                if (newCluster)
                {
                    if (clusterSizePos >= 0) PatchSize8(output, clusterSizePos);
                    long clusterPos = output.Position - segData;
                    Id(output, WebmDemuxer.IdCluster);
                    clusterSizePos = output.Position;
                    Size8(output, 0);
                    clusterStartMs = Math.Max(0, pts);
                    UInt(output, 0xE7, (ulong)clusterStartMs);
                    clusterBytes = 0;
                    if (videoKey || !hasVideo) cues.Add((clusterStartMs, pick + 1, clusterPos));
                    rel = pts - clusterStartMs;
                }
                bool blockGroup = sampleDuration > 0;
                long blockGroupSizePosition = -1;
                if (blockGroup)
                {
                    Id(output, 0xA0); // BlockGroup carries an explicit per-sample duration.
                    blockGroupSizePosition = output.Position;
                    Size8(output, 0);
                    Id(output, 0xA1);
                }
                else Id(output, 0xA3);
                Size(output, 4 + s.Size);
                output.WriteByte((byte)(0x80 | (pick + 1)));
                BinaryPrimitives.WriteInt16BigEndian(tc, checked((short)rel));
                output.Write(tc);
                byte flags = (byte)((s.Lacing & 3) << 1);
                if (!blockGroup && s.Key) flags |= 0x80;
                output.WriteByte(flags);
                Mp4Muxer.CopyRange(inputs[pick].Source, s.Offset, s.Size, output, buffer);
                if (blockGroup)
                {
                    if (!s.Key)
                        WriteSignedInteger(output, 0xFB, 0); // The source gives no dependency graph; mark the frame non-RAP.
                    UInt(output, 0x9B, (ulong)sampleDuration);
                    PatchSize8(output, blockGroupSizePosition);
                }
                clusterBytes += s.Size;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (clusterSizePos >= 0) PatchSize8(output, clusterSizePos);

        // ---- cues, seek head, segment size
        long cuesPos = output.Position - segData;
        var cueData = new MemoryStream();
        foreach (var (time, track, pos) in cues)
        {
            var positions = new MemoryStream();
            UInt(positions, 0xF7, (ulong)track);
            UInt(positions, 0xF1, (ulong)pos);
            var point = new MemoryStream();
            UInt(point, 0xB3, (ulong)time);
            Master(point, 0xB7, positions.ToArray());
            Master(cueData, 0xBB, point.ToArray());
        }
        if (cues.Count > 0) Master(output, WebmDemuxer.IdCues, cueData.ToArray());
        long end = output.Position;

        var seek = new MemoryStream();
        SeekEntry(seek, WebmDemuxer.IdInfo, infoPos);
        SeekEntry(seek, WebmDemuxer.IdTracks, tracksPos);
        if (tagsPos >= 0) SeekEntry(seek, WebmDemuxer.IdTags, tagsPos);
        if (chaptersPos >= 0) SeekEntry(seek, WebmDemuxer.IdChapters, chaptersPos);
        if (attachmentsPos >= 0) SeekEntry(seek, WebmDemuxer.IdAttachments, attachmentsPos);
        if (cues.Count > 0) SeekEntry(seek, WebmDemuxer.IdCues, cuesPos);
        var seekHead = new MemoryStream();
        Master(seekHead, WebmDemuxer.IdSeekHead, seek.ToArray());
        if (seekHead.Length > SeekHeadReserve - 2) throw new InvalidOperationException("Seek head does not fit its reservation.");
        output.Position = seekHeadPos;
        output.Write(seekHead.ToArray());
        Void(output, SeekHeadReserve - (int)seekHead.Length);
        output.Position = segSizePos;
        Size8(output, end - segData);
        output.Position = end;
        return end - origin;
    }

    private static byte[] Attachments(MediaThumbnail thumbnail)
    {
        var file = new MemoryStream();
        Str(file, 0x467E, "Cover image");
        Str(file, 0x466E, thumbnail.FileName);
        Str(file, 0x4660, thumbnail.MediaType);
        Bin(file, 0x465C, thumbnail.Data);
        UInt(file, 0x46AE, 1);
        var attachments = new MemoryStream();
        Master(attachments, 0x61A7, file.ToArray());
        return attachments.ToArray();
    }

    private static byte[] Tags(MediaMergeMetadata metadata)
    {
        var tag = new MemoryStream();
        Master(tag, 0x63C0, []); // Empty Targets applies these values to the whole file.
        if (metadata.Artist is { Length: > 0 } artist) WriteSimpleTag(tag, "ARTIST", artist);
        if (metadata.Album is { Length: > 0 } album) WriteSimpleTag(tag, "ALBUM", album);
        if (metadata.AlbumArtist is { Length: > 0 } albumArtist) WriteSimpleTag(tag, "ALBUM_ARTIST", albumArtist);
        if (metadata.TrackNumber is { } track) WriteSimpleTag(tag, "PART_NUMBER", track.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (metadata.DiscNumber is { } disc) WriteSimpleTag(tag, "DISC_NUMBER", disc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (metadata.ReleaseDate is { Length: > 0 } releaseDate) WriteSimpleTag(tag, "DATE_RELEASED", releaseDate);
        if (metadata.Uploader is { Length: > 0 } uploader) WriteSimpleTag(tag, "UPLOADER", uploader);
        if (metadata.Description is { Length: > 0 } description) WriteSimpleTag(tag, "COMMENT", description);
        var tags = new MemoryStream();
        Master(tags, 0x7373, tag.ToArray());
        return tags.ToArray();
    }

    private static bool HasMatroskaTags(MediaMergeMetadata metadata) =>
        metadata.Artist is { Length: > 0 } || metadata.Album is { Length: > 0 }
        || metadata.AlbumArtist is { Length: > 0 } || metadata.TrackNumber is not null
        || metadata.DiscNumber is not null || metadata.ReleaseDate is { Length: > 0 }
        || metadata.Uploader is { Length: > 0 } || metadata.Description is { Length: > 0 };

    private static void WriteSimpleTag(Stream tag, string name, string value)
    {
        var simple = new MemoryStream();
        Str(simple, 0x45A3, name);
        Str(simple, 0x4487, value);
        Master(tag, 0x67C8, simple.ToArray());
    }

    private static byte[] Chapters(IReadOnlyList<MediaMergeChapter> chapters)
    {
        var edition = new MemoryStream();
        UInt(edition, 0x45DB, 1); // Default edition.
        for (int i = 0; i < chapters.Count; i++)
        {
            MediaMergeChapter chapter = chapters[i];
            var atom = new MemoryStream();
            UInt(atom, 0x73C4, (ulong)i + 1);
            UInt(atom, 0x91, checked((ulong)chapter.Start.Ticks * 100));
            if (chapter.End is { } end) UInt(atom, 0x92, checked((ulong)end.Ticks * 100));
            var display = new MemoryStream();
            Str(display, 0x85, chapter.Title);
            Master(atom, 0x80, display.ToArray());
            Master(edition, 0xB6, atom.ToArray());
        }
        var container = new MemoryStream();
        Master(container, 0x45B9, edition.ToArray());
        return container.ToArray();
    }

    internal static Timeline BuildTimeline(IReadOnlyList<DemuxedTrack> tracks, CancellationToken ct = default,
        bool includeDecodeTime = true)
    {
        foreach (DemuxedTrack track in tracks) ValidateMatroskaEditSemantics(track);

        long minPresentationNs = long.MaxValue;
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            DemuxedTrack track = tracks[trackIndex];
            if (track.Timescale == 0) throw new MediaContainerException("A media track has no time scale.");
            long edit = track.SourceContainer == "mp4" ? Math.Max(0, track.EditMediaTime) : 0;
            for (int sampleIndex = 0; sampleIndex < track.Samples.Count; sampleIndex++)
            {
                ct.ThrowIfCancellationRequested();
                TrackSample sample = track.Samples[sampleIndex];
                long presentationTicks = checked(sample.Dts + sample.CtsOffset - edit);
                minPresentationNs = Math.Min(minPresentationNs, ToNs(presentationTicks, track.Timescale));
            }
        }

        long shift = minPresentationNs < 0 ? checked(-minPresentationNs) : 0;
        var timing = new TrackTiming[tracks.Count];
        long durationMilliseconds = 0;
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            DemuxedTrack track = tracks[trackIndex];
            var presentationMilliseconds = new long[track.Samples.Count];
            long[]? decodeNanoseconds = includeDecodeTime ? new long[track.Samples.Count] : null;
            long edit = track.SourceContainer == "mp4" ? Math.Max(0, track.EditMediaTime) : 0;
            for (int sampleIndex = 0; sampleIndex < track.Samples.Count; sampleIndex++)
            {
                ct.ThrowIfCancellationRequested();
                TrackSample sample = track.Samples[sampleIndex];
                if (decodeNanoseconds is not null)
                    decodeNanoseconds[sampleIndex] = ToNs(checked(sample.Dts - edit), track.Timescale);
                long presentationTicks = checked(sample.Dts + sample.CtsOffset - edit);
                long presentationNs = ToNs(presentationTicks, track.Timescale);
                long startNs = checked(presentationNs + shift);
                long startMs = startNs / NanosecondsPerMillisecond;
                presentationMilliseconds[sampleIndex] = startMs;
                long sampleDuration = SampleDurationMilliseconds(track, sample, shift, startMs);
                durationMilliseconds = Math.Max(durationMilliseconds, checked(startMs + sampleDuration));
            }
            timing[trackIndex] = new TrackTiming(decodeNanoseconds, presentationMilliseconds);
        }
        return new Timeline(timing, shift, durationMilliseconds);
    }

    private static void ValidateMatroskaEditSemantics(DemuxedTrack track)
    {
        if (track.SourceContainer != "mp4") return;
        if (track.EditList is not { } edits)
        {
            if (track.EditMediaTime != 0)
                throw new MediaContainerException("Matroska output cannot preserve an MP4 media edit without its edit-list duration.");
            return;
        }

        if (edits.Count != 1)
            throw new MediaContainerException("Matroska output cannot preserve empty or multiple MP4 edit segments.");

        Mp4EditListEntry edit = edits[0];
        if (edit.MediaTime < 0 || edit.MediaRateInteger != 1 || edit.MediaRateFraction != 0
            || edit.SegmentDuration == 0 || track.EditMovieTimescale == 0)
            throw new MediaContainerException("Matroska output supports only a full-length, rate-1 MP4 media edit.");

        long presentationEnd = long.MinValue;
        foreach (TrackSample sample in track.Samples)
        {
            long presentationStart = checked(sample.Dts + sample.CtsOffset);
            if (presentationStart < edit.MediaTime)
                throw new MediaContainerException("Matroska output cannot preserve a trimmed MP4 edit interval.");
            presentationEnd = Math.Max(presentationEnd, checked(presentationStart + sample.Duration));
        }

        if (presentationEnd == long.MinValue)
            throw new MediaContainerException("Matroska output cannot preserve a trimmed MP4 edit interval.");

        long editedPresentationDuration = checked(presentationEnd - edit.MediaTime);
        if (editedPresentationDuration <= 0
            || (Int128)edit.SegmentDuration * track.Timescale
                != (Int128)editedPresentationDuration * track.EditMovieTimescale)
            throw new MediaContainerException("Matroska output cannot preserve a trimmed MP4 edit interval.");
    }

    internal static long SampleDurationMilliseconds(DemuxedTrack track, TrackSample sample, long shift, long startMilliseconds)
    {
        if (sample.Duration <= 0) return 0;
        long edit = track.SourceContainer == "mp4" ? Math.Max(0, track.EditMediaTime) : 0;
        long endTicks = checked(sample.Dts + sample.CtsOffset + sample.Duration - edit);
        long endNanoseconds = checked(ToNs(endTicks, track.Timescale) + shift);
        long endMilliseconds = endNanoseconds / NanosecondsPerMillisecond;
        if (endMilliseconds < startMilliseconds)
            throw new MediaContainerException("A media sample ends before its presentation time.");
        if (endMilliseconds == startMilliseconds)
            throw new MediaContainerException("Matroska's 1 ms timecode cannot represent this sample duration.");
        return endMilliseconds - startMilliseconds;
    }

    private static long ToNs(long ticks, uint timescale) => checked((long)((Int128)ticks * 1_000_000_000 / timescale));

    private static void WriteSignedInteger(Stream output, uint id, long value)
    {
        int bytes = 1;
        while (bytes < sizeof(long))
        {
            int bits = bytes * 8 - 1;
            long minimum = -(1L << bits);
            long maximum = (1L << bits) - 1;
            if (value >= minimum && value <= maximum) break;
            bytes++;
        }

        Id(output, id);
        Size(output, bytes);
        ulong encoded = unchecked((ulong)value);
        for (int index = bytes - 1; index >= 0; index--)
            output.WriteByte((byte)(encoded >> (index * 8)));
    }

    private static byte[] TrackEntry(DemuxedTrack t, int number, bool webm)
    {
        bool video = t.Kind == TrackKind.Video;
        bool audio = t.Kind == TrackKind.Audio;
        bool subtitle = t.Kind == TrackKind.Subtitle;
        string codecId;
        byte[]? priv;
        if (subtitle && t.SourceContainer == "subtitle")
        {
            codecId = "S_TEXT/UTF8";
            priv = null;
        }
        else if (t.SourceContainer == "webm")
        {
            codecId = t.MatroskaCodecId ?? throw new MediaContainerException("A WebM track has no codec id.");
            priv = t.CodecPrivate;
        }
        else
        {
            (codecId, priv) = t.Codec switch
            {
                "h264" => ("V_MPEG4/ISO/AVC", t.CodecPrivate),
                "h265" => ("V_MPEGH/ISO/HEVC", t.CodecPrivate),
                "av1" => ("V_AV1", t.CodecPrivate),
                "vp9" => ("V_VP9", (byte[]?)null),
                "aac" => ("A_AAC", t.CodecPrivate),
                "opus" => ("A_OPUS", t.CodecPrivate),
                "mp3" => ("A_MPEG/L3", (byte[]?)null),
                _ => throw new MediaContainerException($"The {t.Codec} track cannot be written to Matroska.")
            };
            if (t.Codec is "h264" or "h265" or "av1" or "aac" or "opus" && priv is null)
                throw new MediaContainerException($"The {t.Codec} track has no decoder configuration.");
        }
        if (webm && t.Codec is not ("vp8" or "vp9" or "av1" or "opus" or "vorbis"))
            throw new MediaContainerException($"WebM cannot carry {t.Codec}.");

        var e = new MemoryStream();
        UInt(e, 0xD7, (ulong)number);
        UInt(e, 0x73C5, (ulong)number);
        UInt(e, 0x83, video ? 1UL : audio ? 2UL : 17UL);
        UInt(e, 0x9C, t.HasLacing ? 1UL : 0UL);
        if (subtitle)
        {
            Str(e, 0x22B59C, MatroskaLanguageCode(t.Language));
            Str(e, 0x22B59D, t.Language);
            if (t.Name.Length > 0) Str(e, 0x536E, t.Name);
        }
        else Str(e, 0x22B59C, t.Language is { Length: 3 } l && l.All(char.IsAsciiLetterLower) ? l : "und");
        Str(e, 0x86, codecId);
        if (priv is { Length: > 0 }) Bin(e, 0x63A2, priv);
        if (t.SourceContainer == "webm" && t.DefaultDurationNs > 0) UInt(e, 0x23E383, (ulong)t.DefaultDurationNs);
        if (t.CodecDelayNs > 0) UInt(e, 0x56AA, (ulong)t.CodecDelayNs);
        if (t.SeekPreRollNs > 0) UInt(e, 0x56BB, (ulong)t.SeekPreRollNs);
        if (t.SourceContainer == "webm" && t.MatroskaSettings is { Length: > 0 } settings)
            Master(e, video ? 0xE0u : 0xE1u, settings);
        else if (video)
        {
            var v = new MemoryStream();
            UInt(v, 0xB0, (ulong)Math.Max(1, t.Width));
            UInt(v, 0xBA, (ulong)Math.Max(1, t.Height));
            Master(e, 0xE0, v.ToArray());
        }
        else if (audio)
        {
            var a = new MemoryStream();
            Float(a, 0xB5, t.SampleRate > 0 ? t.SampleRate : 48000);
            UInt(a, 0x9F, (ulong)Math.Max(1, t.Channels));
            if (t.BitDepth > 0 && t.Codec is not ("aac" or "opus")) UInt(a, 0x6264, (ulong)t.BitDepth);
            Master(e, 0xE1, a.ToArray());
        }
        return e.ToArray();
    }

    private static string MatroskaLanguageCode(string language)
    {
        string primary = language.Split('-')[0];
        if (primary.Length == 3) return primary.ToLowerInvariant();
        try { return System.Globalization.CultureInfo.GetCultureInfo(primary).ThreeLetterISOLanguageName.ToLowerInvariant(); }
        catch (System.Globalization.CultureNotFoundException) { return "und"; }
    }

    private static void SeekEntry(Stream s, uint id, long position)
    {
        var e = new MemoryStream();
        var idBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);
        Bin(e, 0x53AB, idBytes.AsSpan(IdLength(id) is var n ? 4 - n : 0));
        UInt(e, 0x53AC, (ulong)position);
        Master(s, 0x4DBB, e.ToArray());
    }

    // ---- EBML primitives

    private static int IdLength(uint id) => id >= 0x1000000 ? 4 : id >= 0x10000 ? 3 : id >= 0x100 ? 2 : 1;

    private static void Id(Stream s, uint id)
    {
        for (int i = IdLength(id) - 1; i >= 0; i--) s.WriteByte((byte)(id >> (8 * i)));
    }

    private static void Size(Stream s, long size)
    {
        for (int len = 1; len <= 8; len++)
        {
            if (size < (1L << (7 * len)) - 1)
            {
                ulong v = (ulong)size | (1UL << (7 * len));
                for (int i = len - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
                return;
            }
        }
        throw new InvalidOperationException("EBML size too large.");
    }

    private static void Size8(Stream s, long size)
    {
        s.WriteByte(0x01);
        for (int i = 6; i >= 0; i--) s.WriteByte((byte)(size >> (8 * i)));
    }

    private static void PatchSize8(Stream s, long sizePos)
    {
        long end = s.Position;
        s.Position = sizePos;
        Size8(s, end - sizePos - 8);
        s.Position = end;
    }

    private static void UInt(Stream s, uint id, ulong v)
    {
        int n = 1;
        while (n < 8 && v >> (8 * n) != 0) n++;
        Id(s, id); Size(s, n);
        for (int i = n - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }

    private static void Float(Stream s, uint id, double v)
    {
        Id(s, id); Size(s, 8);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        s.Write(b);
    }

    private static void Str(Stream s, uint id, string v) => Bin(s, id, Encoding.UTF8.GetBytes(v));

    private static void Bin(Stream s, uint id, ReadOnlySpan<byte> v)
    {
        Id(s, id); Size(s, v.Length); s.Write(v);
    }

    private static void Master(Stream s, uint id, byte[] payload) => Bin(s, id, payload);

    private static void Void(Stream s, int total)
    {
        if (total < 2) throw new InvalidOperationException("Void element too small.");
        s.WriteByte(0xEC);
        if (total - 2 <= 126) { s.WriteByte((byte)(0x80 | (total - 2))); s.Write(new byte[total - 2]); }
        else { Size8(s, total - 9); s.Write(new byte[total - 9]); }
    }
}

internal sealed record MergeResult(string Container, long Bytes, IReadOnlyList<string> Codecs, double DurationSeconds,
    MediaMergeMetadata? Metadata = null, string? ThumbnailSha256 = null);

/// <summary>
/// Remux orchestration: demux sources, choose a container that can legally carry every codec, write, and re-read
/// the output to prove it holds every sample of every selected track. Never transcodes; never concatenates files.
/// </summary>
internal static class MediaMerger
{
    internal const int MaxTrackCount = 8;
    private static readonly HashSet<string> Mp4Codecs = ["h264", "h265", "av1", "vp9", "aac", "mp3", "opus", "tx3g"];
    private static readonly HashSet<string> WebmCodecs = ["vp8", "vp9", "av1", "opus", "vorbis"];
    private static readonly HashSet<string> MkvCodecs = ["h264", "h265", "av1", "vp8", "vp9", "aac", "mp3", "opus", "vorbis", "utf8"];

    internal static int AvailableTrackSlots(string container, int existingTrackCount) =>
        container is "mp4" or "mkv" ? Math.Max(0, MaxTrackCount - existingTrackCount) : 0;

    public static List<DemuxedTrack> Demux(Stream s, long length, CancellationToken ct) =>
        WebmDemuxer.LooksLikeMatroska(s, length) ? WebmDemuxer.Read(s, length, ct) : Mp4Demuxer.Read(s, length, ct);

    /// <summary>The output container for these tracks, honouring <paramref name="requested"/> only when it is legal.</summary>
    public static string? ChooseContainer(IReadOnlyList<DemuxedTrack> tracks, string? requested)
    {
        bool mp4 = tracks.All(t => t.SourceContainer == "mp4" && t.Mp4SampleEntry is not null && Mp4Codecs.Contains(t.Codec));
        bool webm = tracks.All(t => WebmCodecs.Contains(t.Codec) && !(t.SourceContainer == "mp4" && t.Codec == "vp9"));
        bool mkv = tracks.All(t => MkvCodecs.Contains(t.Codec));
        bool audioOnly = tracks.All(t => t.Kind == TrackKind.Audio);
        return requested switch
        {
            "mp4" when mp4 && !audioOnly => "mp4",
            "m4a" when mp4 && audioOnly => "m4a",
            "webm" when webm => "webm",
            "mkv" when mkv => "mkv",
            null or "" => mp4 ? (audioOnly ? "m4a" : "mp4") : webm ? "webm" : mkv ? "mkv" : null,
            _ => null
        };
    }

    public static MergeResult Merge(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, string container,
        Stream output, CancellationToken ct, MediaMergeMetadata? metadata = null, MediaThumbnail? thumbnail = null)
    {
        if (inputs.Any(input => input.Track.Kind == TrackKind.Subtitle)
            && (container == "mkv" && inputs.Any(input => input.Track.Kind == TrackKind.Subtitle && input.Track.Codec != "utf8")
                || container == "mp4" && inputs.Any(input => input.Track.Kind == TrackKind.Subtitle && input.Track.Codec != "tx3g")
                || container is not ("mkv" or "mp4")))
            throw new MediaContainerException("The selected subtitle encoding is not supported by this output container.");
        if (thumbnail is not null && container is not ("mp4" or "m4a" or "webm" or "mkv"))
            throw new MediaContainerException("Cover images are supported only in MP4, M4A, WebM, or Matroska output.");
        long bytes;
        MediaMergeMetadata? writtenMetadata;
        switch (container)
        {
            case "mp4":
            case "m4a":
                bytes = Mp4Muxer.Write(inputs, output, ct, metadata, thumbnail, out writtenMetadata);
                break;
            case "webm":
            case "mkv":
                bytes = MatroskaMuxer.Write(inputs, output, webm: container == "webm", ct, metadata, thumbnail);
                writtenMetadata = metadata;
                break;
            default:
                throw new MediaContainerException($"The output container '{container}' is not supported.");
        }
        return new MergeResult(container, bytes, inputs.Select(i => i.Track.Codec).ToArray(),
            inputs.Max(i => i.Track.DurationSeconds), writtenMetadata, thumbnail?.Sha256);
    }

    /// <summary>Re-reads a finished output and checks its track shape and selected metadata.</summary>
    public static void Validate(Stream output, long length, IReadOnlyList<DemuxedTrack> expected, CancellationToken ct,
        MediaMergeMetadata? expectedMetadata = null, string? expectedThumbnailSha256 = null)
        => ValidateCore(output, length, expected, null, ct, expectedMetadata, expectedThumbnailSha256);

    /// <summary>Validates each remuxed sample's payload and representable timing.</summary>
    public static void ValidateRemux(Stream output, long length,
        IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, CancellationToken ct,
        MediaMergeMetadata? expectedMetadata = null, string? expectedThumbnailSha256 = null)
    {
        if (inputs.Count == 0) throw new MediaContainerException("A remux needs at least one source track.");
        ValidateCore(output, length, inputs.Select(input => input.Track).ToArray(), inputs,
            ct, expectedMetadata, expectedThumbnailSha256);
    }

    private static void ValidateCore(Stream output, long length, IReadOnlyList<DemuxedTrack> expected,
        IReadOnlyList<(DemuxedTrack Track, Stream Source)>? inputs, CancellationToken ct,
        MediaMergeMetadata? expectedMetadata, string? expectedThumbnailSha256)
    {
        List<DemuxedTrack> got = Demux(output, length, ct);
        bool matroska = WebmDemuxer.LooksLikeMatroska(output, length);
        if (got.Count != expected.Count)
            throw new MediaContainerException($"The merged file has {got.Count} tracks; {expected.Count} were selected.");
        for (int i = 0; i < expected.Count; i++)
        {
            DemuxedTrack e = expected[i], g = got[i];
            if (g.Kind != e.Kind || g.Codec != e.Codec)
                throw new MediaContainerException($"Merged track {i + 1} is {g.Kind}/{g.Codec}; expected {e.Kind}/{e.Codec}.");
            if (g.Samples.Count != e.Samples.Count || g.PayloadBytes != e.PayloadBytes)
                throw new MediaContainerException($"Merged {e.Kind.ToString().ToLowerInvariant()} track lost samples ({g.Samples.Count}/{e.Samples.Count}).");
            double tolerance = Math.Max(0.5, e.DurationSeconds * 0.01);
            if (!(inputs is not null && matroska) && Math.Abs(g.DurationSeconds - e.DurationSeconds) > tolerance)
                throw new MediaContainerException($"Merged {e.Kind.ToString().ToLowerInvariant()} duration {g.DurationSeconds:0.##}s differs from the source {e.DurationSeconds:0.##}s.");
        }
        if (inputs is not null)
        {
            ValidateSamplePayloads(output, got, inputs, ct);
            ValidateRemuxTimeline(output, length, got, inputs, matroska, ct);
        }
        if (expectedMetadata is not null)
        {
            MediaMergeMetadata actual = WebmDemuxer.LooksLikeMatroska(output, length)
                ? WebmDemuxer.ReadMetadata(output, length) : Mp4Demuxer.ReadMetadata(output, length);
            if (actual.Title != expectedMetadata.Title || actual.Uploader != expectedMetadata.Uploader
                || actual.Description != expectedMetadata.Description || actual.Artist != expectedMetadata.Artist
                || actual.Album != expectedMetadata.Album || actual.AlbumArtist != expectedMetadata.AlbumArtist
                || actual.TrackNumber != expectedMetadata.TrackNumber || actual.DiscNumber != expectedMetadata.DiscNumber
                || actual.ReleaseDate != expectedMetadata.ReleaseDate || !actual.Chapters.SequenceEqual(expectedMetadata.Chapters))
                throw new MediaContainerException("The merged file did not retain the selected title, tags and chapters.");
        }
        if (expectedThumbnailSha256 is not null)
        {
            MediaThumbnail? thumbnail = WebmDemuxer.LooksLikeMatroska(output, length)
                ? WebmDemuxer.ReadThumbnail(output, length) : Mp4Demuxer.ReadThumbnail(output, length);
            if (thumbnail?.Sha256 != expectedThumbnailSha256)
                throw new MediaContainerException("The merged file did not retain the selected cover image.");
        }
    }

    private static void ValidateSamplePayloads(Stream output, IReadOnlyList<DemuxedTrack> actual,
        IReadOnlyList<(DemuxedTrack Track, Stream Source)> expected, CancellationToken ct)
    {
        if (!output.CanRead || !output.CanSeek)
            throw new MediaContainerException("The merged output cannot be checked for sample payload changes.");

        var sourcePositions = new List<(Stream Stream, long Position)>();
        foreach (var input in expected)
        {
            Stream source = input.Source;
            if (!source.CanRead || !source.CanSeek)
                throw new MediaContainerException("A source track cannot be checked for sample payload changes.");

            if (!sourcePositions.Any(saved => ReferenceEquals(saved.Stream, source)))
                sourcePositions.Add((source, source.Position));
        }

        const int bufferSize = 64 * 1024;
        byte[] sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long outputPosition = output.Position;
        try
        {
            for (int trackIndex = 0; trackIndex < expected.Count; trackIndex++)
            {
                DemuxedTrack expectedTrack = expected[trackIndex].Track;
                DemuxedTrack actualTrack = actual[trackIndex];
                if (expectedTrack.Samples.Count != actualTrack.Samples.Count)
                    throw new MediaContainerException($"The merged {expectedTrack.Kind.ToString().ToLowerInvariant()} track changed its sample count.");

                Stream source = expected[trackIndex].Source;
                for (int sampleIndex = 0; sampleIndex < expectedTrack.Samples.Count; sampleIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    TrackSample sourceSample = expectedTrack.Samples[sampleIndex];
                    TrackSample outputSample = actualTrack.Samples[sampleIndex];
                    if (sourceSample.Size != outputSample.Size)
                        throw new MediaContainerException($"The merged {expectedTrack.Kind.ToString().ToLowerInvariant()} track changed a sample size.");
                    if (sourceSample.Size < 0 || sourceSample.Offset < 0 || sourceSample.Offset > source.Length - sourceSample.Size
                        || outputSample.Offset < 0 || outputSample.Offset > output.Length - outputSample.Size)
                        throw new MediaContainerException("A sample lies outside its source or merged file.");

                    source.Position = sourceSample.Offset;
                    output.Position = outputSample.Offset;
                    int remaining = sourceSample.Size;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = Math.Min(bufferSize, remaining);
                        ReadSampleBytes(source, sourceBuffer, count, ct);
                        ReadSampleBytes(output, outputBuffer, count, ct);
                        if (!sourceBuffer.AsSpan(0, count).SequenceEqual(outputBuffer.AsSpan(0, count)))
                            throw new MediaContainerException("The merged file contains a different encoded sample payload.");
                        remaining -= count;
                    }
                }
            }
        }
        finally
        {
            try
            {
                foreach ((Stream stream, long position) in sourcePositions) stream.Position = position;
                output.Position = outputPosition;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sourceBuffer);
                ArrayPool<byte>.Shared.Return(outputBuffer);
            }
        }
    }

    private static void ValidateRemuxTimeline(Stream output, long length, IReadOnlyList<DemuxedTrack> actual,
        IReadOnlyList<(DemuxedTrack Track, Stream Source)> expected, bool matroska, CancellationToken ct)
    {
        if (expected.Count != actual.Count)
            throw new MediaContainerException("The remux changed the selected track count.");

        if (matroska)
        {
            MatroskaMuxer.Timeline wanted = MatroskaMuxer.BuildTimeline(
                expected.Select(input => input.Track).ToArray(), ct, includeDecodeTime: false);
            if (actual.Any(track => track.Timescale != 1000))
                throw new MediaContainerException("The Matroska output does not use the validated 1 ms timecode scale.");

            for (int trackIndex = 0; trackIndex < expected.Count; trackIndex++)
            {
                DemuxedTrack sourceTrack = expected[trackIndex].Track;
                DemuxedTrack outputTrack = actual[trackIndex];
                MatroskaMuxer.TrackTiming sourceTiming = wanted.Tracks[trackIndex];
                if (outputTrack.Samples.Any(sample => sample.CtsOffset != 0))
                    throw new MediaContainerException("A Matroska track contains an unsupported composition offset.");
                for (int sampleIndex = 0; sampleIndex < sourceTrack.Samples.Count; sampleIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    TrackSample sourceSample = sourceTrack.Samples[sampleIndex];
                    TrackSample outputSample = outputTrack.Samples[sampleIndex];
                    if (sourceSample.Duration <= 0)
                        throw new MediaContainerException("A source sample has no duration that can be validated in Matroska.");
                    long durationMilliseconds = MatroskaMuxer.SampleDurationMilliseconds(sourceTrack, sourceSample,
                        wanted.ShiftNanoseconds, sourceTiming.PresentationMilliseconds[sampleIndex]);
                    if (outputSample.Dts != sourceTiming.PresentationMilliseconds[sampleIndex]
                        || outputSample.CtsOffset != 0
                        || outputSample.Duration != durationMilliseconds)
                        throw new MediaContainerException($"The merged {sourceTrack.Kind.ToString().ToLowerInvariant()} track changed sample {sampleIndex + 1} timing.");
                    if (outputSample.Key != sourceSample.Key)
                        throw new MediaContainerException($"The merged {sourceTrack.Kind.ToString().ToLowerInvariant()} track changed sample {sampleIndex + 1} keyframe status.");
                }
            }
            return;
        }

        if (!output.CanSeek || length != output.Length)
            throw new MediaContainerException("The MP4 output length cannot be validated.");
        ulong expectedMovieDuration = 0;
        uint? outputMovieTimescale = null;
        ulong? outputMovieDuration = null;
        for (int trackIndex = 0; trackIndex < expected.Count; trackIndex++)
        {
            DemuxedTrack sourceTrack = expected[trackIndex].Track;
            DemuxedTrack outputTrack = actual[trackIndex];
            if (sourceTrack.SourceContainer != "mp4" || outputTrack.SourceContainer != "mp4"
                || sourceTrack.Timescale != outputTrack.Timescale
                || !Mp4Muxer.EditListsMatch(sourceTrack, outputTrack))
                throw new MediaContainerException("The MP4 remux changed a track's time base or edit-list timeline.");

            ulong trackDuration = Mp4Muxer.MoviePresentationDuration(sourceTrack);
            expectedMovieDuration = Math.Max(expectedMovieDuration, trackDuration);
            if (outputTrack.MovieTimescale == 0
                || (Int128)outputTrack.TrackHeaderDurationMovieTicks * Mp4Muxer.MovieTimescale
                    != (Int128)trackDuration * outputTrack.MovieTimescale)
                throw new MediaContainerException($"The MP4 remux changed the {sourceTrack.Kind.ToString().ToLowerInvariant()} track's presentation duration.");
            if ((outputMovieTimescale is { } savedTimescale && savedTimescale != outputTrack.MovieTimescale)
                || (outputMovieDuration is { } savedDuration
                    && savedDuration != outputTrack.MovieHeaderDurationMovieTicks))
                throw new MediaContainerException("The MP4 tracks disagree about the movie header duration.");
            outputMovieTimescale = outputTrack.MovieTimescale;
            outputMovieDuration = outputTrack.MovieHeaderDurationMovieTicks;

            if (sourceTrack.Samples.Count == 0)
                throw new MediaContainerException("An MP4 remux track has no samples to validate.");
            // Flat MP4 starts DTS at zero; the muxer carries this exact source origin into ctts.
            long decodeOrigin = sourceTrack.Samples[0].Dts;

            for (int sampleIndex = 0; sampleIndex < sourceTrack.Samples.Count; sampleIndex++)
            {
                ct.ThrowIfCancellationRequested();
                TrackSample sourceSample = sourceTrack.Samples[sampleIndex];
                TrackSample outputSample = outputTrack.Samples[sampleIndex];
                long expectedDts = checked(sourceSample.Dts - decodeOrigin);
                long expectedComposition = checked(sourceSample.CtsOffset + decodeOrigin);
                if (expectedComposition is < int.MinValue or > int.MaxValue)
                    throw new MediaContainerException("The MP4 composition offset cannot preserve the source decode origin.");
                long sourcePts = checked(sourceSample.Dts + sourceSample.CtsOffset);
                long outputPts = checked(outputSample.Dts + outputSample.CtsOffset);
                if (outputSample.Dts != expectedDts || outputSample.CtsOffset != (int)expectedComposition
                    || outputPts != sourcePts || outputSample.Duration != sourceSample.Duration)
                    throw new MediaContainerException($"The merged {sourceTrack.Kind.ToString().ToLowerInvariant()} track changed sample {sampleIndex + 1} timing.");
                if (outputSample.Key != sourceSample.Key)
                    throw new MediaContainerException($"The merged {sourceTrack.Kind.ToString().ToLowerInvariant()} track changed sample {sampleIndex + 1} keyframe status.");
            }
        }

        if (outputMovieTimescale is not { } movieTimescale || outputMovieDuration is not { } movieDuration
            || (Int128)movieDuration * Mp4Muxer.MovieTimescale
                != (Int128)expectedMovieDuration * movieTimescale)
            throw new MediaContainerException("The MP4 remux changed the movie header duration.");
    }

    private static void ReadSampleBytes(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            ct.ThrowIfCancellationRequested();
            int amount = stream.Read(buffer, read, count - read);
            if (amount == 0) throw new MediaContainerException("A source or merged sample ended before its declared size.");
            read += amount;
        }
    }
}
