using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Core;

/// <summary>The public integration surface. One call = one bounded discovery operation; nothing runs between calls.</summary>
public interface IMediaResolver
{
    Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken cancellationToken);
}

/// <summary>A small, isolated, site-specific extractor.</summary>
public interface IMediaSourceAdapter
{
    bool CanHandle(Uri url);

    Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken cancellationToken);
}

/// <summary>Machine-readable statement of what an adapter claims; feeds the compatibility report.</summary>
public sealed record AdapterCapabilities(
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> UrlPatterns,
    bool SingleItem,
    bool Playlist,
    bool AudioOnly,
    bool Subtitles,
    string Authentication,
    IReadOnlyList<string> KnownUnsupported,
    string Notes);

/// <summary>
/// Cheap, static description of an adapter. The adapter itself is only instantiated the first time a URL
/// matches <see cref="Matches"/> (lazy initialisation), so listing hundreds of descriptors costs nothing.
/// </summary>
public sealed record AdapterDescriptor(
    string Id,
    string DisplayName,
    Func<Uri, bool> Matches,
    Func<ResolverServices, IMediaSourceAdapter> Factory,
    AdapterCapabilities Capabilities);
