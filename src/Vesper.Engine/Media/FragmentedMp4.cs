using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>Unsupported or malformed ISO BMFF data. Never downgraded to successful output.</summary>
internal sealed class MediaContainerException(string message) : Exception(message);

internal sealed record Mp4TrackProgress(uint Id, ulong? NextDecodeTime, long Samples);
internal sealed record Mp4Progress(uint Sequence, IReadOnlyList<Mp4TrackProgress> Tracks);

/// <summary>
/// Structural validator, not a decoder or a general muxer. Supports clear avc1/avc3/hvc1/hev1/av01/mp4a/Opus
/// fragmented MP4 with moof-relative offsets, tfdt, explicit trun data offsets, and contiguous track timelines.
/// Absolute offsets, edit lists, encrypted samples and track changes are rejected rather than rewritten.
/// No payload-sized allocations: mdat is checked by declared sample ranges, not copied or scanned.
/// </summary>
internal sealed class FragmentedMp4
{
    private const long MaxSamples = 2_000_000;
    private readonly record struct Box(string Type, int Start, int Payload, int End);
    private sealed class Track(uint id, string handler, uint duration, uint size, uint sampleFlags, DemuxedTrack media)
    {
        internal uint Id = id, DefaultDuration = duration, DefaultSize = size;
        internal uint DefaultSampleFlags = sampleFlags;
        internal string Handler = handler;
        internal DemuxedTrack Media = media;
        internal ulong? Next;
        internal long Samples;
    }
    private readonly Dictionary<uint, Track> _tracks = [];
    private bool _captureSamples;
    private uint _sequence;
    internal bool HasAudio => _tracks.Values.Any(t => t.Handler == "soun");
    internal bool HasVideo => _tracks.Values.Any(t => t.Handler == "vide");

