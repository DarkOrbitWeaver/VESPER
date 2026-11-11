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
}
