using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;

namespace Vesper.MediaResolver.Formats;

public enum TransferRole { Muxed, Video, Audio, Subtitle }

/// <summary>
/// One transfer the VESPER engine must perform. For <see cref="StreamProtocol.Http"/> download <see cref="Url"/>.
/// For Hls/Dash the engine uses <see cref="Segments"/> when present (explicit, validated list) or <see cref="Url"/> as the media playlist.
/// <see cref="Context"/> must be applied to every request (credential scoping rules are on <see cref="MediaRequestContext"/>).
/// </summary>
public sealed record TransferStep(
    TransferRole Role,
    Uri Url,
    StreamProtocol Protocol,
    string? Container,
    MediaRequestContext Context,
    long? ExpectedSize,
    SegmentPlan? Segments,
    Uri? ManifestUrl,
    bool IsEncryptedAes128);

/// <summary>Complete, validated hand-off from the resolver to the transfer engine.</summary>
public sealed record DownloadPlan
{
    public required string Provider { get; init; }
    public string? MediaId { get; init; }
    public required string Title { get; init; }
    public required string SuggestedFileName { get; init; }
    public required IReadOnlyList<TransferStep> Steps { get; init; }
    public IReadOnlyList<TransferStep> Subtitles { get; init; } = [];
    public ProcessingRequirement Requirements { get; init; }
    public string? OutputContainer { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool HasAudio { get; init; }
    public bool IsFallback { get; init; }
    public string? FallbackReason { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when finishing the file needs a tool the engine may not have (mux or audio extraction).</summary>
    public bool RequiresExternalHelper =>
        (Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) != 0;
}

public sealed record PlanResult(DownloadPlan? Plan, MediaError? Error)
{
    public bool IsSuccess => Plan is not null;
}

public static class DownloadPlanBuilder
{
    private static readonly ResolverOptions DefaultOptions = new();

    public static PlanResult Build(MediaItem item, FormatSelection selection, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var steps = new List<TransferStep>();
        var warnings = new List<string>();

        if (selection.Video is null && selection.Audio is null)
            return new(null, MediaErrors.NoFormat("Selection contains no stream"));

        foreach (var rep in selection.Streams)
        {
            TransferRole role = rep == selection.Audio ? TransferRole.Audio
                : selection.Audio is not null ? TransferRole.Video
                : rep.Content == StreamContent.VideoAndAudio ? TransferRole.Muxed : TransferRole.Video;
            if (selection.Video is null) role = TransferRole.Audio;

            if (rep.ExpiresAt is { } exp && exp <= now)
                return new(null, MediaErrors.Expired($"Representation {rep.Id} expired at {exp:O}"));
            if (!UrlValidator.IsAllowed(rep.Url, DefaultOptions, out var why))
                return new(null, MediaErrors.Blocked($"Representation {rep.Id} URL rejected: {why}"));
            if (rep.Protocol == StreamProtocol.Dash && rep.Segments is null)
                return new(null, MediaErrors.UnsupportedManifest($"DASH representation {rep.Id} has no segment plan"));
            if (rep.Segments is { } plan && plan.Segments.Count == 0)
                return new(null, MediaErrors.UnsupportedManifest($"Representation {rep.Id} has an empty segment plan"));
            if (rep.ExpiresAt is { } soon && soon - now < TimeSpan.FromMinutes(2))
                warnings.Add($"Source link for {rep.Id} expires within 2 minutes; start the transfer immediately.");

            steps.Add(new TransferStep(role, rep.Url, rep.Protocol, rep.Container, rep.Context, rep.FileSize,
                rep.Segments, rep.ManifestUrl, (rep.Requirements & ProcessingRequirement.DecryptAes128) != 0));
        }

        var subs = new List<TransferStep>();
        foreach (var s in selection.Subtitles)
            if (UrlValidator.IsAllowed(s.Url, DefaultOptions, out _))
                subs.Add(new TransferStep(TransferRole.Subtitle, s.Url, StreamProtocol.Http, s.Format, selection.Video?.Context ?? MediaRequestContext.Empty, null, null, null, false));

        if (!selection.HasAudio) warnings.Add("The selected video has no audio stream.");
        if (selection.NeedsMuxing) warnings.Add("Video and audio are separate streams and require a compatible post-transfer merger.");

        string? container = selection.OutputContainer ?? steps[0].Container;
        DateTimeOffset? expiry = selection.Streams.Where(r => r.ExpiresAt is not null).Select(r => r.ExpiresAt!.Value).DefaultIfEmpty().Min();
        if (expiry == default(DateTimeOffset)) expiry = null;

        return new(new DownloadPlan
        {
            Provider = item.Provider,
            MediaId = item.MediaId,
            Title = item.Title,
            SuggestedFileName = FileNameSanitizer.BuildFileName(item.Title, item.MediaId, container),
            Steps = steps,
            Subtitles = subs,
            Requirements = selection.Requirements,
            OutputContainer = container,
            ExpiresAt = expiry,
            HasAudio = selection.HasAudio,
            IsFallback = selection.IsFallback,
            FallbackReason = selection.FallbackReason,
            Warnings = warnings
        }, null);
    }
}
