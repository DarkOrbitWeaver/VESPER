using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols;
using Vesper.MediaResolver.Protocols.Hls;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ProtocolTests;

public class HlsParserTests
{
    private static readonly Uri MasterUrl = new("https://cdn.example.com/hls/master.m3u8");
    private const string HlsMime = "application/vnd.apple.mpegurl";

    private static ManifestResolver Manifests(FakeHttpHandler? handler = null)
    {
        var o = new ResolverOptions();
        return new ManifestResolver(new SecureHttpClient(o, handler ?? new FakeHttpHandler()), o);
    }

    [Fact]
    public void Master_ParsesVariantsRelativeUrlsAndQuotedCommas()
    {
        var r = HlsParser.Parse(Fixture.Load("hls_master.m3u8"), MasterUrl);

        Assert.True(r.IsMaster);
        Assert.Equal(3, r.Master!.Variants.Count);
        Assert.Equal(new Uri("https://cdn.example.com/hls/v720/prog.m3u8"), r.Master.Variants[1].Url);
        Assert.Equal("avc1.64001f,mp4a.40.2", r.Master.Variants[1].Codecs);
        Assert.Equal(1920, r.Master.Variants[2].Width);
        Assert.Equal(60, r.Master.Variants[2].FrameRate);
        Assert.Equal(5_000_000, r.Master.Variants[2].AverageBandwidth);
        Assert.Equal("Espanol, latino", r.Master.Renditions.Single(x => x.Language == "es").Name);
    }

    [Fact]
    public void Master_WithSeparateAudio_YieldsVideoOnlyVariantsAndAudioRepresentations()
    {
        var master = HlsParser.Parse(Fixture.Load("hls_master.m3u8"), MasterUrl).Master!;
        var result = HlsRepresentationBuilder.FromMaster(master, MasterUrl, MediaRequestContext.Empty, probe: null);

        var video = result.Representations.Where(r => r.Height is not null).ToList();
        Assert.Equal(3, video.Count);
        Assert.All(video, v =>
        {
            Assert.Equal(StreamContent.VideoOnly, v.Content);
            Assert.Equal("aud", v.AudioGroupId);
            Assert.Null(v.FileSize);                       // no duration known: no size estimate
        });
        Assert.Equal(2, result.Representations.Count(r => r.Content == StreamContent.AudioOnly));
        Assert.Contains(result.Representations, r => r.AudioLanguage == "es" && r.Url.AbsolutePath.EndsWith("/audio/es/prog.m3u8"));
        Assert.Contains(video, v => v.Height == 1080 && v.FrameRate == 60 && v.VideoCodec == "h264");
        Assert.Single(result.Subtitles);
        Assert.Equal("hls-webvtt", result.Subtitles[0].Format);
    }

    [Fact]
    public void MediaPlaylist_ParsesSegmentsInitSegmentAndDiscontinuity()
    {
        var url = new Uri("https://cdn.example.com/v/prog.m3u8");
        var p = HlsParser.Parse(Fixture.Load("hls_media_vod.m3u8"), url).Media!;

        Assert.Equal(3, p.Segments.Count);
        Assert.Equal(16.5, p.TotalDuration, 3);
        Assert.True(p.HasDiscontinuities);
        Assert.False(p.Segments[1].DiscontinuityBefore);
        Assert.True(p.Segments[2].DiscontinuityBefore);
        Assert.True(p.HasInitSegment);
        Assert.Equal(new Uri("https://cdn.example.com/v/init.mp4"), p.Segments[0].Map!.Url);
        Assert.False(p.IsLive);
    }

    [Fact]
    public void ByteRanges_ContinueFromPreviousRange_AndAes128IsRecognised()
    {
        var url = new Uri("https://cdn.example.com/v/prog.m3u8");
        var p = HlsParser.Parse(Fixture.Load("hls_media_aes_byterange.m3u8"), url).Media!;

        Assert.Equal(new ByteRange(0, 1000), p.Segments[0].Range);
        Assert.Equal(new ByteRange(1000, 1000), p.Segments[1].Range);
        Assert.Equal(HlsProtection.Aes128, p.Protection);
        Assert.Equal(new Uri("https://keys.example.com/k1"), p.Segments[0].Key!.KeyUri);

        var rep = HlsRepresentationBuilder.FromMedia(p, url, MediaRequestContext.Empty).Representations.Single();
        Assert.True(rep.Requirements.HasFlag(ProcessingRequirement.DecryptAes128));
        Assert.True(rep.Requirements.HasFlag(ProcessingRequirement.HlsSegmentAssembly));
        Assert.Equal(2, rep.Segments!.Segments.Count);
        Assert.True(rep.AudioUnverified);
    }

