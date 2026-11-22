using System.Text.Json;
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

    // ---------------------------------------------------------------------------------------------- Instagram

    private const string InstagramPage = "https://www.instagram.com/reel/FixtureCode/";

    private static string InstagramEmbeddedPage(string first, string second)
    {
        var data = new
        {
            data = new
            {
                xig_polaris_media = new
                {
                    if_not_gated_logged_out = new
                    {
                        media_type = "VIDEO",
                        video_duration = 12.5,
                        video_versions = new[]
                        {
                            new { url = first, width = 720, height = 1280 },
                            new { url = second, width = 1080, height = 1920 }
                        }
                    }
                }
            }
        };
        return "<html><head><title>Fixture Reel</title><meta property=\"og:image\" content=\"https://scontent.cdninstagram.com/thumb.jpg\"></head>"
            + "<script data-sjs>" + JsonSerializer.Serialize(data) + "</script></html>";
    }

    private static string InstagramCarouselPage(string first, string second)
    {
        object[] slides =
        [
            new
            {
                id = "clip-one", media_type = "VIDEO", video_duration = 5,
                video_versions = new[] { new { url = first, width = 720, height = 1280 } }
            },
            new
            {
                id = "photo-only", media_type = "IMAGE", video_duration = 0,
                video_versions = Array.Empty<object>()
            },
            new
            {
                id = "clip-two", media_type = "VIDEO", video_duration = 8,
                video_versions = new[] { new { url = second, width = 1080, height = 1920 } }
            }
        ];
        var data = new
        {
            data = new
            {
                xig_polaris_media = new
                {
                    if_not_gated_logged_out = new
                    {
                        caption = new { text = "Two clips" },
                        owner = new { username = "fixture_creator" },
                        carousel_media = slides
                    }
                }
            }
        };
        return "<html><head><title>Two clips</title></head><script data-sjs>"
            + JsonSerializer.Serialize(data) + "</script></html>";
    }

    [Fact]
    public async Task Instagram_UsesEmbeddedWholeObjectChoicesThroughSharedValidation()
    {
        const string first = "https://scontent.cdninstagram.com/video-720.mp4?token=short";
        const string second = "https://scontent.cdninstagram.com/video-1080.mp4?token=short";
        var h = new FakeHttpHandler()
            .Add(InstagramPage, InstagramEmbeddedPage(first, second))
            .AddMedia(first, "video/mp4", 7200)
            .AddMedia(second, "video/mp4", 10800);

        MediaDiscoveryResult result = await Resolve(h, InstagramPage);

        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal("instagram", result.Item!.Provider);
        Assert.Equal("FixtureCode", result.Item.MediaId);
        Assert.Equal("Fixture Reel", result.Item.Title);
        Assert.Equal(TimeSpan.FromSeconds(12.5), result.Item.Duration);
        Assert.Equal([1280, 1920], result.Item.Representations.Select(format => format.Height).Order().ToArray());
        Assert.All(h.Requests.Where(request => request.Url.Host == "scontent.cdninstagram.com"), request =>
            Assert.Equal("https://www.instagram.com/", request.Headers["Referer"]));
        Assert.DoesNotContain(h.Requests, request => request.Url.AbsolutePath.Contains("/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Instagram_PrefersTheRequestedReelOverOtherEmbeddedVideos()
    {
        const string unrelated = "https://scontent.cdninstagram.com/other-reel.mp4";
        const string unlabelled = "https://scontent.cdninstagram.com/unlabelled.mp4";
        const string requested = "https://scontent.cdninstagram.com/requested-reel.mp4";
        string page = $$$"""
            <script data-sjs>{"if_not_gated_logged_out":{"code":"OtherReel","video_url":"{{{unrelated}}}"}}</script>
            <script data-sjs>{"if_not_gated_logged_out":{"video_url":"{{{unlabelled}}}"}}</script>
            <script data-sjs>{"if_not_gated_logged_out":{"shortcode":"FixtureCode","video_url":"{{{requested}}}"}}</script>
            """;
        var h = new FakeHttpHandler().Add(InstagramPage, page)
            .AddMedia(unrelated, "video/mp4", 1000)
            .AddMedia(unlabelled, "video/mp4", 1000)
            .AddMedia(requested, "video/mp4", 1000);

        MediaDiscoveryResult result = await Resolve(h, InstagramPage);

        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(requested, Assert.Single(result.Item!.Representations).Url.AbsoluteUri);
        Assert.DoesNotContain(h.Requests, request => request.Url.AbsoluteUri == unrelated || request.Url.AbsoluteUri == unlabelled);
    }

    [Fact]
    public async Task Instagram_LoginRedirectIsExplainedWithoutTryingPrivateEndpoints()
    {
        const string login = "https://www.instagram.com/accounts/login/?next=%2Freel%2FFixtureCode%2F";
        var h = new FakeHttpHandler()
            .AddRedirect(InstagramPage, login)
            .Add(login, "<html>Sign in</html>");

        MediaDiscoveryResult result = await Resolve(h, InstagramPage);

        Assert.Equal(MediaErrorCode.LoginRequired, result.Error!.Code);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Instagram_DoesNotTreatLookalikeHostAsItsLoginPage()
    {
        const string lookalike = "https://evilinstagram.com/accounts/login/";
        var h = new FakeHttpHandler()
            .AddRedirect(InstagramPage, lookalike)
            .Add(lookalike, "<html>{\"login_required\":true}</html>");

        MediaDiscoveryResult result = await Resolve(h, InstagramPage);

        Assert.Equal(MediaErrorCode.BrowserContextRequired, result.Error!.Code);
    }

    [Fact]
    public async Task Instagram_PageTitleAloneDoesNotBecomeAPlayableMediaResult()
    {
        var h = new FakeHttpHandler().Add(InstagramPage, "<html><title>Title only</title></html>");

        MediaDiscoveryResult result = await Resolve(h, InstagramPage);

        Assert.False(result.IsSuccess);
        Assert.Equal(MediaErrorCode.BrowserContextRequired, result.Error!.Code);
        Assert.Contains("no verified playable URL", result.Error.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Instagram_CarouselListsOnlyVerifiedVideoSlidesAsSeparateItems()
    {
        const string first = "https://scontent.cdninstagram.com/clip-one.mp4?token=short";
        const string second = "https://scontent.cdninstagram.com/clip-two.mp4?token=short";
        var h = new FakeHttpHandler()
            .Add(InstagramPage, InstagramCarouselPage(first, second))
            .AddMedia(first, "video/mp4", 7200)
            .AddMedia(second, "video/mp4", 10800);

        MediaDiscoveryResult result = await Resolve(h, InstagramPage, CollectionIntent.CollectionOnly);

        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        MediaCollection collection = result.Collection!;
        Assert.Equal(CollectionKind.Album, collection.Kind);
        Assert.Equal(["clip-one", "clip-two"], collection.Entries.Select(entry => entry.MediaId).ToArray());
        Assert.All(collection.Entries, entry => Assert.NotEmpty(entry.Resolved!.Representations));
        Assert.DoesNotContain(h.Requests, request => request.Url.AbsolutePath.Contains("/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Instagram_ItemDiscoveryDoesNotPrefetchTheWholeCarousel()
    {
        const string first = "https://scontent.cdninstagram.com/clip-one.mp4?token=short";
        const string second = "https://scontent.cdninstagram.com/clip-two.mp4?token=short";
        var h = new FakeHttpHandler()
            .Add(InstagramPage, InstagramCarouselPage(first, second))
            .AddMedia(first, "video/mp4", 7200)
            .AddMedia(second, "video/mp4", 10800);

        MediaDiscoveryResult result = await Resolve(h, InstagramPage, CollectionIntent.ItemOnly);

        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal("clip-one", result.Item!.MediaId);
        Assert.Equal(CollectionKind.Album, result.Item.Related!.Kind);
        Assert.Single(h.Requests, request => request.Url.Host == "scontent.cdninstagram.com");
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
        Assert.Equal("Fixture Band", item.Artist);
        Assert.Equal("Fixture Band", item.AlbumArtist);
        Assert.Null(item.Album); // The track page does not identify its album in this source record.
        Assert.Equal(1, item.TrackNumber);
        Assert.Equal("2024-03-12", item.ReleaseDate);
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
        MediaItem firstTrack = Assert.IsType<MediaItem>(c.Entries[0].Resolved);
        Assert.Equal("Fixture Album", firstTrack.Album);
        Assert.Equal("Fixture Band", firstTrack.Artist);
        Assert.Equal("Fixture Band", firstTrack.AlbumArtist);
        Assert.Equal(1, firstTrack.TrackNumber);
        Assert.Equal("2024-03-12", firstTrack.ReleaseDate);
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

    private const string PlayerApi = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";

    private static readonly long YtExpiry = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
    private static string YtMediaUrl(int itag, long size) => $"https://rr1---sn-fixture.googlevideo.com/videoplayback?expire={YtExpiry}&itag={itag}&clen={size}";

    private static string YtFormat(int itag, string mime, long size, int? height = null, string extra = "", string? url = null) =>
        $"{{\"itag\":{itag},\"url\":\"{url ?? YtMediaUrl(itag, size)}\",\"mimeType\":\"{mime.Replace("\"", "\\\"")}\",\"contentLength\":\"{size}\",\"lastModified\":\"170000000000000{itag % 10}\""
        + (height is null ? "" : $",\"height\":{height},\"width\":{height * 16 / 9},\"fps\":30") + extra + "}";

    /// <summary>InnerTube player answer (visionOS strategy): 5 usable formats, one ciphered, one carrying the n challenge.</summary>
    private static readonly string YtApiPlayer =
        "{\"playabilityStatus\":{\"status\":\"OK\"},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"Fixture Video\",\"lengthSeconds\":\"212\",\"author\":\"Fixture Channel\"},\"streamingData\":{\"adaptiveFormats\":["
        + string.Join(",",
            YtFormat(401, "video/mp4; codecs=\"av01.0.12M.08\"", 900, 2160),
            YtFormat(137, "video/mp4; codecs=\"avc1.640028\"", 800, 1080),
            YtFormat(248, "video/webm; codecs=\"vp9\"", 700, 1080),
            YtFormat(140, "audio/mp4; codecs=\"mp4a.40.2\"", 300, extra: ",\"audioChannels\":2,\"audioSampleRate\":\"44100\""),
            YtFormat(251, "audio/webm; codecs=\"opus\"", 250, extra: ",\"audioChannels\":2,\"audioSampleRate\":\"48000\""),
            "{\"itag\":299,\"signatureCipher\":\"s=abc&url=https%3A%2F%2Fexample.com\",\"mimeType\":\"video/mp4; codecs=\\\"avc1.64002a\\\"\",\"height\":1080}",
            YtFormat(136, "video/mp4; codecs=\"avc1.4d401f\"", 600, 720, url: "https://rr1---sn-fixture.googlevideo.com/videoplayback?expire=4102444800&itag=136&n=abcdef"))
        + "]}}";

    private static FakeHttpHandler YtServer()
    {
        var h = new FakeHttpHandler()
            .AddFile("https://www.youtube.com/watch?v=EK3tCffac5k", "youtube_watch.html", "text/html")
            .Add(PlayerApi, YtApiPlayer, "application/json");
        foreach (var (itag, size) in new[] { (401, 900L), (137, 800L), (248, 700L), (140, 300L), (251, 250L) })
            h.AddMedia(YtMediaUrl(itag, size), "video/mp4", (int)size);
        return h;
    }

    private static string YtPage(string playerJson) => "<html><script>var ytInitialPlayerResponse = " + playerJson + ";</script></html>";

    [Fact]
    public async Task YouTube_MixUrl_ResolvesTheCurrentVideoOnly_AndHintsAtADynamicMix()
    {
        var h = YtServer();
        var res = await Resolve(h, MixUrl);

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal(1, h.Count("watch?v=EK3tCffac5k"));                    // the watch page is fetched once
        Assert.DoesNotContain(h.Requests, r => r.Url.AbsoluteUri.Contains("list=") || r.Url.AbsolutePath.EndsWith("/browse")); // the mix is NOT enumerated
        Assert.Equal("EK3tCffac5k", item.MediaId);
        Assert.Equal("Fixture Video", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(212), item.Duration);
        Assert.NotNull(item.Related);
        Assert.Equal(CollectionKind.DynamicMix, item.Related!.Kind);
        Assert.True(item.Related.IsDynamic);
        Assert.Equal("RDEK3tCffac5k", item.Related.Id);

        Assert.Equal(5, item.Representations.Count);                        // ciphered and n-challenge formats omitted, not faked
        Assert.DoesNotContain(item.Representations, r => r.Id is "yt-299" or "yt-136");
        Assert.Contains(res.Notes, n => n.Contains("signature"));
        Assert.Contains(res.Notes, n => n.Contains("throttl"));
        Assert.Contains(res.Notes, n => n.Contains("'visionos'"));
        Assert.All(item.Representations, r => Assert.StartsWith("youtube:EK3tCffac5k:", r.ContentIdentity));
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
        const string next = """
            {"contents":{"twoColumnWatchNextResults":{"playlist":{"playlist":{"title":"Mix - Fixture","contents":[
              {"playlistPanelVideoRenderer":{"videoId":"DDDDDDDDDDD","title":{"simpleText":"Related two"},"lengthText":{"simpleText":"1:01:01"}}},
              {"playlistPanelVideoRenderer":{"videoId":"EEEEEEEEEEE","title":{"simpleText":"Related three"},"lengthText":{"simpleText":"2:03"}}}
            ]}}}}}
            """;
        var h = new FakeHttpHandler()
            .AddFile("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k", "youtube_mix.html", "text/html")
            .Add("https://www.youtube.com/youtubei/v1/next?prettyPrint=false", next, "application/json");
        var res = await Resolve(h, MixUrl, CollectionIntent.CollectionOnly);

        Assert.True(res.Status == DiscoveryStatus.Partial,
            res.Error?.Diagnostic ?? string.Join("; ", res.Warnings.Select(w => w.Diagnostic)));
        var c = res.Collection!;
        Assert.True(c.IsDynamic);
        Assert.False(c.IsComplete);
        Assert.Null(c.TotalCount);
        Assert.Equal(CollectionKind.DynamicMix, c.Kind);
        Assert.Equal("Mix - Fixture", c.Title);
        Assert.Equal(["EK3tCffac5k", "CCCCCCCCCCC", "DDDDDDDDDDD", "EEEEEEEEEEE"], c.Entries.Select(e => e.MediaId));
        Assert.Equal(TimeSpan.FromSeconds(3661), c.Entries[2].Duration);
        Assert.Equal(TimeSpan.FromSeconds(123), c.Entries[3].Duration);
        var requests = h.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/next", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, requests.Count); // one page adds the fourth item; one terminal response confirms there are no more
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("1", request.Headers["X-YouTube-Client-Name"]);
        });
        using var body = JsonDocument.Parse(requests[0].Body!);
        Assert.Equal("RDEK3tCffac5k", body.RootElement.GetProperty("playlistId").GetString());
        Assert.Equal("DDDDDDDDDDD", body.RootElement.GetProperty("videoId").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("OAE%3D", body.RootElement.GetProperty("params").GetString());
        Assert.Contains(res.Warnings, w => w.Code == MediaErrorCode.PlaylistPartiallyResolved);
    }

    [Fact]
    public async Task YouTube_MixContinuationFailureKeepsTheWatchPageSnapshot()
    {
        var h = new FakeHttpHandler()
            .AddFile("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k", "youtube_mix.html", "text/html");
        var res = await Resolve(h, MixUrl, CollectionIntent.CollectionOnly);

        Assert.Equal(DiscoveryStatus.Partial, res.Status);
        Assert.Equal(3, res.Collection!.Entries.Count);
        Assert.Contains(res.Warnings, warning => warning.Diagnostic.Contains("HTTP 404", StringComparison.Ordinal));
        Assert.Contains(res.Warnings, warning => warning.Diagnostic.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task YouTube_MixPagingStopsAtTheConfiguredPageLimit()
    {
        const string next = """
            {"contents":{"twoColumnWatchNextResults":{"playlist":{"playlist":{"contents":[
              {"playlistPanelVideoRenderer":{"videoId":"DDDDDDDDDDD"}},
              {"playlistPanelVideoRenderer":{"videoId":"EEEEEEEEEEE"}}
            ]}}}}}
            """;
        var h = new FakeHttpHandler()
            .AddFile("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k", "youtube_mix.html", "text/html")
            .Add("https://www.youtube.com/youtubei/v1/next?prettyPrint=false", next, "application/json");
        using var resolver = new DefaultMediaResolver(new ResolverOptions { MaxCollectionPages = 1 }, h);
        var res = await resolver.ResolveAsync(new()
        {
            Url = new(MixUrl), Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.CollectionOnly }
        }, CancellationToken.None);

        Assert.Equal(DiscoveryStatus.Partial, res.Status);
        Assert.Equal(4, res.Collection!.Entries.Count);
        Assert.Single(h.Requests, request => request.Url.AbsolutePath.EndsWith("/next", StringComparison.Ordinal));
        Assert.Contains(res.Warnings, warning => warning.Diagnostic.Contains("configured page limit", StringComparison.Ordinal));
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
    private const string TikTokCdn = "https://v16.tiktokcdn-fixture.example/";

    private static FakeHttpHandler AddTikTokMedia(FakeHttpHandler handler) => handler
        .AddMedia(TikTokCdn + "h264_540.mp4?expire=4000000000", length: 1700)
        .AddMedia(TikTokCdn + "h265_720.mp4?expire=4000000000", length: 2800)
        .AddMedia(TikTokCdn + "play.mp4");

    [Theory]
    [InlineData("7234567890123456789", true)]
    [InlineData("7234567890123456780", false)]
    public async Task TikTok_EmbedRequiresTheRequestedVideo(string id, bool expectedSuccess)
    {
        string url = $"https://www.tiktok.com/embed/v2/{id}";
        var handler = AddTikTokMedia(new FakeHttpHandler().Add(url, Fixture.Load("tiktok_page.html"), "text/html"));
        var result = await Resolve(handler, url);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (expectedSuccess)
            Assert.Equal(id, result.Item!.MediaId);
        else
            Assert.Equal(MediaErrorCode.NoDownloadableMedia, result.Error!.Code);
    }

    [Fact]
    public async Task TikTok_ReturnsQualitiesWithoutLeakingHostOnlyCookiesToCdn()
    {
        var h = AddTikTokMedia(new FakeHttpHandler().Add(TikTokUrl, Fixture.Load("tiktok_page.html"), "text/html",
            headers: new() { ["Set-Cookie"] = "tt_chain_token=abc; Path=/; HttpOnly" }));
        var res = await Resolve(h, TikTokUrl);

        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.Equal("7234567890123456789", item.MediaId);
        Assert.Equal("fixtureuser", item.Uploader);
        Assert.Equal(TimeSpan.FromSeconds(15), item.Duration);
        Assert.Equal(3, item.Representations.Count);
        Assert.Contains(item.Representations, r => r.VideoCodec == "h265" && r.Height == 1280 && r.FileSize == 2800 && !r.FileSizeIsEstimate);
        var ctx = item.Representations[0].Context;
        Assert.Null(ctx.CookieHeader);
        Assert.Equal(item.Representations[0].Url.Host, Assert.Single(ctx.CredentialHosts));
        Assert.Equal(item.Representations[0].Url.GetLeftPart(UriPartial.Authority), ctx.CredentialOrigin!.GetLeftPart(UriPartial.Authority));
        Assert.False(ctx.MayForwardCredentialsTo(new Uri("https://evil.example.net/")));
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
    [Fact]
    public async Task TikTok_OneVerifiedMirrorPerQualityPreservesSignatureAndCodec()
    {
        const string body = """
        <script id="__UNIVERSAL_DATA_FOR_REHYDRATION__">{"__DEFAULT_SCOPE__":{"webapp.video-detail":{"statusCode":0,"itemInfo":{"itemStruct":{"id":"7234567890123456789","desc":"Fixture","video":{"duration":22,"width":576,"height":1024,"bitrateInfo":[{"GearName":"hd","CodecType":"h265_hvc1","PlayAddr":{"DataSize":"400577","Width":1080,"Height":1920,"UrlList":["https://v16.tiktokcdn.com/a?signature=a%2Bb&expire=4102444800","https://v19.tiktokcdn.com/a?signature=c%2Fd&expire=4102444800"]}}],"playAddr":{"UrlList":["https://www.tiktok.com/aweme/v1/play/?video_id=123"]}}}}}}}</script>
        """;
        const string denied = "https://v16.tiktokcdn.com/a?signature=a%2Bb&expire=4102444800";
        const string playable = "https://v19.tiktokcdn.com/a?signature=c%2Fd&expire=4102444800";
        var handler = new FakeHttpHandler().Add(TikTokUrl, body).Add(denied, "request refused from native route", status: 403)
            .AddMedia(playable, length: 4096).Add("https://www.tiktok.com/aweme/v1/play/?video_id=123", "<html>Challenge</html>");
        var result = await Resolve(handler, TikTokUrl);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        var format = Assert.Single(result.Item!.Representations);
        Assert.Equal("h265", format.VideoCodec);
        Assert.Equal(1920, format.Height);
        Assert.Equal(4096, format.FileSize);
        Assert.Equal(playable, format.Url.AbsoluteUri);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("session=fixture; Domain=.tiktok.com; Path=/; Secure", "session=fixture")]
    [InlineData("session=fixture; Path=/; Secure", null)]
    [InlineData("session=fixture; Domain=.tiktok.com; Path=/account; Secure", null)]
    public async Task TikTok_AnonymousSessionHonorsCookieDomainAndPath(string setCookie, string? expected)
    {
        const string media = "https://v16-webapp-prime.us.tiktok.com/video/tos/fixture/?signature=fixture";
        string body = "<script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\">{\"__DEFAULT_SCOPE__\":{\"webapp.video-detail\":{\"statusCode\":0,\"itemInfo\":{\"itemStruct\":{\"id\":\"7234567890123456789\",\"video\":{\"playAddr\":\"" + media + "\"}}}}}}</script>";
        var h = new FakeHttpHandler().Add(TikTokUrl, body, headers: new() { ["Set-Cookie"] = setCookie }).AddMedia(media);
        var result = await Resolve(h, TikTokUrl);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(expected, Assert.Single(result.Item!.Representations).Context.CookieHeader);
    }

    [Fact]
    public async Task TikTok_NextDataPlayerLayoutIsParsed()
    {
        const string body = """
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"statusCode":0,"itemInfo":{"itemStruct":{"id":"7234567890123456789","desc":"Next layout","video":{"duration":9,"playAddr":"https://v16.tiktokcdn.com/video.mp4?signature=abc"}}}}}}</script>
        """;
        var result = await Resolve(new FakeHttpHandler().Add(TikTokUrl, body)
            .AddMedia("https://v16.tiktokcdn.com/video.mp4?signature=abc"), TikTokUrl);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal("Next layout", result.Item!.Title);
        Assert.Equal(TimeSpan.FromSeconds(9), result.Item.Duration);
        Assert.Equal("https://v16.tiktokcdn.com/video.mp4?signature=abc", Assert.Single(result.Item.Representations).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData(10204, MediaErrorCode.RegionRestricted)]
    [InlineData(10222, MediaErrorCode.LoginRequired)]
    [InlineData(10223, MediaErrorCode.LoginRequired)]
    public async Task TikTok_StatusCodesAreTyped(int status, MediaErrorCode expected)
    {
        string body = "<script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\">{\"__DEFAULT_SCOPE__\":{\"webapp.video-detail\":{\"statusCode\":" + status + ",\"statusMsg\":\"fixture\"}}}</script>";
        var result = await Resolve(new FakeHttpHandler().Add(TikTokUrl, body), TikTokUrl);
        Assert.Equal(expected, result.Error!.Code);
    }

}
