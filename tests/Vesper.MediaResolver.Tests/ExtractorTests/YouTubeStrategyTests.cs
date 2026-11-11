using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

/// <summary>Layered YouTube strategies, byte-probe verification, identity and paging (offline, deterministic).</summary>
public sealed class YouTubeStrategyTests
{
    private const string Watch = "https://www.youtube.com/watch?v=EK3tCffac5k";
    private const string Api = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    private const string PageHtml = "<html><script>ytcfg.set({\"VISITOR_DATA\":\"CgtWSVNJVE9SXzEyMw%3D%3D\",\"STS\":20760,\"INNERTUBE_CLIENT_VERSION\":\"2.20261101.01.00\"});"
        + "var ytInitialPlayerResponse = {\"playabilityStatus\":{\"status\":\"OK\"},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"Page title\",\"lengthSeconds\":\"10\"}};</script></html>";

    private static string Media(int itag) => $"https://rr3---sn-test.googlevideo.com/videoplayback?expire=4102444800&itag={itag}&clen=4000000&lmt=1712345678901234";

    private static string Fmt(int itag, string mime, string extra = "", string? url = null) =>
        $"{{\"itag\":{itag},\"url\":\"{url ?? Media(itag)}\",\"mimeType\":\"{mime.Replace("\"", "\\\"")}\",\"contentLength\":\"4000000\",\"lastModified\":\"1712345678901234\"{extra}}}";

    private static readonly string ApiOk =
        "{\"playabilityStatus\":{\"status\":\"OK\"},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"Api title\",\"lengthSeconds\":\"10\"},\"streamingData\":{\"adaptiveFormats\":["
        + string.Join(",",
            Fmt(137, "video/mp4; codecs=\"avc1.640028\"", ",\"height\":1080,\"width\":1920,\"fps\":30"),
            Fmt(140, "audio/mp4; codecs=\"mp4a.40.2\"", ",\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\",\"audioIsDefault\":true}"),
            Fmt(140, "audio/mp4; codecs=\"mp4a.40.2\"", ",\"isDrc\":true,\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\"}", Media(140) + "&xtags=drc%3D1"),
            Fmt(251, "audio/webm; codecs=\"opus\"", ",\"audioTrack\":{\"id\":\"es.3\",\"displayName\":\"Spanish\"}"))
        + "]}}";

    private static async Task<MediaDiscoveryResult> Resolve(FakeHttpHandler h, string url = Watch)
    {
        using var resolver = new DefaultMediaResolver(handler: h);
        return await resolver.ResolveAsync(new() { Url = new(url) }, CancellationToken.None);
    }

    [Fact]
    public async Task VisionOsStrategy_UsesPageSessionValues_AndVerifiesBytesBeforeOffering()
    {
        var h = new FakeHttpHandler().Add(Watch, PageHtml).Add(Api, ApiOk, "application/json").AddMedia(Media(137), "video/mp4", 64);
        var res = await Resolve(h);
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("Api title", item.Title);
        Assert.Contains(res.Notes, n => n.Contains("'visionos'"));
        var api = h.Requests.Single(r => r.Url.AbsoluteUri == Api);
        Assert.Equal(HttpMethod.Post, api.Method);
        Assert.Equal("101", api.Headers["X-YouTube-Client-Name"]);
        Assert.Equal("CgtWSVNJVE9SXzEyMw%3D%3D", api.Headers["X-Goog-Visitor-Id"]);
        var media = h.Requests.Where(r => r.Url.Host.EndsWith("googlevideo.com", StringComparison.Ordinal)).ToList();
        Assert.Single(media);                                                // exactly one byte probe
        Assert.True(media[0].Headers.ContainsKey("Range"));
        Assert.DoesNotContain(media, r => r.Headers.ContainsKey("Cookie"));   // the consent cookie never leaves youtube.com
        // The DRC variant is metadata-identical to the regular AAC track, so the shared deduplicator keeps one of them;
        // a different language is never collapsed.
        Assert.Equal(3, item.Representations.Count);
        Assert.Equal(item.Representations.Count, item.Representations.Select(r => r.Id).Distinct().Count());
        Assert.Single(item.Representations, r => r.Id.StartsWith("yt-140-", StringComparison.Ordinal));
        Assert.Contains(item.Representations, r => r.Id == "yt-251-es.3" && r.AudioLanguage == "es");
        Assert.Equal(2, item.AudioTracks.Count);
        Assert.Equal("youtube:EK3tCffac5k:137:1712345678901234:4000000", item.Representations.Single(r => r.Id == "yt-137").ContentIdentity);
    }

    [Fact]
    public async Task FormatsRefusedBeyondTheFirstBytes_AreNeverOfferedAsDownloadable()
    {
        var h = new FakeHttpHandler().Add(Watch, PageHtml).Add(Api, ApiOk, "application/json").Add(Media(137), "", "text/plain", 403);
        var res = await Resolve(h);
        Assert.False(res.IsSuccess);
        Assert.Equal(MediaErrorCode.AuthorizationRequired, res.Error!.Code);
        Assert.Contains("HTTP 403", res.Error.Diagnostic);
    }

    [Fact]
    public async Task BotGate_BeatsLowerPriorityStrategyFailures_AndListsTheTrail()
    {
        const string bot = "{\"playabilityStatus\":{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"Sign in to confirm you're not a bot\"}}";
        string page = PageHtml.Replace("\"videoDetails\"", "\"streamingData\":{\"adaptiveFormats\":[" + Fmt(18, "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"", url: Media(18) + "&n=challenge") + "]},\"videoDetails\"");
        var res = await Resolve(new FakeHttpHandler().Add(Watch, page).Add(Api, bot, "application/json"));
        Assert.Equal(MediaErrorCode.BrowserContextRequired, res.Error!.Code);
        Assert.Contains("bot", res.Error.Diagnostic);
        Assert.Contains("watch-page", res.Error.Diagnostic);
    }

    private static string Entry(string id) =>
        $"{{\"playlistVideoRenderer\":{{\"videoId\":\"{id}\",\"title\":{{\"runs\":[{{\"text\":\"Video {id}\"}}]}},\"lengthSeconds\":\"61\"}}}}";

    [Fact]
    public async Task PlaylistContinuation_IsFollowedAndDeduplicated()
    {
        const string url = "https://www.youtube.com/playlist?list=PLpaged123";
        string page = "<html><script>ytcfg.set({\"VISITOR_DATA\":\"Cgt2aXNpdG9y\",\"INNERTUBE_CLIENT_VERSION\":\"2.20261101.01.00\"});var ytInitialData = {\"metadata\":{\"playlistMetadataRenderer\":{\"title\":\"Paged\"}},\"contents\":["
            + Entry("AAAAAAAAAAA") + "," + Entry("BBBBBBBBBBB") + ",{\"continuationItemRenderer\":{\"continuationEndpoint\":{\"continuationCommand\":{\"token\":\"TOKEN_PAGE_2_abcdef\"}}}}]};</script></html>";
        string next = "{\"onResponseReceivedActions\":[{\"appendContinuationItemsAction\":{\"continuationItems\":[" + Entry("CCCCCCCCCCC") + "," + Entry("AAAAAAAAAAA") + "]}}]}";
        var h = new FakeHttpHandler().Add(url, page).Add("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", next, "application/json");
        var res = await Resolve(h, url);
        Assert.Equal(DiscoveryStatus.Success, res.Status);
        Assert.True(res.Collection!.IsComplete);
        Assert.Equal(["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC"], res.Collection.Entries.Select(e => e.MediaId));
        Assert.Equal("1", h.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/browse", StringComparison.Ordinal)).Headers["X-YouTube-Client-Name"]);
    }

    [Theory]
    [InlineData("https://music.youtube.com/watch?v=EK3tCffac5k&feature=share", "EK3tCffac5k")]
    [InlineData("https://www.youtube-nocookie.com/embed/EK3tCffac5k?start=3", "EK3tCffac5k")]
    [InlineData("https://www.youtube.com/shorts/EK3tCffac5k?feature=share", "EK3tCffac5k")]
    public void MoreUrlForms(string url, string id) => Assert.Equal(id, YouTubeAdapter.Parse(new Uri(url))!.VideoId);

    private static MediaRepresentation V(string codec, string container) => new()
    {
        Id = "v-" + codec, Url = new("https://cdn.example.com/v"), Content = StreamContent.VideoOnly, VideoCodec = codec,
        Container = container, Height = 1080, FileSize = 1000
    };

    private static MediaRepresentation A(string codec, string container) => new()
    {
        Id = "a-" + codec, Url = new("https://cdn.example.com/a"), Content = StreamContent.AudioOnly, AudioCodec = codec,
        Container = container, FileSize = 100
    };

    [Fact]
    public void MergeCapability_OffersOnlyRemuxableCombinations()
    {
        Assert.Equal("mp4", MergeCapability.MergeContainer(V("h264", "mp4"), A("aac", "m4a")));
        Assert.Equal("webm", MergeCapability.MergeContainer(V("vp9", "webm"), A("opus", "webm")));
        Assert.Equal("webm", MergeCapability.MergeContainer(V("av1", "mp4"), A("opus", "webm")));
        Assert.Equal("mkv", MergeCapability.MergeContainer(V("h264", "mp4"), A("opus", "webm")));
        Assert.Null(MergeCapability.MergeContainer(V("h264", "mp4") with { FileSize = null }, A("aac", "m4a")));
        Assert.Null(MergeCapability.MergeContainer(V("h264", "mp4") with { Protocol = StreamProtocol.Hls }, A("aac", "m4a")));
        Assert.Null(MergeCapability.MergeContainer(V("prores", "mov"), A("aac", "m4a")));
        Assert.Equal("m4a", MergeCapability.AudioContainer(A("aac", "m4a")));
        Assert.Equal("webm", MergeCapability.AudioContainer(A("opus", "webm")));
    }
}
