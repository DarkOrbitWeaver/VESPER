using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

/// <summary>
/// The outcome of applying preferences to an item. <see cref="Video"/> is a complete stream or a video-only stream;
/// <see cref="Audio"/> is a separate audio stream (or the only stream for audio-only selections).
/// </summary>
public sealed record FormatSelection
{
    public MediaRepresentation? Video { get; init; }
    public MediaRepresentation? Audio { get; init; }
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];

    public ProcessingRequirement Requirements { get; init; }
    public string? OutputContainer { get; init; }
    public long? EstimatedSize { get; init; }
    public bool EstimatedSizeIsEstimate { get; init; }

    public bool IsFallback { get; init; }
    public string? FallbackReason { get; init; }
    public string Summary { get; init; } = "";

    public bool HasVideo => Video is { HasVideo: true };
    /// <summary>False when the chosen video stream has no audio and no separate audio stream exists.</summary>
    public bool HasAudio => Audio is not null || Video is { HasAudio: true };
    public bool NeedsMuxing => (Requirements & ProcessingRequirement.MuxAudioVideo) != 0;

    public IEnumerable<MediaRepresentation> Streams
    {
        get
        {
            if (Video is not null) yield return Video;
            if (Audio is not null) yield return Audio;
        }
    }
}

public sealed record SelectionResult(FormatSelection? Selection, MediaError? Error)
{
    public bool IsSuccess => Selection is not null;
}
