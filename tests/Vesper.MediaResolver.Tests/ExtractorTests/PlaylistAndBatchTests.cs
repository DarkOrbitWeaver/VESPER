using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Playlists;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ExtractorTests;

public class CollectionPagerTests
{
    private static readonly Uri ListUrl = new("https://example.com/list");

    private static MediaCollection Page(string[] ids, string? next) => new()
    {
        SourceUrl = ListUrl,
        Provider = "test",
        Entries = ids.Select((id, i) => new CollectionEntry { MediaId = id, Url = new Uri($"https://example.com/{id}"), Index = i }).ToList(),
        NextPageToken = next,
        IsComplete = next is null
    };

    private static MediaDiscoveryRequest Req() => new() { Url = ListUrl };

    private static FakeResolver ThreePages() => new(r => r.Options.PageToken switch
    {
        null => MediaDiscoveryResult.FromCollection(Page(["e0", "e1", "e2"], "1")),
        "1" => MediaDiscoveryResult.FromCollection(Page(["e2", "e3", "e4"], "2")),
        "2" => MediaDiscoveryResult.FromCollection(Page(["e5"], null)),
        _ => MediaDiscoveryResult.Failure(MediaErrors.Unavailable("bad token"))
    });

    [Fact]
    public async Task Pages_AreConcatenated_AndDuplicatesRemoved()
    {
        var fake = ThreePages();
        var snap = await new CollectionPager(fake).CollectAsync(Req());

        Assert.True(new[] { "e0", "e1", "e2", "e3", "e4", "e5" }.SequenceEqual(snap.Entries.Select(e => e.MediaId)));
        Assert.Equal(Enumerable.Range(0, 6), snap.Entries.Select(e => e.Index));
        Assert.Equal(3, fake.Calls.Count);
        Assert.False(snap.Truncated);
        Assert.Empty(snap.Errors);
        Assert.Equal("1", fake.Calls[1].Options.PageToken);
        Assert.All(fake.Calls, c => Assert.Equal(CollectionIntent.CollectionOnly, c.Options.CollectionIntent));
    }

    [Fact]
    public async Task Enumeration_IsLazy()
    {
        var fake = ThreePages();
        int n = 0;
        await foreach (var _ in new CollectionPager(fake).EnumerateAsync(Req()))
            if (++n == 2) break;
        Assert.Single(fake.Calls);                      // stopping early never fetches the next page
    }

    [Fact]
    public async Task RepeatedPageToken_StopsInsteadOfLoopingForever()
    {
        int n = 0;
        var fake = new FakeResolver(_ => MediaDiscoveryResult.FromCollection(Page([$"p{n++}"], "same")));
        var snap = await new CollectionPager(fake).CollectAsync(Req());

        Assert.True(fake.Calls.Count <= 3);
        Assert.Contains(snap.Errors, e => e.Code == MediaErrorCode.PlaylistPartiallyResolved);
        Assert.NotEmpty(snap.Entries);
    }

    [Fact]
    public async Task ItemCap_TruncatesAndReportsWhereToResume()
    {
        var snap = await new CollectionPager(ThreePages(), maxItems: 4).CollectAsync(Req());
        Assert.Equal(4, snap.Entries.Count);
        Assert.True(snap.Truncated);
        Assert.NotNull(snap.NextPageToken);
    }

    [Fact]
    public async Task PageCap_StopsEndlessCollections()
    {
        int n = 0;
        var fake = new FakeResolver(_ => { int i = n++; return MediaDiscoveryResult.FromCollection(Page([$"x{i}"], $"t{i + 1}")); });
        var snap = await new CollectionPager(fake, maxPages: 3).CollectAsync(Req());
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal(3, snap.Entries.Count);
        Assert.True(snap.Truncated);
    }

    [Fact]
    public async Task LaterPageFailure_KeepsEarlierEntriesAndReportsTheError()
    {
        var fake = new FakeResolver(r => r.Options.PageToken is null
            ? MediaDiscoveryResult.FromCollection(Page(["a", "b"], "2"))
            : MediaDiscoveryResult.Failure(MediaErrors.RateLimited("429")));
        var snap = await new CollectionPager(fake).CollectAsync(Req());
        Assert.Equal(2, snap.Entries.Count);
        Assert.Contains(snap.Errors, e => e.Code == MediaErrorCode.RateLimited);
        Assert.Equal("2", snap.NextPageToken);
    }

