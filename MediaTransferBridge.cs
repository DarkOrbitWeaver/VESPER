using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using System.Text.Json;

namespace VESPER;

internal enum MediaOutputPreference { Original, CompatibleVideo, M4a }

/// <summary>Single-stream transfer handoff, including durable non-secret source provenance.</summary>
internal sealed record MediaDownloadChoice(
    string Url, string FileName, JobKind Kind, string? Referer, string? UserAgent,
    string? OriginalPageUrl = null, string? Provider = null, string? MediaId = null,
    string? Quality = null, string? ThumbnailUrl = null, DateTimeOffset? ExpiresAt = null,
    string? MediaPlanJson = null, string? FormatId = null, MediaOutput Output = MediaOutput.Original,
    string? ConvertedVideoQuality = null, string? ConvertedAudioCodec = null,
    IReadOnlyList<SubtitleTrack>? SubtitleTracks = null)
{
    public string DisplayContainer => Output switch
    {
        MediaOutput.Mp4 => "MP4",
        MediaOutput.M4a => "M4A",
        _ => Kind == JobKind.Hls ? "HLS" : Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()
    };
    public string DisplayDetail => Output switch
    {
        MediaOutput.M4a => $"AAC · Converts from {ConvertedAudioCodec}",
        MediaOutput.Mp4 => $"{ConvertedVideoQuality}H.264 + AAC · Converts if supported",
        _ => MediaTransferBridge.DisplayQuality(Quality ?? "Available source")
    };
}

