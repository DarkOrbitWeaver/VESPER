using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Protocols.Dash;

public sealed record DashRepresentationInfo
{
    public required string Id { get; init; }
    public required string AdaptationSetKey { get; init; }
    /// <summary>video, audio or text.</summary>
    public required string ContentType { get; init; }
    public string? MimeType { get; init; }
    public string? Codecs { get; init; }
    public int? Bandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public int? AudioSampleRate { get; init; }
    public int? Channels { get; init; }
    public string? Lang { get; init; }
    public bool IsAudioDescription { get; init; }

    /// <summary>Set when the representation is one plain file (BaseURL + optional SegmentBase): downloadable with ordinary HTTP.</summary>
    public Uri? ProgressiveUrl { get; init; }

    /// <summary>Set for template/list based representations. Never partially filled.</summary>
    public SegmentPlan? Segments { get; init; }
}

public sealed record DashManifest
{
    public bool IsDynamic { get; init; }
    public TimeSpan? Duration { get; init; }
    public int PeriodCount { get; init; }
    public bool IsProtected { get; init; }
    public IReadOnlyList<string> ProtectionSchemes { get; init; } = [];
    public IReadOnlyList<DashRepresentationInfo> Representations { get; init; } = [];
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
