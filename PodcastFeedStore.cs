using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace VESPER;

internal sealed record PodcastFeedSubscription
{
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> QueuedEpisodeIds { get; init; } = [];
}

/// <summary>Stores only stable feed addresses and hashes of episodes accepted by the queue.</summary>
internal sealed class PodcastFeedStore
{
    private const int Version = 1;
    private const int MaxFeeds = 100;
    private const int MaxQueuedEpisodesPerFeed = 10_000;
    private const int MaxQueuedEpisodesTotal = 100_000;
    private const int MaxStoreBytes = 12 * 1024 * 1024;
    private static readonly HashSet<string> SafeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "category", "channel", "collection", "feed", "feed_id", "format", "id", "lang", "language",
        "limit", "output", "page", "project", "section", "show", "site", "source", "tag", "type", "version"
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public List<PodcastFeedSubscription>? Feeds { get; set; }
    }

    private readonly string _path;
    private List<PodcastFeedSubscription> _feeds = [];
    private bool _canWrite = true;

    private PodcastFeedStore(string path) => _path = path;

    public string? LoadProblem { get; private set; }
    public IReadOnlyList<PodcastFeedSubscription> Feeds => _feeds
        .Select(feed => feed with { QueuedEpisodeIds = [.. feed.QueuedEpisodeIds] }).ToList();

    public static PodcastFeedStore Load(string path)
    {
        var store = new PodcastFeedStore(path);
        store.Read();
        return store;
    }

    public bool TryAddFeed(string text, out PodcastFeedSubscription feed, out string problem)
    {
        feed = null!;
        if (!CanChange(out problem)) return false;
        if (!TryNormalizeFeedUrl(text, out string url, out problem)) return false;

        PodcastFeedSubscription? existing = _feeds.FirstOrDefault(item => SameUrl(item.Url, url));
        if (existing is not null)
        {
            feed = existing with { QueuedEpisodeIds = [.. existing.QueuedEpisodeIds] };
            problem = "This feed is already in your list.";
            return true;
        }
        if (_feeds.Count >= MaxFeeds)
        {
            problem = "The feed list is full. Remove a feed before adding another.";
            return false;
        }

        feed = new PodcastFeedSubscription { Url = url, Title = new Uri(url).Host };
        List<PodcastFeedSubscription> previous = _feeds;
        _feeds = [.. previous, feed];
        if (Save(out problem)) return true;
        _feeds = previous;
        feed = null!;
        return false;
    }

    public bool TryUpdateTitle(string feedUrl, string title, out string problem)
    {
        if (!CanChange(out problem) || !TryNormalizeFeedUrl(feedUrl, out string url, out problem)) return false;
        int index = FindFeed(url);
        if (index < 0)
        {
            problem = "The feed is no longer in the list.";
            return false;
        }

        string safeTitle = CleanTitle(title, new Uri(url).Host);
        PodcastFeedSubscription previous = _feeds[index];
        if (previous.Title == safeTitle) return true;
        _feeds[index] = previous with { Title = safeTitle };
        if (Save(out problem)) return true;
        _feeds[index] = previous;
        return false;
    }

    public bool TryRemoveFeed(string feedUrl, out string problem)
    {
        if (!CanChange(out problem) || !TryNormalizeFeedUrl(feedUrl, out string url, out problem)) return false;
        int index = FindFeed(url);
        if (index < 0)
        {
            problem = "The feed is no longer in the list.";
            return false;
        }

        PodcastFeedSubscription removed = _feeds[index];
        _feeds.RemoveAt(index);
        if (Save(out problem)) return true;
        _feeds.Insert(index, removed);
        return false;
    }

    public bool IsQueued(string feedUrl, string episodeId)
    {
        if (!TryNormalizeFeedUrl(feedUrl, out string url, out _)) return false;
        int index = FindFeed(url);
        return index >= 0 && _feeds[index].QueuedEpisodeIds.Contains(episodeId, StringComparer.Ordinal);
    }

    public bool TryMarkQueued(string feedUrl, IEnumerable<string> episodeIds, out string problem)
    {
        if (!CanChange(out problem) || !TryNormalizeFeedUrl(feedUrl, out string url, out problem)) return false;
        int index = FindFeed(url);
        if (index < 0)
        {
            problem = "The feed is no longer in the list.";
            return false;
        }

        string[] additions = episodeIds.Distinct(StringComparer.Ordinal)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (additions.Any(id => id.Length != 64 || !id.All(Uri.IsHexDigit)))
        {
            problem = "An episode could not be saved safely.";
            return false;
        }

        PodcastFeedSubscription previous = _feeds[index];
        string[] queued = previous.QueuedEpisodeIds.Concat(additions)
            .Distinct(StringComparer.Ordinal).ToArray();
        int total = _feeds.Sum(item => item.QueuedEpisodeIds.Count)
            - previous.QueuedEpisodeIds.Count + queued.Length;
        if (queued.Length > MaxQueuedEpisodesPerFeed || total > MaxQueuedEpisodesTotal)
        {
            problem = "The episode history is full. Remove an unused feed before queueing more.";
            return false;
        }

        _feeds[index] = previous with { QueuedEpisodeIds = [.. queued] };
        if (Save(out problem)) return true;
        _feeds[index] = previous;
        return false;
    }

    internal static bool TryNormalizeFeedUrl(string? text, out string url, out string problem)
    {
        url = "";
        if (!UrlPolicy.TryParse(text, out Uri uri, out problem)) return false;
        if (uri.AbsoluteUri.Length > 2048)
        {
            problem = "Keep the feed link under 2,048 characters.";
            return false;
        }

        var query = new List<string>();
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (string pair in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                string key;
                string value;
                try
                {
                    key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
                    value = Uri.UnescapeDataString(separator < 0 ? "" : pair[(separator + 1)..]);
                }
                catch (UriFormatException)
                {
                    problem = "The feed link has an invalid query string.";
                    return false;
                }

                if (!SafeQueryKeys.Contains(key) || value.Length > 64
                    || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '~' or '-')))
                {
                    problem = "This feed link has query data that cannot be safely saved. Use its public feed address without a signed token.";
                    return false;
                }
                query.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            }
        }

        var builder = new UriBuilder(uri) { Fragment = "", Query = string.Join("&", query) };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        url = UrlPolicy.Normalize(builder.Uri);
        problem = "";
        return true;
    }

    internal static string EpisodeIdentity(string feedUrl, CollectionEntry entry)
    {
        string mediaId = entry.MediaId.Trim();
        if (Uri.TryCreate(mediaId, UriKind.Absolute, out Uri? mediaUrl))
        {
            var builder = new UriBuilder(mediaUrl) { Query = "", Fragment = "" };
            mediaId = UrlPolicy.Normalize(builder.Uri);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(feedUrl + "\n" + mediaId);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string RequestId(string feedUrl, string episodeIdentity)
    {
        string feedIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(feedUrl))).ToLowerInvariant();
        return "podcast-" + feedIdentity[..24] + "-" + episodeIdentity[..32];
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > MaxStoreBytes)
                throw new InvalidDataException("The podcast feed list is too large to load.");

            StoreDocument? document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_path));
            if (document is null || document.Version != Version || document.Feeds is null || document.Feeds.Count > MaxFeeds)
                throw new InvalidDataException("The podcast feed list has an unsupported format.");

            var parsed = new List<PodcastFeedSubscription>(document.Feeds.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;
            foreach (PodcastFeedSubscription? feed in document.Feeds)
            {
                if (feed is null || !TryNormalizeFeedUrl(feed.Url, out string url, out _)
                    || url != feed.Url || !seen.Add(url)
                    || feed.QueuedEpisodeIds is null || feed.QueuedEpisodeIds.Count > MaxQueuedEpisodesPerFeed)
                    throw new InvalidDataException("The podcast feed list contains an invalid entry.");
                if (feed.QueuedEpisodeIds.Any(id => id is null || id.Length != 64 || !id.All(Uri.IsHexDigit)
                    || !string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal)))
                    throw new InvalidDataException("The podcast feed list contains an invalid episode ID.");

                total += feed.QueuedEpisodeIds.Count;
                if (total > MaxQueuedEpisodesTotal)
                    throw new InvalidDataException("The podcast feed list contains too many episode IDs.");
                parsed.Add(feed with { Title = CleanTitle(feed.Title, new Uri(url).Host) });
            }
            _feeds = parsed;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException)
        {
            _canWrite = false;
            LoadProblem = ex.Message;
        }
    }

    private bool CanChange(out string problem)
    {
        if (!_canWrite)
        {
            problem = "The saved feed list could not be read, so it will not be overwritten. " + LoadProblem;
            return false;
        }
        problem = "";
        return true;
    }

    private int FindFeed(string url) => _feeds.FindIndex(feed => SameUrl(feed.Url, url));

    private static bool SameUrl(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string CleanTitle(string? title, string fallback)
        => FileNames.Sanitize(title, fallback);

    private bool Save(out string problem)
    {
        string? folder = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            problem = "The feed list location is invalid.";
            return false;
        }

        string temp = _path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(folder);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument { Version = Version, Feeds = _feeds }, JsonOptions);
            if (json.Length > MaxStoreBytes)
            {
                problem = "The podcast feed list is too large to save.";
                return false;
            }
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            problem = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The feed list could not be saved: " + ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