    [Fact]
    public async Task FirstPageFailure_AndNonCollections_AreReported()
    {
        var fail = new FakeResolver(_ => MediaDiscoveryResult.Failure(MediaErrors.Unavailable("x")));
        var s1 = await new CollectionPager(fail).CollectAsync(Req());
        Assert.Empty(s1.Entries);
        Assert.Single(s1.Errors);

        var item = new FakeResolver(_ => MediaDiscoveryResult.FromItem(T.Item(T.Video("v", 720))));
        var s2 = await new CollectionPager(item).CollectAsync(Req());
        Assert.Equal(MediaErrorCode.InvalidInput, s2.Errors[0].Code);
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionPager(ThreePages()).CollectAsync(Req(), cts.Token));
    }
}

public class BatchResolverTests
{
    private static MediaItem ItemFor(int n)
    {
        bool has1080 = n % 3 != 0;
        var reps = has1080
            ? new[] { T.Video("a1080", 1080), T.Video("a720", 720) }
            : new[] { T.Video("a720", 720), T.Video("a480", 480) };
        return T.Item(reps) with { MediaId = $"m{n}", SourceUrl = new Uri($"https://example.com/item{n}") };
    }

    private static int NumberOf(Uri u) => int.Parse(u.AbsolutePath.Replace("/item", "").Replace("/bad", "").Trim('/'));

    private static FakeResolver Catalogue() => new(r =>
        r.Url.AbsolutePath.StartsWith("/bad")
            ? MediaDiscoveryResult.Failure(MediaErrors.Unavailable("removed"))
            : MediaDiscoveryResult.FromItem(ItemFor(NumberOf(r.Url))));

    private static List<BatchEntry> Entries(int count, Func<int, bool>? bad = null) =>
        Enumerable.Range(0, count).Select(i => new BatchEntry(new Uri(bad?.Invoke(i) == true ? $"https://example.com/bad{i}" : $"https://example.com/item{i}"), $"e{i}")).ToList();