    internal static FragmentedMp4 Open(ReadOnlySpan<byte> data, bool captureSamples = false)
    {
        var result = new FragmentedMp4 { _captureSamples = captureSamples };
        var top = Boxes(data, 0, data.Length);
        if (top.Count(b => b.Type == "ftyp") != 1 || top.Count(b => b.Type == "moov") != 1
            || top.Any(b => b.Type is not ("ftyp" or "moov" or "free" or "skip")))
            throw Bad("Initialization must contain exactly one ftyp and one moov, without media or protection boxes.");
        if (top.Single(b => b.Type == "ftyp") is var ftyp && ftyp.End - ftyp.Payload < 8)
            throw Bad("Truncated ftyp.");
        var moov = top.Single(b => b.Type == "moov");
        var children = Boxes(data, moov.Payload, moov.End);
        if (children.Any(b => b.Type == "pssh")) throw Bad("Protected initialization is not supported.");
        var traks = children.Where(b => b.Type == "trak").ToList();
        if (traks.Count is < 1 or > 2) throw Bad("Only one audio track, one video track, or a source-muxed pair is supported.");
        var defaults = new Dictionary<uint, (uint Duration, uint Size, uint Flags)>();
        var mvex = One(children, "mvex");
        foreach (var trex in Boxes(data, mvex.Payload, mvex.End).Where(b => b.Type == "trex"))
        {
            Need(trex, 24);
            if (U32(data, trex.Payload) != 0 || U32(data, trex.Payload + 8) != 1)
                throw Bad("Unsupported trex version or sample-description index.");
            if (!defaults.TryAdd(U32(data, trex.Payload + 4),
                (U32(data, trex.Payload + 12), U32(data, trex.Payload + 16), U32(data, trex.Payload + 20))))
                throw Bad("Duplicate trex track.");
        }
        var handlers = new HashSet<string>();
        foreach (var trak in traks)
        {
            var tk = Boxes(data, trak.Payload, trak.End);
            if (tk.Any(b => b.Type == "edts")) throw Bad("Edit-list timelines require remuxing and are not supported.");
            var tkhd = One(tk, "tkhd"); Need(tkhd, 24);
            int version = data[tkhd.Payload];
            if (version is not (0 or 1)) throw Bad("Unsupported tkhd version.");
            uint id = U32(data, tkhd.Payload + (version == 0 ? 12 : 20));
            var mdia = One(tk, "mdia"); var md = Boxes(data, mdia.Payload, mdia.End);
            var hdlr = One(md, "hdlr"); Need(hdlr, 12);
            string handler = Encoding.ASCII.GetString(data.Slice(hdlr.Payload + 8, 4));
            if (handler is not ("vide" or "soun") || !handlers.Add(handler)) throw Bad("Unsupported or duplicate track type.");
            var mdhd = One(md, "mdhd"); Need(mdhd, 24);
            if (data[mdhd.Payload] is not (0 or 1)) throw Bad("Unsupported mdhd version.");
            int mdhdVersion = data[mdhd.Payload];
            if (mdhdVersion == 1) Need(mdhd, 34);
            uint timescale = U32(data, mdhd.Payload + (mdhdVersion == 0 ? 12 : 20));
            if (timescale == 0) throw Bad("Zero track timescale.");
            var minf = One(md, "minf"); var stbl = One(Boxes(data, minf.Payload, minf.End), "stbl");
            var st = Boxes(data, stbl.Payload, stbl.End); var stsd = One(st, "stsd"); Need(stsd, 8);
            if (U32(data, stsd.Payload) != 0 || U32(data, stsd.Payload + 4) != 1) throw Bad("Exactly one clear sample description is required.");
            var entry = Boxes(data, stsd.Payload + 8, stsd.End).SingleOrDefault();
            int prefix; string config;
            switch (entry.Type)
            {
                case "avc1" or "avc3": prefix = 78; config = "avcC"; break;
                case "hvc1" or "hev1": prefix = 78; config = "hvcC"; break;
                case "av01": prefix = 78; config = "av1C"; break;
                case "mp4a": prefix = 28; config = "esds"; break;
                case "Opus": prefix = 28; config = "dOps"; break;
                default: throw Bad("Unsupported or encrypted sample description.");
            }
            if ((handler == "soun") != (entry.Type is "mp4a" or "Opus")) throw Bad("Sample description does not match the track handler.");
            Need(entry, prefix);
            var configs = Boxes(data, entry.Payload + prefix, entry.End);
            if (configs.Any(b => b.Type == "sinf") || configs.Count(b => b.Type == config) != 1)
                throw Bad("Missing codec configuration or encrypted sample entry.");
            foreach (var table in st.Where(b => b.Type is "stts" or "stsc" or "stco" or "co64" or "stsz"))
            {
                Need(table, table.Type == "stsz" ? 12 : 8);
                if (U32(data, table.Payload + (table.Type == "stsz" ? 8 : 4)) != 0)
                    throw Bad("Initialization contains non-fragmented samples.");
            }
            if (id == 0 || !defaults.TryGetValue(id, out var def))
                throw Bad("Missing or invalid track defaults.");
            byte[] sampleEntry = data.Slice(entry.Start, entry.End - entry.Start).ToArray();
            var mediaTrack = new DemuxedTrack
            {
                Kind = handler == "vide" ? TrackKind.Video : TrackKind.Audio,
                SourceContainer = "mp4",
                Timescale = timescale,
                Language = Mp4Demuxer.UnpackLanguage(U16(data, mdhd.Payload + (mdhdVersion == 0 ? 20 : 32))),
                Mp4SampleEntry = sampleEntry
            };
            Mp4Demuxer.ParseSampleEntry(mediaTrack, sampleEntry, 0, sampleEntry.Length);
            if (handler == "vide" && (mediaTrack.Width <= 0 || mediaTrack.Height <= 0))
                throw Bad("The video sample entry has invalid dimensions.");
            if (handler == "soun" && (mediaTrack.SampleRate <= 0 || mediaTrack.Channels <= 0))
                throw Bad("The audio sample entry has invalid channels or sample rate.");
            if (!result._tracks.TryAdd(id, new Track(id, handler, def.Duration, def.Size, def.Flags, mediaTrack)))
                throw Bad("Missing, duplicate or invalid track defaults.");
        }
        if (defaults.Count != result._tracks.Count) throw Bad("Track defaults do not match initialization tracks.");
        return result;
    }

    internal Mp4Progress Snapshot() => new(_sequence, _tracks.Values.Select(t => new Mp4TrackProgress(t.Id, t.Next, t.Samples)).ToArray());

    internal List<DemuxedTrack> GetTracks() => [.. _tracks.Values.Select(t => t.Media)];

