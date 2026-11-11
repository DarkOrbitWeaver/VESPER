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

/// <summary>One source of truth for the desktop, native browser picker and developer transfer checks.</summary>
internal static class MediaTransferBridge
{
    internal static MediaItem PlayableOnly(MediaItem item) => item with
    {
        Representations = item.Representations.Where(r => !r.IsLive
            && (r.Content is StreamContent.VideoAndAudio or StreamContent.AudioOnly)
            && (r.Protocol == StreamProtocol.Http || r.Protocol == StreamProtocol.Hls && r.Segments?.HasDiscontinuities != true
                || r.Protocol == StreamProtocol.Dash && r.Segments is { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
                   && r.Container is "mp4" or "m4a")
            && !r.Context.HasCredentials
            && r.Context.Origin is null
            && r.Context.AcceptLanguage is null
            && (r.Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) == 0)
            .ToArray()
    };

    internal static (MediaDownloadChoice? Choice, string? Error) Prepare(MediaItem item, FormatPreferences prefs,
        MediaRepresentation? exact = null)
    {
        var selection = exact is not null
            ? new FormatSelection
            {
                Video = exact.Content == StreamContent.AudioOnly ? null : exact,
                Audio = exact.Content == StreamContent.AudioOnly ? exact : null,
                Requirements = exact.Requirements,
                OutputContainer = exact.Container,
                EstimatedSize = exact.FileSize,
                Summary = exact.Content == StreamContent.AudioOnly ? "Audio only" : $"{(exact.Height is { } height ? height + "p" : "Source")} · {exact.Container ?? "stream"}"
            }
            : FormatSelector.Select(PlayableOnly(item), prefs).Selection;
        if (selection is null) return (null, "No compatible format satisfies this preference.");
        PlanResult planResult = DownloadPlanBuilder.Build(item, selection);
        if (planResult.Plan is not { } plan)
            return (null, planResult.Error?.UserMessage ?? "This format isn't ready to download.");
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

}
