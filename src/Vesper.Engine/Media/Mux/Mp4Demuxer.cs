using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

internal enum TrackKind { Video, Audio }

/// <summary>One coded frame (MP4 sample) or one Matroska block, located in a source stream. Times use the track timescale.</summary>
internal struct TrackSample
{
    /// <summary>Absolute position of the payload inside the source stream.</summary>
    public long Offset;
    public int Size;
    /// <summary>MP4: decode time. Matroska: block (presentation) time.</summary>
    public long Dts;
    /// <summary>MP4 composition offset (PTS - DTS). Always 0 for Matroska sources.</summary>
    public int CtsOffset;
    public int Duration;
    public bool Key;
    /// <summary>Matroska lacing bits (0 = none). Laced payloads are only ever copied verbatim to Matroska output.</summary>
    public byte Lacing;
}

/// <summary>A single elementary track discovered by a demuxer. Payload bytes stay in the source; only offsets are held.</summary>
internal sealed class DemuxedTrack
{
    public TrackKind Kind;
    /// <summary>Normalised codec family: h264, h265, av1, vp9, vp8, aac, opus, vorbis, mp3, flac, ac3, eac3.</summary>
    public string Codec = "";
    /// <summary>mp4 or webm (Matroska family).</summary>
    public string SourceContainer = "";
    public uint Timescale;
    /// <summary>Verbatim MP4 sample entry (stsd child) for MP4 sources; reused byte-for-byte when writing MP4.</summary>
    public byte[]? Mp4SampleEntry;
    /// <summary>Decoder configuration in Matroska form (avcC/hvcC/av1C payload, AudioSpecificConfig, OpusHead, Xiph headers).</summary>
    public byte[]? CodecPrivate;
    public string? MatroskaCodecId;
    /// <summary>Raw payload of the Matroska Video/Audio master element (keeps Colour/HDR, projection, etc.).</summary>
    public byte[]? MatroskaSettings;
    public int Width, Height, Channels, SampleRate, BitDepth;
    public long CodecDelayNs, SeekPreRollNs, DefaultDurationNs;
    public string Language = "und";
    /// <summary>MP4 edit-list media_time (track timescale), 0 when there is none.</summary>
    public long EditMediaTime;
    public List<TrackSample> Samples = new();

    public bool HasLacing => Samples.Exists(s => s.Lacing != 0);
    public long PayloadBytes { get { long t = 0; foreach (var s in Samples) t += s.Size; return t; } }
    public long DurationTicks => Samples.Count == 0 ? 0 : Samples[^1].Dts + Math.Max(0, Samples[^1].Duration) - Samples[0].Dts;
    public double DurationSeconds => Timescale == 0 ? 0 : DurationTicks / (double)Timescale;
    public override string ToString() => $"{Kind} {Codec} ({SourceContainer}) {Samples.Count} samples {DurationSeconds:0.###}s";
}

