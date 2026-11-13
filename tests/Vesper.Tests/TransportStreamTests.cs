using Vesper.Engine;

namespace Vesper.Tests;

public sealed class TransportStreamTests
{
    [Fact]
    public void RealSyntheticTransportSegments_ContainAudioAndVideo()
    {
        var inspector = new TransportStreamValidator();
        for (int i = 0; i < 5; i++) inspector.Validate(FaultServer.HlsSegment(i));
        inspector.Finish(requireAudio: true, requireVideo: true);
    }
    [Fact]
    public void NullPacketsAlone_AreNotPlayableMedia()
    {
        var packet = new byte[188]; packet[0] = 0x47; packet[1] = 0x1f; packet[2] = 0xff; packet[3] = 0x10;
        var inspector = new TransportStreamValidator(); inspector.Validate(packet);
        Assert.Throws<MediaContainerException>(() => inspector.Finish());
    }
    [Fact]
    public void BadProgramTableCrc_IsRejected()
    {
        var bytes = FaultServer.HlsSegment(0);
        bool corrupted = false;
        for (int p = 0; p < bytes.Length; p += 188)
            if (((bytes[p + 1] & 0x1f) << 8 | bytes[p + 2]) == 0)
            { bytes[p + 12] ^= 1; corrupted = true; break; }
        Assert.True(corrupted);
        var inspector = new TransportStreamValidator();
        Assert.Throws<MediaContainerException>(() => inspector.Validate(bytes));
    }
    [Fact]
    public void ScrambledPackets_AreRejectedWithoutTryingToBypassProtection()
    {
        var bytes = FaultServer.HlsSegment(0); bytes[3] |= 0x80;
        Assert.Throws<MediaContainerException>(() => new TransportStreamValidator().Validate(bytes));
    }

    [Fact]
    public void H264AacTransportStream_DemuxesAndRemuxesToValidatedMatroska()
    {
        byte[] transport = FaultServer.HlsExpected();
        using var input = new MemoryStream(transport);
        using var samples = new MemoryStream();
        List<DemuxedTrack> tracks = MpegTsDemuxer.Read(input, input.Length, samples, CancellationToken.None);

        DemuxedTrack video = Assert.Single(tracks, track => track.Kind == TrackKind.Video);
        DemuxedTrack audio = Assert.Single(tracks, track => track.Kind == TrackKind.Audio);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(640, video.Width);
        Assert.Equal(360, video.Height);
        Assert.NotEmpty(video.Samples);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal(48_000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.NotEmpty(audio.Samples);

        using var output = new MemoryStream();
        var inputs = tracks.Select(track => (Track: track, Source: (Stream)samples)).ToList();
        MergeResult result = MediaMerger.Merge(inputs, "mkv", output, CancellationToken.None);

        Assert.Equal("mkv", result.Container);
        MediaMerger.Validate(output, output.Length, tracks, CancellationToken.None);
    }

    [Fact]
    public void Demux_RejectsPartialTransportPackets()
    {
        using var input = new MemoryStream(new byte[187]);
        using var samples = new MemoryStream();

        Assert.Throws<MediaContainerException>(() =>
            MpegTsDemuxer.Read(input, input.Length, samples, CancellationToken.None));
    }

    [Fact]
    public void Demux_RejectsBackwardDecodeTimestampDiscontinuity()
    {
        byte[] first = FaultServer.HlsSegment(0);
        RewindSecondVideoPes(first);
        using var input = new MemoryStream();
        input.Write(first);
        for (int i = 1; i < 5; i++) input.Write(FaultServer.HlsSegment(i));
        input.Position = 0;
        using var samples = new MemoryStream();

        Assert.Throws<MediaContainerException>(() =>
            MpegTsDemuxer.Read(input, input.Length, samples, CancellationToken.None));
    }

    private static void RewindSecondVideoPes(byte[] transport)
    {
        const int videoPid = 0x100;
        int pesCount = 0;
        for (int packetOffset = 0; packetOffset < transport.Length; packetOffset += 188)
        {
            Span<byte> packet = transport.AsSpan(packetOffset, 188);
            int pid = ((packet[1] & 0x1f) << 8) | packet[2];
            if (pid != videoPid || (packet[1] & 0x40) == 0) continue;
            int control = (packet[3] >> 4) & 3;
            if ((control & 1) == 0) continue;
            int payload = 4;
            if ((control & 2) != 0) payload += 1 + packet[4];
            if (payload + 15 >= packet.Length || packet[payload] != 0 || packet[payload + 1] != 0 || packet[payload + 2] != 1)
                continue;
            if (++pesCount != 2) continue;
            int flags = (packet[payload + 7] >> 6) & 3;
            WriteTimestamp(packet.Slice(payload + 9, 5), flags == 3 ? 3 : 2, 0);
            if (flags == 3) WriteTimestamp(packet.Slice(payload + 14, 5), 1, 0);
            return;
        }
        throw new InvalidOperationException("The fixture has fewer than two video PES packets.");
    }

    private static void WriteTimestamp(Span<byte> bytes, int prefix, ulong value)
    {
        bytes[0] = (byte)((prefix << 4) | (int)(((value >> 30) & 7) << 1) | 1);
        bytes[1] = (byte)(value >> 22);
        bytes[2] = (byte)((((value >> 15) & 0x7f) << 1) | 1);
        bytes[3] = (byte)(value >> 7);
        bytes[4] = (byte)(((value & 0x7f) << 1) | 1);
    }
}
