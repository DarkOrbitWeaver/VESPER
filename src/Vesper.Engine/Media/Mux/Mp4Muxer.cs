using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>Big-endian ISO-BMFF box builder.</summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream _ms = new();
    public long Length => _ms.Length;
    public byte[] ToArray() => _ms.ToArray();
    public int Begin(string type) { int p = (int)_ms.Length; U32(0); FourCc(type); return p; }
    public int BeginFull(string type, byte version, uint flags) { int p = Begin(type); U32(((uint)version << 24) | (flags & 0xFFFFFF)); return p; }
    public void End(int start)
    {
        long size = _ms.Length - start;
        long keep = _ms.Position;
        _ms.Position = start;
        U32(checked((uint)size));
        _ms.Position = keep;
    }
    public void U8(byte v) => _ms.WriteByte(v);
    public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
    public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
    public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _ms.Write(b); }
    public void Bytes(ReadOnlySpan<byte> v) => _ms.Write(v);
    public void Ascii(string v) => _ms.Write(Encoding.ASCII.GetBytes(v));
    public void FourCc(string v)
    {
        if (v.Length != 4) throw new ArgumentException("An MP4 box type must contain four characters.", nameof(v));
        _ms.Write(Encoding.Latin1.GetBytes(v));
    }
    public void Zero(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
    public void Matrix() { U32(0x00010000); U32(0); U32(0); U32(0); U32(0x00010000); U32(0); U32(0); U32(0); U32(0x40000000); }
}

/// <summary>
/// Writes a self-contained, non-fragmented MP4 (moov before mdat) from MP4-sourced tracks. Sample bytes are copied
/// unchanged (remux, never transcode); sample entries are copied verbatim; chunks are interleaved by time so
/// players can stream the file progressively.
/// </summary>
internal static class Mp4Muxer
{
    private const uint MovieTimescale = 1000;

    private sealed record ChapterSample(byte[] Data, uint Duration);
    private sealed record ChapterTrackData(IReadOnlyList<MediaMergeChapter> Chapters, IReadOnlyList<ChapterSample> Samples, long PayloadBytes);

    private sealed class Chunk
    {
        public int Track, First, Count;
        public long Bytes, Offset;
        public double Start;
    }

    public static long Write(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, Stream output,
        CancellationToken ct, MediaMergeMetadata? metadata, MediaThumbnail? thumbnail, out MediaMergeMetadata? writtenMetadata)
    {
        if (inputs.Count is < 1 or > 8) throw new MediaContainerException("An MP4 output needs between one and eight tracks.");
        foreach (var (t, _) in inputs)
            if (t.Mp4SampleEntry is null || t.SourceContainer != "mp4" || t.HasLacing || t.Samples.Count == 0)
                throw new MediaContainerException($"The {t.Codec} track cannot be written to MP4 without conversion.");

        ulong movieDuration = inputs.Max(input => ToMovie(MediaDuration(input.Track) - Math.Max(0, input.Track.EditMediaTime), input.Track.Timescale));
        ChapterTrackData chapterTrack = BuildChapterTrack(metadata?.Chapters ?? [], movieDuration);
        writtenMetadata = metadata is null ? null : metadata with { Chapters = chapterTrack.Chapters };

        var chunks = new List<Chunk>();
        for (int ti = 0; ti < inputs.Count; ti++)
        {
            var t = inputs[ti].Track;
            long first = t.Samples[0].Dts;
            Chunk? cur = null;
            for (int i = 0; i < t.Samples.Count; i++)
            {
                var s = t.Samples[i];
                if (cur is null || s.Dts - t.Samples[cur.First].Dts >= t.Timescale / 2 || cur.Bytes + s.Size > 4 << 20 || cur.Count >= 2048)
                {
                    cur = new Chunk { Track = ti, First = i, Start = (s.Dts - first) / (double)t.Timescale };
                    chunks.Add(cur);
                }
                cur.Count++;
                cur.Bytes += s.Size;
            }
        }
        chunks = chunks.OrderBy(c => c.Start).ThenBy(c => c.Track).ToList();
        long mediaPayload = 0;
        foreach (var c in chunks) { c.Offset = mediaPayload; mediaPayload += c.Bytes; }
        long payload = checked(mediaPayload + chapterTrack.PayloadBytes);

        byte[] ftyp = BuildFtyp(inputs.Select(i => i.Track).ToList());
        int mdatHeader = payload + 8 > uint.MaxValue ? 16 : 8;
        byte[] moov = BuildMoov(inputs, chunks, 0, false, mediaPayload, chapterTrack, writtenMetadata, thumbnail);
        bool co64 = ftyp.Length + moov.Length + mdatHeader + payload > uint.MaxValue;
        if (co64) moov = BuildMoov(inputs, chunks, 0, true, mediaPayload, chapterTrack, writtenMetadata, thumbnail);
        long baseOffset = ftyp.Length + moov.Length + mdatHeader;
        byte[] final = BuildMoov(inputs, chunks, baseOffset, co64, mediaPayload, chapterTrack, writtenMetadata, thumbnail);
        if (final.Length != moov.Length) throw new InvalidOperationException("MP4 header size changed between passes.");

        output.Write(ftyp);
        output.Write(final);
        Span<byte> h = stackalloc byte[16];
        if (mdatHeader == 8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)(payload + 8));
            Encoding.ASCII.GetBytes("mdat", h[4..]);
            output.Write(h[..8]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(h, 1);
            Encoding.ASCII.GetBytes("mdat", h[4..]);
            BinaryPrimitives.WriteUInt64BigEndian(h[8..], (ulong)(payload + 16));
            output.Write(h);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            foreach (var c in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var (t, src) = inputs[c.Track];
                int i = c.First, end = c.First + c.Count;
                while (i < end)
                {
                    long start = t.Samples[i].Offset, len = t.Samples[i].Size;
                    int j = i + 1;
                    while (j < end && t.Samples[j].Offset == start + len && len < 8 << 20) { len += t.Samples[j].Size; j++; }
                    CopyRange(src, start, len, output, buffer);
                    i = j;
                }
            }
            foreach (ChapterSample sample in chapterTrack.Samples) output.Write(sample.Data);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return baseOffset + payload;
    }