    internal void Restore(Mp4Progress progress)
    {
        if (progress.Tracks is null || progress.Tracks.Count != _tracks.Count
            || progress.Tracks.Select(t => t.Id).Distinct().Count() != _tracks.Count) throw Bad("Invalid saved track state.");
        foreach (var t in progress.Tracks)
        {
            if (!_tracks.TryGetValue(t.Id, out var track) || t.Samples < 0 || (t.Samples > 0) != (t.NextDecodeTime is not null))
                throw Bad("Saved tracks differ from initialization.");
            track.Next = t.NextDecodeTime; track.Samples = t.Samples;
        }
        _sequence = progress.Sequence;
    }

    internal void ValidateFragment(ReadOnlySpan<byte> data, long sourceOffset = 0)
    {
        var boxes = Boxes(data, 0, data.Length);
        bool sawMedia = false;
        for (int i = 0; i < boxes.Count; i++)
        {
            var moof = boxes[i];
            if (moof.Type is "styp" or "sidx" or "emsg" or "prft" or "free") continue;
            if (moof.Type != "moof" || ++i >= boxes.Count || boxes[i].Type != "mdat")
                throw Bad("A media fragment must contain moof immediately followed by mdat.");
            var mdat = boxes[i];
            var children = Boxes(data, moof.Payload, moof.End);
            if (children.Any(b => b.Type is not ("mfhd" or "traf"))) throw Bad("Unsupported movie-fragment box.");
            var mfhd = One(children, "mfhd"); Need(mfhd, 8);
            uint sequence = U32(data, mfhd.Payload + 4);
            if (U32(data, mfhd.Payload) != 0 || sequence <= _sequence) throw Bad("Duplicate or unordered fragment sequence.");
            var ranges = new List<(long Start, long End)>();
            var seen = new HashSet<uint>();
            foreach (var traf in children.Where(b => b.Type == "traf"))
            {
                var tb = Boxes(data, traf.Payload, traf.End);
                if (tb.Any(b => b.Type is not ("tfhd" or "tfdt" or "trun")))
                    throw Bad("Encrypted or unsupported track-fragment metadata.");
                var tfhd = One(tb, "tfhd"); Need(tfhd, 8);
                uint flags = U32(data, tfhd.Payload);
                if ((flags & 0x020000) == 0 || (flags & ~0x02003au) != 0)
                    throw Bad("Only moof-relative track fragments are supported; absolute offsets require remuxing.");
                uint id = U32(data, tfhd.Payload + 4);
                if (!_tracks.TryGetValue(id, out var track) || !seen.Add(id)) throw Bad("Unknown or duplicate fragment track.");
                int p = tfhd.Payload + 8;
                uint duration = track.DefaultDuration, size = track.DefaultSize, sampleFlags = track.DefaultSampleFlags;
                if ((flags & 2) != 0 && Read(data, ref p, tfhd.End) != 1) throw Bad("Sample description changed.");
                if ((flags & 8) != 0) duration = Read(data, ref p, tfhd.End);
                if ((flags & 16) != 0) size = Read(data, ref p, tfhd.End);
                if ((flags & 32) != 0) sampleFlags = Read(data, ref p, tfhd.End);
                if (p != tfhd.End) throw Bad("Invalid tfhd length.");
                var tfdt = One(tb, "tfdt"); Need(tfdt, 8);
                uint tv = U32(data, tfdt.Payload);
                if (tv is not (0 or 0x01000000)) throw Bad("Unsupported tfdt version/flags.");
                if (tv != 0) Need(tfdt, 12);
                ulong decode = tv == 0 ? U32(data, tfdt.Payload + 4) : BinaryPrimitives.ReadUInt64BigEndian(data.Slice(tfdt.Payload + 4, 8));
                if (track.Next is { } expected && decode != expected) throw Bad("Track timeline has a gap, overlap or discontinuity.");
                var runs = tb.Where(b => b.Type == "trun").ToList();
                if (runs.Count is < 1 or > 4096) throw Bad("Missing or excessive sample runs.");
                foreach (var run in runs)
                {
                    Need(run, 12);
                    uint rf = U32(data, run.Payload);
                    if ((rf & 1) == 0 || (rf & ~0x01000f05u) != 0) throw Bad("Unsupported trun flags or missing data offset.");
                    if ((rf & 0x00000400) != 0 && (rf & 0x00000004) != 0)
                        throw Bad("A track run has conflicting sample-flag fields.");
                    uint count = U32(data, run.Payload + 4);
                    if (count is 0 or > 100_000 || track.Samples > MaxSamples - count)
                        throw Bad("Invalid or excessive sample count.");
                    int q = run.Payload + 8;
                    long start = checked((long)moof.Start + unchecked((int)Read(data, ref q, run.End)));
                    uint firstFlags = (rf & 4) != 0 ? Read(data, ref q, run.End) : sampleFlags;
                    long bytes = 0;
                    long sampleOffset = start;
                    for (int sample = 0; sample < count; sample++)
                    {
                        uint d = (rf & 0x100) != 0 ? Read(data, ref q, run.End) : duration;
                        uint n = (rf & 0x200) != 0 ? Read(data, ref q, run.End) : size;
                        uint sf = (rf & 0x400) != 0 ? Read(data, ref q, run.End) : sample == 0 ? firstFlags : sampleFlags;
                        uint rawCts = (rf & 0x800) != 0 ? Read(data, ref q, run.End) : 0;
                        long cts = (rf & 0x800) == 0 ? 0 : (rf >> 24) == 1 ? unchecked((int)rawCts) : rawCts;
                        if (d == 0 || d > int.MaxValue || n == 0 || n > int.MaxValue || decode > long.MaxValue
                            || decode > ulong.MaxValue - d || cts is < int.MinValue or > int.MaxValue)
                            throw Bad("Invalid sample duration, size, composition offset or decode timestamp.");
                        if (_captureSamples)
                            track.Media.Samples.Add(new TrackSample
                            {
                                Offset = checked(sourceOffset + sampleOffset),
                                Size = (int)n,
                                Dts = (long)decode,
                                CtsOffset = (int)cts,
                                Duration = (int)d,
                                Key = (sf & 0x00010000) == 0
                            });
                        decode += d;
                        bytes = checked(bytes + n);
                        sampleOffset = checked(sampleOffset + n);
                    }
                    if (q != run.End || start < mdat.Payload || start > mdat.End || bytes > mdat.End - start)
                        throw Bad("A sample run points outside its mdat payload.");
                    ranges.Add((start, start + bytes));
                    track.Samples = checked(track.Samples + count);
                }
                track.Next = decode;
            }
            long covered = mdat.Payload;
            foreach (var range in ranges.OrderBy(r => r.Start))
            {
                if (range.Start != covered) throw Bad("Overlapping samples or unreferenced mdat bytes.");
                covered = range.End;
            }
            if (covered != mdat.End || ranges.Count == 0) throw Bad("Fragment has incomplete or unreferenced media data.");
            _sequence = sequence; sawMedia = true;
        }
        if (!sawMedia) throw Bad("No media fragment was present.");
    }

