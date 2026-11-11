using Vesper.Engine;

namespace Vesper.Tests;

public sealed class HlsSafetyTests
{
    private static readonly Uri Base = new("https://cdn.example.com/index.m3u8");
    [Fact]
    public void ImplicitRange_CannotBeginWithoutAPreviousRange()
    {
        var parsed = HlsParser.ParseMedia("#EXTM3U\n#EXTINF:1,\n#EXT-X-BYTERANGE:188\na.ts\n#EXT-X-ENDLIST", Base);
        Assert.NotNull(parsed.Unsupported);
    }
    [Fact]
    public void ImplicitRange_CannotChangeResources()
    {
        var parsed = HlsParser.ParseMedia("#EXTM3U\n#EXTINF:1,\n#EXT-X-BYTERANGE:188@0\na.ts\n#EXTINF:1,\n#EXT-X-BYTERANGE:188\nb.ts\n#EXT-X-ENDLIST", Base);
        Assert.NotNull(parsed.Unsupported);
    }
    [Fact]
    public void ImplicitRanges_OfTheSameResourceAreOrdered()
    {
        var parsed = HlsParser.ParseMedia("#EXTM3U\n#EXTINF:1,\n#EXT-X-BYTERANGE:188@0\na.ts\n#EXTINF:1,\n#EXT-X-BYTERANGE:188\na.ts\n#EXT-X-ENDLIST", Base);
        Assert.Null(parsed.Unsupported); Assert.Equal(188, parsed.Segments[1].RangeOffset);
    }
    [Theory]
    [InlineData("#EXT-X-DISCONTINUITY")]
    [InlineData("#EXT-X-GAP")]
    [InlineData("#EXT-X-I-FRAMES-ONLY")]
    public void UnsupportedTimelineOperations_AreNotIgnored(string tag)
    {
        Assert.NotNull(HlsParser.ParseMedia($"#EXTM3U\n{tag}\n#EXTINF:1,\na.ts\n#EXT-X-ENDLIST", Base).Unsupported);
    }
    [Fact]
    public void InvalidExplicitAesIv_DoesNotFallBackToTheSequenceNumber()
    {
        Assert.NotNull(HlsParser.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\",IV=0xnothex\n#EXTINF:1,\na.ts\n#EXT-X-ENDLIST", Base).Unsupported);
    }
    [Fact]
    public void EncryptedMap_RequiresItsOwnExplicitIv()
    {
        Assert.NotNull(HlsParser.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\na.m4s\n#EXT-X-ENDLIST", Base).Unsupported);
    }
    [Fact]
    public void MatchingAudioGroup_NotAnyAudioGroup_DeterminesSeparateAudio()
    {
        var variants = HlsParser.ParseMaster("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"external\",URI=\"audio.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1000,AUDIO=\"embedded\",CODECS=\"avc1.4d401f,mp4a.40.2\"\nvideo.m3u8\n", Base);
        Assert.False(Assert.Single(variants).NeedsSeparateAudio);
    }
    [Fact]
    public void VodDeclaration_WithoutEndList_IsNotCompletedMedia()
    {
        Assert.False(HlsParser.ParseMedia("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:1,\na.ts\n", Base).IsVod);
    }
    [Fact]
    public void ArbitraryBytes_AreNotSuccessfulTransportStreamData()
    {
        Assert.Throws<MediaContainerException>(() => MediaPartialIntegrity.ValidateTransportStream(new byte[188]));
        Assert.Throws<MediaContainerException>(() => MediaPartialIntegrity.ValidateTransportStream(new byte[189]));
    }
}
