using Vesper.MediaResolver.Models;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class PodcastFeedStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-podcast-tests-").FullName;

    [Fact]
    public void FeedStoreKeepsStablePublicQueryPartsButRefusesSignedQueryData()
    {
        string path = Path.Combine(_root, "podcast-feeds.json");
        PodcastFeedStore store = PodcastFeedStore.Load(path);

        Assert.False(store.TryAddFeed("https://feeds.example/show.xml?token=signed-secret", out _, out string rejected));
        Assert.Contains("safely saved", rejected, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.TryAddFeed("https://user:password@feeds.example/show.xml", out _, out _));
        Assert.False(File.Exists(path));

        Assert.True(store.TryAddFeed("https://feeds.example/show.xml?feed=42&lang=en", out PodcastFeedSubscription feed, out _));
        Assert.Equal("https://feeds.example/show.xml?feed=42&lang=en", feed.Url);
        string saved = File.ReadAllText(path);
        Assert.Contains("feed=42", saved);
        Assert.DoesNotContain("signed-secret", saved);

        PodcastFeedStore reopened = PodcastFeedStore.Load(path);
        Assert.Equal(feed.Url, Assert.Single(reopened.Feeds).Url);
    }

    [Fact]
    public void EpisodeHistoryStoresOnlyStableHashesAndIsIdempotentAcrossSignedEnclosureRefreshes()
    {
        string path = Path.Combine(_root, "podcast-feeds.json");
        const string feedUrl = "https://feeds.example/show.xml";
        PodcastFeedStore store = PodcastFeedStore.Load(path);
        Assert.True(store.TryAddFeed(feedUrl, out _, out _));

        var first = new CollectionEntry
        {
            MediaId = "https://cdn.example/episode.mp3?token=first-private-value",
            Url = new Uri("https://cdn.example/episode.mp3?token=first-private-value")
        };
        var refreshed = first with
        {
            MediaId = "https://cdn.example/episode.mp3?token=second-private-value",
            Url = new Uri("https://cdn.example/episode.mp3?token=second-private-value")
        };
        string firstId = PodcastFeedStore.EpisodeIdentity(feedUrl, first);
        string refreshedId = PodcastFeedStore.EpisodeIdentity(feedUrl, refreshed);

        Assert.Equal(firstId, refreshedId);
        Assert.Equal(64, firstId.Length);
        Assert.True(store.TryMarkQueued(feedUrl, [firstId], out _));
        Assert.True(store.IsQueued(feedUrl, refreshedId));

        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("first-private-value", saved);
        Assert.DoesNotContain("second-private-value", saved);
        Assert.DoesNotContain("episode.mp3", saved);
        Assert.Equal(PodcastFeedStore.RequestId(feedUrl, firstId), PodcastFeedStore.RequestId(feedUrl, refreshedId));
    }

    [Fact]
    public void FeedPathCaseIsPreservedWhenAvoidingDuplicates()
    {
        PodcastFeedStore store = PodcastFeedStore.Load(Path.Combine(_root, "podcast-feeds.json"));

        Assert.True(store.TryAddFeed("https://feeds.example/Show.xml", out _, out _));
        Assert.True(store.TryAddFeed("https://feeds.example/show.xml", out _, out _));

        Assert.Equal(2, store.Feeds.Count);
    }

    [Fact]
    public void UnreadableFeedHistoryIsLeftUntouched()
    {
        string path = Path.Combine(_root, "podcast-feeds.json");
        const string corrupt = "{ invalid feed data";
        File.WriteAllText(path, corrupt);
        PodcastFeedStore store = PodcastFeedStore.Load(path);

        Assert.False(string.IsNullOrWhiteSpace(store.LoadProblem));
        Assert.False(store.TryAddFeed("https://feeds.example/show.xml", out _, out _));
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void NullEpisodeHashesDoNotCrashOrOverwriteFeedHistory()
    {
        string path = Path.Combine(_root, "podcast-feeds.json");
        const string invalid = """{"Version":1,"Feeds":[{"Url":"https://feeds.example/show.xml","Title":"Show","QueuedEpisodeIds":[null]}]}""";
        File.WriteAllText(path, invalid);

        PodcastFeedStore store = PodcastFeedStore.Load(path);

        Assert.False(string.IsNullOrWhiteSpace(store.LoadProblem));
        Assert.False(store.TryAddFeed("https://feeds.example/another.xml", out _, out _));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
