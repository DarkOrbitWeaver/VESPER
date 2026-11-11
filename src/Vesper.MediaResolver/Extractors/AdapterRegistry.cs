using Vesper.MediaResolver.Core;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// The list of dedicated adapters, in match order (first match wins; an adapter may return NotThisSite to defer).
/// Entries are cheap descriptors: adapters are instantiated lazily on first match. The generic adapter is the
/// implicit last resort and is not listed here. Add an adapter by adding its descriptor.
/// </summary>
public static class AdapterRegistry
{
    public static IReadOnlyList<AdapterDescriptor> Default { get; } =
    [
        YouTubeAdapter.Descriptor,
        VimeoAdapter.Descriptor,
        DailymotionAdapter.Descriptor,
        RedditAdapter.Descriptor,
        InternetArchiveAdapter.Descriptor,
        BandcampAdapter.Descriptor,
        BilibiliAdapter.Descriptor,
        TikTokAdapter.Descriptor,
        PeerTubeAdapter.Descriptor   // path-based, matches any host: keep last
    ];
}
