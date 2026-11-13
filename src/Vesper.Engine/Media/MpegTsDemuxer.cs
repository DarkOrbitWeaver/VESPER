using System.Buffers.Binary;

namespace Vesper.Engine;

/// <summary>Extracts H.264 access units and AAC-LC frames from a single MPEG-TS program.</summary>
internal static class MpegTsDemuxer
{
    private const long MaxSamples = 1_000_000;
    private const ulong TimestampModulus = 1UL << 33;
    private const ulong TimestampHalf = TimestampModulus / 2;

    private sealed class Clock
    {
        internal ulong? Raw;
        internal long Value;
    }

    private sealed class H264State
    {
        internal readonly List<byte[]> SequenceSets = [];
        internal readonly List<byte[]> PictureSets = [];
        internal readonly int[] Dimensions = new int[2];
    }

    private sealed class TrackState(TransportStreamInfo stream)
    {
        internal readonly TransportStreamInfo Stream = stream;
        internal readonly DemuxedTrack Track = new()
        {
            Kind = stream.IsVideo ? TrackKind.Video : TrackKind.Audio,
            Codec = stream.StreamType == 0x1b ? "h264" : stream.StreamType == 0x0f ? "aac" : "",
            SourceContainer = "mpegts",
            Timescale = 90_000,
            Language = stream.Language
        };
        internal readonly H264State? H264 = stream.StreamType == 0x1b ? new H264State() : null;
        internal readonly Clock Pts = new();
        internal readonly Clock Dts = new();
        internal long? LastDecodeTime;
        internal byte[]? AudioConfig;
    }

    private readonly record struct PesTiming(int PayloadOffset, long Pts, long Dts, bool HasDts);
    private readonly record struct SpsInfo(int Width, int Height);

    internal static List<DemuxedTrack> Read(Stream input, long length, Stream payloads, CancellationToken ct)
    {
        if (!input.CanRead || length <= 0 || length % 188 != 0)
            throw Bad("Expected a non-empty stream of complete 188-byte transport packets.");
        if (!payloads.CanWrite || !payloads.CanSeek)
            throw Bad("The media sample store must be writable and seekable.");

        var tracks = new Dictionary<int, TrackState>();
        var parser = new TransportStreamValidator(pes =>
        {
            ct.ThrowIfCancellationRequested();
            ProcessPes(pes, tracks, payloads);
        });
        byte[] packet = new byte[188];
        for (long position = 0; position < length; position += packet.Length)
        {
            ct.ThrowIfCancellationRequested();
            ReadExact(input, packet);
            parser.Validate(packet);
        }
        parser.Finish();
        if (tracks.Count == 0) throw Bad("The transport stream contains no supported H.264 or AAC track.");

        foreach (TrackState state in tracks.Values)
        {
            if (state.Track.Samples.Count == 0) throw Bad("A declared transport track contains no complete media samples.");
            if (state.H264 is not null)
            {
                if (state.H264.SequenceSets.Count == 0 || state.H264.PictureSets.Count == 0 || state.Track.CodecPrivate is null)
                    throw Bad("The H.264 stream has no complete sequence and picture configuration.");
                SetVideoDurations(state.Track);
            }
        }
        return [.. tracks.OrderBy(pair => pair.Key).Select(pair => pair.Value.Track)];
    }

    private static void ProcessPes(TransportPes pes, Dictionary<int, TrackState> tracks, Stream payloads)
    {
        if (pes.Stream.StreamType is not (0x1b or 0x0f))
            throw Bad($"MPEG-TS stream type 0x{pes.Stream.StreamType:x2} is not supported for demuxing.");
        if (!tracks.TryGetValue(pes.Stream.Pid, out TrackState? state))
            tracks.Add(pes.Stream.Pid, state = new TrackState(pes.Stream));

        PesTiming timing = ReadTiming(pes.Data, state);
        if (pes.Stream.StreamType == 0x1b) ProcessH264(pes.Data.AsMemory(timing.PayloadOffset), timing, state, payloads);
        else ProcessAac(pes.Data.AsSpan(timing.PayloadOffset), timing, state, payloads);
    }

