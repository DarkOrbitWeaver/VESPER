using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>
/// Bounded Matroska/WebM reader. Header elements (Info, Tracks) are loaded with size caps; clusters are walked
/// element-by-element and only block headers are read, so memory stays proportional to the block count, not the
/// file size. Encrypted or compressed tracks (ContentEncodings) are rejected.
/// </summary>
internal static class WebmDemuxer
{
    internal const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B,
        IdCluster = 0x1F43B675, IdCues = 0x1C53BB6B, IdSeekHead = 0x114D9B74, IdTags = 0x1254C367,
        IdChapters = 0x1043A770, IdAttachments = 0x1941A469;
    private const int MaxSamples = 20_000_000;

    public static bool LooksLikeMatroska(Stream s, long length)
    {
        if (length < 4) return false;
        Span<byte> b = stackalloc byte[4];
        MuxIo.ReadExact(s, 0, b);
        return BinaryPrimitives.ReadUInt32BigEndian(b) == IdEbml;
    }

    private static MediaContainerException Bad(string why) => new($"Invalid WebM: {why}.");

    private sealed class Reader(Stream s, long length)
    {
        private readonly byte[] _b = new byte[16];
        public long Length => length;

        public (uint Id, long Size, int HeaderLength) Header(long pos)
        {
            int avail = (int)Math.Min(12, length - pos);
            if (avail < 2) throw Bad("an element header is truncated");
            MuxIo.ReadExact(s, pos, _b.AsSpan(0, avail));
            int idLen = LeadingLength(_b[0], 4) ?? throw Bad("an element id is malformed");
            if (idLen + 1 > avail) throw Bad("an element header is truncated");
            uint id = 0;
            for (int i = 0; i < idLen; i++) id = (id << 8) | _b[i];
            int sizeLen = LeadingLength(_b[idLen], 8) ?? throw Bad("an element size is malformed");
            if (idLen + sizeLen > avail) throw Bad("an element header is truncated");
            long size = _b[idLen] & (0xFF >> sizeLen);
            bool allOnes = size == (0xFF >> sizeLen);
            for (int i = 1; i < sizeLen; i++)
            {
                size = (size << 8) | _b[idLen + i];
                allOnes &= _b[idLen + i] == 0xFF;
            }
            return (id, allOnes ? -1 : size, idLen + sizeLen);
        }

        public byte[] Read(long pos, int count)
        {
            var data = new byte[count];
            MuxIo.ReadExact(s, pos, data);
            return data;
        }
    }

    internal static int? LeadingLength(byte first, int max)
    {
        for (int i = 0; i < max; i++) if ((first & (0x80 >> i)) != 0) return i + 1;
        return null;
    }

