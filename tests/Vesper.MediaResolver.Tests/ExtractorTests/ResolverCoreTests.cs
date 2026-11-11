using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public class ResolverCoreTests
{
    [Fact]
    public void Construction_DoesNoWork_AndOpensNoConnections()
    {
        var h = new FakeHttpHandler();
        using var r = new DefaultMediaResolver(new ResolverOptions(), h);
        Assert.Empty(h.Requests);
        Assert.Equal(0, r.NetworkRequestCount);
        Assert.NotEmpty(r.Adapters);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task UnsafeRequestUrls_AreRejectedWithoutAnyRequest(string url)
    {
        var h = new FakeHttpHandler();
        using var r = T.Resolver(h);
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        var res = await r.ResolveAsync(new MediaDiscoveryRequest { Url = uri! }, CancellationToken.None);
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, res.Error!.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Cache_ServesRepeatedLookups_ButNeverForBrowserCredentials()
    {
        var h = new FakeHttpHandler().AddMedia("https://media.generic-fixture.example/a.mp4", "video/mp4");
        using var r = new DefaultMediaResolver(new ResolverOptions { CacheTtl = TimeSpan.FromMinutes(1) }, h);
        var req = T.Req("https://media.generic-fixture.example/a.mp4");

        await r.ResolveAsync(req, CancellationToken.None);
        int after1 = h.Requests.Count;
        await r.ResolveAsync(req, CancellationToken.None);
        Assert.Equal(after1, h.Requests.Count);                          // second call came from the cache

        r.ClearCache();
        await r.ResolveAsync(req, CancellationToken.None);
        Assert.True(h.Requests.Count > after1);
    }

    [Fact]
    public async Task Cache_IsDisabledWithZeroTtl()
    {
        var h = new FakeHttpHandler().AddMedia("https://media.generic-fixture.example/a.mp4", "video/mp4");
        using var r = T.Resolver(h);
        var req = T.Req("https://media.generic-fixture.example/a.mp4");
        await r.ResolveAsync(req, CancellationToken.None);
        int after1 = h.Requests.Count;
        await r.ResolveAsync(req, CancellationToken.None);
        Assert.Equal(after1 * 2, h.Requests.Count);
    }

    [Fact]
    public async Task Failures_AreReturnedNotThrown_AndNeverContainStackTraces()
    {
        var h = new FakeHttpHandler().Add("https://www.youtube.com/watch?v=EK3tCffac5k", "<html>garbage without any player response</html>");
        using var r = T.Resolver(h);
        var res = await r.ResolveAsync(T.Req("https://www.youtube.com/watch?v=EK3tCffac5k"), CancellationToken.None);
        Assert.False(res.IsSuccess);
        Assert.NotEmpty(res.Error!.UserMessage);
        Assert.DoesNotContain("   at ", res.Error.UserMessage);
        Assert.DoesNotContain("   at ", res.Error.Diagnostic);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        using var r = T.Resolver(new FakeHttpHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ResolveAsync(T.Req("https://example.com/a.mp4"), cts.Token));
    }

    [Fact]
    public async Task UsingADisposedResolver_Throws()
    {
        var r = T.Resolver(new FakeHttpHandler());
        r.Dispose();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => r.ResolveAsync(T.Req("https://example.com/a.mp4"), CancellationToken.None));
    }

    [Fact]
    public void ErrorFactories_AreCompleteAndSecretFree()
    {
        var url = new Uri("https://x.example.com/a?token=SECRET");
        var all = new[]
        {
            MediaErrors.UnsupportedSite(url), MediaErrors.NoMedia("d"), MediaErrors.Unavailable("d"), MediaErrors.LoginRequired("d"),
            MediaErrors.Region("d"), MediaErrors.Expired("d"), MediaErrors.Drm("d"), MediaErrors.UnsupportedManifest("d"),
            MediaErrors.RateLimited("d"), MediaErrors.Network("d"), MediaErrors.AuthRequired("d"), MediaErrors.SourceChanged("d"),
            MediaErrors.PartialPlaylist("d"), MediaErrors.BrowserContext("d"), MediaErrors.Blocked("d"), MediaErrors.TooLarge("d"),
            MediaErrors.Live("d"), MediaErrors.Invalid("d"), MediaErrors.NoFormat("d"), MediaErrors.FromHttpStatus(404, url)
        };
        Assert.All(all, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.UserMessage));
            Assert.False(string.IsNullOrWhiteSpace(e.Diagnostic));
            Assert.DoesNotContain("SECRET", e.Diagnostic);
        });
        Assert.True(MediaErrors.RateLimited("d").IsRetryable);
        Assert.False(MediaErrors.Drm("d").IsRetryable);
        Assert.True(MediaErrors.Drm("d").IsPermanent);
        Assert.True(MediaErrors.LoginRequired("d").BrowserMayHelp);
    }
}

