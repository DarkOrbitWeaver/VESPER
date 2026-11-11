using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Protocols;

/// <summary>What an HLS or DASH manifest contributes to a MediaItem. Throwing (not returning) is used for DRM/live/unsupported.</summary>
public sealed record ManifestResult(
    IReadOnlyList<MediaRepresentation> Representations,
    IReadOnlyList<AudioTrack> AudioTracks,
    IReadOnlyList<SubtitleTrack> Subtitles,
    TimeSpan? Duration);
