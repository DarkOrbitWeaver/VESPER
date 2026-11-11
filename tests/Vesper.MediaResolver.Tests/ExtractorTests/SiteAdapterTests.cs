using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

/// <summary>
/// Offline regression tests against synthetic fixtures (see Fixtures/FIXTURES.md). They prove parsing and typed behaviour,
/// not that the live site still serves this shape.
/// </summary>
public class SiteAdapterTests
{
    private const string HlsMime = "application/vnd.apple.mpegurl";
    private const string DashMime = "application/dash+xml";

    private static FakeHttpHandler WithHls(FakeHttpHandler h, string host, string path = "master.m3u8") => h
        .AddFile($"https://{host}/{path}", "hls_master.m3u8", HlsMime)
        .AddFile($"https://{host}/v360/prog.m3u8", "hls_media_vod.m3u8", HlsMime);

    private static async Task<MediaDiscoveryResult> Resolve(FakeHttpHandler h, string url, CollectionIntent intent = CollectionIntent.Auto, string? token = null)
    {
        using var resolver = T.Resolver(h);
        return await resolver.ResolveAsync(T.Req(url, intent, token), CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------------------- Vimeo

    [Fact]
    public async Task Vimeo_ReturnsProgressiveHlsSubtitlesAndExpiry()
    {
        var h = WithHls(new FakeHttpHandler(), "adaptive.vimeo-fixture.example")
            .AddFile("https://player.vimeo.com/video/76979871/config", "vimeo_config.json");
        var res = await Resolve(h, "https://vimeo.com/76979871");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("vimeo", item.Provider);
        Assert.Equal("76979871", item.MediaId);
        Assert.Equal("Fixture Vimeo Video", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(62), item.Duration);
        Assert.Equal("Fixture Owner", item.Uploader);
        Assert.Contains(item.Representations, r => r.Protocol == StreamProtocol.Http && r.Height == 720 && r.Container == "mp4");
        Assert.Contains(item.Representations, r => r.Protocol == StreamProtocol.Hls);
        Assert.Contains(item.Subtitles, s => s.Url.AbsoluteUri == "https://player.vimeo.com/texttrack/1.vtt");
        Assert.Contains(item.Subtitles, s => s.Format == "hls-webvtt" && s.Language == "en");
        Assert.Equal("https://i.vimeocdn.com/video/1-1280.jpg", item.Thumbnail!.AbsoluteUri);
        Assert.NotNull(item.EarliestExpiry);
        Assert.All(item.Representations, r => Assert.Equal("https://vimeo.com/", r.Context.Referer));
    }

    [Fact]
    public async Task Vimeo_UnlistedHash_IsForwardedToTheConfigRequest()
    {
        var h = WithHls(new FakeHttpHandler(), "adaptive.vimeo-fixture.example")
            .AddFile("https://player.vimeo.com/video/76979871/config?h=abcdef1234", "vimeo_config.json");
        var res = await Resolve(h, "https://vimeo.com/76979871/abcdef1234");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Single(h.Requests, r => r.Url.AbsoluteUri.Contains("config?h=abcdef1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Vimeo_PrivateVideo_ReportsLoginRequired_AndMissingVideoReportsUnavailable()
    {
        var priv = new FakeHttpHandler().Add("https://player.vimeo.com/video/111111/config", "{}", "application/json", 403);
        var r1 = await Resolve(priv, "https://vimeo.com/111111");
        Assert.Equal(MediaErrorCode.LoginRequired, r1.Error!.Code);
        Assert.True(r1.Error.BrowserMayHelp);

        var gone = new FakeHttpHandler().Add("https://player.vimeo.com/video/222222/config", "{\"message\":\"Video not found\"}", "application/json");
        var r2 = await Resolve(gone, "https://vimeo.com/222222");
        Assert.Equal(MediaErrorCode.VideoUnavailable, r2.Error!.Code);
    }

    // ---------------------------------------------------------------------------------------------- Dailymotion

    [Fact]
    public async Task Dailymotion_ReturnsHlsAndMp4()
    {
        var h = WithHls(new FakeHttpHandler(), "cdn.dmcdn-fixture.example")
            .AddFile("https://www.dailymotion.com/player/metadata/video/x8abc12", "dailymotion_metadata.json");
        var res = await Resolve(h, "https://www.dailymotion.com/video/x8abc12");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("x8abc12", item.MediaId);
        Assert.Equal(TimeSpan.FromSeconds(95), item.Duration);
        Assert.Contains(item.Representations, r => r.Protocol == StreamProtocol.Http && r.Height == 480);
        Assert.Contains(item.Representations, r => r.Protocol == StreamProtocol.Hls);
        Assert.Equal("https://s1.dmcdn-fixture.example/720.jpg", item.Thumbnail!.AbsoluteUri);
        Assert.Contains(item.Subtitles, s => s.Url.AbsoluteUri == "https://static.dmcdn-fixture.example/en.vtt");
        Assert.Contains(item.Subtitles, s => s.Format == "hls-webvtt" && s.Language == "en");
    }

    [Fact]
    public async Task Dailymotion_ShortLinkWorks_AndGeoBlockIsTyped()
    {
        var h = new FakeHttpHandler().Add("https://www.dailymotion.com/player/metadata/video/x8abc12",
            "{\"error\":{\"title\":\"Content not available in your country\",\"message\":\"geoblocked\",\"code\":\"DM006\"}}", "application/json");
        var res = await Resolve(h, "https://dai.ly/x8abc12");
        Assert.Equal(MediaErrorCode.RegionRestricted, res.Error!.Code);
        Assert.True(res.Error.IsPermanent);
    }

    // ---------------------------------------------------------------------------------------------- Reddit

    private static FakeHttpHandler RedditServer() => new FakeHttpHandler()
        .AddFile("https://www.reddit.com/comments/abc123.json?raw_json=1&limit=1", "reddit_post.json")
        .AddFile("https://v.redd.it/abc123xyz/DASHPlaylist.mpd?a=1", "reddit_dash.mpd", DashMime);

    [Fact]
    public async Task Reddit_VideoAndAudioAreSeparate_SoSelectionRequiresMuxing()
    {
        var res = await Resolve(RedditServer(), "https://www.reddit.com/r/test/comments/abc123/fixture_title/");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;

        Assert.Equal("Fixture Reddit video", item.Title);
        Assert.Equal("u_fixture", item.Uploader);
        Assert.Equal(TimeSpan.FromSeconds(30), item.Duration);
        Assert.Equal(2, item.Representations.Count(r => r.Content == StreamContent.VideoOnly));
        Assert.Single(item.Representations, r => r.Content == StreamContent.AudioOnly);
        Assert.All(item.Representations, r => Assert.Equal(StreamProtocol.Http, r.Protocol));

        var sel = FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!;
        Assert.Equal(1280, sel.Video!.Height);
        Assert.NotNull(sel.Audio);
        Assert.True(sel.NeedsMuxing);
    }

    [Fact]
    public async Task Reddit_ShortLinkResolvesTheSamePost()
    {
        var res = await Resolve(RedditServer(), "https://redd.it/abc123");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
    }

    [Fact]
    public async Task Reddit_PostWithoutHostedVideo_IsNotReportedAsDownloadable()
    {
        var h = new FakeHttpHandler().Add("https://www.reddit.com/comments/zzz999.json?raw_json=1&limit=1",
            "[{\"data\":{\"children\":[{\"data\":{\"id\":\"zzz999\",\"title\":\"text post\"}}]}}]", "application/json");
        var res = await Resolve(h, "https://www.reddit.com/r/test/comments/zzz999/text_post/");
        Assert.Equal(MediaErrorCode.NoDownloadableMedia, res.Error!.Code);
        Assert.False(res.Error.BrowserMayHelp);
        Assert.Single(h.Requests);
    }

    // ---------------------------------------------------------------------------------------------- Internet Archive

    [Fact]
    public async Task InternetArchive_Video_ListsFilesWithSizesAndSubtitles()
    {
        var h = new FakeHttpHandler().AddFile("https://archive.org/metadata/fixture_movie", "archive_video.json");
        var res = await Resolve(h, "https://archive.org/details/fixture_movie");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("Fixture Movie", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(600.5), item.Duration);
        Assert.Equal(2, item.Representations.Count);
        var mp4 = item.Representations.Single(r => r.Container == "mp4");
        Assert.Equal("https://archive.org/download/fixture_movie/fixture_movie.mp4", mp4.Url.AbsoluteUri);
        Assert.Equal(52428800, mp4.FileSize);
        Assert.False(mp4.FileSizeIsEstimate);
        Assert.Equal(480, mp4.Height);
        Assert.Equal("en", Assert.Single(item.Subtitles).Language);
    }

    [Fact]
    public async Task InternetArchive_MultiTrackAudio_IsACollectionWithPreResolvedTracks()
    {
        var h = new FakeHttpHandler().AddFile("https://archive.org/metadata/fixture_album", "archive_album.json");
        var res = await Resolve(h, "https://archive.org/details/fixture_album");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var c = res.Collection!;
        Assert.Equal(3, c.Entries.Count);
        Assert.Equal(CollectionKind.Album, c.Kind);
        Assert.Equal(2, c.Entries[0].Resolved!.Representations.Count);          // mp3 + ogg of the same track
        Assert.Equal("https://archive.org/download/fixture_album/01%20-%20First.mp3",
            c.Entries[0].Resolved!.Representations.First(r => r.Container == "mp3").Url.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(200), c.Entries[1].Duration);
        Assert.All(c.Entries, e => Assert.Equal(MediaKind.Audio, e.Resolved!.Kind));
    }

    [Fact]
    public async Task InternetArchive_MissingItem_IsUnavailable()
    {
        var h = new FakeHttpHandler().Add("https://archive.org/metadata/nothing_here", "{}", "application/json");
        var res = await Resolve(h, "https://archive.org/details/nothing_here");
        Assert.Equal(MediaErrorCode.VideoUnavailable, res.Error!.Code);
    }

    // ---------------------------------------------------------------------------------------------- PeerTube

    private const string PtId = "kkGMgK9ZtnKfYAgnEtQxbv";

    [Fact]
    public async Task PeerTube_ListsWebFilesAudioOnlyAndCaptions()
    {
        var h = new FakeHttpHandler()
            .AddFile($"https://peertube.example.org/api/v1/videos/{PtId}", "peertube_video.json")
            .Add($"https://peertube.example.org/api/v1/videos/{PtId}/captions",
                "{\"total\":1,\"data\":[{\"language\":{\"id\":\"en\",\"label\":\"English\"},\"captionPath\":\"/lazy-static/video-captions/x-en.vtt\"}]}", "application/json");
        var res = await Resolve(h, $"https://peertube.example.org/w/{PtId}");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("peertube", item.Provider);
        Assert.Equal(PtId, item.MediaId);
        Assert.Equal(3, item.Representations.Count);
        Assert.Contains(item.Representations, r => r.Content == StreamContent.AudioOnly);
        Assert.Equal("https://peertube.example.org/static/webseed/720.mp4", item.Representations.Single(r => r.Height == 720).Url.AbsoluteUri);
        Assert.Equal("https://peertube.example.org/lazy-static/video-captions/x-en.vtt", Assert.Single(item.Subtitles).Url.AbsoluteUri);
        Assert.Equal("https://peertube.example.org/lazy-static/thumbnails/x.jpg", item.Thumbnail!.AbsoluteUri);
    }

    [Fact]
    public async Task PeerTube_PathOnAnUnrelatedHost_FallsThroughInsteadOfClaimingSupport()
    {
        var h = new FakeHttpHandler();                           // everything 404s as plain text
        var res = await Resolve(h, "https://other.example.com/w/abcdef123456");
        Assert.False(res.IsSuccess);
        Assert.NotEqual("peertube", res.Item?.Provider);
        Assert.Single(h.Requests, r => r.Url.AbsoluteUri.Contains("/api/v1/videos/", StringComparison.Ordinal));            // probed once, then deferred to the generic adapter
    }

    [Fact]
    public async Task PeerTube_Playlist_PaginatesWithAToken()
    {
        string Videos(int total) => "{\"total\":" + total + ",\"data\":[" +
            "{\"video\":{\"shortUUID\":\"aaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"V1\",\"duration\":10}}," +
            "{\"video\":{\"shortUUID\":\"bbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"V2\",\"duration\":20}}]}";

        var h = new FakeHttpHandler()
            .Add("https://peertube.example.org/api/v1/video-playlists/pl1abcdefghij", "{\"displayName\":\"My list\",\"videosLength\":120}", "application/json")
            .Add("https://peertube.example.org/api/v1/video-playlists/pl1abcdefghij/videos?start=0&count=50", Videos(120), "application/json");
        var res = await Resolve(h, "https://peertube.example.org/w/p/pl1abcdefghij");

        Assert.True(res.IsSuccess || res.Status == DiscoveryStatus.Partial);
        var c = res.Collection!;
        Assert.Equal("My list", c.Title);
        Assert.Equal(2, c.Entries.Count);
        Assert.Equal("50", c.NextPageToken);
        Assert.False(c.IsComplete);
        Assert.Equal(120, c.TotalCount);
        Assert.Equal("https://peertube.example.org/w/aaaaaaaaaaaaaaaaaaaaaa", c.Entries[0].Url.AbsoluteUri);
    }

    // ---------------------------------------------------------------------------------------------- Bandcamp

    [Fact]
    public async Task Bandcamp_TrackPage_ReturnsPublicStreamOnly()
    {
        var h = new FakeHttpHandler().AddFile("https://fixtureband.bandcamp.com/track/song-one", "bandcamp_album.html", "text/html");
        var res = await Resolve(h, "https://fixtureband.bandcamp.com/track/song-one");

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("Song One", item.Title);
        Assert.Equal("Fixture Band", item.Uploader);
        Assert.Equal(MediaKind.Audio, item.Kind);
        Assert.Equal(TimeSpan.FromSeconds(201.5), item.Duration);
        var rep = Assert.Single(item.Representations);
        Assert.Equal(StreamContent.AudioOnly, rep.Content);
        Assert.Equal("mp3", rep.AudioCodec);
        Assert.Equal(128, rep.BitrateKbps);
        Assert.Equal("https://t4.bcbits.com/stream/abc/mp3-128/1001?p=0&ts=4000000000", rep.Url.AbsoluteUri);
        Assert.Equal("https://f4.bcbits.com/img/a123456_10.jpg", item.Thumbnail!.AbsoluteUri);
    }

    [Fact]
    public async Task Bandcamp_Album_ListsStreamableTracksAndSkipsLockedOnes()
    {
        var h = new FakeHttpHandler().AddFile("https://fixtureband.bandcamp.com/album/fixture-album", "bandcamp_album.html", "text/html");
        var res = await Resolve(h, "https://fixtureband.bandcamp.com/album/fixture-album");

        var c = res.Collection!;
        Assert.Equal(CollectionKind.Album, c.Kind);
        Assert.Equal(2, c.Entries.Count);                     // third track has file:null
        Assert.Equal(3, c.TotalCount);
        Assert.Equal("Fixture Album", c.Title);
        Assert.All(c.Entries, e => Assert.NotNull(e.Resolved));
        Assert.Equal("https://fixtureband.bandcamp.com/track/song-two", c.Entries[1].Url.AbsoluteUri);
    }

    // ---------------------------------------------------------------------------------------------- Bilibili

    private static FakeHttpHandler BiliServer() => new FakeHttpHandler()
        .AddFile("https://api.bilibili.com/x/web-interface/view?bvid=BV1xx411c7mD", "bilibili_view.json")
        .AddFile("https://api.bilibili.com/x/player/playurl?bvid=BV1xx411c7mD&cid=279786&fnval=4048&fourk=1", "bilibili_playurl.json")
        .AddFile("https://api.bilibili.com/x/player/playurl?bvid=BV1xx411c7mD&cid=279787&fnval=4048&fourk=1", "bilibili_playurl.json");

    [Fact]
    public async Task Bilibili_DashStreams_CarryRefererHdrAndExpiry()
    {
        var res = await Resolve(BiliServer(), "https://www.bilibili.com/video/BV1xx411c7mD");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;

        Assert.Equal("BV1xx411c7mD_p1", item.MediaId);
        Assert.Equal("Fixture Bilibili video - Part 1", item.Title);
        Assert.Equal(3, item.Representations.Count(r => r.Content == StreamContent.VideoOnly));
        Assert.Equal(2, item.Representations.Count(r => r.Content == StreamContent.AudioOnly));
        Assert.Contains(item.Representations, r => r.Height == 2160 && r.Hdr == HdrKind.Hdr10 && r.VideoCodec == "h265");
        Assert.All(item.Representations, r => Assert.Equal("https://www.bilibili.com/", r.Context.Referer));
        Assert.NotNull(item.EarliestExpiry);
        Assert.Equal(CollectionKind.Playlist, item.Related!.Kind);

        var best = FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!;
        Assert.Equal(2160, best.Video!.Height);
        Assert.Equal(192, best.Audio!.BitrateKbps);
        Assert.True(best.NeedsMuxing);
    }

    [Fact]
    public async Task Bilibili_PageParameterSelectsThePart_AndCollectionIntentListsParts()
    {
        var part2 = await Resolve(BiliServer(), "https://www.bilibili.com/video/BV1xx411c7mD?p=2");
        Assert.Equal("BV1xx411c7mD_p2", part2.Item!.MediaId);
        Assert.EndsWith("Part 2", part2.Item.Title);

        var parts = await Resolve(BiliServer(), "https://www.bilibili.com/video/BV1xx411c7mD", CollectionIntent.CollectionOnly);
        Assert.Equal(2, parts.Collection!.Entries.Count);
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD?p=2", parts.Collection.Entries[1].Url.AbsoluteUri);
    }

    [Fact]
    public async Task Bilibili_ApiErrorsAreTyped()
    {
        var h = new FakeHttpHandler().Add("https://api.bilibili.com/x/web-interface/view?bvid=BV1xx411c7mD",
            "{\"code\":-404,\"message\":\"not found\"}", "application/json");
        Assert.Equal(MediaErrorCode.VideoUnavailable, (await Resolve(h, "https://www.bilibili.com/video/BV1xx411c7mD")).Error!.Code);
    }

    // ---------------------------------------------------------------------------------------------- YouTube

    private const string MixUrl = "https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k&start_radio=1&pp=oAcB";

    private static FakeHttpHandler YtServer() => new FakeHttpHandler()
        .AddFile("https://www.youtube.com/watch?v=EK3tCffac5k", "youtube_watch.html", "text/html");

    private static string YtPage(string playerJson) => "<html><script>var ytInitialPlayerResponse = " + playerJson + ";</script></html>";

    [Fact]
    public async Task YouTube_MixUrl_ResolvesTheCurrentVideoOnly_AndHintsAtADynamicMix()
    {
        var h = YtServer();
        var res = await Resolve(h, MixUrl);

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Single(h.Requests);                                          // the mix is NOT enumerated
        Assert.Equal("EK3tCffac5k", item.MediaId);
        Assert.Equal("Fixture Video", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(212), item.Duration);
        Assert.NotNull(item.Related);
        Assert.Equal(CollectionKind.DynamicMix, item.Related!.Kind);
        Assert.True(item.Related.IsDynamic);
        Assert.Equal("RDEK3tCffac5k", item.Related.Id);

        Assert.Equal(5, item.Representations.Count);                        // signatureCipher format omitted, not faked
        Assert.DoesNotContain(item.Representations, r => r.Id == "yt-299");
        Assert.Contains(res.Notes, n => n.Contains("signature"));
        Assert.Contains(res.Notes, n => n.Contains("throttl"));
        Assert.Equal(4, item.Chapters.Count);
        Assert.Equal(TimeSpan.FromSeconds(212), item.Chapters[^1].End);
        Assert.Equal(2, item.Subtitles.Count);
        Assert.Contains(item.Subtitles, s => s.IsAutoGenerated && s.Url.AbsoluteUri.EndsWith("&fmt=vtt"));
        Assert.NotNull(item.EarliestExpiry);
    }

    [Fact]
    public async Task YouTube_DefaultMenu_OffersBestBalancedSmallestAndAudio()
    {
        var item = (await Resolve(YtServer(), "https://youtu.be/EK3tCffac5k")).Item!;
        var menu = QualityMenuBuilder.BuildDefault(item);
        Assert.Contains(menu, m => m.Kind == QualityOptionKind.BestWithAudio && m.Selection.Video!.Height == 2160);
        Assert.Contains(menu, m => m.Kind == QualityOptionKind.Balanced && m.Selection.Video!.Height == 1080);
        Assert.Contains(menu, m => m.Kind == QualityOptionKind.AudioOnly);
        Assert.All(menu.Where(m => m.Selection.Audio is not null && m.Selection.Video is not null), m => Assert.True(m.Selection.NeedsMuxing));
    }

    [Fact]
    public async Task YouTube_MixWithCollectionIntent_IsASnapshotNeverAFixedPlaylist()
    {
        var h = new FakeHttpHandler().AddFile("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k", "youtube_mix.html", "text/html");
        var res = await Resolve(h, MixUrl, CollectionIntent.CollectionOnly);

        Assert.Equal(DiscoveryStatus.Partial, res.Status);
        var c = res.Collection!;
        Assert.True(c.IsDynamic);
        Assert.False(c.IsComplete);
        Assert.Null(c.TotalCount);
        Assert.Equal(CollectionKind.DynamicMix, c.Kind);
        Assert.Equal("Mix - Fixture", c.Title);
        Assert.Equal(3, c.Entries.Count);
        Assert.Equal(TimeSpan.FromSeconds(3661), c.Entries[2].Duration);
        Assert.Contains(res.Warnings, w => w.Code == MediaErrorCode.PlaylistPartiallyResolved);
    }

    [Fact]
    public async Task YouTube_Playlist_DedupesAndAdmitsItIsOnlyTheFirstPage()
    {
        var h = new FakeHttpHandler().AddFile("https://www.youtube.com/playlist?list=PLabc123", "youtube_playlist.html", "text/html");
        var res = await Resolve(h, "https://www.youtube.com/playlist?list=PLabc123");

        Assert.Equal(DiscoveryStatus.Partial, res.Status);
        var c = res.Collection!;
        Assert.Equal("Fixture Playlist", c.Title);
        Assert.Equal(2, c.Entries.Count);                                    // duplicate AAAAAAAAAAA removed
        Assert.False(c.IsComplete);
        Assert.Null(c.NextPageToken);
        Assert.Equal(CollectionKind.Playlist, c.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA", c.Entries[0].Url.AbsoluteUri);   // entry URL carries no list: single-item intent
    }

    [Theory]
    [InlineData("https://youtu.be/EK3tCffac5k", "EK3tCffac5k")]
    [InlineData("https://www.youtube.com/shorts/EK3tCffac5k", "EK3tCffac5k")]
    [InlineData("https://m.youtube.com/watch?v=EK3tCffac5k&t=10s", "EK3tCffac5k")]
    [InlineData("https://www.youtube.com/embed/EK3tCffac5k", "EK3tCffac5k")]
    [InlineData("https://www.youtube.com/live/EK3tCffac5k", "EK3tCffac5k")]
    public void YouTube_UrlForms(string url, string id) => Assert.Equal(id, YouTubeAdapter.Parse(new Uri(url))!.VideoId);

    [Theory]
    [InlineData("https://www.youtube.com/@somechannel")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/")]
    public void YouTube_NonVideoUrls_AreNotParsed(string url) => Assert.Null(YouTubeAdapter.Parse(new Uri(url)));

    [Fact]
    public void YouTube_MixDetection()
    {
        Assert.True(YouTubeAdapter.Parse(new Uri(MixUrl))!.IsDynamicMix);
        Assert.True(YouTubeAdapter.Parse(new Uri("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDMM"))!.IsDynamicMix);
        Assert.False(YouTubeAdapter.Parse(new Uri("https://www.youtube.com/watch?v=EK3tCffac5k&list=PLabc123"))!.IsDynamicMix);
        Assert.False(YouTubeAdapter.Parse(new Uri("https://www.youtube.com/playlist?list=RDCLAK5uy_abc"))!.IsDynamicMix);
    }

    [Fact]
    public async Task YouTube_ChannelUrl_IsAnHonestInvalidInputError()
    {
        var res = await Resolve(new FakeHttpHandler(), "https://www.youtube.com/@somechannel");
        Assert.Equal(MediaErrorCode.InvalidInput, res.Error!.Code);
    }

    [Fact]
    public async Task YouTube_BotCheck_AsksForBrowserContext_NotForAWorkaround()
    {
        var page = YtPage("{\"playabilityStatus\":{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"Sign in to confirm you're not a bot\"}}");
        var res = await Resolve(new FakeHttpHandler().Add("https://www.youtube.com/watch?v=EK3tCffac5k", page), "https://www.youtube.com/watch?v=EK3tCffac5k");
        Assert.Equal(MediaErrorCode.BrowserContextRequired, res.Error!.Code);
        Assert.True(res.NeedsBrowserContext);
        Assert.False(res.Error.IsRetryable);
    }

    [Fact]
    public async Task YouTube_OnlyCipheredFormats_AreNotPassedOffAsDownloadable()
    {
        var page = YtPage("{\"playabilityStatus\":{\"status\":\"OK\"},\"streamingData\":{\"formats\":[{\"itag\":18,\"signatureCipher\":\"s=a&url=x\",\"mimeType\":\"video/mp4; codecs=\\\"avc1.42001E, mp4a.40.2\\\"\"}]},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"t\"}}");
        var res = await Resolve(new FakeHttpHandler().Add("https://www.youtube.com/watch?v=EK3tCffac5k", page), "https://www.youtube.com/watch?v=EK3tCffac5k");
        Assert.False(res.IsSuccess);
        Assert.Equal(MediaErrorCode.BrowserContextRequired, res.Error!.Code);
        Assert.Contains("signatureCipher", res.Error.Diagnostic);
    }

    [Theory]
    [InlineData("{\"playabilityStatus\":{\"status\":\"ERROR\",\"reason\":\"Video unavailable\"}}", MediaErrorCode.VideoUnavailable)]
    [InlineData("{\"playabilityStatus\":{\"status\":\"UNPLAYABLE\",\"reason\":\"The uploader has not made this video available in your country\"}}", MediaErrorCode.RegionRestricted)]
    [InlineData("{\"playabilityStatus\":{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"This video is private\"}}", MediaErrorCode.LoginRequired)]
    [InlineData("{\"playabilityStatus\":{\"status\":\"LIVE_STREAM_OFFLINE\",\"reason\":\"Offline\"}}", MediaErrorCode.LiveStreamNotSupported)]
    public async Task YouTube_PlayabilityStatuses_MapToTypedErrors(string player, MediaErrorCode expected)
    {
        var h = new FakeHttpHandler().Add("https://www.youtube.com/watch?v=EK3tCffac5k", YtPage(player));
        var res = await Resolve(h, "https://www.youtube.com/watch?v=EK3tCffac5k");
        Assert.Equal(expected, res.Error!.Code);
    }

    [Fact]
    public async Task YouTube_DrmFormats_AreReportedAsDrm()
    {
        var page = YtPage("{\"playabilityStatus\":{\"status\":\"OK\"},\"streamingData\":{\"adaptiveFormats\":[{\"itag\":1,\"url\":\"https://r.example.com/v\",\"drmFamilies\":[\"WIDEVINE\"],\"mimeType\":\"video/mp4; codecs=\\\"avc1.4d401e\\\"\"}]},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"t\"}}");
        var res = await Resolve(new FakeHttpHandler().Add("https://www.youtube.com/watch?v=EK3tCffac5k", page), "https://www.youtube.com/watch?v=EK3tCffac5k");
        Assert.Equal(MediaErrorCode.DrmProtected, res.Error!.Code);
    }

    // ---------------------------------------------------------------------------------------------- TikTok

    private const string TikTokUrl = "https://www.tiktok.com/@fixtureuser/video/7234567890123456789";

    [Fact]
    public async Task TikTok_ReturnsQualitiesAndScopesSessionCookiesToTikTokHosts()
    {
        var h = new FakeHttpHandler().Add(TikTokUrl, Fixture.Load("tiktok_page.html"), "text/html",
            headers: new() { ["Set-Cookie"] = "tt_chain_token=abc; Path=/; HttpOnly" });
        var res = await Resolve(h, TikTokUrl);

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("7234567890123456789", item.MediaId);
        Assert.Equal("fixtureuser", item.Uploader);
        Assert.Equal(TimeSpan.FromSeconds(15), item.Duration);
        Assert.Equal(3, item.Representations.Count);
        Assert.Contains(item.Representations, r => r.VideoCodec == "h265" && r.Height == 1280 && r.FileSize == 2800000);
        var ctx = item.Representations[0].Context;
        Assert.Equal("tt_chain_token=abc", ctx.CookieHeader);
        Assert.Contains("tiktok.com", ctx.CredentialHosts);
        Assert.False(ctx.MayForwardCredentialsTo("evil.example.net"));
    }

    [Fact]
    public async Task TikTok_CaptchaPhotoPostAndMissingVideo_AreTyped()
    {
        var captcha = new FakeHttpHandler().Add(TikTokUrl, "<html>Please solve the captcha</html>");
        Assert.Equal(MediaErrorCode.BrowserContextRequired, (await Resolve(captcha, TikTokUrl)).Error!.Code);

        string page(string inner) => "<script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\" type=\"application/json\">" +
            "{\"__DEFAULT_SCOPE__\":{\"webapp.video-detail\":" + inner + "}}</script>";

        var photo = new FakeHttpHandler().Add(TikTokUrl, page("{\"statusCode\":0,\"itemInfo\":{\"itemStruct\":{\"id\":\"1\",\"imagePost\":{\"images\":[]}}}}"));
        Assert.Equal(MediaErrorCode.NoDownloadableMedia, (await Resolve(photo, TikTokUrl)).Error!.Code);

        var gone = new FakeHttpHandler().Add(TikTokUrl, page("{\"statusCode\":10216,\"statusMsg\":\"video_not_exist\"}"));
        Assert.Equal(MediaErrorCode.VideoUnavailable, (await Resolve(gone, TikTokUrl)).Error!.Code);

        var priv = new FakeHttpHandler().Add(TikTokUrl, page("{\"statusCode\":10222}"));
        Assert.Equal(MediaErrorCode.LoginRequired, (await Resolve(priv, TikTokUrl)).Error!.Code);
    }
}
