using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public sealed class MetadataCacheExpiryTests
{
    private static readonly Uri Page = new("https://video.example.com/watch/1");

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

    [Fact]
    public void MixedRepresentationExpiriesUseTheEarliestExpiryAndSafetyMargin()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var earliest = now.AddMinutes(2);
        var cache = new MetadataCache(4);
        var result = MediaDiscoveryResult.FromItem(Item(
            Representation("soon", earliest),
            Representation("later", now.AddMinutes(10)),
            Representation("no-expiry", null)));

        cache.Set("mixed", result, now, TimeSpan.FromHours(1));

        DateTimeOffset lastSafeHit = earliest.AddSeconds(-30);
        Assert.True(cache.TryGet("mixed", lastSafeHit.AddTicks(-1), out _));
        Assert.False(cache.TryGet("mixed", lastSafeHit, out _));
    }

    [Fact]
    public void CollectionCacheLifetimeUsesTheEarliestResolvedEntryExpiry()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var earliest = now.AddSeconds(50);
        var collection = new MediaCollection
        {
            SourceUrl = Page,
            Provider = "fixture",
            Entries =
            [
                new CollectionEntry
                {
                    MediaId = "one",
                    Url = Page,
                    Resolved = Item(Representation("soon", earliest))
                },
                new CollectionEntry
                {
                    MediaId = "two",
                    Url = Page,
                    Resolved = Item(Representation("later", now.AddMinutes(5)))
                }
            ]
        };
        var cache = new MetadataCache(4);

        cache.Set("collection", MediaDiscoveryResult.FromCollection(collection), now, TimeSpan.FromHours(1));

        DateTimeOffset lastSafeHit = earliest.AddSeconds(-30);
        Assert.True(cache.TryGet("collection", lastSafeHit.AddTicks(-1), out _));
        Assert.False(cache.TryGet("collection", lastSafeHit, out _));
    }

    [Fact]
    public void NoKnownExpiryKeepsTheConfiguredCacheTtlAndExpiresAtItsBoundary()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var cache = new MetadataCache(4);
        cache.Set("stable", MediaDiscoveryResult.FromItem(Item(Representation("stable", null))), now, TimeSpan.FromMinutes(5));

        DateTimeOffset expires = now.AddMinutes(5);
        Assert.True(cache.TryGet("stable", expires.AddTicks(-1), out _));
        Assert.False(cache.TryGet("stable", expires, out _));
    }

    private static MediaItem Item(params MediaRepresentation[] representations) => new()
    {
        SourceUrl = Page,
        Provider = "fixture",
        Representations = representations
    };

    private static MediaRepresentation Representation(string id, DateTimeOffset? expiresAt) => new()
    {
        Id = id,
        Url = new Uri($"https://cdn.example.com/{id}.mp4"),
        ExpiresAt = expiresAt
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
