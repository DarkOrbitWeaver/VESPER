using System.Runtime.CompilerServices;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Playlists;

/// <summary>Everything a bounded enumeration learned about a collection.</summary>
public sealed record CollectionSnapshot(
    MediaCollection? FirstPage,
    IReadOnlyList<CollectionEntry> Entries,
    bool Truncated,
    string? NextPageToken,
    IReadOnlyList<MediaError> Errors);

/// <summary>
/// Lazy, bounded pagination over a collection URL. Guarantees: entries are de-duplicated by media id, page tokens
/// that repeat stop the loop (no infinite playlists), item and page counts are capped, cancellation is honoured between
/// and during pages, and a failing later page yields the partial result plus a typed error.
/// Use one pager per enumeration.
/// </summary>
public sealed class CollectionPager
{
    private readonly IMediaResolver _resolver;
    private readonly int _maxItems;
    private readonly int _maxPages;
    private readonly List<MediaError> _errors = new();

    public MediaCollection? FirstPage { get; private set; }
    public bool Truncated { get; private set; }
    public string? NextPageToken { get; private set; }
    public IReadOnlyList<MediaError> Errors => _errors;

    public CollectionPager(IMediaResolver resolver, int maxItems = 500, int maxPages = 20)
    {
        _resolver = resolver;
        _maxItems = Math.Max(1, maxItems);
        _maxPages = Math.Max(1, maxPages);
    }

    public async IAsyncEnumerable<CollectionEntry> EnumerateAsync(
        MediaDiscoveryRequest first, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = first.Options.PageToken;
        int emitted = 0;

        for (int page = 0; page < _maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (token is not null && !seenTokens.Add(token))
            {
                _errors.Add(MediaErrors.PartialPlaylist("Pagination repeated a page token; stopped to avoid an endless collection"));
                yield break;
            }

            var req = new MediaDiscoveryRequest
            {
                Url = first.Url,
                Browser = first.Browser,
                Options = new DiscoveryOptions
                {
                    CollectionIntent = CollectionIntent.CollectionOnly,
                    PageToken = token,
                    MaxCollectionItems = first.Options.MaxCollectionItems,
                    IncludeSubtitles = false,
                    ResolveEmbeds = first.Options.ResolveEmbeds
                }
            };

            var result = await _resolver.ResolveAsync(req, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Collection is null)
            {
                _errors.Add(result.Error ?? MediaErrors.Invalid("The URL does not describe a collection"));
                yield break;
            }
            var c = result.Collection;
            if (page == 0) FirstPage = c;
            _errors.AddRange(result.Warnings);

            foreach (var entry in c.Entries)
            {
                if (!seenIds.Add(entry.MediaId)) continue;
                if (emitted >= _maxItems)
                {
                    Truncated = true;
                    NextPageToken = token;
                    yield break;
                }
                emitted++;
                yield return entry with { Index = emitted - 1 };
            }

            token = c.NextPageToken;
            NextPageToken = token;
            if (token is null) yield break;
        }

        Truncated = true; // page cap reached with more pages available
    }

    public async Task<CollectionSnapshot> CollectAsync(MediaDiscoveryRequest first, CancellationToken ct = default)
    {
        var entries = new List<CollectionEntry>();
        await foreach (var e in EnumerateAsync(first, ct).ConfigureAwait(false)) entries.Add(e);
        return new CollectionSnapshot(FirstPage, entries, Truncated, NextPageToken, _errors.ToList());
    }
}
