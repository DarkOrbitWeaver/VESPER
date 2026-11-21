using System.Buffers.Binary;

namespace Vesper.Engine;

internal readonly record struct AacAdtsFrame(int HeaderLength, int FrameLength, int Profile,
    int FrequencyIndex, int Channels)
{
    internal int SampleRate => AacAdtsParser.SampleRate(FrequencyIndex);
    internal int PayloadLength => FrameLength - HeaderLength;
}

internal static class AacAdtsParser
{
    private static readonly int[] SampleRates =
        [96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000, 7_350];

    internal static AacAdtsFrame ReadFrame(ReadOnlySpan<byte> payload, int position, string packetName)
    {
        if (payload.Length - position < 7 || payload[position] != 0xff || (payload[position + 1] & 0xf6) != 0xf0)
            throw Bad($"{packetName} contains a truncated or invalid ADTS frame.");

        int profile = ((payload[position + 2] >> 6) & 3) + 1;
        int frequency = (payload[position + 2] >> 2) & 15;
        int channels = ((payload[position + 2] & 1) << 2) | (payload[position + 3] >> 6);
        int headerLength = (payload[position + 1] & 1) == 0 ? 9 : 7;
        int frameLength = ((payload[position + 3] & 3) << 11) | (payload[position + 4] << 3) | (payload[position + 5] >> 5);
        int rawBlocks = payload[position + 6] & 3;
        if (profile != 2 || frequency >= SampleRates.Length || channels is < 1 or > 7 || rawBlocks != 0
            || frameLength < headerLength || frameLength > payload.Length - position)
            throw Bad("Only complete AAC-LC ADTS frames with one raw block are supported.");

        return new AacAdtsFrame(headerLength, frameLength, profile, frequency, channels);
    }

    internal static int SampleRate(int frequencyIndex)
    {
        if ((uint)frequencyIndex >= (uint)SampleRates.Length)
            throw Bad("The AAC sample rate is unsupported.");
        return SampleRates[frequencyIndex];
    }

    internal static byte[] AudioSpecificConfig(AacAdtsFrame frame)
    {
        int value = (frame.Profile << 11) | (frame.FrequencyIndex << 7) | (frame.Channels << 3);
        return [(byte)(value >> 8), (byte)value];
    }

    private static MediaContainerException Bad(string message) => new(message);
}

/// <summary>Demuxes HLS packed AAC audio segments with their required ID3 timestamp tags.</summary>
internal static class PackedAudioDemuxer
{
    private const long MaxSamples = 1_000_000;
    private const long MaxSegmentBytes = 64L * 1024 * 1024;
    private const ulong TimestampModulus = 1UL << 33;
    private const ulong TimestampHalf = TimestampModulus / 2;
    private const long TimestampScale = 90_000;
    private const int AacFrameSamples = 1024;
    private static readonly byte[] TimestampOwner = "com.apple.streaming.transportStreamTimestamp"u8.ToArray();

    private sealed class Clock
    {
        internal ulong? Raw;
        internal long Value;
    }

    internal static List<DemuxedTrack> Read(Stream input, IReadOnlyList<long> segmentLengths, Stream payloads,
        CancellationToken ct)
    {
        if (!input.CanRead || !input.CanSeek || !payloads.CanWrite || !payloads.CanSeek)
            throw Bad("The packed audio and media sample stores must be seekable.");
        if (segmentLengths.Count == 0)
            throw Bad("The packed audio rendition contains no media segments.");

        long totalLength = 0;
        foreach (long segmentLength in segmentLengths)
        {
            if (segmentLength is <= 0 or > MaxSegmentBytes || totalLength > long.MaxValue - segmentLength)
                throw Bad("A packed audio segment has an invalid size.");
            totalLength += segmentLength;
        }
        if (input.Length != totalLength)
            throw Bad("The packed audio segment boundaries do not match the saved rendition length.");

        var track = new DemuxedTrack
        {
            Kind = TrackKind.Audio,
            Codec = "aac",
            SourceContainer = "mpegts",
            Language = "und"
        };
        var clock = new Clock();
        byte[]? audioConfig = null;
        long inputOffset = 0;

        foreach (long segmentLength in segmentLengths)
        {
            ct.ThrowIfCancellationRequested();
            byte[] segment = ReadSegment(input, inputOffset, checked((int)segmentLength));
            inputOffset += segmentLength;

            int audioOffset = ReadId3Timestamp(segment, out ulong rawTimestamp);
            long timestamp = Unwrap(rawTimestamp, clock);
            long segmentDts = 0;
            int frameIndex = 0;
            while (audioOffset < segment.Length)
            {
                ct.ThrowIfCancellationRequested();
                AacAdtsFrame frame = AacAdtsParser.ReadFrame(segment, audioOffset, "An AAC HLS segment");
                int sampleRate = frame.SampleRate;
                if (audioConfig is null)
                {
                    audioConfig = AacAdtsParser.AudioSpecificConfig(frame);
                    track.CodecPrivate = audioConfig;
                    track.SampleRate = sampleRate;
                    track.Channels = frame.Channels;
                    track.Timescale = (uint)sampleRate;
                }
                else if (track.SampleRate != sampleRate || track.Channels != frame.Channels)
                {
                    throw Bad("The AAC sample rate or channel layout changed inside the packed audio rendition.");
                }

                if (frameIndex == 0)
                    segmentDts = checked((long)((Int128)timestamp * sampleRate / TimestampScale));
                long dts = checked(segmentDts + (long)frameIndex * AacFrameSamples);
                long outputOffset = payloads.Position;
                ReadOnlySpan<byte> sample = segment.AsSpan(audioOffset + frame.HeaderLength, frame.PayloadLength);
                payloads.Write(sample);
                AddSample(track, outputOffset, sample.Length, dts);
                audioOffset += frame.FrameLength;
                frameIndex++;
            }

            if (frameIndex == 0)
                throw Bad("An AAC HLS segment contains no complete ADTS frames.");
        }

        if (track.Samples.Count == 0)
            throw Bad("The packed audio rendition contains no complete AAC samples.");
        return [track];
    }