/// <summary>Read-only, seekable window over part of another stream (for example one track region inside a partial file).</summary>
internal sealed class RegionStream(Stream inner, long start, long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        long remaining = length - _position;
        if (remaining <= 0) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
        inner.Position = start + _position;
        int n = inner.Read(buffer);
        _position += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class MuxIo
{
    public static void ReadExact(Stream s, long position, Span<byte> buffer)
    {
        s.Position = position;
        int done = 0;
        while (done < buffer.Length)
        {
            int n = s.Read(buffer[done..]);
            if (n == 0) throw new MediaContainerException("The media file ended unexpectedly.");
            done += n;
        }
    }

    public static byte[] Load(Stream s, long position, long length, long limit, string what)
    {
        if (length < 0 || length > limit) throw new MediaContainerException($"The {what} is larger than the supported limit.");
        var data = new byte[length];
        ReadExact(s, position, data);
        return data;
    }

    public static string FourCc(ReadOnlySpan<byte> b) =>
        string.Create(4, b[..4].ToArray(), (dst, src) =>
        {
            for (int i = 0; i < 4; i++) dst[i] = src[i] == 0xA9 ? '\u00A9' : src[i] is >= 32 and < 127 ? (char)src[i] : '?';
        });
}

/// <summary>
/// Bounded ISO-BMFF reader for progressive MP4 (stbl sample tables) and fragmented MP4 (moof/trun). Media payloads
/// are never loaded; moov/moof boxes are bounded in size. Every sample range is checked against the file length.
/// Encrypted sample entries (encv/enca) are rejected rather than passed through.
/// </summary>
internal static class Mp4Demuxer
{
    private const long MaxMoovBytes = 96L * 1024 * 1024;
    private const long MaxMoofBytes = 32L * 1024 * 1024;
    private const int MaxSampleBytes = 96 * 1024 * 1024;
    private const int MaxSamples = 20_000_000;

    private sealed class Defaults { public uint Duration, Size, Flags; }
    private sealed class State
    {
        public required DemuxedTrack Track;
        public Defaults Trex = new();
        public long NextDts;
    }

    public static List<DemuxedTrack> Read(Stream s, long length, CancellationToken ct)
    {
        var states = new Dictionary<uint, State>();
        var order = new List<uint>();
        Span<byte> hdr = stackalloc byte[16];
        long pos = 0;
        bool sawMoov = false;
        int total = 0;
        while (pos < length)
        {
            ct.ThrowIfCancellationRequested();
            if (length - pos < 8) throw Bad("trailing bytes follow the last box");
            MuxIo.ReadExact(s, pos, hdr[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = MuxIo.FourCc(hdr[4..8]);
            int headerLen = 8;
            if (size == 1)
            {
                if (length - pos < 16) throw Bad("a 64-bit box header is truncated");
                MuxIo.ReadExact(s, pos + 8, hdr.Slice(8, 8));
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                if (large > long.MaxValue) throw Bad("a box size is out of range");
                size = (long)large;
                headerLen = 16;
            }
            else if (size == 0) size = length - pos;
            if (size < headerLen || size > length - pos) throw Bad($"box '{type}' has an invalid size");

            if (type == "moov")
            {
                if (sawMoov) throw Bad("the file has two movie headers");
                sawMoov = true;
                byte[] moov = MuxIo.Load(s, pos + headerLen, size - headerLen, MaxMoovBytes, "movie header");
                ParseMoov(moov, states, order);
                foreach (var st in states.Values) total += st.Track.Samples.Count;
            }
            else if (type == "moof")
            {
                if (!sawMoov) throw Bad("a movie fragment precedes the movie header");
                byte[] moof = MuxIo.Load(s, pos + headerLen, size - headerLen, MaxMoofBytes, "movie fragment");
                total += ParseMoof(moof, pos, states);
                if (total > MaxSamples) throw Bad("the file declares too many samples");
            }
            pos += size;
        }
        if (!sawMoov) throw Bad("no movie header (moov) was found");

        var result = new List<DemuxedTrack>();
        foreach (uint id in order)
        {
            var t = states[id].Track;
            if (t.Samples.Count == 0) continue;
            foreach (var smp in t.Samples)
                if (smp.Size <= 0 || smp.Size > MaxSampleBytes || smp.Offset < 0 || smp.Offset > length - smp.Size)
                    throw Bad($"a {t.Kind.ToString().ToLowerInvariant()} sample points outside the file");
            result.Add(t);
        }
        if (result.Count == 0) throw Bad("no audio or video samples were found");
        return result;
    }

    internal static MediaMergeMetadata ReadMetadata(Stream s, long length)
    {
        byte[] moov = ReadMovieHeader(s, length);

        string? title = null, uploader = null, description = null;
        string? artist = null, album = null, albumArtist = null, releaseDate = null;
        int? trackNumber = null, discNumber = null;
        uint chapterTrackId = 0;
        var tracks = new List<(uint Id, string Handler, int Start, int End)>();
        foreach (var child in Children(moov, 0, moov.Length))
        {
            if (child.Type == "udta")
            {
                foreach (var value in Children(moov, child.Start, child.End))
                {
                    if (value.Type == "titl") title = Read3GppText(moov, value, 512);
                    else if (value.Type == "dscp") description = Read3GppText(moov, value, 8192);
                    else if (value.Type == "auth") uploader = Read3GppText(moov, value, 256);
                    else if (value.Type == "meta")
                    {
                        MediaMergeMetadata apple = ReadAppleMetadata(moov, value);
                        title ??= apple.Title;
                        artist = apple.Artist;
                        album = apple.Album;
                        albumArtist = apple.AlbumArtist;
                        trackNumber = apple.TrackNumber;
                        discNumber = apple.DiscNumber;
                        releaseDate = apple.ReleaseDate;
                    }
                }
            }
            else if (child.Type == "trak")
            {
                var tkhd = Find(moov, child.Start, child.End, "tkhd") ?? throw Bad("a track has no header");
                int version = moov[tkhd.Start];
                uint id = U32(moov, tkhd.Start + (version == 1 ? 20 : 12), tkhd.End);
                var mdia = Find(moov, child.Start, child.End, "mdia") ?? throw Bad("a track has no media box");
                var hdlr = Find(moov, mdia.Start, mdia.End, "hdlr") ?? throw Bad("a track has no handler");
                string handler = MuxIo.FourCc(moov.AsSpan(hdlr.Start + 8, 4));
                tracks.Add((id, handler, child.Start, child.End));
                if ((handler is "vide" or "soun") && Find(moov, child.Start, child.End, "tref") is { } tref
                    && Find(moov, tref.Start, tref.End, "chap") is { } chap)
                {
                    if (chap.End - chap.Start < 4) throw Bad("a chapter track reference is truncated");
                    chapterTrackId = U32(moov, chap.Start, chap.End);
                }
            }
        }

        if (chapterTrackId != 0 && !tracks.Any(track => track.Id == chapterTrackId && track.Handler == "text"))
            throw Bad("a chapter track reference does not name a text track");
        IReadOnlyList<MediaMergeChapter> chapters = chapterTrackId == 0 ? [] : ReadChapterTrack(s, length, moov,
            tracks.First(track => track.Id == chapterTrackId && track.Handler == "text"));
        return new MediaMergeMetadata
        {
            Title = title, Uploader = uploader, Description = description, Artist = artist, Album = album,
            AlbumArtist = albumArtist, TrackNumber = trackNumber, DiscNumber = discNumber,
            ReleaseDate = releaseDate, Chapters = chapters
        };
    }

    internal static MediaThumbnail? ReadThumbnail(Stream s, long length)
    {
        byte[] moov = ReadMovieHeader(s, length);
        MediaThumbnail? found = null;
        foreach (var udta in Children(moov, 0, moov.Length).Where(child => child.Type == "udta"))
        foreach (var meta in Children(moov, udta.Start, udta.End).Where(child => child.Type == "meta"))
        {
            if (meta.End - meta.Start < 4) throw Bad("an MP4 metadata box is truncated");
            var list = Find(moov, meta.Start + 4, meta.End, "ilst");
            if (list is null) continue;
            foreach (var cover in Children(moov, list.Value.Start, list.Value.End).Where(child => child.Type == "covr"))
            foreach (var data in Children(moov, cover.Start, cover.End).Where(child => child.Type == "data"))
            {
                if (data.End - data.Start < 8 || data.End - data.Start - 8 > MediaThumbnail.MaxBytes)
                    throw Bad("an MP4 cover image has an invalid size");
                uint kind = U32(moov, data.Start, data.End);
                string mediaType = kind switch { 13 => "image/jpeg", 14 => "image/png", _ => "" };
                if (mediaType.Length == 0) continue;
                byte[] bytes = moov.AsSpan(data.Start + 8, data.End - data.Start - 8).ToArray();
                MediaThumbnail? thumbnail = MediaThumbnail.TryCreate(bytes);
                if (thumbnail is null || thumbnail.MediaType != mediaType)
                    throw Bad("an MP4 cover image is invalid");
                if (found is not null) throw Bad("the MP4 output contains more than one cover image");
                found = thumbnail;
            }
        }
        return found;
    }

    private static byte[] ReadMovieHeader(Stream s, long length)
    {
        long pos = 0;
        Span<byte> header = stackalloc byte[16];
        while (pos < length)
        {
            if (length - pos < 8) throw Bad("a top-level box header is truncated");
            MuxIo.ReadExact(s, pos, header[..8]);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = MuxIo.FourCc(header[4..8]);
            int headerLength = 8;
            if (size == 1)
            {
                if (length - pos < 16) throw Bad("a 64-bit top-level box header is truncated");
                MuxIo.ReadExact(s, pos + 8, header[8..16]);
                size = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerLength = 16;
            }
            else if (size == 0) size = (ulong)(length - pos);
            if (size < (ulong)headerLength || size > (ulong)(length - pos)) throw Bad($"box '{type}' has an invalid size");
            if (type == "moov") return MuxIo.Load(s, pos + headerLength, (long)size - headerLength, MaxMoovBytes, "movie header");
            pos += (long)size;
        }
        throw Bad("no movie header was found");
    }

    private static MediaMergeMetadata ReadAppleMetadata(byte[] moov, (string Type, int Start, int End) meta)
    {
        if (meta.End - meta.Start < 4) throw Bad("an MP4 metadata box is truncated");
        var list = Find(moov, meta.Start + 4, meta.End, "ilst");
        if (list is null) return new MediaMergeMetadata();
        string? title = null, artist = null, album = null, albumArtist = null, releaseDate = null;
        int? track = null, disc = null;
        foreach (var item in Children(moov, list.Value.Start, list.Value.End))
        {
            var data = Find(moov, item.Start, item.End, "data");
            if (data is null || data.Value.End - data.Value.Start < 8) continue;
            uint kind = U32(moov, data.Value.Start, data.Value.End);
            if (item.Type is "\u00A9nam" or "\u00A9ART" or "\u00A9alb" or "aART" or "\u00A9day")
            {
                if (kind != 1) continue;
                string value = ReadAppleText(moov, data.Value, item.Type switch
                {
                    "\u00A9nam" => 512,
                    "\u00A9alb" => 512,
                    "\u00A9day" => 10,
                    _ => 256
                });
                if (item.Type == "\u00A9nam") title = value;
                else if (item.Type == "\u00A9ART") artist = value;
                else if (item.Type == "\u00A9alb") album = value;
                else if (item.Type == "aART") albumArtist = value;
                else releaseDate = value;
            }
            else if (item.Type is "trkn" or "disk" && kind == 0 && data.Value.End - data.Value.Start >= 16)
            {
                ushort value = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(data.Value.Start + 10, 2));
                if (value is 0 or > 999) throw Bad("an MP4 track or disc number is invalid");
                if (item.Type == "trkn") track = value; else disc = value;
            }
        }
        return new MediaMergeMetadata
        {
            Title = title, Artist = artist, Album = album, AlbumArtist = albumArtist,
            TrackNumber = track, DiscNumber = disc, ReleaseDate = releaseDate
        };
    }

    private static string ReadAppleText(byte[] data, (string Type, int Start, int End) box, int maxLength)
    {
        int start = box.Start + 8, length = box.End - start;
        if (length > maxLength * 4) throw Bad("an MP4 metadata string is too long");
        try
        {
            string value = new UTF8Encoding(false, true).GetString(data, start, length);
            if (value.Length > maxLength) throw Bad("an MP4 metadata string is too long");
            return value;
        }
        catch (DecoderFallbackException) { throw Bad("an MP4 metadata string is not valid UTF-8"); }
    }

    private static string Read3GppText(byte[] moov, (string Type, int Start, int End) box, int maxLength)
    {
        if (box.End - box.Start < 6 || U32(moov, box.Start, box.End) != 0) throw Bad("a 3GPP text metadata box is malformed");
        int start = box.Start + 6, end = box.End;
        if (end > start && moov[end - 1] == 0) end--;
        if (end - start > maxLength * 4) throw Bad("a metadata string is too long");
        try
        {
            string value = new UTF8Encoding(false, true).GetString(moov, start, end - start);
            if (value.Length > maxLength) throw Bad("a metadata string is too long");
            return value;
        }
        catch (DecoderFallbackException) { throw Bad("a metadata string is not valid UTF-8"); }
    }

    private static IReadOnlyList<MediaMergeChapter> ReadChapterTrack(Stream s, long length, byte[] moov,
        (uint Id, string Handler, int Start, int End) chapterTrack)
    {
        if (chapterTrack.Handler != "text") throw Bad("a chapter track is not a text track");
        var mdia = Find(moov, chapterTrack.Start, chapterTrack.End, "mdia") ?? throw Bad("a chapter track has no media box");
        var mdhd = Find(moov, mdia.Start, mdia.End, "mdhd") ?? throw Bad("a chapter track has no media header");
        uint timescale = U32(moov, mdhd.Start + (moov[mdhd.Start] == 1 ? 20 : 12), mdhd.End);
        if (timescale == 0) throw Bad("a chapter track timescale is zero");
        var minf = Find(moov, mdia.Start, mdia.End, "minf") ?? throw Bad("a chapter track has no media information");
        var stbl = Find(moov, minf.Start, minf.End, "stbl") ?? throw Bad("a chapter track has no sample table");
        var stsd = Find(moov, stbl.Start, stbl.End, "stsd") ?? throw Bad("a chapter track has no sample description");
        if (U32(moov, stsd.Start + 4, stsd.End) != 1) throw Bad("a chapter track has an invalid sample description count");
        int entryStart = stsd.Start + 8;
        uint entrySize = U32(moov, entryStart, stsd.End);
        if (entrySize < 16 || entryStart + entrySize > stsd.End || MuxIo.FourCc(moov.AsSpan(entryStart + 4, 4)) != "tx3g")
            throw Bad("a chapter track does not use timed text samples");

        var stsz = Find(moov, stbl.Start, stbl.End, "stsz") ?? throw Bad("a chapter track has no sample sizes");
        uint fixedSize = U32(moov, stsz.Start + 4, stsz.End), count = U32(moov, stsz.Start + 8, stsz.End);
        if (count is 0 or > 402) throw Bad("a chapter track has an invalid sample count");
        var sizes = new int[count];
        for (int i = 0; i < sizes.Length; i++)
        {
            uint size = fixedSize != 0 ? fixedSize : U32(moov, stsz.Start + 12 + i * 4, stsz.End);
            if (size is < 2 or > 2050) throw Bad("a chapter text sample has an invalid size");
            sizes[i] = (int)size;
        }

        var stts = Find(moov, stbl.Start, stbl.End, "stts") ?? throw Bad("a chapter track has no sample times");
        uint entries = U32(moov, stts.Start + 4, stts.End);
        var durations = new uint[count];
        int sampleIndex = 0, timePos = stts.Start + 8;
        for (uint entry = 0; entry < entries; entry++, timePos += 8)
        {
            uint runCount = U32(moov, timePos, stts.End), runDuration = U32(moov, timePos + 4, stts.End);
            for (uint i = 0; i < runCount; i++)
            {
                if (sampleIndex >= durations.Length) throw Bad("a chapter time table has too many samples");
                durations[sampleIndex++] = runDuration;
            }
        }
        if (sampleIndex != durations.Length) throw Bad("a chapter time table does not cover every sample");

        var stsc = Find(moov, stbl.Start, stbl.End, "stsc") ?? throw Bad("a chapter track has no sample-to-chunk table");
        if (U32(moov, stsc.Start + 4, stsc.End) != 1 || U32(moov, stsc.Start + 8, stsc.End) != 1
            || U32(moov, stsc.Start + 12, stsc.End) != count || U32(moov, stsc.Start + 16, stsc.End) != 1)
            throw Bad("a chapter track uses an unsupported chunk layout");
        long chunkOffset;
        if (Find(moov, stbl.Start, stbl.End, "stco") is { } stco)
        {
            if (U32(moov, stco.Start + 4, stco.End) != 1) throw Bad("a chapter track has an invalid chunk offset count");
            chunkOffset = U32(moov, stco.Start + 8, stco.End);
        }
        else if (Find(moov, stbl.Start, stbl.End, "co64") is { } co64)
        {
            if (U32(moov, co64.Start + 4, co64.End) != 1) throw Bad("a chapter track has an invalid chunk offset count");
            ulong offset = U64(moov, co64.Start + 8, co64.End);
            if (offset > long.MaxValue) throw Bad("a chapter chunk offset is out of range");
            chunkOffset = (long)offset;
        }
        else throw Bad("a chapter track has no chunk offset");

        var chapters = new List<MediaMergeChapter>();
        long payloadOffset = chunkOffset;
        ulong timestamp = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            if (payloadOffset < 0 || payloadOffset > length - sizes[i]) throw Bad("a chapter sample points outside the file");
            byte[] sample = MuxIo.Load(s, payloadOffset, sizes[i], 2050, "chapter text sample");
            ushort textLength = U16(sample, 0, sample.Length);
            if (textLength > sample.Length - 2) throw Bad("a chapter text length exceeds its sample");
            if (textLength > 0)
            {
                string title;
                try { title = new UTF8Encoding(false, true).GetString(sample, 2, textLength); }
                catch (DecoderFallbackException) { throw Bad("a chapter title is not valid UTF-8"); }
                if (title.Length > 256) throw Bad("a chapter title is too long");
                long startTicks = checked((long)((Int128)timestamp * TimeSpan.TicksPerSecond / timescale));
                long endTicks = checked((long)((Int128)(timestamp + durations[i]) * TimeSpan.TicksPerSecond / timescale));
                chapters.Add(new MediaMergeChapter(title, TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(endTicks)));
            }
            timestamp += durations[i];
            payloadOffset += sizes[i];
        }
        return chapters;
    }

    // ------------------------------------------------------------------------------------------------ boxes

    private static MediaContainerException Bad(string why) => new($"Invalid MP4: {why}.");

    private static IEnumerable<(string Type, int Start, int End)> Children(byte[] b, int start, int end)
    {
        int p = start;
        while (p < end)
        {
            if (end - p < 8) throw Bad("a child box header is truncated");
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            string type = MuxIo.FourCc(b.AsSpan(p + 4));
            int header = 8;
            if (size == 1)
            {
                if (end - p < 16) throw Bad("a child box header is truncated");
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8));
                if (large > int.MaxValue) throw Bad("a child box is too large");
                size = (long)large; header = 16;
            }
            else if (size == 0) size = end - p;
            if (size < header || size > end - p) throw Bad($"child box '{type}' has an invalid size");
            yield return (type, p + header, p + (int)size);
            p += (int)size;
        }
    }

    private static (string Type, int Start, int End)? Find(byte[] b, int start, int end, string type)
    {
        foreach (var c in Children(b, start, end)) if (c.Type == type) return c;
        return null;
    }

    private static uint U32(byte[] b, int p, int end)
    {
        if (p < 0 || p + 4 > end) throw Bad("a field is truncated");
        return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
    }
    private static ulong U64(byte[] b, int p, int end)
    {
        if (p < 0 || p + 8 > end) throw Bad("a field is truncated");
        return BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p));
    }
    private static ushort U16(byte[] b, int p, int end)
    {
        if (p < 0 || p + 2 > end) throw Bad("a field is truncated");
        return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
    }

    private static void ParseMoov(byte[] b, Dictionary<uint, State> states, List<uint> order)
    {
        foreach (var c in Children(b, 0, b.Length))
        {
            if (c.Type == "trak")
            {
                var parsed = ParseTrak(b, c.Start, c.End);
                if (parsed is null) continue;
                if (states.ContainsKey(parsed.Value.Id)) throw Bad("two tracks share an id");
                states[parsed.Value.Id] = new State { Track = parsed.Value.Track };
                order.Add(parsed.Value.Id);
            }
        }
        if (Find(b, 0, b.Length, "mvex") is { } mvex)
            foreach (var t in Children(b, mvex.Start, mvex.End))
                if (t.Type == "trex" && t.End - t.Start >= 24)
                {
                    uint id = U32(b, t.Start + 4, t.End);
                    if (states.TryGetValue(id, out var st))
                        st.Trex = new Defaults { Duration = U32(b, t.Start + 12, t.End), Size = U32(b, t.Start + 16, t.End), Flags = U32(b, t.Start + 20, t.End) };
                }
    }

    private static (uint Id, DemuxedTrack Track)? ParseTrak(byte[] b, int start, int end)
    {
        var tkhd = Find(b, start, end, "tkhd") ?? throw Bad("a track has no header");
        int tv = b[tkhd.Start];
        uint id = U32(b, tkhd.Start + (tv == 1 ? 20 : 12), tkhd.End);
        var mdia = Find(b, start, end, "mdia") ?? throw Bad("a track has no media box");
        var hdlr = Find(b, mdia.Start, mdia.End, "hdlr") ?? throw Bad("a track has no handler");
        string handler = MuxIo.FourCc(b.AsSpan(hdlr.Start + 8, 4));
        if (handler is not ("vide" or "soun")) return null; // text, hint, metadata tracks are not media we merge
        var mdhd = Find(b, mdia.Start, mdia.End, "mdhd") ?? throw Bad("a track has no media header");
        int mv = b[mdhd.Start];
        uint timescale = U32(b, mdhd.Start + (mv == 1 ? 20 : 12), mdhd.End);
        if (timescale == 0) throw Bad("a track timescale is zero");
        ushort lang = U16(b, mdhd.Start + (mv == 1 ? 32 : 20), mdhd.End);

        var track = new DemuxedTrack
        {
            Kind = handler == "vide" ? TrackKind.Video : TrackKind.Audio,
            SourceContainer = "mp4",
            Timescale = timescale,
            Language = UnpackLanguage(lang)
        };

        if (Find(b, start, end, "edts") is { } edts && Find(b, edts.Start, edts.End, "elst") is { } elst)
        {
            int ev = b[elst.Start];
            uint count = U32(b, elst.Start + 4, elst.End);
            int p = elst.Start + 8;
            for (uint i = 0; i < count && i < 16; i++)
            {
                long mediaTime = ev == 1 ? (long)U64(b, p + 8, elst.End) : (int)U32(b, p + 4, elst.End);
                p += ev == 1 ? 20 : 12;
                if (mediaTime >= 0) { track.EditMediaTime = mediaTime; break; }
            }
        }

        var minf = Find(b, mdia.Start, mdia.End, "minf") ?? throw Bad("a track has no media information");
        var stbl = Find(b, minf.Start, minf.End, "stbl") ?? throw Bad("a track has no sample table");
        var stsd = Find(b, stbl.Start, stbl.End, "stsd") ?? throw Bad("a track has no sample description");
        if (U32(b, stsd.Start + 4, stsd.End) < 1) throw Bad("a track has no sample entry");
        int entryStart = stsd.Start + 8;
        uint entrySize = U32(b, entryStart, stsd.End);
        if (entrySize < 16 || entryStart + entrySize > stsd.End) throw Bad("a sample entry has an invalid size");
        track.Mp4SampleEntry = b.AsSpan(entryStart, (int)entrySize).ToArray();
        ParseSampleEntry(track, b, entryStart, entryStart + (int)entrySize);
        ReadSampleTables(track, b, stbl.Start, stbl.End);
        return (id, track);
    }

    internal static void ParseSampleEntry(DemuxedTrack t, byte[] b, int start, int end)
    {
        string fourcc = MuxIo.FourCc(b.AsSpan(start + 4, 4));
        int payload = start + 8;
        if (fourcc is "encv" or "enca" or "drmi" or "drms")
            throw new MediaContainerException("The track is encrypted (DRM). VESPER does not decrypt protected media.");
        int childStart;
        if (t.Kind == TrackKind.Video)
        {
            t.Width = U16(b, payload + 24, end);
            t.Height = U16(b, payload + 26, end);
            childStart = payload + 78;
        }
        else
        {
            ushort version = U16(b, payload + 8, end);
            t.Channels = U16(b, payload + 16, end);
            t.BitDepth = U16(b, payload + 18, end);
            t.SampleRate = (int)(U32(b, payload + 24, end) >> 16);
            childStart = payload + 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
        }
        t.Codec = fourcc switch
        {
            "avc1" or "avc3" => "h264", "hvc1" or "hev1" => "h265", "dvh1" or "dvhe" => "h265", "av01" => "av1",
            "vp09" => "vp9", "vp08" => "vp8", "mp4a" => "aac", "Opus" => "opus", "fLaC" => "flac", "ac-3" => "ac3",
            "ec-3" => "eac3", ".mp3" => "mp3", _ => throw new MediaContainerException($"The MP4 codec '{fourcc}' is not supported for merging.")
        };
        if (childStart > end) throw Bad("a sample entry is truncated");
        foreach (var c in Children(b, childStart, end))
        {
            switch (c.Type)
            {
                case "avcC" or "hvcC" or "av1C" or "vpcC":
                    t.CodecPrivate = b.AsSpan(c.Start, c.End - c.Start).ToArray();
                    break;
                case "esds":
                    ParseEsds(t, b, c.Start, c.End);
                    break;
                case "dOps":
                    t.CodecPrivate = OpusHeadFromDops(b, c.Start, c.End, t);
                    break;
                case "sinf":
                    throw new MediaContainerException("The track carries protection information (DRM). VESPER does not decrypt protected media.");
            }
        }
        if (t.Codec is "h264" or "h265" or "av1" && t.CodecPrivate is null)
            throw Bad($"the {t.Codec} track has no decoder configuration");
    }

    private static void ParseEsds(DemuxedTrack t, byte[] b, int start, int end)
    {
        int p = start + 4; // version/flags
        while (p < end)
        {
            byte tag = b[p++];
            int len = 0;
            for (int i = 0; i < 4 && p < end; i++)
            {
                byte v = b[p++];
                len = (len << 7) | (v & 0x7F);
                if ((v & 0x80) == 0) break;
            }
            if (len < 0 || p + len > end) throw Bad("an audio descriptor is truncated");
            switch (tag)
            {
                case 0x03: // ES_Descriptor: ES_ID(2) flags(1) [+ optional fields], then nested descriptors
                {
                    if (len < 3) throw Bad("an ES descriptor is truncated");
                    byte flags = b[p + 2];
                    int skip = 3 + ((flags & 0x80) != 0 ? 2 : 0) + ((flags & 0x20) != 0 ? 2 : 0);
                    if ((flags & 0x40) != 0) skip += 1 + b[p + skip];
                    p += skip;
                    continue; // nested descriptors follow
                }
                case 0x04: // DecoderConfigDescriptor
                {
                    if (len < 13) throw Bad("a decoder configuration is truncated");
                    byte oti = b[p];
                    if (oti is 0x69 or 0x6B) t.Codec = "mp3";
                    else if (oti != 0x40 && oti != 0x66 && oti != 0x67 && oti != 0x68)
                        throw new MediaContainerException($"The MP4 audio object type 0x{oti:X2} is not supported for merging.");
                    p += 13;
                    continue; // DecoderSpecificInfo follows
                }
                case 0x05:
                    t.CodecPrivate = b.AsSpan(p, len).ToArray();
                    return;
                default:
                    p += len;
                    break;
            }
        }
    }

    /// <summary>ISO-BMFF dOps (big-endian) to the OpusHead layout Matroska expects (little-endian).</summary>
    private static byte[] OpusHeadFromDops(byte[] b, int start, int end, DemuxedTrack t)
    {
        if (end - start < 11) throw Bad("the Opus configuration is truncated");
        byte channels = b[start + 1];
        ushort preSkip = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(start + 2));
        uint rate = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(start + 4));
        short gain = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(start + 8));
        byte family = b[start + 10];
        var head = new List<byte>(Encoding.ASCII.GetBytes("OpusHead")) { 1, channels };
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, preSkip); head.AddRange(tmp[..2].ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, rate); head.AddRange(tmp.ToArray());
        BinaryPrimitives.WriteInt16LittleEndian(tmp, gain); head.AddRange(tmp[..2].ToArray());
        head.Add(family);
        if (family != 0) head.AddRange(b.AsSpan(start + 11, end - start - 11).ToArray());
        t.CodecDelayNs = preSkip * 1_000_000_000L / 48_000;
        t.SeekPreRollNs = 80_000_000;
        t.Channels = channels;
        return head.ToArray();
    }

    internal static string UnpackLanguage(ushort v)
    {
        if (v == 0 || v == 0x7FFF) return "und";
        Span<char> c = stackalloc char[3];
        c[0] = (char)(((v >> 10) & 0x1F) + 0x60); c[1] = (char)(((v >> 5) & 0x1F) + 0x60); c[2] = (char)((v & 0x1F) + 0x60);
        foreach (char ch in c) if (ch is < 'a' or > 'z') return "und";
        return new string(c);
    }

    private static void ReadSampleTables(DemuxedTrack t, byte[] b, int start, int end)
    {
        var stsz = Find(b, start, end, "stsz");
        if (stsz is null) return;
        uint fixedSize = U32(b, stsz.Value.Start + 4, stsz.Value.End);
        uint count = U32(b, stsz.Value.Start + 8, stsz.Value.End);
        if (count == 0) return; // fragmented: samples live in moof boxes
        if (count > MaxSamples) throw Bad("the sample table is too large");

        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint sz = fixedSize != 0 ? fixedSize : U32(b, stsz.Value.Start + 12 + i * 4, stsz.Value.End);
            if (sz == 0 || sz > MaxSampleBytes) throw Bad("a sample has an invalid size");
            sizes[i] = (int)sz;
        }
        var durations = new int[count];
        var stts = Find(b, start, end, "stts") ?? throw Bad("the time-to-sample table is missing");
        {
            uint entries = U32(b, stts.Start + 4, stts.End);
            int idx = 0, p = stts.Start + 8;
            for (uint e = 0; e < entries && idx < count; e++, p += 8)
            {
                uint n = U32(b, p, stts.End), d = U32(b, p + 4, stts.End);
                for (uint k = 0; k < n && idx < count; k++) durations[idx++] = (int)Math.Min(d, int.MaxValue);
            }
            if (idx != count) throw Bad("the time-to-sample table does not cover every sample");
        }
        var cts = new int[count];
        if (Find(b, start, end, "ctts") is { } ctts)
        {
            uint entries = U32(b, ctts.Start + 4, ctts.End);
            int idx = 0, p = ctts.Start + 8;
            for (uint e = 0; e < entries && idx < count; e++, p += 8)
            {
                uint n = U32(b, p, ctts.End); int off = (int)U32(b, p + 4, ctts.End);
                for (uint k = 0; k < n && idx < count; k++) cts[idx++] = off;
            }
        }
        HashSet<uint>? sync = null;
        if (Find(b, start, end, "stss") is { } stss)
        {
            uint entries = U32(b, stss.Start + 4, stss.End);
            if (entries > count) throw Bad("the sync-sample table is too large");
            sync = new HashSet<uint>();
            for (int i = 0; i < entries; i++) sync.Add(U32(b, stss.Start + 8 + i * 4, stss.End));
        }
        var offsets = new List<long>();
        if (Find(b, start, end, "stco") is { } stco)
        {
            uint n = U32(b, stco.Start + 4, stco.End);
            for (int i = 0; i < n; i++) offsets.Add(U32(b, stco.Start + 8 + i * 4, stco.End));
        }
        else if (Find(b, start, end, "co64") is { } co64)
        {
            uint n = U32(b, co64.Start + 4, co64.End);
            for (int i = 0; i < n; i++) offsets.Add((long)U64(b, co64.Start + 8 + i * 8, co64.End));
        }
        else throw Bad("the chunk offset table is missing");
        var stsc = Find(b, start, end, "stsc") ?? throw Bad("the sample-to-chunk table is missing");
        uint stscEntries = U32(b, stsc.Start + 4, stsc.End);
        var runs = new List<(uint First, uint PerChunk)>();
        for (int i = 0; i < stscEntries; i++)
            runs.Add((U32(b, stsc.Start + 8 + i * 12, stsc.End), U32(b, stsc.Start + 12 + i * 12, stsc.End)));
        if (runs.Count == 0) throw Bad("the sample-to-chunk table is empty");

        long dts = 0;
        int sample = 0;
        for (int chunk = 0; chunk < offsets.Count && sample < count; chunk++)
        {
            uint chunkNo = (uint)chunk + 1;
            uint per = 0;
            foreach (var r in runs) if (r.First <= chunkNo) per = r.PerChunk; else break;
            long off = offsets[chunk];
            for (uint k = 0; k < per && sample < count; k++, sample++)
            {
                t.Samples.Add(new TrackSample
                {
                    Offset = off, Size = sizes[sample], Dts = dts, CtsOffset = cts[sample], Duration = durations[sample],
                    Key = t.Kind == TrackKind.Audio || sync is null || sync.Contains((uint)sample + 1)
                });
                off += sizes[sample];
                dts += durations[sample];
            }
        }
        if (sample != count) throw Bad("the chunk tables do not cover every sample");
    }

    private static int ParseMoof(byte[] b, long moofStart, Dictionary<uint, State> states)
    {
        int added = 0;
        long previousTrafEnd = moofStart;
        bool firstTraf = true;
        foreach (var traf in Children(b, 0, b.Length))
        {
            if (traf.Type != "traf") continue;
            var tfhd = Find(b, traf.Start, traf.End, "tfhd") ?? throw Bad("a track fragment has no header");
            uint flags = U32(b, tfhd.Start, tfhd.End) & 0xFFFFFF;
            uint id = U32(b, tfhd.Start + 4, tfhd.End);
            int p = tfhd.Start + 8;
            long? baseOffset = null;
            uint? dDur = null, dSize = null, dFlags = null;
            if ((flags & 0x01) != 0) { baseOffset = (long)U64(b, p, tfhd.End); p += 8; }
            if ((flags & 0x02) != 0) p += 4;
            if ((flags & 0x08) != 0) { dDur = U32(b, p, tfhd.End); p += 4; }
            if ((flags & 0x10) != 0) { dSize = U32(b, p, tfhd.End); p += 4; }
            if ((flags & 0x20) != 0) { dFlags = U32(b, p, tfhd.End); }
            if (!states.TryGetValue(id, out var st)) continue; // fragment for a track we do not merge
            long dataBase = baseOffset ?? ((flags & 0x020000) != 0 || firstTraf ? moofStart : previousTrafEnd);
            firstTraf = false;

            if (Find(b, traf.Start, traf.End, "tfdt") is { } tfdt)
                st.NextDts = b[tfdt.Start] == 1 ? (long)U64(b, tfdt.Start + 4, tfdt.End) : U32(b, tfdt.Start + 4, tfdt.End);

            long cursor = dataBase;
            bool firstTrun = true;
            foreach (var trun in Children(b, traf.Start, traf.End))
            {
                if (trun.Type != "trun") continue;
                int version = b[trun.Start];
                uint tf = U32(b, trun.Start, trun.End) & 0xFFFFFF;
                uint count = U32(b, trun.Start + 4, trun.End);
                if (count > 1_000_000) throw Bad("a fragment declares too many samples");
                int q = trun.Start + 8;
                if ((tf & 0x01) != 0) { cursor = dataBase + (int)U32(b, q, trun.End); q += 4; }
                else if (firstTrun) cursor = dataBase;
                firstTrun = false;
                uint? firstFlags = null;
                if ((tf & 0x04) != 0) { firstFlags = U32(b, q, trun.End); q += 4; }
                for (uint i = 0; i < count; i++)
                {
                    uint dur = (tf & 0x100) != 0 ? U32(b, q, trun.End) : dDur ?? st.Trex.Duration; if ((tf & 0x100) != 0) q += 4;
                    uint size = (tf & 0x200) != 0 ? U32(b, q, trun.End) : dSize ?? st.Trex.Size; if ((tf & 0x200) != 0) q += 4;
                    uint sflags = (tf & 0x400) != 0 ? U32(b, q, trun.End) : (i == 0 && firstFlags is { } ff) ? ff : dFlags ?? st.Trex.Flags;
                    if ((tf & 0x400) != 0) q += 4;
                    int cto = 0;
                    if ((tf & 0x800) != 0) { uint raw = U32(b, q, trun.End); cto = version == 0 ? (int)Math.Min(raw, int.MaxValue) : (int)raw; q += 4; }
                    if (size == 0 || size > MaxSampleBytes) throw Bad("a fragment sample has an invalid size");
                    if (dur > int.MaxValue) throw Bad("a fragment sample has an invalid duration");
                    st.Track.Samples.Add(new TrackSample
                    {
                        Offset = cursor, Size = (int)size, Dts = st.NextDts, CtsOffset = cto, Duration = (int)dur,
                        Key = st.Track.Kind == TrackKind.Audio || ((sflags >> 16) & 1) == 0
                    });
                    cursor += size;
                    st.NextDts += dur;
                    added++;
                }
            }
            previousTrafEnd = cursor;
        }
        return added;
    }
}
