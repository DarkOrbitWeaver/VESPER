using System.Text.Json;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public class PlayerConfigDiscoveryTests
{
    private const string Page = "https://player-config.example/watch";
    private const string Target = "https://player-config.example/media-object";
    private const string Decoy = "https://player-config.example/ad.mp4";

    public static IEnumerable<object[]> SourceListLayouts()
    {
        foreach (string player in new[] { "player", "player_config", "VIDEO_CONFIG", "playback" })
        foreach (string list in new[] { "sources", "Sources", "video_sources", "VIDEO-SOURCES" })
        foreach (string source in new[] { "src", "File", "url", "content_url" })
            yield return [player, list, source];
    }

    [Theory]
    [MemberData(nameof(SourceListLayouts))]
    public async Task TypedSourceListSurvivesStructuralSpellingChanges(string player, string list, string source)
    {
        var descriptor = new Dictionary<string, object> { [source] = Target, ["mime_type"] = "video/mp4" };
        var config = new Dictionary<string, object>
        {
            ["props"] = new { pageProps = new Dictionary<string, object>
            {
                [player] = new Dictionary<string, object> { [list] = new[] { descriptor } },
                ["recommendations"] = new[] { new { sources = new[] { new { src = Decoy } } } }
            } }
        };
        await AssertExact(config);
    }

    public static IEnumerable<object[]> SingleSourceLayouts()
    {
        foreach (string player in new[] { "player", "media", "VideoConfig", "PLAYBACK", "video" })
        foreach (string source in new[] { "src", "file", "video_url", "playback-url", "url" })
        foreach (bool nested in new[] { false, true })
            yield return [player, source, nested];
    }

    [Theory]
    [MemberData(nameof(SingleSourceLayouts))]
    public async Task TypedSingularPlayerSourceSurvivesWrapperChanges(string player, string source, bool nested)
    {
        var descriptor = new Dictionary<string, object> { [source] = Target, ["content_type"] = "video/mp4" };
        var config = new Dictionary<string, object>
        {
            [player] = nested ? new Dictionary<string, object> { ["config"] = descriptor } : descriptor,
            ["ads"] = new { sources = new[] { Decoy } }
        };
        await AssertExact(config);
    }

    [Fact]
    public async Task RepeatedEquivalentPlayerConfigurationsAreNotAmbiguous()
    {
        var source = new { sources = new[] { new { src = Target, type = "video/mp4" } } };
        await AssertExact(new { player = source, video = source });
    }

    [Fact]
    public async Task TwoDistinctPlayerConfigurationsAreNotCombined()
    {
        await AssertRefused(new
        {
            player = new { src = Target, type = "video/mp4" },
            video = new { src = Decoy, type = "video/mp4" }
        });
    }

    [Fact]
    public async Task RootSourceListDoesNotHideAnotherExplicitPlayer()
    {
        await AssertRefused(new
        {
            sources = new[] { new { src = Target, type = "video/mp4" } },
            player = new { src = Decoy, type = "video/mp4" }
        });
    }

    [Fact]
    public async Task UnrelatedUrlFieldsAndRecommendationsAreNotPlayerEvidence()
    {
        await AssertRefused(new { url = Decoy, recommendations = new[] { new { player = new { src = Target } } } });
    }

    [Fact]
    public async Task ConflictingAliasKeysAreNotResolvedByPropertyOrder()
    {
        await AssertRefused(new { player = new Dictionary<string, object>
        {
            ["video_url"] = Target, ["video-url"] = Decoy, ["mimeType"] = "video/mp4"
        } });
    }

    [Fact]
    public async Task BudgetExhaustionCannotProveAUniquePlayer()
    {
        var config = new Dictionary<string, object> { ["player"] = new { src = Target, type = "video/mp4" } };
        for (int i = 0; i < 520; i++) config["irrelevant" + i] = i;
        await AssertRefused(config);
    }

    [Fact]
    public async Task ExplicitPlayerConfigAssignmentCanDeclareSingleSource()
    {
        string body = "<script>window.playerConfig = " + JsonSerializer.Serialize(new { file = Target, type = "video/mp4" }) + ";</script>";
        var h = Handler(body);
        using var resolver = T.Resolver(h);
        var result = await resolver.ResolveAsync(T.Req(Page), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(Target, Assert.Single(result.Item!.Representations).Url.AbsoluteUri);
    }

    private static FakeHttpHandler Handler(string body) => new FakeHttpHandler()
        .Add(Page, body, "text/html").AddMedia(Target, "video/mp4", 4096).AddMedia(Decoy, "video/mp4", 4096);
    private static FakeHttpHandler Config(object config) => Handler("<script type=\"application/json\">" +
        JsonSerializer.Serialize(config) + "</script>");

    private static async Task AssertExact(object config)
    {
        var h = Config(config);
        using var resolver = T.Resolver(h);
        var result = await resolver.ResolveAsync(T.Req(Page), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(Target, Assert.Single(result.Item!.Representations).Url.AbsoluteUri);
        Assert.DoesNotContain(h.Requests, request => request.Url.AbsoluteUri == Decoy);
    }

    private static async Task AssertRefused(object config)
    {
        var h = Config(config);
        using var resolver = T.Resolver(h);
        var result = await resolver.ResolveAsync(T.Req(Page), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(h.Requests, request => request.Url.AbsoluteUri == Target || request.Url.AbsoluteUri == Decoy);
    }
}