    private static int ReadId3Timestamp(ReadOnlySpan<byte> segment, out ulong timestamp)
    {
        timestamp = 0;
        if (segment.Length < 10 || !segment[..3].SequenceEqual("ID3"u8)
            || segment[3] != 3 || segment[4] != 0 || segment[5] != 0)
            throw Bad("An AAC HLS segment must begin with an unmodified ID3v2.3 tag.");

        int tagSize = ReadSynchsafeSize(segment.Slice(6, 4));
        if (tagSize > segment.Length - 10)
            throw Bad("An AAC HLS segment contains a truncated ID3 tag.");

        int position = 10;
        int tagEnd = position + tagSize;
        bool foundTimestamp = false;
        while (position < tagEnd)
        {
            if (segment[position] == 0)
            {
                if (segment[position..tagEnd].IndexOfAnyExcept((byte)0) >= 0)
                    throw Bad("An ID3 tag has invalid data after its padding.");
                break;
            }
            if (tagEnd - position < 10)
                throw Bad("An ID3 frame header is truncated.");

            ReadOnlySpan<byte> frameHeader = segment.Slice(position, 10);
            if (!IsFrameId(frameHeader[..4]))
                throw Bad("An ID3 tag contains an invalid frame identifier.");
            uint rawFrameLength = BinaryPrimitives.ReadUInt32BigEndian(frameHeader[4..8]);
            if (rawFrameLength > int.MaxValue)
                throw Bad("An ID3 frame is too large.");
            int frameLength = (int)rawFrameLength;
            if (frameLength > tagEnd - position - 10)
                throw Bad("An ID3 frame extends beyond its tag.");

            if (frameHeader[..4].SequenceEqual("PRIV"u8))
            {
                if (frameHeader[8] != 0 || frameHeader[9] != 0)
                    throw Bad("The ID3 timestamp frame uses unsupported flags.");
                ReadOnlySpan<byte> body = segment.Slice(position + 10, frameLength);
                int ownerEnd = body.IndexOf((byte)0);
                if (ownerEnd < 0)
                    throw Bad("An ID3 private frame has no owner terminator.");
                if (body[..ownerEnd].SequenceEqual(TimestampOwner))
                {
                    if (foundTimestamp || body.Length - ownerEnd - 1 != 8)
                        throw Bad("An ID3 tag contains a duplicate or invalid transport timestamp frame.");
                    timestamp = BinaryPrimitives.ReadUInt64BigEndian(body[(ownerEnd + 1)..]);
                    if (timestamp >= TimestampModulus)
                        throw Bad("An ID3 transport timestamp exceeds the MPEG 33-bit range.");
                    foundTimestamp = true;
                }
            }

            position += 10 + frameLength;
        }

        if (!foundTimestamp)
            throw Bad("An ID3 tag has no com.apple.streaming.transportStreamTimestamp frame.");
        return tagEnd;
    }

    private static int ReadSynchsafeSize(ReadOnlySpan<byte> bytes)
    {
        int size = 0;
        foreach (byte value in bytes)
        {
            if ((value & 0x80) != 0)
                throw Bad("An ID3 tag has an invalid synchsafe size.");
            size = (size << 7) | value;
        }
        return size;
    }

    private static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        foreach (byte value in id)
            if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'0' and <= (byte)'9'))
                return false;
        return true;
    }

    private static long Unwrap(ulong raw, Clock clock)
    {
        if (raw >= TimestampModulus)
            throw Bad("An ID3 transport timestamp exceeds the MPEG 33-bit range.");
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
        if (value < clock.Value)
            throw Bad("Packed AAC timestamps move backward without a discontinuity.");
        clock.Raw = raw;
        clock.Value = value;
        return value;
    }

    private static byte[] ReadSegment(Stream input, long offset, int length)
    {
        var segment = new byte[length];
        input.Position = offset;
        input.ReadExactly(segment);
        return segment;
    }

    private static void AddSample(DemuxedTrack track, long offset, int size, long dts)
    {
        if (size <= 0 || track.Samples.Count >= MaxSamples)
            throw Bad("A packed audio track has an empty sample or exceeds the sample limit.");
        if (track.Samples.Count > 0 && dts <= track.Samples[^1].Dts)
            throw Bad("A packed audio track contains duplicate or backward sample timestamps.");
        track.Samples.Add(new TrackSample
        {
            Offset = offset,
            Size = size,
            Dts = dts,
            Duration = AacFrameSamples,
            Key = true
        });
    }

    private static MediaContainerException Bad(string message) => new(message);
}
