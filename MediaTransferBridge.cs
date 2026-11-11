using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>Single-stream transfer handoff, including durable non-secret source provenance.</summary>
internal sealed record MediaDownloadChoice(
    string Url, string FileName, JobKind Kind, string? Referer, string? UserAgent,
    string? OriginalPageUrl = null, string? Provider = null, string? MediaId = null,
    string? Quality = null, string? ThumbnailUrl = null, DateTimeOffset? ExpiresAt = null,
    string? MediaPlanJson = null, string? FormatId = null);

/// <summary>
/// One source of truth for the desktop, native browser picker and developer transfer checks. A representation is only
/// offered when VESPER can execute every transfer and processing step it needs: separate video/audio streams are
/// offered only when the native remuxer can merge them without transcoding (see <see cref="MergeCapability"/>).
/// </summary>
internal static class MediaTransferBridge
{
    internal static string DisplayQuality(string quality) => quality
        .Replace("+ separate audio (merge required)", "+ audio", StringComparison.OrdinalIgnoreCase)
        .Replace(" (merged by VESPER)", "", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirect(MediaRepresentation r) => !r.IsLive
        && (r.Content is StreamContent.VideoAndAudio or StreamContent.AudioOnly)
        && (r.Protocol == StreamProtocol.Http || r.Protocol == StreamProtocol.Hls && r.Segments?.HasDiscontinuities != true
            || r.Protocol == StreamProtocol.Dash && r.Segments is { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
               && r.Container is "mp4" or "m4a")
        && !r.Context.HasCredentials
        && r.Context.Origin is null
        && r.Context.AcceptLanguage is null
        && (r.Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) == 0;

    internal static MediaItem PlayableOnly(MediaItem item)
    {
        var reps = item.Representations;
        return item with
        {
            Representations = reps.Where(r => IsDirect(r)
                || (r.Content == StreamContent.VideoOnly && reps.Any(a => MergeCapability.MergeContainer(r, a) is not null)))
                .ToArray()
        };
    }

    internal static (MediaDownloadChoice? Choice, string? Error) Prepare(MediaItem item, FormatPreferences prefs,
        MediaRepresentation? exact = null)
    {
        prefs = prefs with { NativeMergeOnly = true };
        FormatSelection? selection;
        if (exact is not null && exact.Content == StreamContent.VideoOnly)
        {
            // An exact video-only choice is paired with the preferred audio the native remuxer can merge with it.
            var pool = item with { Representations = item.Representations.Where(r => ReferenceEquals(r, exact) || r == exact || r.Content == StreamContent.AudioOnly).ToArray() };
            var paired = FormatSelector.Select(PlayableOnly(pool), prefs).Selection;
            selection = paired?.Video == exact && paired.Audio is not null ? paired : null;
            if (selection is null) return (null, "This video-only format has no audio stream VESPER can merge with it.");
        }
        else if (exact is not null)
        {
            selection = new FormatSelection
            {
                Video = exact.Content == StreamContent.AudioOnly ? null : exact,
                Audio = exact.Content == StreamContent.AudioOnly ? exact : null,
                Requirements = exact.Requirements,
                OutputContainer = exact.Container,
                EstimatedSize = exact.FileSize,
                Summary = exact.Content == StreamContent.AudioOnly ? "Audio only" : $"{(exact.Height is { } height ? height + "p" : "Source")} · {exact.Container ?? "stream"}"
            };
        }
        else selection = FormatSelector.Select(PlayableOnly(item), prefs).Selection;
        if (selection is null) return (null, "No compatible format satisfies this preference.");

        PlanResult planResult = DownloadPlanBuilder.Build(item, selection);
        if (planResult.Plan is not { } plan)
            return (null, planResult.Error?.UserMessage ?? "This format isn't ready to download.");

        if (selection.NeedsMuxing)
        {
            if (selection.Video is not { } video || selection.Audio is not { } audio
                || MergeCapability.MergeContainer(video, audio, selection.OutputContainer) is not { } container)
                return (null, "VESPER cannot merge this video/audio combination without transcoding.");
            return BuildMerge(item, selection, plan, [video, audio], container);
        }
        // Audio with provider byte identity goes through the native job: bounded range requests, container validation,
        // a clean non-fragmented M4A/WebM output and identity-checked continuation after a link refresh.
        if (selection.Video is null && selection.Audio is { ContentIdentity: not null } only
            && MergeCapability.AudioContainer(only) is { } audioContainer)
            return BuildMerge(item, selection, plan, [only], audioContainer);

        if (plan.RequiresExternalHelper || plan.Steps.Count != 1 ||
            (plan.Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) != 0)
            return (null, "This format requires audio/video processing that VESPER doesn't support yet.");
        TransferStep step = plan.Steps[0];
        if (step.Protocol is not (StreamProtocol.Http or StreamProtocol.Hls or StreamProtocol.Dash) || step.Context.HasCredentials
            || step.Context.Origin is not null || step.Context.AcceptLanguage is not null)
            return (null, "This source needs an unsupported transfer method or request context.");
        if (step.Protocol == StreamProtocol.Hls && step.Segments is not null &&
            !step.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return (null, "This HLS stream needs a segment download path that isn't supported.");
        string? dashPlan = null;
        if (step.Protocol == StreamProtocol.Dash)
        {
            if (step.Segments is not { IsFragmentedMp4: true, InitializationUrl: { } init, HasDiscontinuities: false } segments
                || step.Container is not ("mp4" or "m4a"))
                return (null, "Only clear fragmented MP4 DASH streams with initialization data are supported.");
            MediaRepresentation rep = selection.Streams.Single();
            dashPlan = new DashDownloadPlan
            {
                RepresentationId = rep.Id, Provider = item.Provider, MediaId = item.MediaId,
                Initialization = new DashFragment(init.AbsoluteUri, segments.InitializationRange?.Offset, segments.InitializationRange?.Length),
                Fragments = segments.Segments.Select(s => new DashFragment(s.Url.AbsoluteUri, s.Range?.Offset, s.Range?.Length)).ToArray(),
                RequireAudio = selection.HasAudio, RequireVideo = selection.Video is not null
            }.ToJson();
        }
        string name = step.Protocol == StreamProtocol.Hls
            ? Path.GetFileNameWithoutExtension(plan.SuggestedFileName) : plan.SuggestedFileName;
        return (new MediaDownloadChoice(step.Url.AbsoluteUri, name,
            step.Protocol == StreamProtocol.Hls ? JobKind.Hls : step.Protocol == StreamProtocol.Dash ? JobKind.Dash : JobKind.Http,
            step.Context.Referer, step.Context.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, selection.Summary,
            item.Thumbnail?.AbsoluteUri, plan.ExpiresAt, dashPlan, selection.Streams.Single().Id), null);
    }

    /// <summary>Builds the engine's merge plan. Every track shares one request context; no credentials are persisted.</summary>
    private static (MediaDownloadChoice? Choice, string? Error) BuildMerge(MediaItem item, FormatSelection selection,
        DownloadPlan plan, IReadOnlyList<MediaRepresentation> tracks, string container)
    {
        MediaRequestContext ctx = tracks[0].Context;
        if (tracks.Any(t => t.Context.HasCredentials || t.Context.Origin is not null || t.Context.AcceptLanguage is not null
            || t.Context.UserAgent != ctx.UserAgent || t.Context.Referer != ctx.Referer))
            return (null, "These streams need different request contexts, which a single merge job cannot carry.");
        if (tracks.Any(t => t.FileSize is not > 0 || t.Container is null))
            return (null, "A stream's exact size is unknown, so it cannot be merged safely.");
        var merge = new MediaMergePlan
        {
            Provider = item.Provider,
            MediaId = item.MediaId,
            OutputContainer = container,
            RequireAudio = tracks.Any(t => t.HasAudio),
            RequireVideo = tracks.Any(t => t.HasVideo),
            Tracks = tracks.Select(t => new MergeTrack(t.HasVideo ? "video" : "audio", t.Id, t.Url.AbsoluteUri, t.FileSize!.Value,
                t.Container!, t.HasVideo ? t.VideoCodec : t.AudioCodec, t.ContentIdentity,
                t.AudioLanguage is { Length: 3 } lang ? lang : null)).ToArray()
        };
        string name = Path.ChangeExtension(plan.SuggestedFileName, container);
        string summary = DisplayQuality(selection.Summary);
        return (new MediaDownloadChoice(tracks[0].Url.AbsoluteUri, name, JobKind.Merge, ctx.Referer, ctx.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, summary, item.Thumbnail?.AbsoluteUri, plan.ExpiresAt,
            merge.ToJson(), string.Join("+", tracks.Select(t => t.Id))), null);
    }
}