/// <summary>
/// One source of truth for the desktop, native browser picker and developer transfer checks. A representation is only
/// offered when VESPER can execute every transfer and processing step it needs: separate video/audio streams are
/// offered only when the native remuxer can merge them without transcoding (see <see cref="MergeCapability"/>).
/// </summary>
internal static class MediaTransferBridge
{
    private const int HighQualitySourceAacKbps = 192;

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
        MediaRepresentation? exact = null, MediaOutputPreference output = MediaOutputPreference.Original,
        double? clipStartSeconds = null, double? clipEndSeconds = null)
    {
        if ((clipStartSeconds is null) != (clipEndSeconds is null)
            || clipStartSeconds is double start && (!double.IsFinite(start) || start < 0)
            || clipEndSeconds is double end && (!double.IsFinite(end) || end <= (clipStartSeconds ?? 0) || end > 31_536_000))
            return (null, "Enter a valid start and end time.");
        if (exact is not null || output == MediaOutputPreference.Original)
            return PrepareSource(item, prefs, exact, out _, clipStartSeconds, clipEndSeconds);
        bool audioOnly = prefs.Mode == QualityMode.AudioOnly || item.Representations.All(r => !r.HasVideo);
        if (output == MediaOutputPreference.M4a && audioOnly)
            return PrepareM4a(item, prefs, clipStartSeconds, clipEndSeconds);
        if (audioOnly || output != MediaOutputPreference.CompatibleVideo)
            return PrepareSource(item, prefs, exact: null, out _, clipStartSeconds, clipEndSeconds);

        MediaItem native = item with
        {
            Representations = item.Representations.Where(r =>
                r.Protocol != StreamProtocol.Hls && r.Container is "mp4" or "m4a" && !r.AudioUnverified
                && (r.Content == StreamContent.AudioOnly ? r.AudioCodec == "aac"
                    : r.VideoCodec == "h264" && (!r.HasAudio || r.AudioCodec == "aac"))).ToArray()
        };
        FormatPreferences nativePrefs = prefs with { Container = "mp4", ContainerStrict = true };
        if (PrepareSource(native, nativePrefs, exact: null, out FormatSelection? nativeSelection,
                clipStartSeconds, clipEndSeconds).Choice is { } compatible
            && nativeSelection is { HasVideo: true })
            return (compatible, null);

        var original = PrepareSource(item, prefs, exact: null, out FormatSelection? source,
            clipStartSeconds, clipEndSeconds);
        if (original.Choice is { } choice && CanConvert(choice)
            && source is { HasAudio: true, Video: { VideoCodec: not null, Hdr: HdrKind.Sdr, Height: null or <= 2304, Width: null or <= 4096 } video }
            && (source.Audio ?? video).AudioCodec is not null)
        {
            string? videoQuality = video.Height is int height
                ? video.FrameRate is > 30.5 ? $"{height}p {Math.Round(video.FrameRate.Value)} fps · " : $"{height}p · "
                : null;
            return (choice with { Output = MediaOutput.Mp4, ConvertedVideoQuality = videoQuality }, null);
        }
        return original;
    }

    private static (MediaDownloadChoice? Choice, string? Error) PrepareM4a(MediaItem item, FormatPreferences prefs,
        double? clipStartSeconds, double? clipEndSeconds)
    {
        var original = PrepareSource(item, prefs, exact: null, out FormatSelection? source,
            clipStartSeconds, clipEndSeconds);
        if (original.Choice is not { } choice || source?.Audio is not { } audio) return original;

        if (IsNativeAac(audio))
            return (choice with { FileName = Path.ChangeExtension(choice.FileName, ".m4a") }, null);

        string? codec = audio.AudioCodec;
        bool canEncode = codec is not null && CanConvert(choice)
            && audio.AudioChannels is null or 1 or 2 && audio.AudioSampleRate is null or 44100 or 48000;

        // Keep high-bitrate AAC at its source quality, or use available native AAC if encoding is unavailable.
        MediaRepresentation[] nativeAac = item.Representations.Where(candidate => IsNativeAac(candidate)
            && (!canEncode || candidate.BitrateKbps >= HighQualitySourceAacKbps)
            && (candidate.AudioChannels ?? 2) == (audio.AudioChannels ?? 2)
            && candidate.IsDefaultAudio == audio.IsDefaultAudio && candidate.IsAudioDescription == audio.IsAudioDescription
            && candidate.AudioTrackId == audio.AudioTrackId
            && string.Equals(candidate.AudioLanguage, audio.AudioLanguage, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nativeAac.Length > 0 && PrepareSource(item with { Representations = nativeAac }, prefs,
                exact: null, out _, clipStartSeconds, clipEndSeconds).Choice is { } sourceAac)
            return (sourceAac with { FileName = Path.ChangeExtension(sourceAac.FileName, ".m4a") }, null);

        if (canEncode && codec is not null)
            return (choice with { Output = MediaOutput.M4a, ConvertedAudioCodec = codec.ToUpperInvariant() }, null);

        return original;
    }

    private static bool IsNativeAac(MediaRepresentation stream) =>
        stream is { Content: StreamContent.AudioOnly, AudioCodec: "aac", Container: "mp4" or "m4a", AudioUnverified: false }
        && stream.Protocol != StreamProtocol.Hls;

    private static bool CanConvert(MediaDownloadChoice choice) => OperatingSystem.IsWindows()
        && choice.Kind is JobKind.Http or JobKind.Merge
        && Path.GetExtension(choice.FileName).ToLowerInvariant() is ".webm" or ".mkv" or ".mp4" or ".m4a";

    private static (MediaDownloadChoice? Choice, string? Error) PrepareSource(MediaItem item, FormatPreferences prefs,
        MediaRepresentation? exact, out FormatSelection? selection, double? clipStartSeconds = null, double? clipEndSeconds = null)
    {
        prefs = prefs with { NativeMergeOnly = true };
        if (exact is not null && exact.Content == StreamContent.VideoOnly)
        {
            // An exact video-only choice is paired with the preferred audio the native remuxer can merge with it.
            var pool = item with { Representations = item.Representations.Where(r => ReferenceEquals(r, exact) || r == exact || r.Content == StreamContent.AudioOnly).ToArray() };
            var paired = FormatSelector.Select(PlayableOnly(pool), prefs).Selection;
            selection = paired?.Video == exact && paired.Audio is not null ? paired : null;
            if (selection is null) return (null, prefs.AudioTrackId is null
                ? "This video-only format has no audio stream VESPER can merge with it."
                : "The selected audio track can't be merged with this video format.");
        }
        else if (exact is not null)
        {
            if (!string.IsNullOrWhiteSpace(prefs.AudioTrackId) && exact.AudioTrackId != prefs.AudioTrackId)
            {
                selection = null;
                return (null, "The selected audio track isn't available in this exact format.");
            }
            FormatSelection? subtitleSelection = FormatSelector.Select(item with { Representations = [exact] },
                new FormatPreferences
                {
                    SubtitleLanguages = prefs.SubtitleLanguages,
                    IncludeAutoSubtitles = prefs.IncludeAutoSubtitles
                }).Selection;
            selection = new FormatSelection
            {
                Video = exact.Content == StreamContent.AudioOnly ? null : exact,
                Audio = exact.Content == StreamContent.AudioOnly ? exact : null,
                Requirements = exact.Requirements,
                OutputContainer = exact.Container,
                EstimatedSize = exact.FileSize,
                Summary = exact.Content == StreamContent.AudioOnly
                    ? $"audio {exact.AudioCodec ?? exact.Container ?? ""} {(exact.BitrateKbps is { } k ? k + " kbps" : "")}".Trim()
                    : $"{(exact.Height is { } height ? height + "p" : "Source")} · {exact.Container ?? "stream"}",
                Subtitles = subtitleSelection?.Subtitles ?? []
            };
        }
        else selection = FormatSelector.Select(PlayableOnly(item), prefs).Selection;
        if (selection is null) return (null, prefs.AudioTrackId is null
            ? "No compatible format satisfies this preference."
            : "No supported format can use the selected audio track.");

        PlanResult planResult = DownloadPlanBuilder.Build(item, selection);
        if (planResult.Plan is not { } plan)
            return (null, planResult.Error?.UserMessage ?? "This format isn't ready to download.");
        IReadOnlyList<SubtitleTrack> subtitles = ValidatedSubtitles(selection, plan);
        if (subtitles.Count != selection.Subtitles.Count)
            return (null, "A selected subtitle link failed source validation.");
        if (subtitles.Any(subtitle => !IsSupportedSubtitle(subtitle)))
            return (null, "This subtitle uses a transport or format that VESPER can't save as a sidecar yet.");

        if (selection.NeedsMuxing)
        {
            if (selection.Video is not { } video || selection.Audio is not { } audio
                || MergeCapability.MergeContainer(video, audio, selection.OutputContainer) is not { } container)
                return (null, "VESPER cannot merge this video/audio combination without transcoding.");
            return BuildMerge(item, selection, plan, [video, audio], container, clipStartSeconds, clipEndSeconds);
        }
        // Audio with provider byte identity goes through the native job: bounded range requests, container validation,
        // a clean non-fragmented M4A/WebM output and identity-checked continuation after a link refresh.
        if (selection.Video is null && selection.Audio is { ContentIdentity: not null } only
            && MergeCapability.AudioContainer(only) is { } audioContainer)
            return BuildMerge(item, selection, plan, [only], audioContainer, clipStartSeconds, clipEndSeconds);

        if (clipStartSeconds is not null)
            return (null, "Time ranges are supported for clear segmented HLS or DASH audio/video renditions.");

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
                Fragments = segments.Segments.Select(s => new DashFragment(s.Url.AbsoluteUri, s.Range?.Offset,
                    s.Range?.Length, s.DurationSeconds, s.StartSeconds)).ToArray(),
                ClipRange = clipStartSeconds is double start ? new MediaTimeRange(start, clipEndSeconds!.Value) : null,
                RequireAudio = selection.HasAudio, RequireVideo = selection.Video is not null
            }.ToJson();
        }
        string name = step.Protocol == StreamProtocol.Hls
            ? Path.GetFileNameWithoutExtension(plan.SuggestedFileName) : plan.SuggestedFileName;
        string? mediaPlan = dashPlan;
        if (step.Protocol == StreamProtocol.Hls && clipStartSeconds is double clipStart)
            mediaPlan = JsonSerializer.Serialize(new MediaTimeRange(clipStart, clipEndSeconds!.Value));
        return (new MediaDownloadChoice(step.Url.AbsoluteUri, name,
            step.Protocol == StreamProtocol.Hls ? JobKind.Hls : step.Protocol == StreamProtocol.Dash ? JobKind.Dash : JobKind.Http,
            step.Context.Referer, step.Context.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, selection.Summary,
            item.Thumbnail?.AbsoluteUri, plan.ExpiresAt, mediaPlan, selection.Streams.Single().Id,
            SubtitleTracks: subtitles), null);
    }

    /// <summary>Builds the engine's merge plan. Every track shares one request context; no credentials are persisted.</summary>
    private static (MediaDownloadChoice? Choice, string? Error) BuildMerge(MediaItem item, FormatSelection selection,
        DownloadPlan plan, IReadOnlyList<MediaRepresentation> tracks, string container,
        double? clipStartSeconds, double? clipEndSeconds)
    {
        IReadOnlyList<SubtitleTrack> subtitles = ValidatedSubtitles(selection, plan);
        if (subtitles.Count != selection.Subtitles.Count)
            return (null, "A selected subtitle link failed source validation.");
        if (subtitles.Any(subtitle => !IsSupportedSubtitle(subtitle)))
            return (null, "This subtitle uses a transport or format that VESPER can't save as a sidecar yet.");
        if (clipStartSeconds is not null && (tracks.Count != 2
            || tracks.Any(track => track.Protocol is not (StreamProtocol.Hls or StreamProtocol.Dash))))
            return (null, "Time ranges are supported for clear segmented HLS or DASH audio/video renditions.");
        MediaRequestContext ctx = tracks[0].Context;
        if (tracks.Any(t => t.Context.HasCredentials || t.Context.Origin is not null || t.Context.AcceptLanguage is not null
            || t.Context.UserAgent != ctx.UserAgent || t.Context.Referer != ctx.Referer))
            return (null, "These streams need different request contexts, which a single merge job cannot carry.");
        if (tracks.Any(t => t.Container is null || t.Protocol == StreamProtocol.Http && t.FileSize is not > 0
            || t.Protocol == StreamProtocol.Hls && t.Container is not ("mp4" or "m4a" or "ts")
            || t.Protocol == StreamProtocol.Dash && t.Segments is not { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
            || t.Protocol is not (StreamProtocol.Http or StreamProtocol.Hls or StreamProtocol.Dash)))
            return (null, "A selected stream has no complete native download plan.");

        var mergeTracks = new List<MergeTrack>(tracks.Count);
        foreach (MediaRepresentation track in tracks)
        {
            string role = track.HasVideo ? "video" : "audio";
            MergeMediaSource? source = null;
            if (track.Protocol == StreamProtocol.Hls)
                source = new MergeMediaSource("hls", PlaylistUrl: track.Url.AbsoluteUri);
            else if (track.Protocol == StreamProtocol.Dash && track.Segments is { } segments)
            {
                var dash = new DashDownloadPlan
                {
                    RepresentationId = track.Id,
                    Provider = item.Provider,
                    MediaId = item.MediaId,
                    Initialization = new DashFragment(segments.InitializationUrl!.AbsoluteUri,
                        segments.InitializationRange?.Offset, segments.InitializationRange?.Length),
                    Fragments = segments.Segments.Select(segment => new DashFragment(segment.Url.AbsoluteUri,
                        segment.Range?.Offset, segment.Range?.Length, segment.DurationSeconds, segment.StartSeconds)).ToArray(),
                    RequireAudio = role == "audio",
                    RequireVideo = role == "video"
                };
                source = new MergeMediaSource("dash", Dash: dash);
            }
            mergeTracks.Add(new MergeTrack(role, track.Id, track.Url.AbsoluteUri, source is null ? track.FileSize!.Value : 0,
                track.Container!, track.HasVideo ? track.VideoCodec : track.AudioCodec, track.ContentIdentity,
                track.AudioLanguage is { Length: 3 } lang ? lang : null, source));
        }
        var merge = new MediaMergePlan
        {
            Version = mergeTracks.Any(track => track.Source is not null) ? 2 : 1,
            Provider = item.Provider,
            MediaId = item.MediaId,
            OutputContainer = container,
            RequireAudio = tracks.Any(t => t.HasAudio),
            RequireVideo = tracks.Any(t => t.HasVideo),
            Metadata = MergeMetadata(item, includeChapters: clipStartSeconds is null),
            ClipRange = clipStartSeconds is double start ? new MediaTimeRange(start, clipEndSeconds!.Value) : null,
            Tracks = mergeTracks
        };
        string name = Path.ChangeExtension(plan.SuggestedFileName, container);
        string summary = DisplayQuality(selection.Summary);
        return (new MediaDownloadChoice(tracks[0].Url.AbsoluteUri, name, JobKind.Merge, ctx.Referer, ctx.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, summary, item.Thumbnail?.AbsoluteUri, plan.ExpiresAt,
            merge.ToJson(), string.Join("+", tracks.Select(t => t.Id)), SubtitleTracks: subtitles), null);
    }

    private static MediaMergeMetadata? MergeMetadata(MediaItem item, bool includeChapters)
    {
        var metadata = new MediaMergeMetadata
        {
            Title = LimitMetadata(item.Title, 512),
            Uploader = LimitMetadata(item.Uploader, 256),
            Description = LimitMetadata(item.Description, 8192),
            Duration = item.Duration is { } duration && duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(365)
                ? duration : null
        };
        if (includeChapters && item.Chapters.Count is > 0 and <= 200)
        {
            var chapters = item.Chapters.Select(chapter =>
                new MediaMergeChapter(LimitMetadata(chapter.Title, 256) ?? "", chapter.Start, chapter.End)).ToArray();
            if (ValidChapters(chapters, metadata.Duration)) metadata = metadata with { Chapters = chapters };
        }
        return metadata.Title is null && metadata.Uploader is null && metadata.Description is null && metadata.Chapters.Count == 0
            ? null : metadata;
    }

    private static bool ValidChapters(IReadOnlyList<MediaMergeChapter> chapters, TimeSpan? duration)
    {
        TimeSpan previousStart = TimeSpan.MinValue;
        TimeSpan? previousEnd = null;
        foreach (MediaMergeChapter chapter in chapters)
        {
            if (chapter.Title.Length == 0 || chapter.Start < TimeSpan.Zero || chapter.Start <= previousStart
                || chapter.Start > TimeSpan.FromDays(365)
                || duration is { } mediaDuration && chapter.Start >= mediaDuration
                || chapter.End is { } end && (end <= chapter.Start || end > TimeSpan.FromDays(365))
                || duration is { } endDuration && chapter.End is { } chapterEnd && chapterEnd > endDuration
                || previousEnd is { } priorEnd && chapter.Start < priorEnd) return false;
            previousStart = chapter.Start;
            previousEnd = chapter.End;
        }
        return true;
    }

    private static string? LimitMetadata(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim().Replace("\0", "", StringComparison.Ordinal);
        trimmed = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(trimmed));
        if (trimmed.Length <= maxLength) return trimmed;
        string limited = trimmed[..maxLength];
        if (char.IsHighSurrogate(limited[^1])) limited = limited[..^1];
        return limited;
    }

    private static IReadOnlyList<SubtitleTrack> ValidatedSubtitles(FormatSelection selection, DownloadPlan plan) =>
        selection.Subtitles.Where(subtitle => plan.Subtitles.Any(step => step.Url == subtitle.Url
            && step.Container == subtitle.Format)).ToArray();

    internal static bool IsDirectSubtitle(SubtitleTrack track) =>
        track.Format.ToLowerInvariant() is "vtt" or "srt" or "ttml";

    private static bool IsSupportedSubtitle(SubtitleTrack track) => IsDirectSubtitle(track)
        || string.Equals(track.Format, "hls-webvtt", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<MediaDownloadChoice> ExpandSubtitleChoices(IEnumerable<MediaDownloadChoice> choices)
    {
        var expanded = new List<MediaDownloadChoice>();
        foreach (MediaDownloadChoice choice in choices)
        {
            expanded.Add(choice with { SubtitleTracks = null });
            foreach (SubtitleTrack subtitle in choice.SubtitleTracks ?? [])
            {
                string language = Vesper.MediaResolver.Utilities.TextUtil.NormalizeLang(subtitle.Language);
                bool segmented = string.Equals(subtitle.Format, "hls-webvtt", StringComparison.OrdinalIgnoreCase);
                if (!IsDirectSubtitle(subtitle) && !segmented) continue;
                string format = segmented ? "vtt" : subtitle.Format.ToLowerInvariant();
                string fileName = FileNames.Sanitize($"{Path.GetFileNameWithoutExtension(choice.FileName)}.{language}.{format}");
                expanded.Add(choice with
                {
                    Url = subtitle.Url.AbsoluteUri,
                    FileName = fileName,
                    Kind = segmented ? JobKind.Hls : JobKind.Http,
                    Provider = null,
                    MediaId = null,
                    Quality = $"Subtitle · {language}",
                    ThumbnailUrl = null,
                    ExpiresAt = null,
                    MediaPlanJson = segmented ? JsonSerializer.Serialize(new { Version = 1, Type = "hls-webvtt" }) : null,
                    FormatId = segmented ? $"subtitle:{language}" : null,
                    Output = MediaOutput.Original,
                    ConvertedVideoQuality = null,
                    ConvertedAudioCodec = null,
                    SubtitleTracks = null
                });
            }
        }
        return expanded;
    }

    internal static bool SupportsSegmentClip(MediaDownloadChoice choice)
    {
        if (choice.Kind == JobKind.Hls) return true;
        if (choice.MediaPlanJson is null) return false;
        try
        {
            if (choice.Kind == JobKind.Dash)
            {
                DashDownloadPlan? dash = JsonSerializer.Deserialize<DashDownloadPlan>(choice.MediaPlanJson);
                return dash is { Fragments.Count: > 0 }
                    && dash.Fragments.All(fragment => fragment.DurationSeconds is > 0 and <= 31_536_000);
            }
            if (choice.Kind != JobKind.Merge) return false;
            MediaMergePlan? plan = JsonSerializer.Deserialize<MediaMergePlan>(choice.MediaPlanJson);
            return plan is { Version: 2, Tracks.Count: 2 }
                && plan.Tracks.All(track => track.Source?.Protocol is "hls" or "dash");
        }
        catch (JsonException) { return false; }
    }
}
