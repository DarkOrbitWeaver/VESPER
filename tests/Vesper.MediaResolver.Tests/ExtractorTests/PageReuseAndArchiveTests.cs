using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public sealed class PageReuseAndArchiveTests
{
    [Fact]
    public async Task ReusingARequest_DoesNotReuseThePreviousCallsHtmlBody()
    {
        const string page = "https://generic-fixture.example/watch";
        var handler = new FakeHttpHandler().Add(page, "<video src=\"https://cdn.example.com/a.mp4\"></video>")
            .AddMedia("https://cdn.example.com/a.mp4").AddMedia("https://cdn.example.com/b.mp4");
        using var resolver = new DefaultMediaResolver(new ResolverOptions { CacheTtl = TimeSpan.Zero }, handler);
        var request = new MediaDiscoveryRequest { Url = new(page) };
        Assert.Contains((await resolver.ResolveAsync(request, CancellationToken.None)).Item!.Representations, r => r.Url.AbsolutePath == "/a.mp4");
        handler.Add(page, "<video src=\"https://cdn.example.com/b.mp4\"></video>");
        Assert.Contains((await resolver.ResolveAsync(request, CancellationToken.None)).Item!.Representations, r => r.Url.AbsolutePath == "/b.mp4");
        Assert.Equal(2, handler.Requests.Count(r => r.Url.AbsoluteUri == page));
    }

    private static FakeHttpHandler Archive() => new FakeHttpHandler().Add("https://archive.org/metadata/derived-fixture", """
        {"metadata":{"title":"Two public-domain tracks","mediatype":"audio"},"files":[
        {"name":"one.mp3","source":"original","bitrate":"128","size":"1000"},
        {"name":"one_64kb.mp3","source":"derivative","original":"one.mp3","bitrate":"64","size":"500"},
        {"name":"two.mp3","source":"original","bitrate":"128","size":"1000"}]}
        """, "application/json");

    [Fact]
    public async Task DeclaredArchiveDerivatives_DoNotBecomeDuplicatePlaylistTracks()
    {
        using var resolver = new DefaultMediaResolver(handler: Archive());
        var result = await resolver.ResolveAsync(new() { Url = new("https://archive.org/details/derived-fixture") }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.Equal(2, result.Collection!.Entries.Count);
        Assert.Equal(2, result.Collection.Entries[0].Resolved!.Representations.Count);
    }

    [Fact]
    public async Task ExactArchiveDerivativeUrl_SelectsItsOriginalTrackGroup()
    {
        using var resolver = new DefaultMediaResolver(handler: Archive());
        var result = await resolver.ResolveAsync(new() { Url = new("https://archive.org/details/derived-fixture/one_64kb.mp3") }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.NotNull(result.Item); Assert.Null(result.Collection);
        Assert.Equal(2, result.Item.Representations.Count);
    }
}