    internal void Finish(bool requireAudio, bool requireVideo)
    {
        if ((requireAudio && !HasAudio) || (requireVideo && !HasVideo)
            || _tracks.Values.Any(t => t.Samples == 0)) throw Bad("The output is missing a required audio/video track or contains no samples for a declared track.");
    }

    private static List<Box> Boxes(ReadOnlySpan<byte> data, int start, int end)
    {
        var boxes = new List<Box>();
        while (start < end)
        {
            if (end - start < 8 || boxes.Count >= 100_000) throw Bad("Truncated box header or excessive box count.");
            uint size = U32(data, start); int header = 8; ulong length = size;
            if (size == 1)
            {
                if (end - start < 16) throw Bad("Truncated extended-size box.");
                length = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(start + 8, 8)); header = 16;
            }
            if (length < (ulong)header || length > (ulong)(end - start)) throw Bad("Invalid or out-of-bounds box size.");
            boxes.Add(new Box(Encoding.ASCII.GetString(data.Slice(start + 4, 4)), start, start + header, start + (int)length));
            start += (int)length;
        }
        return boxes;
    }
    private static Box One(List<Box> boxes, string type)
    {
        var found = boxes.Where(b => b.Type == type).ToList();
        return found.Count == 1 ? found[0] : throw Bad($"Expected exactly one {type} box.");
    }
    private static void Need(Box b, int length) { if (b.End - b.Payload < length) throw Bad($"Truncated {b.Type} box."); }
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p, 4));
    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p, 2));
    private static uint Read(ReadOnlySpan<byte> b, ref int p, int end)
    {
        if (end - p < 4) throw Bad("Truncated sample table.");
        uint value = U32(b, p); p += 4; return value;
    }
    private static MediaContainerException Bad(string message) => new(message);
}
