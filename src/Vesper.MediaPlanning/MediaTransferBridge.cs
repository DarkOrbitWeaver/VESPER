using StreamContent = Vesper.MediaResolver.Models.StreamContent;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Utilities;
using System.Text.Json;

namespace VESPER;

internal enum MediaOutputPreference { Original, CompatibleVideo, M4a, Opus, Wav, Mp3 }

/// <summary>Single-stream transfer handoff, including durable non-secret source provenance.</summary>
internal sealed record MediaDownloadChoice(
    string Url, string FileName, JobKind Kind, string? Referer, string? UserAgent,
    string? OriginalPageUrl = null, string? Provider = null, string? MediaId = null,
    string? Quality = null, string? ThumbnailUrl = null, DateTimeOffset? ExpiresAt = null,
    string? MediaPlanJson = null, string? FormatId = null, MediaOutput Output = MediaOutput.Original,
    string? ConvertedVideoQuality = null, string? ConvertedAudioCodec = null,
    IReadOnlyList<SubtitleTrack>? SubtitleTracks = null, string? MediaMetadataJson = null,
    int EmbeddedSubtitleCount = 0, string? SubtitleNotice = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyDictionary<string, string>? Secrets = null,
    [property: System.Text.Json.Serialization.JsonIgnore] bool CanUseFullFileClip = false)
{
    public string DisplayContainer => Output switch
    {
        MediaOutput.Mp4 => "MP4",
        MediaOutput.M4a => "M4A",
        MediaOutput.Opus => "Opus",
        MediaOutput.Wav => "WAV",
        MediaOutput.Mp3 => "MP3",
        _ => Kind == JobKind.Hls ? "HLS" : Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()
    };
    public string DisplayDetail => Output switch
    {
        MediaOutput.Opus => "Opus · copied without re-encoding",
        MediaOutput.Wav => "WAV · decoded PCM (does not improve source quality)",
        MediaOutput.Mp3 => $"MP3 · re-encoded from {ConvertedAudioCodec} (can't exceed the source quality)",
        MediaOutput.M4a => $"M4A · re-encoded from {ConvertedAudioCodec} (can't exceed the source quality)",
        MediaOutput.Mp4 => $"MP4 · {ConvertedVideoQuality}H.264 + AAC",
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
    private const int MaxEmbeddedSubtitleTracks = 25;
    private const string LocalMp4ClipSourceError = "This source doesn't provide enough information for VESPER to trim the MP4.";
    private const string LocalMp4ClipSubtitleError = "Trim can't include embedded subtitles. Turn subtitle embedding off or choose a segmented format.";
    private static readonly ResolverOptions SubtitleUrlOptions = new();

    internal static string DisplayQuality(string quality) => quality
        .Replace("+ separate audio (merge required)", "+ audio", StringComparison.OrdinalIgnoreCase)
        .Replace(" (merged by VESPER)", "", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirect(MediaRepresentation r) => !r.IsLive
        && (r.Content is StreamContent.VideoAndAudio or StreamContent.AudioOnly
            || r.StandaloneVideo && r.Protocol == StreamProtocol.Http)
        && (r.Protocol == StreamProtocol.Http || r.Protocol == StreamProtocol.Hls && r.Segments?.HasDiscontinuities != true
            || r.Protocol == StreamProtocol.Dash && r.Segments is { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
               && r.Container is "mp4" or "m4a")
        && CanCarryContext(r.Context)
        && (r.Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) == 0;

    private static bool CanCarryContext(MediaRequestContext context) =>
        context.SensitiveHeaders.All(h => h.Key == "Authorization")
        && context.Origin is null && context.AcceptLanguage is null;

    // Only this destination's allowed credentials enter the engine. Its redirect walker narrows
    // them further to the initial origin; secrets never enter a plan, database or recovery manifest.
    private static IReadOnlyDictionary<string, string>? TransferSecrets(MediaRequestContext context, Uri target)
    {
        if (!context.MayForwardCredentialsTo(target)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.CookieHeader is { } cookie) headers["Cookie"] = cookie;
        foreach (var header in context.SensitiveHeaders) headers[header.Key] = header.Value;
        return headers.Count == 0 ? null : headers;
    }

    private static bool SameSecretHeaders(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        if ((first?.Count ?? 0) != (second?.Count ?? 0)) return false;
        if (first is null) return true;
        return first.All(pair => second!.TryGetValue(pair.Key, out string? value)
            && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

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
        if (exact is null && output is MediaOutputPreference.Opus or MediaOutputPreference.Wav)
            return PrepareAudioOutput(item, prefs, output, clipStartSeconds, clipEndSeconds);
        if (exact is not null || output == MediaOutputPreference.Original)
            return PrepareSource(item, prefs, exact, out _, clipStartSeconds, clipEndSeconds);
        bool audioOnly = prefs.Mode == QualityMode.AudioOnly || item.Representations.All(r => !r.HasVideo);
        if (output == MediaOutputPreference.Mp3 && audioOnly)
            return PrepareMp3(item, prefs, clipStartSeconds, clipEndSeconds);
        if (output == MediaOutputPreference.M4a && audioOnly)
            return PrepareM4a(item, prefs, clipStartSeconds, clipEndSeconds);
        if (audioOnly || output != MediaOutputPreference.CompatibleVideo)
            return PrepareSource(item, prefs, exact: null, out _, clipStartSeconds, clipEndSeconds);

        MediaItem native = item with
        {
            Representations = item.Representations.Where(r =>
                (r.Protocol != StreamProtocol.Hls || r.Segments?.IsFragmentedMp4 == true)
                && r.Container is "mp4" or "m4a" && !r.AudioUnverified
                && (r.Content == StreamContent.AudioOnly ? r.AudioCodec == "aac"
                    : r.VideoCodec == "h264" && (!r.HasAudio || r.AudioCodec == "aac"))).ToArray()
        };
        FormatPreferences nativePrefs = prefs with { Container = "mp4", ContainerStrict = true };
        var nativeResult = PrepareSource(native, nativePrefs, exact: null, out FormatSelection? nativeSelection,
            clipStartSeconds, clipEndSeconds);
        if (nativeResult.Choice is { } compatible && nativeSelection is { HasVideo: true })
            return (compatible, null);
        if (clipStartSeconds is not null && nativeSelection is { HasVideo: true })
            return (null, nativeResult.Error ?? "The selected MP4-compatible format can't be trimmed.");

        var original = PrepareSource(item, prefs, exact: null, out FormatSelection? source,
            clipStartSeconds, clipEndSeconds);
        if (original.Choice is { } choice && CanConvert(choice)
            && choice.MediaPlanJson != DirectMediaPlan.AudioIfPresent
            && !prefs.EmbedSubtitles
            && source is { HasAudio: true, Video: { VideoCodec: not null, Hdr: HdrKind.Sdr, Height: null or <= 2304, Width: null or <= 4096 } video }
            && (source.Audio ?? video).AudioCodec is not null
            && CodecCapabilities.Current.CanDecode(video.VideoCodec) && CodecCapabilities.Current.CanDecode((source.Audio ?? video).AudioCodec))
        {
            string? videoQuality = video.Height is int height
                ? video.FrameRate is > 30.5 ? $"{height}p {Math.Round(video.FrameRate.Value)} fps · " : $"{height}p · "
                : null;
            return (choice with { Output = MediaOutput.Mp4, ConvertedVideoQuality = videoQuality,
                CanUseFullFileClip = false }, null);
        }
        return original;
    }

    private static (MediaDownloadChoice? Choice, string? Error) PrepareAudioOutput(MediaItem item, FormatPreferences prefs,
        MediaOutputPreference output, double? start, double? end)
    {
        bool opus = output == MediaOutputPreference.Opus;
        var pool = item with { Representations = item.Representations.Where(r => r.Content == StreamContent.AudioOnly
            && (r.Container is ("mp4" or "m4a" or "webm" or "mkv")
                || !opus && r.Protocol == StreamProtocol.Http && r.Container is ("mp3" or "ogg" or "oga" or "opus")
                    && r.AudioCodec is ("mp3" or "vorbis" or "opus"))
            && (opus ? r.AudioCodec == "opus" : OperatingSystem.IsWindows() && CodecCapabilities.Current.CanDecode(r.AudioCodec))).ToArray() };
        var planned = PrepareSource(pool, prefs with { Mode = QualityMode.AudioOnly }, null, out _);
        if (planned.Choice is not { } choice)
            return (null, opus ? "This source has no supported native Opus track to copy." : "WAV requires a source audio track and a Windows decoder for its codec.");
        if (choice.Kind is not (JobKind.Http or JobKind.Merge))
            return (null, "This output requires a complete supported audio file.");
        if (opus && start is not null) return (null, "Opus clipping is not available; choose WAV for an exact audio clip.");
        string? plan = choice.MediaPlanJson;
        if (start is double first)
        {
            var range = new MediaTimeRange(first, end!.Value);
            if (choice.Kind == JobKind.Merge)
            {
                var merge = JsonSerializer.Deserialize<MediaMergePlan>(plan ?? "null");
                if (merge is null) return (null, "The audio source plan is invalid.");
                plan = (merge with { OutputRange = range }).ToJson();
            }
            else plan = JsonSerializer.Serialize(range);
        }
        return (choice with { Output = opus ? MediaOutput.Opus : MediaOutput.Wav, MediaPlanJson = plan,
            MediaMetadataJson = choice.Kind == JobKind.Http ? SerializeAudioMetadata(item) : null }, null);
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
        // Only offer a conversion this PC can actually perform (VES-033); otherwise fall back to a native copy.
        bool canEncode = codec is not null && CanConvert(choice) && CodecCapabilities.Current.CanDecode(codec)
            && audio.AudioChannels is null or 1 or 2 && audio.AudioSampleRate is null or 44100 or 48000;

        // A native AAC stream is copied, never re-encoded: no generation loss, no converter, no wait.
        // Re-encoding Opus to AAC cannot add quality, so it only happens when there is no native AAC at all.
        MediaRepresentation[] nativeAac = item.Representations.Where(candidate => IsNativeAac(candidate)
            && (candidate.AudioChannels ?? 2) == (audio.AudioChannels ?? 2)
            && candidate.IsDefaultAudio == audio.IsDefaultAudio && candidate.IsAudioDescription == audio.IsAudioDescription
            && candidate.AudioTrackId == audio.AudioTrackId
            && string.Equals(candidate.AudioLanguage, audio.AudioLanguage, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nativeAac.Length > 0 && PrepareSource(item with { Representations = nativeAac }, prefs,
                exact: null, out _, clipStartSeconds, clipEndSeconds).Choice is { } sourceAac)
            return (sourceAac with { FileName = Path.ChangeExtension(sourceAac.FileName, ".m4a") }, null);

        if (canEncode && codec is not null)
        {
            // A merge job keeps its tags in the plan. A direct file needs them here, or the converted M4A has none.
            string? tags = choice.MediaMetadataJson
                ?? (choice.Kind == JobKind.Http && choice.Provider is not null ? SerializeAudioMetadata(item) : null);
            return (choice with { Output = MediaOutput.M4a, ConvertedAudioCodec = codec.ToUpperInvariant(), MediaMetadataJson = tags }, null);
        }

        return original;
    }

    private static (MediaDownloadChoice? Choice, string? Error) PrepareMp3(MediaItem item, FormatPreferences prefs,
        double? clipStartSeconds, double? clipEndSeconds)
    {
        if (clipStartSeconds is not null)
            return (null, "MP3 clipping isn't available; choose WAV for an exact audio clip.");

        var original = PrepareSource(item, prefs with { Mode = QualityMode.AudioOnly }, exact: null,
            out FormatSelection? source, clipStartSeconds, clipEndSeconds);
        if (original.Choice is not { } choice || source?.Audio is not { } audio)
            return original;

        if (audio is { Container: "mp3", AudioCodec: "mp3" })
            return (choice with { FileName = Path.ChangeExtension(choice.FileName, ".mp3") }, null);

        CodecCapabilities capabilities = CodecCapabilities.Current;
        if (!OperatingSystem.IsWindows() || !capabilities.Known || !capabilities.Mp3Encoder)
            return (null, "MP3 conversion isn't available because this PC has no confirmed native MP3 encoder.");

        // Prefer a source the native encoder can actually consume. Picking the
        // highest-ranked audio first can hide an available AAC alternative.
        var convertible = item with
        {
            Representations = item.Representations.Where(r => r.Content == StreamContent.AudioOnly
                && r.Container is "mp4" or "m4a" or "webm" or "mkv"
                && r.AudioCodec is not null && capabilities.CanDecode(r.AudioCodec)
                && r.AudioChannels is null or 1 or 2
                && r.AudioSampleRate is null or 32000 or 44100 or 48000).ToArray()
        };
        var supported = PrepareSource(convertible, prefs with { Mode = QualityMode.AudioOnly }, null,
            out FormatSelection? supportedSource);
        if (supported.Choice is not { } supportedChoice || supportedSource?.Audio is not { AudioCodec: { } codec }
            || !CanConvert(supportedChoice))
            return (null, "This source doesn't have a supported track and native MP3 encoder for conversion.");
        choice = supportedChoice;

        string? tags = choice.MediaMetadataJson
            ?? (choice.Kind == JobKind.Http && item.Provider is not null ? SerializeAudioMetadata(item) : null);
        return (choice with
        {
            Output = MediaOutput.Mp3,
            ConvertedAudioCodec = codec.ToUpperInvariant(),
            MediaMetadataJson = tags
        }, null);
    }

    private static bool IsNativeAac(MediaRepresentation stream) =>
        stream is { Content: StreamContent.AudioOnly, AudioCodec: "aac", Container: "mp4" or "m4a", AudioUnverified: false }
        && stream.Protocol != StreamProtocol.Hls;

    private static bool CanConvert(MediaDownloadChoice choice) => OperatingSystem.IsWindows()
        && choice.Kind is JobKind.Http or JobKind.Merge
        && Path.GetExtension(choice.FileName).ToLowerInvariant() is ".webm" or ".mkv" or ".mp4" or ".m4a";

    // A complete direct AAC file can be downloaded normally and cut locally.
    // It does not need a pre-known exact size or a provider range identity.
    private static bool CanClipDirectAac(MediaRepresentation? track) =>
        track is { Protocol: StreamProtocol.Http, Content: StreamContent.AudioOnly,
            Container: "mp4" or "m4a", AudioCodec: "aac", AudioUnverified: false, IsLive: false }
        && !HasIncompatibleAacProfile(track);

    private static bool CanClipLocalMp4Tracks(IReadOnlyList<MediaRepresentation> tracks, string? outputContainer)
    {
        if (tracks.Count is < 1 or > 2
            || tracks.Any(track => track.Protocol != StreamProtocol.Http || track.IsLive
                || track.FileSize is not (> 0 and <= MediaMergePlan.MaxTrackBytes) || track.FileSizeIsEstimate
                || string.IsNullOrWhiteSpace(track.ContentIdentity) || HasIncompatibleAacProfile(track)))
            return false;

        if (outputContainer == "m4a")
            return tracks.Count == 1 && tracks[0] is
            {
                Content: StreamContent.AudioOnly, Container: "mp4" or "m4a", AudioCodec: "aac"
            };

        if (outputContainer != "mp4") return false;
        if (tracks.Count == 1)
        {
            return tracks[0] is
            {
                Content: StreamContent.VideoAndAudio, Container: "mp4", VideoCodec: "h264", AudioCodec: "aac",
                AudioUnverified: false
            };
        }

        if (tracks.Count(track => track.Content == StreamContent.VideoOnly) != 1
            || tracks.Count(track => track.Content == StreamContent.AudioOnly) != 1)
            return false;

        MediaRepresentation video = tracks.Single(track => track.Content == StreamContent.VideoOnly);
        MediaRepresentation audio = tracks.Single(track => track.Content == StreamContent.AudioOnly);
        return video.Container == "mp4" && video.VideoCodec == "h264"
            && (audio.Container is "mp4" or "m4a") && audio.AudioCodec == "aac";
    }

    private static bool HasIncompatibleAacProfile(MediaRepresentation track)
    {
        if (track.AudioCodec != "aac" || string.IsNullOrWhiteSpace(track.RawCodecs)) return false;
        foreach (string rawCodec in track.RawCodecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = rawCodec.Split('.');
            if (parts.Length < 2 || !parts[0].Equals("mp4a", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length < 3) continue;
            if (!parts[1].Equals("40", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[2], out int audioObjectType) || audioObjectType != 2)
                return true;
        }
        return false;
    }

    private static (MediaDownloadChoice? Choice, string? Error) PrepareSource(MediaItem item, FormatPreferences prefs,
        MediaRepresentation? exact, out FormatSelection? selection, double? clipStartSeconds = null, double? clipEndSeconds = null)
    {
        int unsupportedSubtitleCount = UnsupportedSelectedSubtitleCount(item, prefs);
        if (prefs.EmbedSubtitles && prefs.SubtitleLanguages.Count > 0
            && unsupportedSubtitleCount == prefs.SubtitleLanguages.Count)
        {
            selection = null;
            return (null, "None of the selected subtitles are available. Choose another language or turn off subtitles.");
        }
        item = item with { Subtitles = SelectableSubtitleTracks(item.Subtitles) };
        prefs = prefs with { NativeMergeOnly = true };
        if (exact is { Content: StreamContent.VideoOnly, StandaloneVideo: false })
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
                    : $"{(exact.Height is { } height ? height + "p" : "Source")} · {exact.Container ?? "stream"}"
                        + (exact.StandaloneVideo ? " · no audio" : ""),
                Subtitles = subtitleSelection?.Subtitles ?? []
            };
        }
        else selection = FormatSelector.Select(PlayableOnly(item), prefs).Selection;
        if (selection is null) return (null, prefs.AudioTrackId is null
            ? "No compatible format satisfies this preference."
            : "No supported format can use the selected audio track.");

        // Segmented merge currently cannot embed captions. For presets, prefer an
        // equivalent complete-file recipe that can, without silently reducing quality.
        if (prefs.EmbedSubtitles && exact is null && clipStartSeconds is null
            && selection.Video is { } selectedVideo
            && selection.Streams.Any(track => track.Protocol != StreamProtocol.Http)
            && item.Representations.Any(track => track.Protocol == StreamProtocol.Http))
        {
            var directItem = item with
            {
                Representations = item.Representations.Where(track => track.Protocol == StreamProtocol.Http).ToArray()
            };
            var direct = PrepareSource(directItem, prefs, null, out FormatSelection? alternative);
            if (direct.Choice is { EmbeddedSubtitleCount: > 0 }
                && alternative?.Video is { } alternativeVideo
                && alternativeVideo.Height == selectedVideo.Height && alternativeVideo.Hdr == selectedVideo.Hdr
                && Math.Abs((alternativeVideo.FrameRate ?? 30) - (selectedVideo.FrameRate ?? 30)) < 1
                && alternative.HasAudio == selection.HasAudio)
            {
                selection = alternative;
                return direct;
            }
        }

        PlanResult planResult = DownloadPlanBuilder.Build(item, selection);
        if (planResult.Plan is not { } plan)
            return (null, planResult.Error?.UserMessage ?? "This format isn't ready to download.");
        IReadOnlyList<SubtitleTrack> subtitles = ValidatedSubtitles(selection, plan);
        unsupportedSubtitleCount += selection.Subtitles.Count - subtitles.Count;

        if (selection.NeedsMuxing)
        {
            if (selection.Video is not { } video || selection.Audio is not { } audio
                || MergeCapability.MergeContainer(video, audio, selection.OutputContainer) is not { } container)
                return (null, "VESPER cannot merge this video/audio combination without transcoding.");
            return BuildMerge(item, selection, plan, [video, audio], container, clipStartSeconds, clipEndSeconds,
                prefs.EmbedSubtitles, unsupportedSubtitleCount);
        }
        if (selection.Video is { BrowserRangeVerified: true } ranged && selection.Audio is null)
        {
            if (ranged.ResourceETag is null || ranged.ResourceUrlSha256 is null || ranged.ContentIdentity is null
                || MergeCapability.MuxedContainer(ranged, prefs.Container) is not { } rangedContainer)
                return (null, "The observed file has no verified native delivery plan.");
            return BuildMerge(item, selection, plan, [ranged], rangedContainer, clipStartSeconds, clipEndSeconds,
                prefs.EmbedSubtitles, unsupportedSubtitleCount);
        }
        if (prefs.EmbedSubtitles && clipStartSeconds is null && selection.Video is { } muxedVideo
            && selection.Audio is null && HasEmbeddableSubtitle(subtitles, muxedVideo)
            && muxedVideo.Protocol == StreamProtocol.Http && muxedVideo.ContentIdentity is { Length: > 0 }
            && !muxedVideo.FileSizeIsEstimate
            && MergeCapability.MuxedContainer(muxedVideo, prefs.Container) is { } muxedContainer)
            return BuildMerge(item, selection, plan, [muxedVideo], muxedContainer, null, null,
                embedSubtitles: true, unsupportedSubtitleCount: unsupportedSubtitleCount);

        bool directAacClip = selection.Video is null && CanClipDirectAac(selection.Audio);
        // Audio with provider byte identity goes through the native job: bounded range requests, container validation,
        // a clean non-fragmented M4A/WebM output and identity-checked continuation after a link refresh.
        if (selection.Video is null && selection.Audio is { ContentIdentity: not null } only
            && MergeCapability.AudioContainer(only) is { } audioContainer
            && (!directAacClip || CanClipLocalMp4Tracks([only], audioContainer)))
            return BuildMerge(item, selection, plan, [only], audioContainer, clipStartSeconds, clipEndSeconds,
                unsupportedSubtitleCount: unsupportedSubtitleCount);

        if (plan.RequiresExternalHelper || plan.Steps.Count != 1 ||
            (plan.Requirements & (ProcessingRequirement.MuxAudioVideo | ProcessingRequirement.ExtractAudio)) != 0)
            return (null, "This format requires audio/video processing that VESPER doesn't support yet.");
        TransferStep step = plan.Steps[0];
        if (step.Protocol is not (StreamProtocol.Http or StreamProtocol.Hls or StreamProtocol.Dash) || !CanCarryContext(step.Context))
            return (null, "This source needs an unsupported transfer method or request context.");
        if (clipStartSeconds is double audioStart && directAacClip && step.Protocol == StreamProtocol.Http)
        {
            if (selection.Subtitles.Count > 0) return (null, LocalMp4ClipSubtitleError);
            return (new MediaDownloadChoice(step.Url.AbsoluteUri, Path.ChangeExtension(plan.SuggestedFileName, ".m4a"),
                JobKind.Http, step.Context.Referer, step.Context.UserAgent, item.SourceUrl.AbsoluteUri,
                item.Provider, item.MediaId, selection.Summary, item.Thumbnail?.AbsoluteUri, plan.ExpiresAt,
                JsonSerializer.Serialize(new MediaTimeRange(audioStart, clipEndSeconds!.Value)), selection.Audio!.Id,
                Output: MediaOutput.M4a, Secrets: TransferSecrets(step.Context, step.Url)), null);
        }
        bool silentWebm = item.Provider == "generic" && selection.Audio is null
            && selection.Video is { Protocol: StreamProtocol.Http, Container: "webm", StandaloneVideo: true,
                AudioUnverified: false, IsLive: false, VideoCodec: "vp8" or "vp9" }
            && item.Duration is { TotalSeconds: > 0 }
            && !UrlUtil.HostIs(item.SourceUrl, "instagram.com", "tiktok.com", "youtube.com", "youtube-nocookie.com", "youtu.be", "facebook.com", "fb.watch");
        if (clipStartSeconds is not null && step.Protocol == StreamProtocol.Http && !silentWebm)
        {
            if (selection.Video is not { } localMuxedVideo || selection.Audio is not null
                || !CanClipLocalMp4Tracks([localMuxedVideo], step.Container ?? selection.OutputContainer)
                || MergeCapability.MuxedContainer(localMuxedVideo, "mp4") is not { } localMuxedContainer)
                return (null, step.Container == "webm"
                    ? "This WebM cannot be trimmed by the current keyframe clipper. Only verified silent VP8/VP9 WebM is supported."
                    : LocalMp4ClipSourceError);
            return BuildMerge(item, selection, plan, [localMuxedVideo], localMuxedContainer,
                clipStartSeconds, clipEndSeconds, prefs.EmbedSubtitles, unsupportedSubtitleCount);
        }
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
        if (item.Kind == MediaKind.Audio && step.Protocol == StreamProtocol.Http && step.Container == "vorbis")
            name = Path.ChangeExtension(name, ".ogg");
        string? mediaPlan = dashPlan;
        string summary = selection.Summary;
        if (step.Protocol == StreamProtocol.Hls && clipStartSeconds is double clipStart)
            mediaPlan = JsonSerializer.Serialize(new MediaTimeRange(clipStart, clipEndSeconds!.Value));
        if (item.Provider == "generic" && step.Protocol == StreamProtocol.Http && step.Container is ("mp4" or "webm")
            && selection.Video is { } directVideo && (directVideo.StandaloneVideo || step.Container == "mp4" && directVideo is { AudioUnverified: true, MutedLoop: true })
            && !UrlUtil.HostIs(item.SourceUrl, "instagram.com", "tiktok.com", "youtube.com", "youtube-nocookie.com", "youtu.be", "facebook.com", "fb.watch"))
        {
            mediaPlan = DirectMediaPlan.AudioIfPresent;
            string audioLabel = directVideo.StandaloneVideo ? "no audio" : "may be silent";
            summary = directVideo.Height is int height ? $"{height}p · {audioLabel}" : $"Video · {audioLabel}";
        }
        if (silentWebm && clipStartSeconds is double webmStart)
        {
            if (selection.Subtitles.Count > 0) return (null, LocalMp4ClipSubtitleError);
            mediaPlan = DirectMediaPlan.WebmClip(new MediaTimeRange(webmStart, clipEndSeconds!.Value));
        }
        return (new MediaDownloadChoice(step.Url.AbsoluteUri, name,
            step.Protocol == StreamProtocol.Hls ? JobKind.Hls : step.Protocol == StreamProtocol.Dash ? JobKind.Dash : JobKind.Http,
            step.Context.Referer, step.Context.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, summary,
            item.Thumbnail?.AbsoluteUri, plan.ExpiresAt, mediaPlan, selection.Streams.Single().Id,
            SubtitleTracks: subtitles, Secrets: TransferSecrets(step.Context, step.Url),
            SubtitleNotice: SubtitleNotice(prefs.EmbedSubtitles, subtitles.Count, 0,
                unsupportedSubtitleCount: unsupportedSubtitleCount),
            MediaMetadataJson: step.Protocol == StreamProtocol.Http && item.Kind == MediaKind.Audio
                && step.Container is ("mp3" or "flac" or "ogg" or "vorbis" or "opus")
                    ? SerializeAudioMetadata(item) : null,
            CanUseFullFileClip: clipStartSeconds is null && step.Protocol == StreamProtocol.Http
                && (silentWebm || directAacClip || selection.Video is { } localClipVideo && selection.Audio is null
                    && CanClipLocalMp4Tracks([localClipVideo], step.Container ?? selection.OutputContainer))), null);
    }

    /// <summary>Builds the engine's merge plan. Every track shares one request context; no credentials are persisted.</summary>
    private static (MediaDownloadChoice? Choice, string? Error) BuildMerge(MediaItem item, FormatSelection selection,
        DownloadPlan plan, IReadOnlyList<MediaRepresentation> tracks, string container,
        double? clipStartSeconds, double? clipEndSeconds, bool embedSubtitles = false,
        int unsupportedSubtitleCount = 0)
    {
        IReadOnlyList<SubtitleTrack> validatedSubtitles = ValidatedSubtitles(selection, plan);
        unsupportedSubtitleCount += selection.Subtitles.Count - validatedSubtitles.Count;
        SubtitleTrack[] subtitles = validatedSubtitles.Where(IsSupportedSubtitle).ToArray();
        unsupportedSubtitleCount += validatedSubtitles.Count - subtitles.Length;
        bool segmentedClip = tracks.Count == 2
            && tracks.All(track => track.Protocol is StreamProtocol.Hls or StreamProtocol.Dash);
        bool localMp4Clip = CanClipLocalMp4Tracks(tracks, container);
        if (clipStartSeconds is not null && !segmentedClip && !localMp4Clip)
            return (null, "This format can't be trimmed. Choose a compatible MP4 or segmented HLS/DASH format.");
        if (clipStartSeconds is not null && localMp4Clip && selection.Subtitles.Count > 0)
            return (null, LocalMp4ClipSubtitleError);
        MediaRequestContext ctx = tracks[0].Context;
        if (tracks.Any(t => !CanCarryContext(t.Context)
            || t.Context.UserAgent != ctx.UserAgent || t.Context.Referer != ctx.Referer
            || t.Context.CookieHeader != ctx.CookieHeader
            || !t.Context.SensitiveHeaders.OrderBy(h => h.Key).SequenceEqual(ctx.SensitiveHeaders.OrderBy(h => h.Key))))
            return (null, "These streams need different request contexts, which a single merge job cannot carry.");

        IReadOnlyDictionary<string, string>? secrets = TransferSecrets(ctx, tracks[0].Url);
        if (tracks.Any(track => !SameSecretHeaders(secrets, TransferSecrets(track.Context, track.Url))))
            return (null, "These streams need different credential headers, which a single merge job cannot carry.");
        if (secrets is not null && tracks.Any(track => !UrlPolicy.IsSameOrigin(tracks[0].Url, track.Url)))
            return (null, "Credentialed merge tracks must share one exact origin.");
        if (tracks.Any(t => t.Container is null || t.Protocol == StreamProtocol.Http && t.FileSize is not > 0
            || t.Protocol == StreamProtocol.Hls && t.Container is not ("mp4" or "m4a" or "ts")
            || t.Protocol == StreamProtocol.Dash && t.Segments is not { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
            || t.Protocol is not (StreamProtocol.Http or StreamProtocol.Hls or StreamProtocol.Dash)))
            return (null, "A selected stream has no complete native download plan.");

        SubtitleTrack[] embeddedSubtitles = [];
        bool subtitleLimitReached = false;
        if (embedSubtitles && clipStartSeconds is null && selection.HasVideo
            && tracks.All(track => track.Protocol == StreamProtocol.Http && !track.FileSizeIsEstimate
                && track.ContentIdentity is { Length: > 0 }))
        {
            IEnumerable<SubtitleTrack> candidates = subtitles.Where(IsEmbeddableSubtitle);
            if (secrets is not null)
                candidates = candidates.Where(subtitle => UrlPolicy.IsSameOrigin(tracks[0].Url, subtitle.Url));
            SubtitleTrack[] selected = candidates.ToArray();
            subtitleLimitReached = selected.Length > MaxEmbeddedSubtitleTracks;
            string output = container == "webm" ? "mkv" : container;
            if (selected.Length is > 0 and <= MaxEmbeddedSubtitleTracks && output is ("mp4" or "mkv"))
            {
                embeddedSubtitles = selected;
                container = output;
            }
        }

        var mergeTracks = new List<MergeTrack>(tracks.Count);
        foreach (MediaRepresentation track in tracks)
        {
            string role = track.HasVideo && track.HasAudio ? "muxed" : track.HasVideo ? "video" : "audio";
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
                track.AudioLanguage is { Length: 3 } lang ? lang : null, source,
                source is null ? track.ResourceETag : null, source is null ? track.ResourceUrlSha256 : null));
        }
        var merge = new MediaMergePlan
        {
            Version = mergeTracks.Any(track => track.Source is not null) ? 2
                : embeddedSubtitles.Length > 0 || mergeTracks.Any(track => track.Role == "muxed") ? 3 : 1,
            Provider = item.Provider,
            MediaId = item.MediaId,
            OutputContainer = container,
            RequireAudio = tracks.Any(t => t.HasAudio),
            RequireVideo = tracks.Any(t => t.HasVideo),
            Metadata = MergeMetadata(item, includeChapters: clipStartSeconds is null),
            ClipRange = clipStartSeconds is double start ? new MediaTimeRange(start, clipEndSeconds!.Value) : null,
            Tracks = mergeTracks,
            Subtitles = embeddedSubtitles.Select(subtitle => new MergeSubtitle(subtitle.Url.AbsoluteUri,
                subtitle.Format.ToLowerInvariant(), TextUtil.NormalizeLang(subtitle.Language),
                SubtitleName(subtitle.Name, subtitle.Language))).ToArray()
        };
        if (clipStartSeconds is not null && localMp4Clip && !merge.CanClipLocalMp4)
            return (null, LocalMp4ClipSourceError);
        string name = Path.ChangeExtension(plan.SuggestedFileName, container);
        string summary = DisplayQuality(selection.Summary);
        return (new MediaDownloadChoice(tracks[0].Url.AbsoluteUri, name, JobKind.Merge, ctx.Referer, ctx.UserAgent,
            item.SourceUrl.AbsoluteUri, item.Provider, item.MediaId, summary, item.Thumbnail?.AbsoluteUri, plan.ExpiresAt,
            merge.ToJson(), string.Join("+", tracks.Select(t => t.Id)), SubtitleTracks: subtitles,
            EmbeddedSubtitleCount: embeddedSubtitles.Length,
            SubtitleNotice: SubtitleNotice(embedSubtitles, subtitles.Length, embeddedSubtitles.Length,
                subtitleLimitReached, unsupportedSubtitleCount),
            Secrets: secrets,
            CanUseFullFileClip: clipStartSeconds is null && CanClipLocalMp4Tracks(tracks, container)), null);
    }

    private static MediaMergeMetadata? MergeMetadata(MediaItem item, bool includeChapters)
    {
        var metadata = new MediaMergeMetadata
        {
            Title = LimitMetadata(item.Title, 512),
            Uploader = LimitMetadata(item.Uploader, 256),
            Description = LimitMetadata(item.Description, 8192),
            Artist = LimitMetadata(item.Artist, 256),
            Album = LimitMetadata(item.Album, 512),
            AlbumArtist = LimitMetadata(item.AlbumArtist, 256),
            TrackNumber = item.TrackNumber is > 0 and <= 999 ? item.TrackNumber : null,
            DiscNumber = item.DiscNumber is > 0 and <= 999 ? item.DiscNumber : null,
            ReleaseDate = item.ReleaseDate is { Length: <= 10 } releaseDate
                && DateOnly.TryParseExact(releaseDate, ["yyyy", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _) ? releaseDate : null,
            Duration = item.Duration is { } duration && duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(365)
                ? duration : null
        };
        if (includeChapters && item.Chapters.Count is > 0 and <= 200)
        {
            var chapters = item.Chapters.Select(chapter =>
                new MediaMergeChapter(LimitMetadata(chapter.Title, 256) ?? "", chapter.Start, chapter.End)).ToArray();
            if (ValidChapters(chapters, metadata.Duration)) metadata = metadata with { Chapters = chapters };
        }
        return metadata.Title is null && metadata.Uploader is null && metadata.Description is null && metadata.Artist is null
            && metadata.Album is null && metadata.AlbumArtist is null && metadata.TrackNumber is null && metadata.DiscNumber is null
            && metadata.ReleaseDate is null && metadata.Chapters.Count == 0
            ? null : metadata;
    }

    private static string? SerializeAudioMetadata(MediaItem item)
    {
        MediaMergeMetadata? metadata = MergeMetadata(item, includeChapters: false);
        if (metadata is null)
        {
            if (item.Thumbnail is null) return null;
            metadata = new MediaMergeMetadata();
        }
        metadata = metadata with { Uploader = null, Description = null, Duration = null, Chapters = [] };
        if (metadata.Title is null && metadata.Artist is null && metadata.Album is null && metadata.AlbumArtist is null
            && metadata.TrackNumber is null && metadata.DiscNumber is null && metadata.ReleaseDate is null
            && item.Thumbnail is null) return null;
        return JsonSerializer.Serialize(metadata);
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

    internal static bool IsEmbeddableSubtitle(SubtitleTrack track) =>
        track.Format.Equals("srt", StringComparison.OrdinalIgnoreCase)
        || track.Format.Equals("vtt", StringComparison.OrdinalIgnoreCase);

    private static bool HasEmbeddableSubtitle(IReadOnlyList<SubtitleTrack> subtitles, MediaRepresentation track)
    {
        bool credentials = TransferSecrets(track.Context, track.Url) is not null;
        return subtitles.Any(subtitle => IsEmbeddableSubtitle(subtitle)
            && (!credentials || UrlPolicy.IsSameOrigin(track.Url, subtitle.Url)));
    }

    private static string SubtitleName(string? name, string language)
    {
        string? cleaned = LimitMetadata(name, 128);
        return cleaned is { Length: > 0 } && !cleaned.Any(char.IsControl) ? cleaned : TextUtil.NormalizeLang(language);
    }

    private static string? SubtitleNotice(bool requested, int selectedCount, int embeddedCount,
        bool subtitleLimitReached = false, int unsupportedSubtitleCount = 0)
    {
        string? message = selectedCount == 0 ? null : !requested
            ? "Selected subtitles will be saved as separate files."
            : subtitleLimitReached
                ? "Too many subtitles to add inside the video. Separate subtitle files are saved instead."
                : embeddedCount == selectedCount
                    ? "Subtitles will appear as a track in the video. Separate files stay available too."
                    : embeddedCount > 0
                        ? "Supported subtitles will appear in the video; separate files stay available too."
                        : "These subtitles are saved separately for this format.";
        if (unsupportedSubtitleCount > 0)
        {
            string unavailable = selectedCount == 0 ? "The selected captions weren't available. No subtitle files will be saved."
                : "Some selected captions weren't available; the rest can still be saved.";
            message = message is null ? unavailable : message + " " + unavailable;
        }
        return message;
    }

    internal static bool IsDirectSubtitle(SubtitleTrack track) =>
        track.Format.ToLowerInvariant() is "vtt" or "srt" or "ttml";

    internal static bool IsSelectableSubtitle(SubtitleTrack track) =>
        IsSupportedSubtitle(track) && UrlValidator.IsAllowed(track.Url, SubtitleUrlOptions, out _);

    private static bool IsSupportedSubtitle(SubtitleTrack track) => IsDirectSubtitle(track)
        || string.Equals(track.Format, "hls-webvtt", StringComparison.OrdinalIgnoreCase);

    internal static SubtitleTrack[] SelectableSubtitleTracks(IEnumerable<SubtitleTrack> tracks) => tracks
        .Where(IsSelectableSubtitle)
        .GroupBy(track => TextUtil.NormalizeLang(track.Language), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderBy(track => track.IsAutoGenerated)
            .ThenBy(track => track.Url.AbsoluteUri, StringComparer.Ordinal)
            .First())
        .OrderBy(track => TextUtil.NormalizeLang(track.Language), StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static int UnsupportedSelectedSubtitleCount(MediaItem item, FormatPreferences preferences)
    {
        int unavailable = 0;
        foreach (string language in preferences.SubtitleLanguages)
        {
            string wanted = TextUtil.NormalizeLang(language);
            SubtitleTrack[] matches = item.Subtitles.Where(track =>
            {
                if (!preferences.IncludeAutoSubtitles && track.IsAutoGenerated) return false;
                string actual = TextUtil.NormalizeLang(track.Language);
                return actual == wanted || TextUtil.PrimaryLang(actual) == TextUtil.PrimaryLang(wanted);
            }).ToArray();
            if (!matches.Any(IsSelectableSubtitle)) unavailable++;
        }
        return unavailable;
    }

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
                string format = segmented ? "srt" : subtitle.Format.ToLowerInvariant();
                string fileName = FileNames.Sanitize($"{Path.GetFileNameWithoutExtension(choice.FileName)}.{language}.{format}");
                expanded.Add(choice with
                {
                    Secrets = null, // A subtitle URL must never inherit the media origin's credentials.
                    Url = subtitle.Url.AbsoluteUri,
                    FileName = fileName,
                    Kind = segmented ? JobKind.Hls : JobKind.Http,
                    Provider = null,
                    MediaId = null,
                    Quality = $"Subtitle · {language}",
                    ThumbnailUrl = null,
                    ExpiresAt = null,
                    MediaPlanJson = segmented ? JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        Type = "hls-webvtt",
                        OutputFormat = "srt"
                    }) : null,
                    FormatId = segmented ? $"subtitle:{language}" : null,
                    Output = MediaOutput.Original,
                    ConvertedVideoQuality = null,
                    ConvertedAudioCodec = null,
                    SubtitleTracks = null,
                    EmbeddedSubtitleCount = 0,
                    SubtitleNotice = null,
                    MediaMetadataJson = null
                });
            }
        }
        return expanded;
    }

    internal static bool SupportsTimeRange(MediaDownloadChoice choice)
    {
        if (choice.Output == MediaOutput.Wav) return choice.Kind is JobKind.Http or JobKind.Merge;
        if (choice.Kind == JobKind.Hls) return true;
        if (choice.Kind == JobKind.Http) return choice.Output == MediaOutput.Original && choice.CanUseFullFileClip;
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
            if (plan is { Version: 2, Tracks.Count: 2 }
                && plan.Tracks.All(track => track.Source?.Protocol is "hls" or "dash"))
                return true;
            if (choice.Output != MediaOutput.Original || plan is null
                || !choice.CanUseFullFileClip || plan.ClipRange is not null) return false;
            return plan.CanClipLocalMp4 || plan.Subtitles.Count > 0
                && (plan with { Subtitles = [] }).CanClipLocalMp4;
        }
        catch (JsonException) { return false; }
    }
}