    public static List<DemuxedTrack> Read(Stream s, long length, CancellationToken ct)
    {
        var r = new Reader(s, length);
        var (id, size, hl) = r.Header(0);
        if (id != IdEbml || size < 0 || size > 4096) throw Bad("the EBML header is missing");
        byte[] ebml = r.Read(hl, (int)size);
        string docType = "";
        foreach (var c in Elements(ebml, 0, ebml.Length)) if (c.Id == 0x4282) docType = Encoding.ASCII.GetString(ebml, c.Start, c.Length);
        if (docType is not ("webm" or "matroska")) throw Bad($"unsupported document type '{docType}'");

        long pos = hl + size;
        var seg = r.Header(pos);
        if (seg.Id != IdSegment) throw Bad("the segment is missing");
        long segStart = pos + seg.HeaderLength;
        long segEnd = seg.Size < 0 ? length : Math.Min(length, segStart + seg.Size);
        if (seg.Size >= 0 && segStart + seg.Size > length) throw Bad("the segment is truncated");

        ulong timecodeScale = 1_000_000;
        var tracks = new Dictionary<ulong, DemuxedTrack>();
        var order = new List<ulong>();
        int total = 0;
        pos = segStart;
        while (pos < segEnd)
        {
            ct.ThrowIfCancellationRequested();
            var e = r.Header(pos);
            long dataStart = pos + e.HeaderLength;
            if (e.Id == IdCluster)
            {
                if (tracks.Count == 0) throw Bad("a cluster precedes the track list");
                long end = e.Size < 0 ? segEnd : dataStart + e.Size;
                if (end > segEnd) throw Bad("a cluster is truncated");
                pos = ReadCluster(r, dataStart, end, e.Size < 0, tracks, ref total);
                if (total > MaxSamples) throw Bad("too many blocks");
                continue;
            }
            if (e.Size < 0) throw Bad("an element of unknown size is not supported here");
            if (dataStart + e.Size > segEnd) throw Bad("an element is truncated");
            if (e.Id == IdInfo)
            {
                if (e.Size > 1 << 20) throw Bad("the info element is too large");
                byte[] info = r.Read(dataStart, (int)e.Size);
                foreach (var c in Elements(info, 0, info.Length))
                    if (c.Id == 0x2AD7B1) timecodeScale = UInt(info, c.Start, c.Length);
                if (timecodeScale == 0 || 1_000_000_000UL % timecodeScale != 0) throw Bad("unsupported timecode scale");
            }
            else if (e.Id == IdTracks)
            {
                if (e.Size > 8 << 20) throw Bad("the track list is too large");
                byte[] tr = r.Read(dataStart, (int)e.Size);
                foreach (var c in Elements(tr, 0, tr.Length))
                    if (c.Id == 0xAE && ParseTrackEntry(tr, c.Start, c.Start + c.Length) is { } t)
                    {
                        if (tracks.ContainsKey(t.Number)) throw Bad("two tracks share a number");
                        tracks[t.Number] = t.Track; order.Add(t.Number);
                    }
                if (tracks.Count == 0) throw Bad("no audio or video track was found");
            }
            pos = dataStart + e.Size;
        }

        uint scale = (uint)(1_000_000_000UL / timecodeScale);
        var result = new List<DemuxedTrack>();
        foreach (var n in order)
        {
            var t = tracks[n];
            if (t.Samples.Count == 0) continue;
            t.Timescale = scale;
            // Fill unknown durations from the next block time; the last block uses the default/previous duration.
            long defaultTicks = t.DefaultDurationNs > 0 ? Math.Max(1, t.DefaultDurationNs / (long)timecodeScale) : 0;
            for (int i = 0; i < t.Samples.Count; i++)
            {
                var smp = t.Samples[i];
                if (smp.Duration > 0) continue;
                long d = i + 1 < t.Samples.Count ? t.Samples[i + 1].Dts - smp.Dts : defaultTicks > 0 ? defaultTicks : (i > 0 ? t.Samples[i - 1].Duration : 0);
                smp.Duration = (int)Math.Clamp(d, 0, int.MaxValue);
                t.Samples[i] = smp;
            }
            result.Add(t);
        }
        if (result.Count == 0) throw Bad("no blocks were found");
        return result;
    }

    internal static MediaMergeMetadata ReadMetadata(Stream s, long length)
    {
        var r = new Reader(s, length);
        var (id, size, headerLength) = r.Header(0);
        if (id != IdEbml || size < 0 || size > 4096) throw Bad("the EBML header is missing");
        long segmentHeaderPos = headerLength + size;
        var segment = r.Header(segmentHeaderPos);
        if (segment.Id != IdSegment) throw Bad("the segment is missing");
        long segmentStart = segmentHeaderPos + segment.HeaderLength;
        long segmentEnd = segment.Size < 0 ? length : Math.Min(length, segmentStart + segment.Size);
        string? title = null, uploader = null, description = null;
        var chapters = new List<MediaMergeChapter>();
        long pos = segmentStart;
        while (pos < segmentEnd)
        {
            var element = r.Header(pos);
            long dataStart = pos + element.HeaderLength;
            if (element.Size < 0)
            {
                if (element.Id == IdCluster) break;
                throw Bad("an element of unknown size is not supported here");
            }
            if (dataStart + element.Size > segmentEnd) throw Bad("an element is truncated");
            if (element.Id is IdInfo or IdTags or IdChapters)
            {
                if (element.Size > 1 << 20) throw Bad("the metadata element is too large");
                byte[] data = r.Read(dataStart, (int)element.Size);
                if (element.Id == IdInfo)
                {
                    foreach (var child in Elements(data, 0, data.Length))
                        if (child.Id == 0x7BA9) title = Text(data, child, 512);
                }
                else if (element.Id == IdTags) ReadTags(data, ref uploader, ref description);
                else ReadChapters(data, chapters);
            }
            pos = dataStart + element.Size;
        }
        return new MediaMergeMetadata { Title = title, Uploader = uploader, Description = description, Chapters = chapters };
    }