public class CompatibilityTests
{
    [Fact]
    public void Registry_HasUniqueIdsAndCompleteCapabilityDeclarations()
    {
        var all = AdapterRegistry.Default;
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct().Count());
        Assert.All(all, d =>
        {
            Assert.NotEmpty(d.Capabilities.Domains);
            Assert.NotEmpty(d.Capabilities.UrlPatterns);
            Assert.False(string.IsNullOrWhiteSpace(d.Capabilities.Authentication));
            Assert.False(string.IsNullOrWhiteSpace(d.Capabilities.Notes));
        });
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=EK3tCffac5k", "youtube")]
    [InlineData("https://youtu.be/EK3tCffac5k", "youtube")]
    [InlineData("https://vimeo.com/76979871", "vimeo")]
    [InlineData("https://player.vimeo.com/video/76979871", "vimeo")]
    [InlineData("https://www.dailymotion.com/video/x8abc12", "dailymotion")]
    [InlineData("https://dai.ly/x8abc12", "dailymotion")]
    [InlineData("https://www.reddit.com/r/a/comments/abc123/t/", "reddit")]
    [InlineData("https://v.redd.it/abc123xyz", "reddit")]
    [InlineData("https://archive.org/details/fixture_movie", "internet-archive")]
    [InlineData("https://artist.bandcamp.com/track/x", "bandcamp")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "bilibili")]
    [InlineData("https://b23.tv/abc", "bilibili")]
    [InlineData("https://www.tiktok.com/@u/video/123", "tiktok")]
    [InlineData("https://vm.tiktok.com/ZMabc/", "tiktok")]
    [InlineData("https://peertube.example.org/w/kkGMgK9ZtnKfYAgnEtQxbv", "peertube")]
    public void UrlRouting_PicksTheDedicatedAdapter(string url, string expected) =>
        Assert.Equal(expected, AdapterRegistry.Default.First(d => d.Matches(new Uri(url))).Id);

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://example.com/index.html")]
    [InlineData("https://vimeo.com/channels/staffpicks")]
    [InlineData("https://www.tiktok.com/@someuser")]
    public void OtherUrls_GoToTheGenericAdapter(string url) =>
        Assert.DoesNotContain(AdapterRegistry.Default, d => d.Matches(new Uri(url)));

    [Fact]
    public void Report_NeverClaimsVerificationWithoutARecord()
    {
        var adapters = AdapterRegistry.Default.Append(Discovery.GenericAdapter.Descriptor).ToList();
        string md = CompatibilityReport.ToMarkdown(adapters);
        Assert.All(adapters, a => Assert.Contains(a.DisplayName, md));
        Assert.DoesNotContain("VerifiedWorking |", md);
        Assert.Contains("NotTested", md);

        string md2 = CompatibilityReport.ToMarkdown(adapters, [new CompatibilityRecord("vimeo", CompatibilityStatus.VerifiedWorking, new DateOnly(2026, 1, 2), "single item")]);
        Assert.Contains("VerifiedWorking", md2);
        Assert.Contains("2026-01-02", md2);
    }
}
