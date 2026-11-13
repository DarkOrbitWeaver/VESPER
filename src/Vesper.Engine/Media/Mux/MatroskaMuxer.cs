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

    public static long Write(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, Stream output, bool webm,
        CancellationToken ct, MediaMergeMetadata? metadata = null, MediaThumbnail? thumbnail = null)
    {
        if (inputs.Count is < 1 or > 8) throw new MediaContainerException("A Matroska output needs between one and eight tracks.");
        long origin = output.Position;

        // ---- timing: decode-order key and presentation time (ns) for every sample
        var dtsNs = new long[inputs.Count][];
        var ptsNs = new long[inputs.Count][];
        long minPts = long.MaxValue;
        for (int ti = 0; ti < inputs.Count; ti++)
        {
            var t = inputs[ti].Track;
            var d = new long[t.Samples.Count];
            var p = new long[t.Samples.Count];
            long edit = t.SourceContainer == "mp4" ? Math.Max(0, t.EditMediaTime) : 0;
            for (int i = 0; i < d.Length; i++)
            {
                var s = t.Samples[i];
                d[i] = ToNs(s.Dts - edit, t.Timescale);
                p[i] = ToNs(s.Dts + s.CtsOffset - edit, t.Timescale);
                if (p[i] < minPts) minPts = p[i];
            }
            dtsNs[ti] = d; ptsNs[ti] = p;
        }
        long shift = minPts < 0 ? -minPts : 0;
        bool hasVideo = inputs.Any(i => i.Track.Kind == TrackKind.Video);

        long durationMs = 0;
        for (int ti = 0; ti < inputs.Count; ti++)
        {
            var t = inputs[ti].Track;
            if (t.Samples.Count == 0) continue;
            long lastEnd = ptsNs[ti].Max() + shift + ToNs(Math.Max(0, t.Samples[^1].Duration), t.Timescale);
            durationMs = Math.Max(durationMs, lastEnd / 1_000_000);
        }

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
        Float(info, 0x4489, durationMs);
        Master(output, WebmDemuxer.IdInfo, info.ToArray());

        long tracksPos = output.Position - segData;
        var tracks = new MemoryStream();
        for (int ti = 0; ti < inputs.Count; ti++) Master(tracks, 0xAE, TrackEntry(inputs[ti].Track, ti + 1, webm));
        Master(output, WebmDemuxer.IdTracks, tracks.ToArray());

        long tagsPos = -1;
        if (metadata is { } tagMetadata && (tagMetadata.Uploader is { Length: > 0 } || tagMetadata.Description is { Length: > 0 }))
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
        if (!webm && thumbnail is not null)
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
                    if (pick < 0 || dtsNs[ti][next[ti]] < dtsNs[pick][next[pick]]) pick = ti;
                }
                if (pick < 0) break;
                var t = inputs[pick].Track;
                var s = t.Samples[next[pick]];
                long pts = (ptsNs[pick][next[pick]] + shift) / 1_000_000;
                next[pick]++;
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
                Id(output, 0xA3);
                Size(output, 4 + s.Size);
                output.WriteByte((byte)(0x80 | (pick + 1)));
                BinaryPrimitives.WriteInt16BigEndian(tc, checked((short)rel));
                output.Write(tc);
                output.WriteByte((byte)((s.Key ? 0x80 : 0) | ((s.Lacing & 3) << 1)));
                Mp4Muxer.CopyRange(inputs[pick].Source, s.Offset, s.Size, output, buffer);
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
        if (metadata.Uploader is { Length: > 0 } uploader) WriteSimpleTag(tag, "ARTIST", uploader);
        if (metadata.Description is { Length: > 0 } description) WriteSimpleTag(tag, "COMMENT", description);
        var tags = new MemoryStream();
        Master(tags, 0x7373, tag.ToArray());
        return tags.ToArray();
    }

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

    private static long ToNs(long ticks, uint timescale) => (long)((Int128)ticks * 1_000_000_000 / timescale);

    private static byte[] TrackEntry(DemuxedTrack t, int number, bool webm)
    {
        bool video = t.Kind == TrackKind.Video;
        string codecId;
        byte[]? priv;
        if (t.SourceContainer == "webm")
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
        UInt(e, 0x83, video ? 1UL : 2UL);
        UInt(e, 0x9C, t.HasLacing ? 1UL : 0UL);
        Str(e, 0x22B59C, t.Language is { Length: 3 } l && l.All(char.IsAsciiLetterLower) ? l : "und");
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
        else
        {
            var a = new MemoryStream();
            Float(a, 0xB5, t.SampleRate > 0 ? t.SampleRate : 48000);
            UInt(a, 0x9F, (ulong)Math.Max(1, t.Channels));
            if (t.BitDepth > 0 && t.Codec is not ("aac" or "opus")) UInt(a, 0x6264, (ulong)t.BitDepth);
            Master(e, 0xE1, a.ToArray());
        }
        return e.ToArray();
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
    private static readonly HashSet<string> Mp4Codecs = ["h264", "h265", "av1", "vp9", "aac", "mp3", "opus"];
    private static readonly HashSet<string> WebmCodecs = ["vp8", "vp9", "av1", "opus", "vorbis"];
    private static readonly HashSet<string> MkvCodecs = ["h264", "h265", "av1", "vp8", "vp9", "aac", "mp3", "opus", "vorbis"];

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
        if (thumbnail is not null && container != "mkv")
            throw new MediaContainerException("Cover images are only supported in Matroska output.");
        long bytes;
        MediaMergeMetadata? writtenMetadata;
        switch (container)
        {
            case "mp4":
            case "m4a":
                bytes = Mp4Muxer.Write(inputs, output, ct, metadata, out writtenMetadata);
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

    /// <summary>Re-reads a finished output and proves it carries exactly the expected tracks and samples.</summary>
    public static void Validate(Stream output, long length, IReadOnlyList<DemuxedTrack> expected, CancellationToken ct,
        MediaMergeMetadata? expectedMetadata = null, string? expectedThumbnailSha256 = null)
    {
        List<DemuxedTrack> got = Demux(output, length, ct);
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
            if (Math.Abs(g.DurationSeconds - e.DurationSeconds) > tolerance)
                throw new MediaContainerException($"Merged {e.Kind.ToString().ToLowerInvariant()} duration {g.DurationSeconds:0.##}s differs from the source {e.DurationSeconds:0.##}s.");
        }
        if (expectedMetadata is not null)
        {
            MediaMergeMetadata actual = WebmDemuxer.LooksLikeMatroska(output, length)
                ? WebmDemuxer.ReadMetadata(output, length) : Mp4Demuxer.ReadMetadata(output, length);
            if (actual.Title != expectedMetadata.Title || actual.Uploader != expectedMetadata.Uploader
                || actual.Description != expectedMetadata.Description || !actual.Chapters.SequenceEqual(expectedMetadata.Chapters))
                throw new MediaContainerException("The merged file did not retain the selected title, tags and chapters.");
        }
        if (expectedThumbnailSha256 is not null)
        {
            MediaThumbnail? thumbnail = WebmDemuxer.ReadThumbnail(output, length);
            if (thumbnail?.Sha256 != expectedThumbnailSha256)
                throw new MediaContainerException("The merged file did not retain the selected cover image.");
        }
    }
}
