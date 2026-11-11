namespace Vesper.MediaResolver.Models;

public sealed record CollectionEntry
{
    public required string MediaId { get; init; }
    public required Uri Url { get; init; }
    public string? Title { get; init; }
    public TimeSpan? Duration { get; init; }
    public Uri? Thumbnail { get; init; }
    public int Index { get; init; }

    /// <summary>Present when the source already delivered full metadata (e.g. Internet Archive, Bandcamp).</summary>
    public MediaItem? Resolved { get; init; }

    /// <summary>Per-entry failure; other entries are unaffected.</summary>
    public MediaError? Error { get; init; }
}

/// <summary>A playlist, album, feed or dynamic mix. Listing never implies permission to download everything.</summary>
public sealed record MediaCollection
{
    public required Uri SourceUrl { get; init; }
    public required string Provider { get; init; }
    public string? CollectionId { get; init; }
    public string Title { get; init; } = "";
    public CollectionKind Kind { get; init; } = CollectionKind.Playlist;

    /// <summary>Dynamic collections (YouTube Mix/radio) change on every request: entries are a snapshot only.</summary>
    public bool IsDynamic { get; init; }

    public int? TotalCount { get; init; }
    public IReadOnlyList<CollectionEntry> Entries { get; init; } = [];

    /// <summary>Opaque token for <c>DiscoveryOptions.PageToken</c>; null when no further page is reachable.</summary>
    public string? NextPageToken { get; init; }

    /// <summary>False when more items exist (or are unreachable) beyond <see cref="Entries"/>.</summary>
    public bool IsComplete { get; init; } = true;
}