    private static PesTiming ReadTiming(ReadOnlySpan<byte> pes, TrackState state)
    {
        if (pes.Length < 9 || pes[0] != 0 || pes[1] != 0 || pes[2] != 1
            || (pes[6] & 0xc0) != 0x80 || pes.Length < 9 + pes[8])
            throw Bad("A media packet has an invalid PES header.");

        int flags = (pes[7] >> 6) & 3;
        if (flags is not (2 or 3)) throw Bad("A supported media PES packet must contain a presentation timestamp.");
        int headerEnd = 9 + pes[8];
        if (headerEnd > pes.Length || pes[8] < (flags == 3 ? 10 : 5)) throw Bad("PES timestamps are truncated.");
        ulong rawPts = ReadTimestamp(pes.Slice(9, 5), flags == 3 ? 3 : 2);
        long pts = Unwrap(rawPts, state.Pts, monotonic: false);
        bool hasDts = flags == 3;
        long dts = hasDts ? Unwrap(ReadTimestamp(pes.Slice(14, 5), 1), state.Dts, monotonic: true) : pts;
        if (!hasDts && state.LastDecodeTime is long previous && dts < previous)
            throw Bad("Presentation timestamps reverse without decoding timestamps; sample order is unsafe.");
        state.LastDecodeTime = dts;
        return new PesTiming(headerEnd, pts, dts, hasDts);
    }

    private static ulong ReadTimestamp(ReadOnlySpan<byte> bytes, int prefix)
    {
        if (bytes.Length < 5 || bytes[0] >> 4 != prefix || (bytes[0] & 1) == 0
            || (bytes[2] & 1) == 0 || (bytes[4] & 1) == 0)
            throw Bad("A PES timestamp has invalid marker bits.");
        return ((ulong)(((uint)bytes[0] >> 1) & 7) << 30)
            | ((ulong)(uint)bytes[1] << 22)
            | ((ulong)(((uint)bytes[2] >> 1) & 0x7f) << 15)
            | ((ulong)(uint)bytes[3] << 7)
            | (ulong)(((uint)bytes[4] >> 1) & 0x7f);
    }

    private static long Unwrap(ulong raw, Clock clock, bool monotonic)
    {
        if (raw >= TimestampModulus) throw Bad("A PES timestamp exceeds the MPEG 33-bit range.");
        if (clock.Raw is null)
        {
            clock.Raw = raw;
            clock.Value = (long)raw;
            return clock.Value;
        }
        long delta = (long)raw - (long)clock.Raw.Value;
        if (delta > (long)TimestampHalf) delta -= (long)TimestampModulus;
        else if (delta < -(long)TimestampHalf) delta += (long)TimestampModulus;
        long value = checked(clock.Value + delta);
        if (monotonic && value < clock.Value) throw Bad("Decode timestamps move backward; a discontinuity requires a new output epoch.");
        clock.Raw = raw;
        clock.Value = value;
        return value;
    }