    [Fact]
    public async Task Master_ResolvedOverHttp_LearnsContainerDurationAndEstimatesSize()
    {
        var h = new FakeHttpHandler()
            .AddFile(MasterUrl.AbsoluteUri, "hls_master.m3u8", HlsMime)
            .AddFile("https://cdn.example.com/hls/v360/prog.m3u8", "hls_media_vod.m3u8", HlsMime);

        var result = await Manifests(h).ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(16.5), result.Duration);
        var v360 = result.Representations.Single(r => r.Height == 360);
        Assert.Equal("mp4", v360.Container);                       // init segment => fragmented MP4
        Assert.Equal(1_650_000, v360.FileSize);                    // 800 kbps average * 16.5 s
        Assert.True(v360.FileSizeIsEstimate);
        Assert.Equal(3, h.Requests.Count);                         // master, selected variant, and its audio probe
    }

    [Fact]
    public async Task Master_ProbeUsesTheSameHighestMuxedVariantAsTheEngine()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhigh.m3u8\n";
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n";
        const string vod = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n#EXT-X-ENDLIST\n";
        var h = new FakeHttpHandler()
            .Add(MasterUrl.AbsoluteUri, master, HlsMime)
            .Add("https://cdn.example.com/hls/low.m3u8", live, HlsMime)
            .Add("https://cdn.example.com/hls/high.m3u8", vod, HlsMime);

        var result = await Manifests(h).ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None);

        Assert.Equal("https://cdn.example.com/hls/high.m3u8", h.Requests[1].Url.AbsoluteUri);
        Assert.Equal(2, result.Representations.Count);
    }

    [Fact]
    public async Task Master_ProbeMetadataAppliesOnlyToTheInspectedVariant()
    {
        const string master = "#EXTM3U\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",URI=\"audio.m3u8\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=800000,AVERAGE-BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,AVERAGE-BANDWIDTH=2000000,RESOLUTION=1920x1080\nhigh.m3u8\n";
        const string inspected = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"key\",IV=0x00000000000000000000000000000000\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\nsegment.m4s\n#EXT-X-ENDLIST\n";
        var h = new FakeHttpHandler()
            .Add(MasterUrl.AbsoluteUri, master, HlsMime)
            .Add("https://cdn.example.com/hls/high.m3u8", inspected, HlsMime);

        var result = await Manifests(h).ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None);

        var high = result.Representations.Single(r => r.Height == 1080);
        var low = result.Representations.Single(r => r.Height == 360);
        var audio = result.Representations.Single(r => r.Content == StreamContent.AudioOnly);
        Assert.Equal("mp4", high.Container);
        Assert.Equal(250_000, high.FileSize);
        Assert.True(high.Requirements.HasFlag(ProcessingRequirement.DecryptAes128));
        Assert.Equal("https://cdn.example.com/hls/high.m3u8", h.Requests[1].Url.AbsoluteUri);
        Assert.Null(low.Container);
        Assert.Null(low.FileSize);
        Assert.False(low.Requirements.HasFlag(ProcessingRequirement.DecryptAes128));
        Assert.Null(audio.Container);
        Assert.False(audio.Requirements.HasFlag(ProcessingRequirement.DecryptAes128));
    }

    [Fact]
    public async Task SeparateAudio_ProbesTheDeclaredDefaultGroupRendition()
    {
        const string master = "#EXTM3U\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Default\",DEFAULT=YES,URI=\"audio.m3u8\"\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Other\",URI=\"other.m3u8\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\",AUDIO=\"aud\"\nvideo.m3u8\n";
        const string vod = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2,\npiece.m4s\n#EXT-X-ENDLIST\n";
        var handler = new FakeHttpHandler().Add(MasterUrl.AbsoluteUri, master, HlsMime)
            .Add("https://cdn.example.com/hls/video.m3u8", vod, HlsMime)
            .Add("https://cdn.example.com/hls/audio.m3u8", vod, HlsMime);

        var result = await Manifests(handler).ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None);

        var video = result.Representations.Single(r => r.Content == StreamContent.VideoOnly);
        var audio = result.Representations.Single(r => r.IsDefaultAudio);
        Assert.Equal("mp4", Vesper.MediaResolver.Formats.MergeCapability.MergeContainer(video, audio));
        Assert.Null(result.Representations.Single(r => r.Label == "Other").Container);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Master_ProbeRejectsTheEngineSelectedVariantWhenItIsLive()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhigh.m3u8\n";
        const string vod = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n#EXT-X-ENDLIST\n";
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n";
        var h = new FakeHttpHandler()
            .Add(MasterUrl.AbsoluteUri, master, HlsMime)
            .Add("https://cdn.example.com/hls/low.m3u8", vod, HlsMime)
            .Add("https://cdn.example.com/hls/high.m3u8", live, HlsMime);

        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests(h).ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None));

        Assert.Equal(MediaErrorCode.LiveStreamNotSupported, ex.Error.Code);
        Assert.Equal("https://cdn.example.com/hls/high.m3u8", h.Requests[1].Url.AbsoluteUri);
    }

    [Fact]
    public async Task FairPlayKeyFormat_IsReportedAsDrm()
    {
        const string drm = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:6,\nseg.ts\n#EXT-X-ENDLIST\n";
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(drm, MasterUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.DrmProtected, ex.Error.Code);
        Assert.True(ex.Error.IsPermanent);
        Assert.False(ex.Error.BrowserMayHelp);
    }

    [Fact]
    public async Task SampleAesIdentity_IsUnsupportedNotDrm()
    {
        const string text = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"https://k.example.com/1\"\n#EXTINF:6,\nseg.ts\n#EXT-X-ENDLIST\n";
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(text, MasterUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public async Task PlaylistWithoutEndList_IsLive()
    {
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:100\n#EXTINF:6,\nseg100.ts\n#EXTINF:6,\nseg101.ts\n";
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(live, MasterUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.LiveStreamNotSupported, ex.Error.Code);
    }

    [Fact]
    public async Task VodPlaylistWithoutEndList_IsStillIncomplete()
    {
        const string vod = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts\n";
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(vod, MasterUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.LiveStreamNotSupported, ex.Error.Code);
    }

    [Fact]
    public async Task OpaquePlaylistDoesNotOfferFormats()
    {
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() => Manifests().ResolveTextAsync(
            "g1ifpR9Pk79g4MVYlAMFQBYLj2sl1DVbHXaU5smx8bs=", MasterUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
        Assert.Contains("did not send a readable", ex.Error.UserMessage);
    }

    [Fact]
    public void MissingHeader_AndHostileUris_AreRejected()
    {
        var a = Assert.Throws<MediaResolverException>(() => HlsParser.Parse("<html>nope</html>", MasterUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, a.Error.Code);

        var b = Assert.Throws<MediaResolverException>(() =>
            HlsParser.Parse("#EXTM3U\n#EXTINF:6,\nfile:///C:/Windows/win.ini\n#EXT-X-ENDLIST\n", MasterUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, b.Error.Code);

        var c = Assert.Throws<MediaResolverException>(() =>
            HlsParser.Parse("#EXTM3U\n#EXTINF:6,\njavascript:alert(1)\n#EXT-X-ENDLIST\n", MasterUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, c.Error.Code);
    }

    [Fact]
    public void SegmentLimit_IsEnforced()
    {
        var sb = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:1\n");
        for (int i = 0; i < 50; i++) sb.Append("#EXTINF:1,\ns").Append(i).Append(".ts\n");
        sb.Append("#EXT-X-ENDLIST\n");
        var ex = Assert.Throws<MediaResolverException>(() => HlsParser.Parse(sb.ToString(), MasterUrl, maxSegments: 10));
        Assert.Equal(MediaErrorCode.ResponseTooLarge, ex.Error.Code);
    }

    [Theory]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188\na.ts\n")]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188@0\na.ts\n#EXTINF:1,\n#EXT-X-BYTERANGE:188\nb.ts\n")]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188@0\na.ts\n#EXTINF:1,\nb.ts\n#EXTINF:1,\n#EXT-X-BYTERANGE:188\na.ts\n")]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188@-1\na.ts\n")]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188@0@1\na.ts\n")]
    [InlineData("#EXTINF:1,\n#EXT-X-BYTERANGE:188@9223372036854775807\na.ts\n")]
    public void InvalidByteRangesAreRejectedDuringDiscovery(string segments)
    {
        var ex = Assert.Throws<MediaResolverException>(() =>
            HlsParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:1\n" + segments + "#EXT-X-ENDLIST\n", MasterUrl));

        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Theory]
    [InlineData(false, "#EXT-X-GAP")]
    [InlineData(false, "#EXT-X-I-FRAMES-ONLY")]
    [InlineData(true, "#EXT-X-GAP")]
    [InlineData(true, "#EXT-X-I-FRAMES-ONLY")]
    public async Task GapAndIFrameOnlyPlaylistsAreRejectedBeforeOffering(bool throughMaster, string feature)
    {
        string media = $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n{feature}\n#EXTINF:1,\nseg.ts\n#EXT-X-ENDLIST\n";
        var h = new FakeHttpHandler();
        if (throughMaster)
        {
            const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360\nprobe.m3u8\n";
            h.Add(MasterUrl.AbsoluteUri, master, HlsMime)
                .Add("https://cdn.example.com/hls/probe.m3u8", media, HlsMime);
        }

        var manifests = Manifests(h);
        Task<ManifestResult> resolve = throughMaster
            ? manifests.ResolveAsync(MasterUrl, MediaRequestContext.Empty, CancellationToken.None)
            : manifests.ResolveTextAsync(media, MasterUrl, MediaRequestContext.Empty, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() => resolve);

        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public async Task LivePlaylistWithGapStillReportsLiveUnsupported()
    {
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-GAP\n#EXTINF:1,\nseg.ts\n";

        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(live, MasterUrl, MediaRequestContext.Empty, CancellationToken.None));

        Assert.Equal(MediaErrorCode.LiveStreamNotSupported, ex.Error.Code);
    }

    [Fact]
    public void AttributeParser_HandlesQuotesAndUnquotedValues()
    {
        var a = HlsAttributes.Parse("BANDWIDTH=1000,CODECS=\"avc1.4d401f,mp4a.40.2\",RESOLUTION=1280x720,NAME=\"a=b,c\"");
        Assert.Equal("1000", a["BANDWIDTH"]);
        Assert.Equal("avc1.4d401f,mp4a.40.2", a["CODECS"]);
        Assert.Equal("1280x720", a["RESOLUTION"]);
        Assert.Equal("a=b,c", a["NAME"]);
    }
}
