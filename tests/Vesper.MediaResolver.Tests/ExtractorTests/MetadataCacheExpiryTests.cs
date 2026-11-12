using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public sealed class MetadataCacheExpiryTests
{
    [Fact]
    public async Task SignedUrlIsResolvedAgainBeforeItsExpirySafetyMargin()
    {
        long expirySeconds = 1_800_000_000;
        var expires = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        var time = new MutableTimeProvider(expires.AddSeconds(-60));
        string url = $"https://cdn.example.com/a.mp4?expires={expirySeconds}";
        var handler = new FakeHttpHandler().AddMedia(url);
        using var resolver = new DefaultMediaResolver(new ResolverOptions
        {
            CacheTtl = TimeSpan.FromSeconds(90),
            TimeProvider = time
        }, handler);

        Assert.True((await resolver.ResolveAsync(new() { Url = new(url) }, CancellationToken.None)).IsSuccess);
        int requestsBeforeRefresh = resolver.NetworkRequestCount;

        time.Advance(TimeSpan.FromSeconds(31));
        var refreshed = await resolver.ResolveAsync(new() { Url = new(url) }, CancellationToken.None);

        Assert.True(refreshed.IsSuccess, refreshed.Error?.Diagnostic);
        Assert.True(resolver.NetworkRequestCount > requestsBeforeRefresh);
        Assert.Equal(expires, refreshed.Item!.EarliestExpiry);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
