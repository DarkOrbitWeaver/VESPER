using System.Buffers.Binary;
using System.Text;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.DiscoveryTests;

/// <summary>Browser-observed direct files are opened once, a bounded amount, to confirm what they contain.</summary>
public sealed class DirectFileFactsTests
{
    private const string Page = "https://unseen-site.example/watch/7";
    private const string VideoUrl = "https://cdn.unseen-site.example/m/7/video";
    private const string AudioUrl = "https://cdn.unseen-site.example/m/7/audio";

    [Theory]
    [InlineData(200, true)]
    [InlineData(404, false)]
    public async Task Exact_lookup_never_reads_the_page_or_calls_its_site_adapter(int status, bool success)
    {
        const string page = "https://www.dailymotion.com/video/x12345";
        var handler = new FakeHttpHandler()
            .Add(page, $"<video src=\"{AudioUrl}\"></video>")
            .AddMedia(AudioUrl, "audio/mp4")
            .Add(VideoUrl, "fixture", "video/mp4", status: status);
        using var resolver = T.Resolver(handler);
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(page),
            Options = new DiscoveryOptions { ExactBrowserSource = true },
            Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page, Candidates = [new() { Url = VideoUrl }]
            }
        }, CancellationToken.None);

        Assert.Equal(success, result.IsSuccess);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Equal(VideoUrl, request.Url.AbsoluteUri));
        if (success) Assert.Equal(VideoUrl, Assert.Single(result.Item!.Representations).Url.AbsoluteUri);
        else Assert.Equal(MediaErrorCode.VideoUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task A_silent_video_file_and_its_audio_file_are_reported_as_what_they_are()
    {
        var handler = new FakeHttpHandler()
            .Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576)), "video/mp4")
            .Add(AudioUrl, Mp4(("soun", "mp4a", 0, 0)), "video/mp4");

        MediaItem item = await ResolveAsync(handler, VideoUrl, AudioUrl);

        MediaRepresentation video = Assert.Single(item.Representations, r => r.Url.AbsoluteUri == VideoUrl);
        Assert.Equal(StreamContent.VideoOnly, video.Content);
        Assert.True(video.StandaloneVideo);
        Assert.False(video.AudioUnverified);
        Assert.Equal((1024, 576), (video.Width, video.Height));
        Assert.NotNull(video.VideoCodec);
        // The address and content type both said "video". The track table says otherwise.
        MediaRepresentation audio = Assert.Single(item.Representations, r => r.Url.AbsoluteUri == AudioUrl);
        Assert.Equal(StreamContent.AudioOnly, audio.Content);
        Assert.Equal(TimeSpan.FromSeconds(10), item.Duration);
    }

    [Fact]
    public async Task A_file_with_both_tracks_has_confirmed_sound()
    {
        var handler = new FakeHttpHandler()
            .Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576), ("soun", "mp4a", 0, 0)), "video/mp4");

        MediaRepresentation file = Assert.Single((await ResolveAsync(handler, VideoUrl)).Representations);

        Assert.Equal(StreamContent.VideoAndAudio, file.Content);
        Assert.False(file.AudioUnverified);
        Assert.NotNull(file.AudioCodec);
    }

    [Fact]
    public async Task A_file_whose_track_table_cannot_be_read_keeps_its_sound_unverified()
    {
        var handler = new FakeHttpHandler().AddMedia(VideoUrl);

        MediaRepresentation file = Assert.Single((await ResolveAsync(handler, VideoUrl)).Representations);

        Assert.Equal(StreamContent.VideoAndAudio, file.Content);
        Assert.True(file.AudioUnverified);
        Assert.Null(file.Height);
    }

    [Fact]
    public async Task An_inconsistent_range_is_not_published()
    {
        var handler = new FakeHttpHandler().Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576)), "video/mp4",
            contentHeaders: new() { ["Content-Range"] = "bytes 0-80/81" }, ignoreRange: true);

        var result = await ResolveResultAsync(handler, VideoUrl);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task At_most_three_files_are_opened_and_each_read_is_bounded()
    {
        var handler = new FakeHttpHandler();
        string[] urls = Enumerable.Range(1, 5).Select(i => $"https://cdn.unseen-site.example/m/7/file-{i}.mp4").ToArray();
        foreach (string url in urls) handler.AddMedia(url);

        await ResolveAsync(handler, urls);

        var reads = handler.Requests.Where(r => r.Method == HttpMethod.Get && r.Headers.TryGetValue("Range", out string? range)
            && range != "bytes=0-0").ToList();
        Assert.Equal(3, reads.Count);
        Assert.All(reads, read => Assert.Equal("bytes=0-262143", read.Headers["Range"]));
    }

    [Fact]
    public async Task X_asset_group_combines_verified_qualities_without_fetching_the_post()
    {
        const string page = "https://x.com/example/status/1234567890";
        const string low = "https://video.twimg.com/ext_tw_video/9876543210/pu/vid/512x256/low.mp4";
        const string high = "https://video.twimg.com/ext_tw_video/9876543210/pu/vid/1024x576/high.mp4";
        var handler = new FakeHttpHandler()
            .Add(low, Mp4(("vide", "avc1", 512, 256), ("soun", "mp4a", 0, 0)), "video/mp4")
            .Add(high, Mp4(("vide", "avc1", 1024, 576), ("soun", "mp4a", 0, 0)), "video/mp4");
        using var resolver = T.Resolver(handler);
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(page), Options = new DiscoveryOptions { ExactBrowserAssetGroup = true },
            Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page,
                Candidates = [new() { Url = low, Kind = "direct" }, new() { Url = high, Kind = "direct" }]
            }
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(new int?[] { 256, 576 }, result.Item!.Representations.Select(r => r.Height).Order().ToArray());
        Assert.All(handler.Requests, request => Assert.Contains(request.Url.AbsoluteUri, new[] { low, high }));
    }

    [Theory]
    [InlineData("https://video.twimg.com/ext_tw_video/9876543211/pu/vid/other.mp4")]
    [InlineData("https://video.twimg.com.evil.example/ext_tw_video/9876543210/pu/vid/other.mp4")]
    [InlineData("https://video.twimg.com/amplify_video/9876543210/pu/vid/other.mp4")]
    public async Task X_asset_group_refuses_unrelated_delivery_namespaces_before_network(string other)
    {
        const string page = "https://x.com/example/status/1234567890";
        const string first = "https://video.twimg.com/ext_tw_video/9876543210/pu/vid/first.mp4";
        var handler = new FakeHttpHandler();
        using var resolver = T.Resolver(handler);
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(page), Options = new DiscoveryOptions { ExactBrowserAssetGroup = true },
            Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page,
                Candidates = [new() { Url = first, Kind = "direct" }, new() { Url = other, Kind = "direct" }]
            }
        }, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Distinct_unidentified_files_are_not_deduplicated_as_same_quality()
    {
        var handler = new FakeHttpHandler().AddMedia(VideoUrl, "video/mp4", 1000)
            .AddMedia(VideoUrl + "/second.mp4", "video/mp4", 2000);
        var item = await ResolveAsync(handler, VideoUrl, VideoUrl + "/second.mp4");
        Assert.Equal(2, item.Representations.Count);
        Assert.All(item.Representations, rep => Assert.Null(rep.Height));
    }

    [Fact]
    public async Task Group_rejects_direct_redirect_to_another_asset()
    {
        const string page = "https://x.com/example/status/1234567890";
        const string first = "https://video.twimg.com/ext_tw_video/9876543210/pu/vid/first.mp4";
        const string second = "https://video.twimg.com/ext_tw_video/9876543210/pu/vid/second.mp4";
        const string unrelated = "https://video.twimg.com/ext_tw_video/9876543211/pu/vid/third.mp4";
        var handler = new FakeHttpHandler().AddMedia(first).AddRedirect(second, unrelated).AddMedia(unrelated);
        using var resolver = T.Resolver(handler);
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(page), Options = new DiscoveryOptions { ExactBrowserAssetGroup = true },
            Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page,
                Candidates = [new() { Url = first, Kind = "direct" }, new() { Url = second, Kind = "direct" }]
            }
        }, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(MediaErrorCode.InvalidInput, result.Error!.Code);
    }

    private static async Task<MediaItem> ResolveAsync(FakeHttpHandler handler, params string[] urls)
    {
        var result = await ResolveResultAsync(handler, urls);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        return result.Item!;
    }

    private static async Task<MediaDiscoveryResult> ResolveResultAsync(FakeHttpHandler handler, params string[] urls)
    {
        using var resolver = T.Resolver(handler);
        return await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(Page), Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = Page, Title = "Observed playback",
                Candidates = urls.Select(url => new BrowserMediaCandidate { Url = url, Kind = "direct", Source = "network" }).ToList()
            }
        }, CancellationToken.None);
    }

    // The fake server stores bodies as text, so every byte of this file stays below 0x80.
    internal static string Mp4(params (string Handler, string Format, uint Width, uint Height)[] tracks)
        => InitSegment(tracks) + "\u0000\u0000\u0000\u0018mdat" + new string('x', 16);

    [Theory]
    [InlineData("init")]
    [InlineData("truncated")]
    [InlineData("fragment")]
    [InlineData("html")]
    public async Task Invalid_direct_mp4_is_not_published(string kind)
    {
        string init = InitSegment(("vide", "avc1", 1024, 576));
        string body = kind switch
        {
            "init" => init,
            "truncated" => init + "\u0000\u0010\u0000\u0000mdat" + new string('x', 1000),
            "fragment" => "\u0000\u0000\u0000\u0008moof\u0000\u0000\u0000\u0018mdat" + new string('x', 16),
            _ => "<!doctype html><html>Access denied</html>"
        };
        var result = await ResolveResultAsync(new FakeHttpHandler().Add(VideoUrl, body, "video/mp4"), VideoUrl);
        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChunkOffsetsMustPointInsideTheWholeResource(bool wide, bool outside)
    {
        byte[] entries = new byte[8 + (wide ? 8 : 4)];
        BinaryPrimitives.WriteUInt32BigEndian(entries.AsSpan(4), 1);
        byte[] Make(uint offset)
        {
            if (wide) BinaryPrimitives.WriteUInt64BigEndian(entries.AsSpan(8), offset);
            else BinaryPrimitives.WriteUInt32BigEndian(entries.AsSpan(8), offset);
            return [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")),
                .. Box("moov", Track("vide", "avc1", 1024, 576, Box(wide ? "co64" : "stco", entries))),
                .. Box("mdat", new byte[64])];
        }
        byte[] file = Make(0);
        file = Make(outside ? 0x6000u : (uint)(file.Length - 64));
        var result = await ResolveResultAsync(new FakeHttpHandler().AddBinary(VideoUrl, file), VideoUrl);
        Assert.Equal(!outside, result.IsSuccess);
    }

    private static string InitSegment(params (string Handler, string Format, uint Width, uint Height)[] tracks)
    {
        byte[] mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 600);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 6000);
        byte[] moov = Box("moov", [Box("mvhd", mvhd), Box("free", new byte[tracks.Length == 1 ? 32 : 24]),
            .. tracks.Select(track => Track(track.Handler, track.Format, track.Width, track.Height))]);
        byte[] file = [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")), .. moov];
        Assert.All(file, value => Assert.InRange(value, 0, 0x7f));
        return Encoding.ASCII.GetString(file);
    }

    private static byte[] Track(string handler, string format, uint width, uint height, byte[]? chunks = null)
    {
        byte[] tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), height << 16);
        byte[] hdlr = new byte[25];
        Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        byte[] stsd = new byte[94];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(8), 86);
        Encoding.ASCII.GetBytes(format).CopyTo(stsd, 12);
        return Box("trak", Box("tkhd", tkhd), Box("mdia", Box("hdlr", hdlr), Box("free", new byte[92]),
            Box("minf", Box("stbl", Box("stsd", stsd), chunks ?? []))));
    }

    private static byte[] Box(string type, params byte[][] body)
    {
        byte[] payload = body.SelectMany(part => part).ToArray();
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    [Theory]
    [InlineData("\"v1\"", true)]
    [InlineData("W/\"v1\"", false)]
    [InlineData(null, false)]
    public async Task GenericFileIdentityRequiresStrongValidator(string? etag, bool proven)
    {
        var h = new FakeHttpHandler().Add(VideoUrl,
            Mp4(("vide","avc1",1024,576),("soun","mp4a",0,0)), "video/mp4",
            headers: etag is null ? null : new() { ["ETag"] = etag });
        var rep = Assert.Single((await ResolveAsync(h, VideoUrl)).Representations);
        Assert.Equal(proven, rep.ContentIdentity?.StartsWith("http-v1:") == true);
        Assert.Equal(proven, rep.ResourceETag is not null);
        Assert.Equal(proven, rep.ResourceUrlSha256 is not null);
    }
}
