using Vesper.MediaResolver.Security;
using System.Text.Json.Serialization;

namespace Vesper.MediaResolver.Models;

public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length - 1;
    public override string ToString() => $"{Offset}-{End}";
}

public sealed record MediaSegment(Uri Url, double DurationSeconds, ByteRange? Range = null,
    bool DiscontinuityBefore = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StartSeconds = null);

/// <summary>Explicit, fully resolved segment list that the transfer engine can fetch in order.</summary>
public sealed record SegmentPlan
{
    public Uri? InitializationUrl { get; init; }
    public ByteRange? InitializationRange { get; init; }
    public IReadOnlyList<MediaSegment> Segments { get; init; } = [];
    public double TotalDurationSeconds { get; init; }
    public bool IsFragmentedMp4 { get; init; }
    public bool HasDiscontinuities { get; init; }
}

/// <summary>
/// One downloadable stream. Everything here is either read from the source or derived deterministically;
/// unknown values stay null rather than being guessed.
/// </summary>
public sealed record MediaRepresentation
{
    public required string Id { get; init; }
    public required Uri Url { get; init; }
    public StreamProtocol Protocol { get; init; } = StreamProtocol.Http;
    public StreamContent Content { get; init; } = StreamContent.VideoAndAudio;

    /// <summary>File extension without dot (mp4, webm, m4a, mp3, ts ...).</summary>
    public string? Container { get; init; }

    /// <summary>Normalised codec family (h264, h265, vp9, av1, aac, opus ...), see <c>CodecInfo</c>.</summary>
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? RawCodecs { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public HdrKind Hdr { get; init; } = HdrKind.Sdr;

    /// <summary>Total stream bitrate in kilobits/s when declared.</summary>
    public int? BitrateKbps { get; init; }
    public int? AudioChannels { get; init; }
    public int? AudioSampleRate { get; init; }
    public string? AudioLanguage { get; init; }
    public string? AudioTrackId { get; init; }
    public bool IsDefaultAudio { get; init; }
    public bool IsAudioDescription { get; init; }
    public bool IsDrc { get; init; }

    /// <summary>For HLS video-only variants: the AUDIO group whose renditions pair with this variant.</summary>
    public string? AudioGroupId { get; init; }

    /// <summary>True when the audio presence could not be verified (e.g. direct file with no codec metadata).</summary>
    public bool AudioUnverified { get; init; }
    // A bounded MP4 probe could not establish the complete top-level layout.
    internal bool Mp4LayoutUnverified { get; init; }
    // The track table belongs to a standalone file with no audio, rather than to an adaptive video track.
    public bool StandaloneVideo { get; init; }
    public bool MutedLoop { get; init; }

    /// <summary>Distinct delivery endpoint to retain as an alternative even at identical quality.</summary>
    public string? DeliveryId { get; init; }
    public string? Label { get; init; }
    public long? FileSize { get; init; }
    public bool FileSizeIsEstimate { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Provider evidence identifying the exact encoded bytes (YouTube: video id, itag, last-modified, content length).
    /// Null when the source offers no such evidence; then a refreshed URL can never continue saved bytes.
    /// </summary>
    public string? ContentIdentity { get; init; }

    // A strong validator applies only to the exact final HTTP URL, including its query.
    // These fields never assert equivalence of two distinct delivery endpoints.
    public string? ResourceETag { get; init; }
    public string? ResourceUrlSha256 { get; init; }
    // An observed partial response was verified as a complete file. Its delivery must retain the validator.
    public bool BrowserRangeVerified { get; init; }
    public MediaRequestContext Context { get; init; } = MediaRequestContext.Empty;
    public Uri? ManifestUrl { get; init; }
    public SegmentPlan? Segments { get; init; }
    // Bounded evidence from a parsed playlist, not a download plan or a byte-identity assertion.
    [JsonIgnore] public IReadOnlyList<Uri> PlaybackEvidenceUrls { get; init; } = [];
    public ProcessingRequirement Requirements { get; init; } = ProcessingRequirement.None;
    public bool IsLive { get; init; }

    public bool HasVideo => Content != StreamContent.AudioOnly;
    public bool HasAudio => Content != StreamContent.VideoOnly;

    public override string ToString() =>
        $"{Id} [{Content}] {(Height is null ? "?" : Height + "p")} {Container ?? "?"} {Protocol} {UrlRedactor.Redact(Url)}";
}