    [Fact]
    public async Task HundredItems_OneQualityChoice_BestLegitimateFallbackWhen1080IsMissing()
    {
        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = Entries(100), Preferences = FormatPreferences.Height(1080), UserAuthorizedBatch = true
        }, null, CancellationToken.None);

        Assert.Equal(100, res.Succeeded);
        Assert.Equal(0, res.Failed);
        Assert.Equal(34, res.Fallbacks);                         // items 0,3,6,...,99 have no 1080p
        Assert.All(res.Outcomes.Where(o => NumberOf(o.Entry.Url) % 3 != 0), o => Assert.Equal(1080, o.Selection!.Video!.Height));
        Assert.All(res.Outcomes.Where(o => NumberOf(o.Entry.Url) % 3 == 0), o =>
        {
            Assert.Equal(720, o.Selection!.Video!.Height);
            Assert.True(o.Plan!.IsFallback);
        });
    }

    [Fact]
    public async Task PerItemOverrides_BeatStoreAndBatchPreference()
    {
        var store = new PreferenceStore();
        store.SetSite("test", FormatPreferences.SmallestSuitable);

        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = Entries(3).Skip(1).ToList(),              // e1 and e2 (both have 1080p)
            Preferences = FormatPreferences.Height(1080),
            PreferenceStore = store,
            ItemOverrides = new Dictionary<string, FormatPreferences> { ["e1"] = FormatPreferences.BestQuality },
            UserAuthorizedBatch = true
        }, null, CancellationToken.None);

        Assert.Equal(1080, res.Outcomes.Single(o => o.Entry.Id == "e1").Selection!.Video!.Height);   // override
        Assert.Equal(720, res.Outcomes.Single(o => o.Entry.Id == "e2").Selection!.Video!.Height);    // saved site preference
    }

    [Fact]
    public async Task BatchPreference_ApplyWhenNothingMoreSpecificExists()
    {
        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = Entries(2).Skip(1).ToList(), Preferences = FormatPreferences.SmallestSuitable,
            PreferenceStore = new PreferenceStore(), UserAuthorizedBatch = true
        }, null, CancellationToken.None);
        Assert.Equal(720, res.Outcomes[0].Selection!.Video!.Height);
    }

    [Fact]
    public async Task OneBadItem_NeverStopsTheOthers_AndErrorsNameTheEntry()
    {
        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = Entries(30, i => i % 3 == 0), Preferences = FormatPreferences.BestQuality, UserAuthorizedBatch = true
        }, null, CancellationToken.None);

        Assert.Equal(20, res.Succeeded);
        Assert.Equal(10, res.Failed);
        Assert.True(res.IsPartial);
        var failed = res.Outcomes.Where(o => !o.IsSuccess).ToList();
        Assert.All(failed, o =>
        {
            Assert.Equal(MediaErrorCode.VideoUnavailable, o.Error!.Code);
            Assert.Equal(o.Entry.Id, o.Error.EntryId);
        });
    }

    [Fact]
    public async Task WithoutExplicitUserAuthorization_NothingRuns()
    {
        var fake = Catalogue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BatchResolver(fake).ResolveAsync(
            new BatchRequest { Entries = Entries(5) }, null, CancellationToken.None));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ItemLimit_TruncatesAndSaysSo()
    {
        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = Entries(25), MaxItems = 10, UserAuthorizedBatch = true
        }, null, CancellationToken.None);
        Assert.Equal(10, res.Outcomes.Count);
        Assert.True(res.Truncated);
        Assert.True(res.IsPartial);
    }

    [Fact]
    public async Task Concurrency_IsBounded()
    {
        var fake = new FakeResolver
        {
            Handler = async (r, ct) =>
            {
                await Task.Delay(20, ct);
                return MediaDiscoveryResult.FromItem(ItemFor(NumberOf(r.Url)));
            }
        };
        var res = await new BatchResolver(fake).ResolveAsync(new BatchRequest
        {
            Entries = Entries(24), MaxConcurrency = 3, UserAuthorizedBatch = true
        }, null, CancellationToken.None);
        Assert.Equal(24, res.Succeeded);
        Assert.InRange(fake.MaxConcurrent, 1, 3);
    }

    [Fact]
    public async Task Cancellation_ReturnsThePartialResultWithoutThrowing()
    {
        var fake = new FakeResolver
        {
            Handler = async (r, ct) =>
            {
                await Task.Delay(40, ct);
                return MediaDiscoveryResult.FromItem(ItemFor(NumberOf(r.Url)));
            }
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var res = await new BatchResolver(fake).ResolveAsync(new BatchRequest
        {
            Entries = Entries(60), MaxConcurrency = 2, UserAuthorizedBatch = true
        }, null, cts.Token);
        Assert.True(res.WasCancelled);
        Assert.True(res.Outcomes.Count < 60);
        Assert.True(res.IsPartial);
    }

    [Fact]
    public async Task PreResolvedEntries_SkipTheResolver_AndCollectionsAreNotItems()
    {
        var fake = new FakeResolver(_ => MediaDiscoveryResult.FromCollection(
            new MediaCollection { SourceUrl = new Uri("https://example.com/c"), Provider = "test" }));
        var res = await new BatchResolver(fake).ResolveAsync(new BatchRequest
        {
            Entries = [new BatchEntry(new Uri("https://example.com/item1"), "pre", ItemFor(1)), new BatchEntry(new Uri("https://example.com/coll"), "coll")],
            UserAuthorizedBatch = true
        }, null, CancellationToken.None);

        Assert.True(res.Outcomes.Single(o => o.Entry.Id == "pre").IsSuccess);
        Assert.Equal(MediaErrorCode.InvalidInput, res.Outcomes.Single(o => o.Entry.Id == "coll").Error!.Code);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task ExpiredItem_FailsAloneWithARetryableError()
    {
        var expired = ItemFor(1) with { Representations = ItemFor(1).Representations.Select(r => r with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }).ToList() };
        var res = await new BatchResolver(Catalogue()).ResolveAsync(new BatchRequest
        {
            Entries = [new BatchEntry(new Uri("https://example.com/item1"), "old", expired), new BatchEntry(new Uri("https://example.com/item2"), "new")],
            UserAuthorizedBatch = true
        }, null, CancellationToken.None);
        Assert.Equal(MediaErrorCode.SourceExpired, res.Outcomes.Single(o => o.Entry.Id == "old").Error!.Code);
        Assert.True(res.Outcomes.Single(o => o.Entry.Id == "new").IsSuccess);
    }
}

public class UrlListParserTests
{
    [Fact]
    public void PastedText_IsSplitCleanedDeduplicatedAndValidated()
    {
        const string text = """
            https://example.com/a  https://example.com/a#t=10
            <https://example.com/b>, "https://example.com/c"
            javascript:alert(1) file:///C:/x http://127.0.0.1/admin not-a-url
            https://example.com/d
            """;
        var r = UrlListParser.Parse(text);
        Assert.True(new[] { "https://example.com/a", "https://example.com/b", "https://example.com/c", "https://example.com/d" }
            .SequenceEqual(r.Urls.Select(u => u.AbsoluteUri)));
        Assert.Contains(r.Rejected, x => x.Text.Contains("127.0.0.1"));
        Assert.Contains(r.Rejected, x => x.Text.StartsWith("javascript"));
        Assert.Contains(r.Rejected, x => x.Text.StartsWith("file:"));
        Assert.Contains(r.Rejected, x => x.Text == "not-a-url");
    }

    [Fact]
    public void Count_IsCapped()
    {
        string text = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"https://example.com/{i}"));
        var r = UrlListParser.Parse(text, maxUrls: 10);
        Assert.Equal(10, r.Urls.Count);
        Assert.True(r.Truncated);
        Assert.Empty(UrlListParser.Parse(null).Urls);
    }
}
