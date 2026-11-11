using Xunit;
using System.Diagnostics;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public sealed class DiscoveryReliabilityTests
{
    private const string Page = "https://www.youtube.com/watch?v=EK3tCffac5k";
    private static string Player(string body) => "<script>var ytInitialPlayerResponse=" + body + ";</script>";
    private sealed class Observer(Action<DiscoveryProgress> action) : IProgress<DiscoveryProgress>
    { public void Report(DiscoveryProgress value) => action(value); }

    [Fact]
    public async Task MissingPlayer_ReusesPageInsteadOfFetchingItTwice()
    {
        var handler = new FakeHttpHandler().Add(Page, "<html><title>A page, not playback</title></html>");
        using var resolver = new DefaultMediaResolver(handler: handler);
        var result = await resolver.ResolveAsync(new() { Url = new(Page) }, CancellationToken.None);
        Assert.Equal(MediaErrorCode.SourceChanged, result.Error!.Code);
        Assert.Equal(1, handler.Count(Page));                                  // the page itself is never fetched twice
    }

    [Fact]
    public async Task MetadataWithoutPlayback_IsNotReportedAsNoMedia()
    {
        var handler = new FakeHttpHandler().Add(Page, Player("""
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"EK3tCffac5k","title":"Metadata only"}}
            """));
        using var resolver = new DefaultMediaResolver(handler: handler);
        var updates = new List<DiscoveryProgress>();
        var result = await resolver.ResolveAsync(new() { Url = new(Page), Progress = new Observer(updates.Add) }, CancellationToken.None);
        Assert.Equal(MediaErrorCode.BrowserContextRequired, result.Error!.Code);
        Assert.Contains(updates, u => u.Title == "Metadata only");
        Assert.Contains("no playback", result.Error.Diagnostic);
        Assert.Equal(1, handler.Count(Page));
    }

    [Fact]
    public async Task MismatchedVideoIdentity_IsRejectedBeforePublishingFormats()
    {
        var handler = new FakeHttpHandler().Add(Page, Player("""
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"AAAAAAAAAAA","title":"Wrong video"},
             "streamingData":{"formats":[{"itag":18,"url":"https://cdn.example.com/video.mp4","mimeType":"video/mp4"}]}}
            """));
        using var resolver = new DefaultMediaResolver(handler: handler);
        var result = await resolver.ResolveAsync(new() { Url = new(Page) }, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(MediaErrorCode.SourceChanged, result.Error!.Code);
        Assert.Contains("different video", result.Error.Diagnostic);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AdaptiveAudioDoesNotHideACompleteHlsAlternative_AndMetadataIsEarly()
    {
        const string manifest = "https://cdn.example.com/complete.m3u8";
        var handler = new FakeHttpHandler { Delay = TimeSpan.FromMilliseconds(25) };
        const string player = """
            {"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"EK3tCffac5k","title":"Early metadata"},
             "streamingData":{"adaptiveFormats":[{"itag":140,"url":"https://cdn.example.com/audio.m4a","mimeType":"audio/mp4; codecs=\"mp4a.40.2\""}],
             "hlsManifestUrl":"https://cdn.example.com/complete.m3u8"}}
            """;
        // The InnerTube strategy answers first; its audio URL passes the byte probe while the manifest is fetched concurrently.
        handler.Add(Page, Player(player)).Add("https://www.youtube.com/youtubei/v1/player?prettyPrint=false", player, "application/json")
            .AddMedia("https://cdn.example.com/audio.m4a", "audio/mp4").Add(manifest, "#EXTM3U\n#EXTINF:2,\nsegment.ts\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
        DiscoveryMeasurement? metrics = null;
        using var resolver = new DefaultMediaResolver(new ResolverOptions { Measurement = m => metrics = m }, handler);
        int requestsAtMetadata = -1;
        var result = await resolver.ResolveAsync(new()
        {
            Url = new(Page), Progress = new Observer(u => { if (u.Title == "Early metadata" && requestsAtMetadata < 0) requestsAtMetadata = handler.Requests.Count; })
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Contains(result.Item!.Representations, r => r.Protocol == StreamProtocol.Hls);
        Assert.Contains(result.Item.Representations, r => r.Content == StreamContent.AudioOnly);
        Assert.Equal(1, requestsAtMetadata);
        Assert.NotNull(metrics!.FirstMetadata);
        Assert.Equal(4, metrics.NetworkRequests);                           // page, player API, byte probe, manifest
    }

    [Fact]
    public async Task BrowserCandidates_AvoidTheWatchPageCompletely()
    {
        var handler = new FakeHttpHandler().AddMedia("https://cdn.example.com/authorized.mp4");
        using var resolver = new DefaultMediaResolver(handler: handler);
        var result = await resolver.ResolveAsync(new()
        {
            Url = new(Page), Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = Page, Title = "Observed playback",
                Candidates = [new BrowserMediaCandidate { Url = "https://cdn.example.com/authorized.mp4", MimeType = "video/mp4" }]
            }
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal("Observed playback", result.Item!.Title);
        Assert.Equal("cdn.example.com", Assert.Single(handler.Requests).Url.Host);
    }

    [Fact]
    public async Task CacheKeys_IncludeCollectionItemLimits()
    {
        const string url = "https://www.youtube.com/playlist?list=PLabc123";
        var handler = new FakeHttpHandler().AddFile(url, "youtube_playlist.html", "text/html");
        using var resolver = new DefaultMediaResolver(handler: handler);
        Task<MediaDiscoveryResult> Resolve(int limit) => resolver.ResolveAsync(new()
        { Url = new(url), Options = new DiscoveryOptions { MaxCollectionItems = limit } }, CancellationToken.None);
        Assert.Single((await Resolve(1)).Collection!.Entries);
        Assert.Equal(2, (await Resolve(3)).Collection!.Entries.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cache_DoesNotRetainAdapterIssuedSessionCookies()
    {
        const string url = "https://www.tiktok.com/@fixtureuser/video/7234567890123456789";
        var handler = new FakeHttpHandler().Add(url, Fixture.Load("tiktok_page.html"), headers: new() { ["Set-Cookie"] = "sid=secret; HttpOnly" });
        using var resolver = new DefaultMediaResolver(handler: handler);
        await resolver.ResolveAsync(new() { Url = new(url) }, CancellationToken.None);
        await resolver.ResolveAsync(new() { Url = new(url) }, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Deadline_IsNetworkFailure_NotInternalError()
    {
        var handler = new FakeHttpHandler { Delay = TimeSpan.FromSeconds(5) };
        using var resolver = new DefaultMediaResolver(new ResolverOptions { DiscoveryTimeout = TimeSpan.FromMilliseconds(50) }, handler);
        var clock = Stopwatch.StartNew();
        var result = await resolver.ResolveAsync(new() { Url = new(Page) }, CancellationToken.None);
        Assert.Equal(MediaErrorCode.NetworkFailure, result.Error!.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var handler = new FakeHttpHandler { Delay = TimeSpan.FromSeconds(5) };
        using var resolver = new DefaultMediaResolver(handler: handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(new() { Url = new(Page) }, cancel.Token));
    }

    [Fact]
    public async Task WarmCache_MeasuresZeroNetworkRequests()
    {
        var handler = new FakeHttpHandler().AddMedia("https://cdn.example.com/a.mp4");
        var measures = new List<DiscoveryMeasurement>();
        using var resolver = new DefaultMediaResolver(new ResolverOptions { Measurement = measures.Add }, handler);
        await resolver.ResolveAsync(new() { Url = new("https://cdn.example.com/a.mp4") }, CancellationToken.None);
        await resolver.ResolveAsync(new() { Url = new("https://cdn.example.com/a.mp4") }, CancellationToken.None);
        Assert.True(measures[1].CacheHit);
        Assert.Equal(0, measures[1].NetworkRequests);
        Assert.NotNull(measures[1].FirstRepresentation);
    }
}