    private static void ProcessH264(ReadOnlyMemory<byte> payload, PesTiming timing, TrackState state, Stream output)
    {
        List<ReadOnlyMemory<byte>> nals = ReadNals(payload);
        var samples = new List<ReadOnlyMemory<byte>>();
        bool key = false;
        int pictures = 0;
        foreach (ReadOnlyMemory<byte> memory in nals)
        {
            ReadOnlySpan<byte> nal = memory.Span;
            if (nal.IsEmpty || (nal[0] & 0x80) != 0) throw Bad("An H.264 NAL unit is empty or has a forbidden bit set.");
            int type = nal[0] & 0x1f;
            if (type == 7)
            {
                AddParameterSet(state.H264!, state.H264!.SequenceSets, nal);
                SpsInfo info = ReadSps(nal);
                if (state.H264.Dimensions[0] != 0 && (state.H264.Dimensions[0] != info.Width || state.H264.Dimensions[1] != info.Height))
                    throw Bad("The H.264 dimensions changed inside one transport program.");
                state.H264.Dimensions[0] = info.Width;
                state.H264.Dimensions[1] = info.Height;
                UpdateAvcConfiguration(state.Track, state.H264);
                continue;
            }
            if (type == 8)
            {
                AddParameterSet(state.H264!, state.H264!.PictureSets, nal);
                UpdateAvcConfiguration(state.Track, state.H264);
                continue;
            }
            if (type is 9 or 10 or 11 or 12) continue;
            if (type is 2 or 3 or 4) throw Bad("H.264 data-partitioned slices are unsupported.");
            if (type is 1 or 5)
            {
                if (ReadFirstMacroblock(nal) == 0 && ++pictures > 1)
                    throw Bad("A PES packet contains more than one H.264 picture; access-unit boundaries are ambiguous.");
                key |= type == 5;
            }
            samples.Add(memory);
        }
        if (pictures == 0) return;
        if (samples.Count == 0) throw Bad("An H.264 picture has no coded slice data.");
        if (state.H264!.SequenceSets.Count == 0 || state.H264.PictureSets.Count == 0)
            throw Bad("H.264 media arrived before its decoder configuration.");

        int size = 0;
        foreach (ReadOnlyMemory<byte> sample in samples) size = checked(size + 4 + sample.Length);
        if (size > 16 * 1024 * 1024) throw Bad("An H.264 access unit exceeds the supported size limit.");
        long offset = output.Position;
        Span<byte> prefix = stackalloc byte[4];
        foreach (ReadOnlyMemory<byte> sample in samples)
        {
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)sample.Length);
            output.Write(prefix);
            output.Write(sample.Span);
        }
        AddSample(state, offset, size, timing.Dts, checked((int)(timing.Pts - timing.Dts)), 0, key);
    }

    private static List<ReadOnlyMemory<byte>> ReadNals(ReadOnlyMemory<byte> memory)
    {
        var result = new List<ReadOnlyMemory<byte>>();
        ReadOnlySpan<byte> payload = memory.Span;
        int start = FindStartCode(payload, 0, out int prefix);
        if (start < 0 || payload[..start].IndexOfAnyExcept((byte)0) >= 0)
            throw Bad("An H.264 PES packet does not contain Annex-B start codes.");
        while (start >= 0)
        {
            int nalStart = start + prefix;
            int next = FindStartCode(payload, nalStart, out int nextPrefix);
            int nalEnd = next < 0 ? payload.Length : next;
            while (nalEnd > nalStart && payload[nalEnd - 1] == 0) nalEnd--;
            if (nalEnd == nalStart) throw Bad("An H.264 start code is not followed by a NAL unit.");
            result.Add(memory.Slice(nalStart, nalEnd - nalStart));
            start = next;
            prefix = nextPrefix;
        }
        return result;
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int length)
    {
        length = 0;
        for (int i = from; i + 3 <= data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1) { length = 3; return i; }
            if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1) { length = 4; return i; }
        }
        return -1;
    }

    private static void AddParameterSet(H264State state, List<byte[]> sets, ReadOnlySpan<byte> nal)
    {
        foreach (byte[] existing in sets)
            if (existing.AsSpan().SequenceEqual(nal)) return;
        if (sets.Count > (sets == state.SequenceSets ? 30 : 254)) throw Bad("The H.264 decoder configuration has too many parameter sets.");
        if (sets.Count > 0) throw Bad("H.264 parameter sets changed inside one transport program.");
        sets.Add(nal.ToArray());
    }

    private static void UpdateAvcConfiguration(DemuxedTrack track, H264State state)
    {
        if (state.SequenceSets.Count == 0 || state.PictureSets.Count == 0) return;
        byte[] sps = state.SequenceSets[0];
        if (sps.Length < 4 || state.Dimensions[0] <= 0 || state.Dimensions[1] <= 0)
            throw Bad("The H.264 sequence parameter set is incomplete.");
        using var config = new MemoryStream();
        config.WriteByte(1);
        config.WriteByte(sps[1]);
        config.WriteByte(sps[2]);
        config.WriteByte(sps[3]);
        config.WriteByte(0xff);
        config.WriteByte((byte)(0xe0 | state.SequenceSets.Count));
        foreach (byte[] set in state.SequenceSets) WriteParameterSet(config, set);
        config.WriteByte((byte)state.PictureSets.Count);
        foreach (byte[] set in state.PictureSets) WriteParameterSet(config, set);
        track.CodecPrivate = config.ToArray();
        track.Width = state.Dimensions[0];
        track.Height = state.Dimensions[1];
    }

    private static void WriteParameterSet(Stream output, byte[] set)
    {
        if (set.Length > ushort.MaxValue) throw Bad("An H.264 parameter set exceeds the AVC configuration limit.");
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)set.Length);
        output.Write(length);
        output.Write(set);
    }

    private static SpsInfo ReadSps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 4) throw Bad("The H.264 sequence parameter set is truncated.");
        byte[] rbsp = RemoveEmulationBytes(nal[1..]);
        var bits = new BitReader(rbsp);
        int profile = bits.ReadBits(8);
        bits.SkipBits(16);
        bits.ReadUnsignedExpGolomb();
        int chroma = 1;
        bool separateColourPlane = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = bits.ReadUnsignedExpGolomb();
            if (chroma > 3) throw Bad("The H.264 chroma format is unsupported.");
            if (chroma == 3) separateColourPlane = bits.ReadBit();
            bits.ReadUnsignedExpGolomb();
            bits.ReadUnsignedExpGolomb();
            bits.ReadBit();
            if (bits.ReadBit())
            {
                int count = chroma == 3 ? 12 : 8;
                for (int i = 0; i < count; i++)
                    if (bits.ReadBit()) SkipScalingList(ref bits, i < 6 ? 16 : 64);
            }
        }
        bits.ReadUnsignedExpGolomb();
        int order = bits.ReadUnsignedExpGolomb();
        if (order == 0) bits.ReadUnsignedExpGolomb();
        else if (order == 1)
        {
            bits.ReadBit();
            bits.ReadSignedExpGolomb();
            bits.ReadSignedExpGolomb();
            int cycle = bits.ReadUnsignedExpGolomb();
            if (cycle > 255) throw Bad("The H.264 picture-order cycle is too large.");
            for (int i = 0; i < cycle; i++) bits.ReadSignedExpGolomb();
        }
        else if (order != 2) throw Bad("The H.264 picture-order type is unsupported.");
        bits.ReadUnsignedExpGolomb();
        bits.ReadBit();
        int widthMbs = bits.ReadUnsignedExpGolomb() + 1;
        int heightMapUnits = bits.ReadUnsignedExpGolomb() + 1;
        bool frameOnly = bits.ReadBit();
        if (!frameOnly) bits.ReadBit();
        bits.ReadBit();
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (bits.ReadBit())
        {
            cropLeft = bits.ReadUnsignedExpGolomb();
            cropRight = bits.ReadUnsignedExpGolomb();
            cropTop = bits.ReadUnsignedExpGolomb();
            cropBottom = bits.ReadUnsignedExpGolomb();
        }
        int chromaArray = separateColourPlane ? 0 : chroma;
        int subWidth = chromaArray is 1 or 2 ? 2 : 1;
        int subHeight = chromaArray == 1 ? 2 : 1;
        int cropUnitX = chromaArray == 0 ? 1 : subWidth;
        int cropUnitY = (chromaArray == 0 ? 1 : subHeight) * (frameOnly ? 1 : 2);
        int width = checked(widthMbs * 16 - (cropLeft + cropRight) * cropUnitX);
        int height = checked((2 - (frameOnly ? 1 : 0)) * heightMapUnits * 16 - (cropTop + cropBottom) * cropUnitY);
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw Bad("The H.264 dimensions exceed the supported range.");
        return new SpsInfo(width, height);
    }

    private static void SkipScalingList(ref BitReader bits, int size)
    {
        int last = 8, next = 8;
        for (int i = 0; i < size; i++)
        {
            if (next != 0) next = (last + bits.ReadSignedExpGolomb() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    private static int ReadFirstMacroblock(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2) throw Bad("An H.264 slice header is truncated.");
        var bits = new BitReader(RemoveEmulationBytes(nal[1..]));
        return bits.ReadUnsignedExpGolomb();
    }

    private static byte[] RemoveEmulationBytes(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        int written = 0, zeros = 0;
        foreach (byte value in data)
        {
            if (zeros >= 2 && value == 3) { zeros = 0; continue; }
            result[written++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.AsSpan(0, written).ToArray();
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        internal BitReader(ReadOnlySpan<byte> data) { _data = data; _position = 0; }
        internal bool ReadBit() => ReadBits(1) != 0;
        internal int ReadBits(int count)
        {
            if (count is < 0 or > 31 || count > _data.Length * 8 - _position) throw Bad("An H.264 bitstream field is truncated.");
            int value = 0;
            for (int i = 0; i < count; i++, _position++)
                value = (value << 1) | ((_data[_position >> 3] >> (7 - (_position & 7))) & 1);
            return value;
        }
        internal void SkipBits(int count) => ReadBits(count);
        internal int ReadUnsignedExpGolomb()
        {
            int zeros = 0;
            while (!ReadBit()) if (++zeros > 30) throw Bad("An H.264 Exp-Golomb value is out of range.");
            int suffix = zeros == 0 ? 0 : ReadBits(zeros);
            return checked((1 << zeros) - 1 + suffix);
        }
        internal int ReadSignedExpGolomb()
        {
            int code = ReadUnsignedExpGolomb();
            return (code & 1) == 0 ? -(code / 2) : (code + 1) / 2;
        }
    }

    private static void ProcessAac(ReadOnlySpan<byte> payload, PesTiming timing, TrackState state, Stream output)
    {
        int position = 0, frameIndex = 0;
        int sampleRate = 0;
        while (position < payload.Length)
        {
            if (payload.Length - position < 7 || payload[position] != 0xff || (payload[position + 1] & 0xf6) != 0xf0)
                throw Bad("An AAC PES packet contains a truncated or invalid ADTS frame.");
            int profile = ((payload[position + 2] >> 6) & 3) + 1;
            int frequency = (payload[position + 2] >> 2) & 15;
            int channels = ((payload[position + 2] & 1) << 2) | (payload[position + 3] >> 6);
            int header = (payload[position + 1] & 1) == 0 ? 9 : 7;
            int frameLength = ((payload[position + 3] & 3) << 11) | (payload[position + 4] << 3) | (payload[position + 5] >> 5);
            int rawBlocks = payload[position + 6] & 3;
            if (profile != 2 || frequency >= 13 || channels is < 1 or > 7 || rawBlocks != 0
                || frameLength < header || frameLength > payload.Length - position)
                throw Bad("Only complete AAC-LC ADTS frames with one raw block are supported.");
            int rate = SampleRates[frequency];
            byte[] config = AudioConfig(profile, frequency, channels);
            if (state.AudioConfig is not null && !state.AudioConfig.AsSpan().SequenceEqual(config))
                throw Bad("The AAC sample rate or channel layout changed inside one transport program.");
            state.AudioConfig ??= config;
            state.Track.CodecPrivate = config;
            state.Track.SampleRate = rate;
            state.Track.Channels = channels;
            state.Track.Timescale = (uint)rate;
            sampleRate = rate;

            long firstFrameTime = checked((long)((Int128)timing.Pts * rate / 90_000));
            long dts = checked(firstFrameTime + (long)frameIndex * 1024);
            long offset = output.Position;
            output.Write(payload.Slice(position + header, frameLength - header));
            AddSample(state, offset, frameLength - header, dts, 0, 1024, key: true);
            position += frameLength;
            frameIndex++;
        }
        if (sampleRate == 0) throw Bad("An AAC PES packet contains no audio frames.");
    }

    private static readonly int[] SampleRates = [96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000, 7_350];

    private static byte[] AudioConfig(int objectType, int frequencyIndex, int channelConfig)
    {
        int value = (objectType << 11) | (frequencyIndex << 7) | (channelConfig << 3);
        return [(byte)(value >> 8), (byte)value];
    }

    private static void AddSample(TrackState state, long offset, int size, long dts, int cts, int duration, bool key)
    {
        if (size <= 0 || state.Track.Samples.Count >= MaxSamples) throw Bad("A media track exceeds the supported sample limit.");
        if (state.Track.Samples.Count > 0 && dts <= state.Track.Samples[^1].Dts)
            throw Bad("A media track contains duplicate or backward sample timestamps.");
        state.Track.Samples.Add(new TrackSample { Offset = offset, Size = size, Dts = dts, CtsOffset = cts, Duration = duration, Key = key });
    }

    private static void SetVideoDurations(DemuxedTrack track)
    {
        var durations = new List<int>();
        for (int i = 0; i + 1 < track.Samples.Count; i++)
        {
            long delta = track.Samples[i + 1].Dts - track.Samples[i].Dts;
            if (delta is <= 0 or > int.MaxValue) throw Bad("The H.264 decode timeline contains a discontinuity.");
            var sample = track.Samples[i];
            sample.Duration = (int)delta;
            track.Samples[i] = sample;
            durations.Add((int)delta);
        }
        if (durations.Count == 0) throw Bad("At least two H.264 pictures are needed to infer the final frame duration safely.");
        durations.Sort();
        var last = track.Samples[^1];
        last.Duration = durations[durations.Count / 2];
        track.Samples[^1] = last;
    }

    private static void ReadExact(Stream input, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = input.Read(buffer[read..]);
            if (n == 0) throw Bad("The transport stream ended during a packet.");
            read += n;
        }
    }

    private static MediaContainerException Bad(string message) => new(message);
}