    internal static void CopyRange(Stream src, long start, long length, Stream dst, byte[] buffer)
    {
        src.Position = start;
        while (length > 0)
        {
            int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (n <= 0) throw new MediaContainerException("A source track ended before all of its samples were read.");
            dst.Write(buffer, 0, n);
            length -= n;
        }
    }

    private static byte[] BuildFtyp(List<DemuxedTrack> tracks)
    {
        var w = new BoxWriter();
        int p = w.Begin("ftyp");
        w.Ascii("isom"); w.U32(512);
        w.Ascii("isom"); w.Ascii("iso2");
        if (tracks.Any(t => t.Codec == "h264")) w.Ascii("avc1");
        if (tracks.Any(t => t.Codec == "av1")) w.Ascii("av01");
        w.Ascii("mp41");
        w.End(p);
        return w.ToArray();
    }

    private static long MediaDuration(DemuxedTrack t)
    {
        long sum = 0;
        foreach (var s in t.Samples) sum += s.Duration;
        return sum;
    }

    private static ChapterTrackData BuildChapterTrack(IReadOnlyList<MediaMergeChapter> chapters, ulong movieDuration)
    {
        if (chapters.Count == 0 || movieDuration == 0 || movieDuration > uint.MaxValue)
            return new ChapterTrackData([], [], 0);

        var marks = new List<(MediaMergeChapter Chapter, uint Start)>();
        foreach (MediaMergeChapter chapter in chapters)
        {
            ulong start = ToMovie(chapter.Start.Ticks, TimeSpan.TicksPerSecond);
            if (start >= movieDuration || marks.Count > 0 && start <= marks[^1].Start) continue;
            marks.Add((chapter, (uint)start));
        }
        if (marks.Count == 0) return new ChapterTrackData([], [], 0);

        var samples = new List<ChapterSample>();
        var written = new List<MediaMergeChapter>(marks.Count);
        uint cursor = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            (MediaMergeChapter chapter, uint start) = marks[i];
            uint next = i + 1 < marks.Count ? marks[i + 1].Start : (uint)movieDuration;
            ulong requestedEnd = chapter.End is { } end ? ToMovie(end.Ticks, TimeSpan.TicksPerSecond) : next;
            uint endTime = (uint)Math.Min(movieDuration, Math.Min(requestedEnd, next));
            if (endTime <= start) continue;
            if (start > cursor) samples.Add(new ChapterSample(TextSample(""), start - cursor));
            samples.Add(new ChapterSample(TextSample(chapter.Title), endTime - start));
            written.Add(new MediaMergeChapter(chapter.Title, FromMovie(start), FromMovie(endTime)));
            cursor = endTime;
        }
        if (written.Count == 0) return new ChapterTrackData([], [], 0);
        if (cursor < movieDuration) samples.Add(new ChapterSample(TextSample(""), (uint)movieDuration - cursor));
        return new ChapterTrackData(written, samples, samples.Sum(sample => (long)sample.Data.Length));
    }

    private static byte[] TextSample(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > 2048) throw new MediaContainerException("A chapter title is too long for an MP4 chapter track.");
        var data = new byte[utf8.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)utf8.Length);
        utf8.CopyTo(data, 2);
        return data;
    }

    private static TimeSpan FromMovie(uint ticks) => TimeSpan.FromTicks((long)ticks * TimeSpan.TicksPerMillisecond);

    private static byte[] ChapterSampleEntry()
    {
        var w = new BoxWriter();
        int entry = w.Begin("tx3g");
        w.Zero(6); w.U16(1);               // SampleEntry header
        w.U32(0); w.U8(0); w.U8(0);       // Display flags and justification
        w.Zero(4);                          // Background color
        w.Zero(8);                          // Default text box
        w.U16(0); w.U16(0); w.U16(1); w.U8(0); w.U8(12);
        w.U8(0xFF); w.U8(0xFF); w.U8(0xFF); w.U8(0xFF); // Default style
        int fonts = w.Begin("ftab");
        w.U16(1); w.U16(1); w.U8(5); w.Ascii("Serif");
        w.End(fonts);
        w.End(entry);
        return w.ToArray();
    }

    private static byte[] UserData(MediaMergeMetadata? metadata, MediaThumbnail? thumbnail)
    {
        var w = new BoxWriter();
        int udta = w.Begin("udta");
        if (metadata?.Title is { Length: > 0 } title) Write3GppText(w, "titl", title);
        if (metadata?.Description is { Length: > 0 } description) Write3GppText(w, "dscp", description);
        if (metadata?.Uploader is { Length: > 0 } uploader) Write3GppText(w, "auth", uploader);
        if (metadata is not null || thumbnail is not null) WriteAppleMetadata(w, metadata, thumbnail);
        w.End(udta);
        return w.ToArray();
    }

    private static void WriteAppleMetadata(BoxWriter w, MediaMergeMetadata? metadata, MediaThumbnail? thumbnail)
    {
        int meta = w.BeginFull("meta", 0, 0);
        int handler = w.BeginFull("hdlr", 0, 0);
        w.U32(0);
        w.Ascii("mdir");
        w.Zero(12);
        w.Ascii("appl");
        w.U8(0);
        w.End(handler);

        int list = w.Begin("ilst");
        if (metadata?.Title is { Length: > 0 } title) WriteTextTag(w, "\u00A9nam", title);
        if (metadata?.Artist is { Length: > 0 } artist) WriteTextTag(w, "\u00A9ART", artist);
        if (metadata?.Album is { Length: > 0 } album) WriteTextTag(w, "\u00A9alb", album);
        if (metadata?.AlbumArtist is { Length: > 0 } albumArtist) WriteTextTag(w, "aART", albumArtist);
        if (metadata?.ReleaseDate is { Length: > 0 } releaseDate) WriteTextTag(w, "\u00A9day", releaseDate);
        if (metadata?.TrackNumber is { } track) WriteNumberTag(w, "trkn", track);
        if (metadata?.DiscNumber is { } disc) WriteNumberTag(w, "disk", disc);
        if (thumbnail is not null) WriteCoverTag(w, thumbnail);
        w.End(list);
        w.End(meta);
    }

    private static void WriteTextTag(BoxWriter w, string type, string value)
    {
        int tag = w.Begin(type);
        int data = w.Begin("data");
        w.U32(1); // UTF-8
        w.U32(0);
        w.Bytes(Encoding.UTF8.GetBytes(value));
        w.End(data);
        w.End(tag);
    }

    private static void WriteNumberTag(BoxWriter w, string type, int value)
    {
        int tag = w.Begin(type);
        int data = w.Begin("data");
        w.U32(0);
        w.U32(0);
        w.U16(0);
        w.U16((ushort)value);
        w.U32(0);
        w.End(data);
        w.End(tag);
    }

    private static void WriteCoverTag(BoxWriter w, MediaThumbnail thumbnail)
    {
        int tag = w.Begin("covr");
        int data = w.Begin("data");
        w.U32(thumbnail.MediaType == "image/jpeg" ? 13u : 14u);
        w.U32(0);
        w.Bytes(thumbnail.Data);
        w.End(data);
        w.End(tag);
    }

    private static void Write3GppText(BoxWriter w, string type, string value)
    {
        int box = w.Begin(type);
        w.U32(0);                         // FullBox version and flags
        w.U16(PackLanguage("und"));
        w.Bytes(Encoding.UTF8.GetBytes(value));
        w.U8(0);
        w.End(box);
    }

    private static ulong ToMovie(long ticks, uint timescale) => ticks <= 0 ? 0 : (ulong)(((Int128)ticks * MovieTimescale + timescale / 2) / timescale);

    private static ulong ToMovie(long ticks, long timescale) => ticks <= 0 ? 0 : (ulong)(((Int128)ticks * MovieTimescale + timescale / 2) / timescale);

    private static byte[] BuildMoov(IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs, List<Chunk> chunks,
        long baseOffset, bool co64, long mediaPayload, ChapterTrackData chapterTrack, MediaMergeMetadata? metadata,
        MediaThumbnail? thumbnail)
    {
        var w = new BoxWriter();
        int moov = w.Begin("moov");
        ulong movieDuration = 0;
        var presentation = new ulong[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            var t = inputs[i].Track;
            presentation[i] = ToMovie(MediaDuration(t) - Math.Max(0, t.EditMediaTime), t.Timescale);
            movieDuration = Math.Max(movieDuration, presentation[i]);
        }

        bool v1 = movieDuration > uint.MaxValue;
        int mvhd = w.BeginFull("mvhd", (byte)(v1 ? 1 : 0), 0);
        if (v1) { w.U64(0); w.U64(0); w.U32(MovieTimescale); w.U64(movieDuration); }
        else { w.U32(0); w.U32(0); w.U32(MovieTimescale); w.U32((uint)movieDuration); }
        w.U32(0x00010000); w.U16(0x0100); w.Zero(10); w.Matrix(); w.Zero(24);
        w.U32((uint)inputs.Count + (chapterTrack.Samples.Count > 0 ? 2u : 1u));
        w.End(mvhd);

        for (int ti = 0; ti < inputs.Count; ti++)
        {
            var t = inputs[ti].Track;
            bool video = t.Kind == TrackKind.Video;
            long mediaDuration = MediaDuration(t);
            int trak = w.Begin("trak");

            bool tv1 = presentation[ti] > uint.MaxValue;
            int tkhd = w.BeginFull("tkhd", (byte)(tv1 ? 1 : 0), 0x000003);
            if (tv1) { w.U64(0); w.U64(0); w.U32((uint)ti + 1); w.U32(0); w.U64(presentation[ti]); }
            else { w.U32(0); w.U32(0); w.U32((uint)ti + 1); w.U32(0); w.U32((uint)presentation[ti]); }
            w.Zero(8); w.U16(0); w.U16((ushort)(video ? 0 : 1)); w.U16((ushort)(video ? 0 : 0x0100)); w.U16(0);
            w.Matrix();
            w.U32(video ? (uint)t.Width << 16 : 0); w.U32(video ? (uint)t.Height << 16 : 0);
            w.End(tkhd);

            if (ti == 0 && chapterTrack.Samples.Count > 0)
            {
                int tref = w.Begin("tref");
                int chap = w.Begin("chap"); w.U32((uint)inputs.Count + 1); w.End(chap);
                w.End(tref);
            }

            if (t.EditMediaTime > 0)
            {
                int edts = w.Begin("edts");
                bool ev1 = presentation[ti] > uint.MaxValue || t.EditMediaTime > int.MaxValue;
                int elst = w.BeginFull("elst", (byte)(ev1 ? 1 : 0), 0);
                w.U32(1);
                if (ev1) { w.U64(presentation[ti]); w.U64((ulong)t.EditMediaTime); }
                else { w.U32((uint)presentation[ti]); w.U32((uint)t.EditMediaTime); }
                w.U16(1); w.U16(0);
                w.End(elst);
                w.End(edts);
            }

            int mdia = w.Begin("mdia");
            bool mv1 = mediaDuration > uint.MaxValue;
            int mdhd = w.BeginFull("mdhd", (byte)(mv1 ? 1 : 0), 0);
            if (mv1) { w.U64(0); w.U64(0); w.U32(t.Timescale); w.U64((ulong)mediaDuration); }
            else { w.U32(0); w.U32(0); w.U32(t.Timescale); w.U32((uint)mediaDuration); }
            w.U16(PackLanguage(t.Language)); w.U16(0);
            w.End(mdhd);

            int hdlr = w.BeginFull("hdlr", 0, 0);
            w.U32(0); w.Ascii(video ? "vide" : "soun"); w.Zero(12); w.Ascii(video ? "VideoHandler" : "SoundHandler"); w.U8(0);
            w.End(hdlr);

            int minf = w.Begin("minf");
            if (video) { int vmhd = w.BeginFull("vmhd", 0, 1); w.Zero(8); w.End(vmhd); }
            else { int smhd = w.BeginFull("smhd", 0, 0); w.Zero(4); w.End(smhd); }
            int dinf = w.Begin("dinf");
            int dref = w.BeginFull("dref", 0, 0); w.U32(1);
            int url = w.BeginFull("url ", 0, 1); w.End(url);
            w.End(dref); w.End(dinf);

            int stbl = w.Begin("stbl");
            int stsd = w.BeginFull("stsd", 0, 0); w.U32(1); w.Bytes(t.Mp4SampleEntry); w.End(stsd);
            WriteTables(w, t, chunks.Where(c => c.Track == ti).ToList(), baseOffset, co64);
            w.End(stbl);
            w.End(minf);
            w.End(mdia);
            w.End(trak);
        }
        if (chapterTrack.Samples.Count > 0)
            WriteChapterTrack(w, chapterTrack, (uint)inputs.Count + 1, movieDuration, baseOffset + mediaPayload, co64);
        if (metadata is not null || thumbnail is not null) w.Bytes(UserData(metadata, thumbnail));
        w.End(moov);
        return w.ToArray();
    }

    private static void WriteChapterTrack(BoxWriter w, ChapterTrackData data, uint trackId, ulong duration,
        long chunkOffset, bool co64)
    {
        int trak = w.Begin("trak");
        int tkhd = w.BeginFull("tkhd", 0, 0x000002); // Disabled marker track, linked from the primary track.
        w.U32(0); w.U32(0); w.U32(trackId); w.U32(0); w.U32((uint)duration);
        w.Zero(8); w.U16(0); w.U16(0); w.U16(0); w.U16(0); w.Matrix(); w.U32(0); w.U32(0);
        w.End(tkhd);

        int mdia = w.Begin("mdia");
        int mdhd = w.BeginFull("mdhd", 0, 0);
        w.U32(0); w.U32(0); w.U32(MovieTimescale); w.U32((uint)duration);
        w.U16(PackLanguage("und")); w.U16(0);
        w.End(mdhd);
        int hdlr = w.BeginFull("hdlr", 0, 0);
        w.U32(0); w.Ascii("text"); w.Zero(12); w.Ascii("ChapterHandler"); w.U8(0);
        w.End(hdlr);

        int minf = w.Begin("minf");
        int nmhd = w.BeginFull("nmhd", 0, 0); w.End(nmhd);
        int dinf = w.Begin("dinf");
        int dref = w.BeginFull("dref", 0, 0); w.U32(1);
        int url = w.BeginFull("url ", 0, 1); w.End(url);
        w.End(dref); w.End(dinf);

        int stbl = w.Begin("stbl");
        int stsd = w.BeginFull("stsd", 0, 0); w.U32(1); w.Bytes(ChapterSampleEntry()); w.End(stsd);
        var runs = new List<(uint Count, uint Duration)>();
        foreach (ChapterSample sample in data.Samples)
        {
            if (runs.Count > 0 && runs[^1].Duration == sample.Duration)
                runs[^1] = (runs[^1].Count + 1, sample.Duration);
            else runs.Add((1, sample.Duration));
        }
        int stts = w.BeginFull("stts", 0, 0); w.U32((uint)runs.Count);
        foreach (var run in runs) { w.U32(run.Count); w.U32(run.Duration); }
        w.End(stts);
        int stsc = w.BeginFull("stsc", 0, 0); w.U32(1); w.U32(1); w.U32((uint)data.Samples.Count); w.U32(1); w.End(stsc);
        int stsz = w.BeginFull("stsz", 0, 0); w.U32(0); w.U32((uint)data.Samples.Count);
        foreach (ChapterSample sample in data.Samples) w.U32((uint)sample.Data.Length);
        w.End(stsz);
        int offsets = w.BeginFull(co64 ? "co64" : "stco", 0, 0); w.U32(1);
        if (co64) w.U64((ulong)chunkOffset); else w.U32((uint)chunkOffset);
        w.End(offsets);
        w.End(stbl); w.End(minf); w.End(mdia); w.End(trak);
    }

    private static void WriteTables(BoxWriter w, DemuxedTrack t, List<Chunk> chunks, long baseOffset, bool co64)
    {
        var samples = t.Samples;
        // stts
        var stts = new List<(uint Count, uint Delta)>();
        foreach (var s in samples)
        {
            uint d = (uint)Math.Max(0, s.Duration);
            if (stts.Count > 0 && stts[^1].Delta == d) stts[^1] = (stts[^1].Count + 1, d);
            else stts.Add((1, d));
        }
        int box = w.BeginFull("stts", 0, 0); w.U32((uint)stts.Count);
        foreach (var e in stts) { w.U32(e.Count); w.U32(e.Delta); }
        w.End(box);

        // ctts (only when composition offsets exist; version 1 permits negative offsets)
        if (samples.Exists(s => s.CtsOffset != 0))
        {
            bool negative = samples.Exists(s => s.CtsOffset < 0);
            var ctts = new List<(uint Count, int Offset)>();
            foreach (var s in samples)
            {
                if (ctts.Count > 0 && ctts[^1].Offset == s.CtsOffset) ctts[^1] = (ctts[^1].Count + 1, s.CtsOffset);
                else ctts.Add((1, s.CtsOffset));
            }
            box = w.BeginFull("ctts", (byte)(negative ? 1 : 0), 0); w.U32((uint)ctts.Count);
            foreach (var e in ctts) { w.U32(e.Count); w.U32((uint)e.Offset); }
            w.End(box);
        }

        // stss (omitted when every sample is a sync sample)
        if (t.Kind == TrackKind.Video && samples.Exists(s => !s.Key))
        {
            var keys = new List<uint>();
            for (int i = 0; i < samples.Count; i++) if (samples[i].Key) keys.Add((uint)i + 1);
            box = w.BeginFull("stss", 0, 0); w.U32((uint)keys.Count);
            foreach (var k in keys) w.U32(k);
            w.End(box);
        }

        // stsc
        var stsc = new List<(uint First, uint Per)>();
        for (int i = 0; i < chunks.Count; i++)
            if (stsc.Count == 0 || stsc[^1].Per != (uint)chunks[i].Count) stsc.Add(((uint)i + 1, (uint)chunks[i].Count));
        box = w.BeginFull("stsc", 0, 0); w.U32((uint)stsc.Count);
        foreach (var e in stsc) { w.U32(e.First); w.U32(e.Per); w.U32(1); }
        w.End(box);

        // stsz
        bool uniform = samples.TrueForAll(s => s.Size == samples[0].Size);
        box = w.BeginFull("stsz", 0, 0);
        w.U32(uniform ? (uint)samples[0].Size : 0); w.U32((uint)samples.Count);
        if (!uniform) foreach (var s in samples) w.U32((uint)s.Size);
        w.End(box);

        // stco / co64
        box = w.BeginFull(co64 ? "co64" : "stco", 0, 0); w.U32((uint)chunks.Count);
        foreach (var c in chunks)
        {
            long off = baseOffset + c.Offset;
            if (co64) w.U64((ulong)off); else w.U32((uint)off);
        }
        w.End(box);
    }

    private static ushort PackLanguage(string lang)
    {
        if (lang.Length != 3 || !lang.All(c => c is >= 'a' and <= 'z')) lang = "und";
        return (ushort)(((lang[0] - 0x60) << 10) | ((lang[1] - 0x60) << 5) | (lang[2] - 0x60));
    }
}