    private static void ReadTags(byte[] data, ref string? uploader, ref string? description)
    {
        foreach (var tag in Elements(data, 0, data.Length).Where(element => element.Id == 0x7373))
        foreach (var child in Elements(data, tag.Start, tag.Start + tag.Length).Where(element => element.Id == 0x67C8))
        {
            string? name = null, value = null;
            foreach (var field in Elements(data, child.Start, child.Start + child.Length))
            {
                if (field.Id == 0x45A3) name = Text(data, field, 32);
                else if (field.Id == 0x4487) value = Text(data, field, 8192);
            }
            if (name is null || value is null) continue;
            if (name.Equals("ARTIST", StringComparison.OrdinalIgnoreCase)) uploader = value;
            else if (name.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)) description = value;
        }
    }

    private static void ReadChapters(byte[] data, List<MediaMergeChapter> chapters)
    {
        foreach (var edition in Elements(data, 0, data.Length).Where(element => element.Id == 0x45B9))
        foreach (var atom in Elements(data, edition.Start, edition.Start + edition.Length).Where(element => element.Id == 0xB6))
        {
            ulong start = 0, end = 0;
            bool hasStart = false, hasEnd = false;
            string? title = null;
            foreach (var field in Elements(data, atom.Start, atom.Start + atom.Length))
            {
                if (field.Id == 0x91) { start = UInt(data, field.Start, field.Length); hasStart = true; }
                else if (field.Id == 0x92) { end = UInt(data, field.Start, field.Length); hasEnd = true; }
                else if (field.Id == 0x80)
                    foreach (var display in Elements(data, field.Start, field.Start + field.Length))
                        if (display.Id == 0x85) { title = Text(data, display, 256); break; }
            }
            if (!hasStart || title is null) throw Bad("a chapter is incomplete");
            chapters.Add(new MediaMergeChapter(title, FromNanoseconds(start), hasEnd ? FromNanoseconds(end) : null));
            if (chapters.Count > 200) throw Bad("there are too many chapters");
        }
    }

    private static TimeSpan FromNanoseconds(ulong nanoseconds)
    {
        if (nanoseconds > (ulong)TimeSpan.FromDays(365).Ticks * 100) throw Bad("a chapter timestamp is out of range");
        return TimeSpan.FromTicks(checked((long)((nanoseconds + 50) / 100)));
    }

    private static string Text(byte[] data, Element element, int maxLength)
    {
        if (element.Length > maxLength * 4) throw Bad("a metadata string is too long");
        string value = Encoding.UTF8.GetString(data, element.Start, element.Length).TrimEnd('\0');
        if (value.Length > maxLength) throw Bad("a metadata string is too long");
        return value;
    }

    private static long ReadCluster(Reader r, long start, long end, bool unknownSize, Dictionary<ulong, DemuxedTrack> tracks, ref int total)
    {
        long clusterTime = 0;
        long pos = start;
        while (pos < end)
        {
            var e = r.Header(pos);
            if (unknownSize && e.Id is IdCluster or IdCues or IdTags or IdChapters or IdAttachments or IdSeekHead or IdInfo or IdTracks)
                return pos; // a live-style cluster ends where the next top-level element begins
            if (e.Size < 0) throw Bad("a cluster child has unknown size");
            long dataStart = pos + e.HeaderLength;
            long dataEnd = dataStart + e.Size;
            if (dataEnd > end) throw Bad("a cluster child is truncated");
            switch (e.Id)
            {
                case 0xE7: // Timecode
                    if (e.Size > 8) throw Bad("a cluster timecode is malformed");
                    clusterTime = (long)UInt(r.Read(dataStart, (int)e.Size), 0, (int)e.Size);
                    break;
                case 0xA3: // SimpleBlock
                    AddBlock(r, dataStart, e.Size, clusterTime, simple: true, keyHint: false, duration: 0, tracks);
                    total++;
                    break;
                case 0xA0: // BlockGroup
                {
                    long blockPos = -1, blockSize = 0, duration = 0;
                    bool referenced = false;
                    long p = dataStart;
                    while (p < dataEnd)
                    {
                        var c = r.Header(p);
                        if (c.Size < 0) throw Bad("a block group child has unknown size");
                        long cs = p + c.HeaderLength;
                        if (cs + c.Size > dataEnd) throw Bad("a block group is truncated");
                        if (c.Id == 0xA1) { blockPos = cs; blockSize = c.Size; }
                        else if (c.Id == 0xFB) referenced = true;
                        else if (c.Id == 0x9B && c.Size <= 8) duration = (long)UInt(r.Read(cs, (int)c.Size), 0, (int)c.Size);
                        p = cs + c.Size;
                    }
                    if (blockPos < 0) throw Bad("a block group has no block");
                    AddBlock(r, blockPos, blockSize, clusterTime, simple: false, keyHint: !referenced, duration, tracks);
                    total++;
                    break;
                }
            }
            pos = dataEnd;
        }
        return end;
    }

    private static void AddBlock(Reader r, long start, long size, long clusterTime, bool simple, bool keyHint, long duration,
        Dictionary<ulong, DemuxedTrack> tracks)
    {
        if (size < 4) throw Bad("a block is truncated");
        byte[] h = r.Read(start, (int)Math.Min(size, 12));
        int nLen = LeadingLength(h[0], 8) ?? throw Bad("a block track number is malformed");
        if (nLen + 3 > h.Length) throw Bad("a block header is truncated");
        ulong number = (ulong)(h[0] & (0xFF >> nLen));
        for (int i = 1; i < nLen; i++) number = (number << 8) | h[i];
        short rel = BinaryPrimitives.ReadInt16BigEndian(h.AsSpan(nLen));
        byte flags = h[nLen + 2];
        if (!tracks.TryGetValue(number, out var t)) return; // block for a track we do not merge (subtitles, etc.)
        int headerLen = nLen + 3;
        long payload = size - headerLen;
        if (payload <= 0 || payload > 96 * 1024 * 1024) throw Bad("a block has an invalid size");
        t.Samples.Add(new TrackSample
        {
            Offset = start + headerLen,
            Size = (int)payload,
            Dts = clusterTime + rel,
            Duration = (int)Math.Clamp(duration, 0, int.MaxValue),
            Key = t.Kind == TrackKind.Audio || (simple ? (flags & 0x80) != 0 : keyHint),
            Lacing = (byte)((flags >> 1) & 3)
        });
    }

    private static (ulong Number, DemuxedTrack Track)? ParseTrackEntry(byte[] b, int start, int end)
    {
        ulong number = 0, type = 0, defaultDuration = 0, codecDelay = 0, seekPreRoll = 0;
        string codecId = "", language = "eng";
        bool languageSet = false;
        byte[]? priv = null, video = null, audio = null;
        foreach (var c in Elements(b, start, end))
        {
            switch (c.Id)
            {
                case 0xD7: number = UInt(b, c.Start, c.Length); break;
                case 0x83: type = UInt(b, c.Start, c.Length); break;
                case 0x86: codecId = Encoding.ASCII.GetString(b, c.Start, c.Length).TrimEnd('\0'); break;
                case 0x63A2: priv = b.AsSpan(c.Start, c.Length).ToArray(); break;
                case 0x23E383: defaultDuration = UInt(b, c.Start, c.Length); break;
                case 0x56AA: codecDelay = UInt(b, c.Start, c.Length); break;
                case 0x56BB: seekPreRoll = UInt(b, c.Start, c.Length); break;
                case 0x22B59C: language = Encoding.ASCII.GetString(b, c.Start, c.Length).TrimEnd('\0'); languageSet = true; break;
                case 0xE0: video = b.AsSpan(c.Start, c.Length).ToArray(); break;
                case 0xE1: audio = b.AsSpan(c.Start, c.Length).ToArray(); break;
                case 0x6D80:
                    throw new MediaContainerException("The WebM track uses content encoding or encryption, which VESPER does not process.");
            }
        }
        if (type is not (1 or 2) || number == 0) return null;
        var t = new DemuxedTrack
        {
            Kind = type == 1 ? TrackKind.Video : TrackKind.Audio,
            SourceContainer = "webm",
            MatroskaCodecId = codecId,
            CodecPrivate = priv,
            DefaultDurationNs = (long)Math.Min(defaultDuration, long.MaxValue),
            CodecDelayNs = (long)Math.Min(codecDelay, long.MaxValue),
            SeekPreRollNs = (long)Math.Min(seekPreRoll, long.MaxValue),
            Language = languageSet && language.Length is >= 2 and <= 8 ? language : "und",
            MatroskaSettings = type == 1 ? video : audio,
            Codec = codecId switch
            {
                "V_VP9" => "vp9", "V_VP8" => "vp8", "V_AV1" => "av1", "A_OPUS" => "opus", "A_VORBIS" => "vorbis",
                "V_MPEG4/ISO/AVC" => "h264", "V_MPEGH/ISO/HEVC" => "h265", "A_AAC" => "aac", "A_MPEG/L3" => "mp3",
                _ => throw new MediaContainerException($"The WebM codec '{codecId}' is not supported for merging.")
            }
        };
        if (video is not null)
            foreach (var c in Elements(video, 0, video.Length))
            {
                if (c.Id == 0xB0) t.Width = (int)Math.Min(UInt(video, c.Start, c.Length), 65535);
                else if (c.Id == 0xBA) t.Height = (int)Math.Min(UInt(video, c.Start, c.Length), 65535);
            }
        if (audio is not null)
            foreach (var c in Elements(audio, 0, audio.Length))
            {
                if (c.Id == 0xB5) t.SampleRate = (int)Float(audio, c.Start, c.Length);
                else if (c.Id == 0x9F) t.Channels = (int)Math.Min(UInt(audio, c.Start, c.Length), 255);
                else if (c.Id == 0x6264) t.BitDepth = (int)Math.Min(UInt(audio, c.Start, c.Length), 64);
            }
        return (number, t);
    }

    internal readonly record struct Element(uint Id, int Start, int Length);

    /// <summary>Children of an in-memory master element. Unknown sizes are not allowed inside bounded headers.</summary>
    internal static IEnumerable<Element> Elements(byte[] b, int start, int end)
    {
        int p = start;
        while (p < end)
        {
            int idLen = LeadingLength(b[p], 4) ?? throw Bad("an element id is malformed");
            if (p + idLen >= end) throw Bad("an element header is truncated");
            uint id = 0;
            for (int i = 0; i < idLen; i++) id = (id << 8) | b[p + i];
            int q = p + idLen;
            int sizeLen = LeadingLength(b[q], 8) ?? throw Bad("an element size is malformed");
            if (q + sizeLen > end) throw Bad("an element header is truncated");
            long size = b[q] & (0xFF >> sizeLen);
            for (int i = 1; i < sizeLen; i++) size = (size << 8) | b[q + i];
            int dataStart = q + sizeLen;
            if (size < 0 || size > end - dataStart) throw Bad("an element is truncated");
            yield return new Element(id, dataStart, (int)size);
            p = dataStart + (int)size;
        }
    }

    internal static ulong UInt(byte[] b, int start, int length)
    {
        if (length > 8) throw Bad("an integer element is too long");
        ulong v = 0;
        for (int i = 0; i < length; i++) v = (v << 8) | b[start + i];
        return v;
    }

    private static double Float(byte[] b, int start, int length) => length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(start)),
        8 => BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(start)),
        0 => 0,
        _ => throw Bad("a float element has an invalid size")
    };
}
