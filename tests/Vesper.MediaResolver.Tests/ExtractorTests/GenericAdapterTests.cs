using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public class GenericAdapterTests
{
    private const string HlsMime = "application/vnd.apple.mpegurl";
    private const string Media = "https://media.generic-fixture.example";
    private const string Page = "https://www.generic-fixture.example";

    private static async Task<MediaDiscoveryResult> Resolve(FakeHttpHandler h, string url, ResolverOptions? o = null)
    {
        using var resolver = T.Resolver(h, o);
        return await resolver.ResolveAsync(T.Req(url), CancellationToken.None);
    }

    [Fact]
    public async Task DirectVideo_IsVerifiedByProbe_AndAudioIsMarkedUnverified()
    {
        var h = new FakeHttpHandler().AddMedia($"{Media}/movie.mp4", "video/mp4", 4321);
        var res = await Resolve(h, $"{Media}/movie.mp4");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var rep = Assert.Single(res.Item!.Representations);
        Assert.Equal("generic", res.Item.Provider);
        Assert.Equal("movie", res.Item.Title);
        Assert.Equal(StreamProtocol.Http, rep.Protocol);
        Assert.Equal(StreamContent.VideoAndAudio, rep.Content);
        Assert.True(rep.AudioUnverified);
        Assert.Equal(4321, rep.FileSize);
        Assert.Equal("mp4", rep.Container);
    }

    [Fact]
    public async Task DirectAudio_IsAudioOnly()
    {
        var h = new FakeHttpHandler().AddMedia($"{Media}/song.mp3", "audio/mpeg", 999);
        var res = await Resolve(h, $"{Media}/song.mp3");
        Assert.Equal(MediaKind.Audio, res.Item!.Kind);
        Assert.Equal(StreamContent.AudioOnly, res.Item.Representations[0].Content);
    }

    [Fact]
    public async Task LinkThatLooksLikeMediaButServesHtml_IsNotReportedDownloadable()
    {
        var h = new FakeHttpHandler().Add($"{Media}/fake.mp4", "<html>login</html>", "text/html");
        var res = await Resolve(h, $"{Media}/fake.mp4");
        Assert.False(res.IsSuccess);
        Assert.Equal(MediaErrorCode.NoDownloadableMedia, res.Error!.Code);
    }

    [Fact]
    public async Task MissingFile_IsUnavailable()
    {
        var res = await Resolve(new FakeHttpHandler(), $"{Media}/gone.mp4");
        Assert.Equal(MediaErrorCode.VideoUnavailable, res.Error!.Code);
        Assert.True(res.Error.IsPermanent);
    }

    [Fact]
    public async Task ExpiredSignedUrl_IsRefusedWithARetryableExpiredError()
    {
        var h = new FakeHttpHandler().AddMedia($"{Media}/x.mp4?expires=1000000001&sig=abc", "video/mp4");
        var res = await Resolve(h, $"{Media}/x.mp4?expires=1000000001&sig=abc");
        Assert.Equal(MediaErrorCode.SourceExpired, res.Error!.Code);
        Assert.True(res.Error.IsRetryable);
        Assert.DoesNotContain("sig=abc", res.Error.Diagnostic);
    }

    [Fact]
    public async Task SignedUrlWithFutureExpiry_ExposesExpiry()
    {
        var h = new FakeHttpHandler().AddMedia($"{Media}/x.mp4?expires=4000000000", "video/mp4");
        var res = await Resolve(h, $"{Media}/x.mp4?expires=4000000000");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4000000000), res.Item!.EarliestExpiry);
    }

    [Fact]
    public async Task RedirectingLinkWithoutExtension_IsFollowedAndVerified()
    {
        var h = new FakeHttpHandler()
            .AddRedirect("https://short.generic-fixture.example/go", $"{Media}/final.mp4")
            .AddMedia($"{Media}/final.mp4", "video/mp4", 777);
        var res = await Resolve(h, "https://short.generic-fixture.example/go");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Equal($"{Media}/final.mp4", res.Item!.Representations[0].Url.AbsoluteUri);
    }

    [Fact]
    public async Task HlsMasterUrl_ProducesVideoAndAudioRepresentations()
    {
        var h = new FakeHttpHandler()
            .AddFile($"{Media}/master.m3u8", "hls_master.m3u8", HlsMime)
            .AddFile($"{Media}/v360/prog.m3u8", "hls_media_vod.m3u8", HlsMime);
        var res = await Resolve(h, $"{Media}/master.m3u8");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Equal(3, res.Item!.Representations.Count(r => r.HasVideo));
        Assert.Equal(2, res.Item.Representations.Count(r => r.Content == StreamContent.AudioOnly));
        Assert.Equal(TimeSpan.FromSeconds(16.5), res.Item.Duration);
    }

    [Fact]
    public async Task HtmlPage_Html5OpenGraphJsonLdAndInlineManifest_AreAllDiscovered()
    {
        var h = new FakeHttpHandler()
            .AddFile($"{Page}/watch/1", "generic_page.html", "text/html")
            .AddMedia($"{Media}/og.mp4").AddMedia($"{Media}/ld.mp4")
            .AddMedia($"{Page}/files/clip-360.mp4").AddMedia($"{Page}/watch/clip.webm", "video/webm")
            .AddMedia($"{Media}/audio.mp3", "audio/mpeg")
            .AddFile($"{Media}/live/stream.m3u8?token=1", "hls_master.m3u8", HlsMime)
            .AddFile($"{Media}/live/v360/prog.m3u8", "hls_media_vod.m3u8", HlsMime);
        var res = await Resolve(h, $"{Page}/watch/1");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("LD Title", item.Title);
        Assert.Contains(item.Representations, r => r.Container == "webm");
        Assert.Contains(item.Representations, r => r.Container == "mp3" && r.Content == StreamContent.AudioOnly);
        Assert.Contains(item.Representations, r => r.Container == "mp4" && r.Protocol == StreamProtocol.Http);
        Assert.Contains(item.Representations, r => r.Protocol == StreamProtocol.Hls);
        Assert.Contains(item.Subtitles, s => s.Url.AbsoluteUri == $"{Page}/subs/en.vtt" && s.Format == "vtt");
        Assert.Contains(item.Subtitles, s => s.Format == "hls-webvtt");          // from the HLS master's SUBTITLES rendition
        Assert.NotNull(item.Thumbnail);
        Assert.All(item.Representations, r => Assert.Equal($"{Page}/watch/1", r.Context.Referer));
        Assert.DoesNotContain(h.Requests, r => r.Url.Host == "ads.generic-fixture.example");   // unrelated iframes are never followed
    }

    [Fact]
    public async Task PageWithoutMedia_SaysSo_AndSuggestsTheBrowser()
    {
        var h = new FakeHttpHandler().Add($"{Page}/empty", "<html><body><p>hello</p></body></html>");
        var res = await Resolve(h, $"{Page}/empty");
        Assert.Equal(MediaErrorCode.NoDownloadableMedia, res.Error!.Code);
        Assert.True(res.NeedsBrowserContext);
    }

    [Fact]
    public async Task PageReferencingAPrivateAddress_NeverContactsIt()
    {
        var h = new FakeHttpHandler().Add($"{Page}/evil", "<video src=\"http://192.168.0.5/a.mp4\"></video>");
        var res = await Resolve(h, $"{Page}/evil");
        Assert.False(res.IsSuccess);
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, res.Error!.Code);
        Assert.DoesNotContain(h.Requests, r => r.Url.Host == "192.168.0.5");
    }

    [Fact]
    public async Task PageWithSupportedEmbed_ResolvesTheEmbedOnce_AndIgnoresOtherIframes()
    {
        var h = new FakeHttpHandler()
            .Add($"{Page}/embed-page", "<iframe src=\"https://ads.generic-fixture.example/a\"></iframe><iframe src=\"https://player.vimeo.com/video/76979871\"></iframe>")
            .AddFile("https://player.vimeo.com/video/76979871/config", "vimeo_config.json")
            .AddFile("https://adaptive.vimeo-fixture.example/master.m3u8", "hls_master.m3u8", HlsMime)
            .AddFile("https://adaptive.vimeo-fixture.example/v360/prog.m3u8", "hls_media_vod.m3u8", HlsMime);
        var res = await Resolve(h, $"{Page}/embed-page");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Equal("vimeo", res.Item!.Provider);
        Assert.DoesNotContain(h.Requests, r => r.Url.Host == "ads.generic-fixture.example");
    }

    [Fact]
    public async Task PodcastFeed_BecomesADeduplicatedCollection_WithoutProbingEveryEpisode()
    {
        var h = new FakeHttpHandler().AddFile("https://pod.generic-fixture.example/feed.xml", "podcast.xml", "application/rss+xml");
        var res = await Resolve(h, "https://pod.generic-fixture.example/feed.xml");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var c = res.Collection!;
        Assert.Equal(CollectionKind.Feed, c.Kind);
        Assert.Equal("Fixture Podcast", c.Title);
        Assert.Equal(2, c.Entries.Count);                                      // duplicate guid and the enclosure-less item are gone
        Assert.Equal(TimeSpan.FromSeconds(754), c.Entries[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(3600), c.Entries[1].Duration);
        Assert.Equal("https://pod.generic-fixture.example/cover.jpg", c.Entries[0].Thumbnail!.AbsoluteUri);
        Assert.Single(h.Requests);                                             // only the feed itself was fetched
    }
}
