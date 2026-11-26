using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Formats;

/// <summary>
/// What VESPER's native remuxer can execute, decided from declared metadata before a format is offered as a complete
/// option. The engine re-checks the real codecs after download and refuses to publish if the source declared something
/// else. Remux only: nothing here implies transcoding.
/// </summary>
public static class MergeCapability
{
    private static readonly HashSet<string> Mp4Video = ["h264", "h265", "av1"];
    private static readonly HashSet<string> Mp4Audio = ["aac", "mp3"];
    private static readonly HashSet<string> WebmVideo = ["vp9", "vp8", "av1"];
    private static readonly HashSet<string> WebmAudio = ["opus", "vorbis"];
    private static readonly HashSet<string> MkvVideo = ["h264", "h265", "av1", "vp9", "vp8"];
    private static readonly HashSet<string> MkvAudio = ["aac", "opus", "vorbis", "mp3"];

    /// <summary>A single plain HTTP object of exactly known size in a container the engine can demux.</summary>
    public static bool IsNativeTrack(MediaRepresentation r) =>
        r.Protocol == StreamProtocol.Http && r.FileSize is > 0 && !r.FileSizeIsEstimate && !r.IsLive
        && r.Container is "mp4" or "m4a" or "webm"
        && HasSafeRequestContext(r);

    private static bool IsSegmentedTrack(MediaRepresentation r) => !r.IsLive && HasSafeRequestContext(r)
        && (r.Protocol == StreamProtocol.Hls && r.Container is "mp4" or "m4a" or "ts"
            || r.Protocol == StreamProtocol.Dash && r.Segments is { IsFragmentedMp4: true, InitializationUrl: not null, HasDiscontinuities: false }
               && r.Container is "mp4" or "m4a");

    private static bool IsNativeHttpTrack(MediaRepresentation r) =>
        r.Protocol == StreamProtocol.Http && r.FileSize is > 0 && !r.FileSizeIsEstimate && !r.IsLive
        && r.Container is "mp4" or "m4a" or "webm";

    private static bool HasMatchingOriginCredentials(MediaRepresentation video, MediaRepresentation audio)
    {
        MediaRequestContext v = video.Context, a = audio.Context;
        if (!v.HasCredentials || !a.HasCredentials || v.Origin is not null || a.Origin is not null
            || v.AcceptLanguage is not null || a.AcceptLanguage is not null
            || v.SensitiveHeaders.Keys.Any(name => !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            || a.SensitiveHeaders.Keys.Any(name => !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(UrlUtil.Origin(video.Url), UrlUtil.Origin(audio.Url), StringComparison.OrdinalIgnoreCase)
            || !v.MayForwardCredentialsTo(video.Url) || !a.MayForwardCredentialsTo(audio.Url))
            return false;

        static Dictionary<string, string>? SecretHeaders(MediaRequestContext context, Uri url)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in context.HeadersFor(url))
            {
                if (!name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (!headers.TryAdd(name, value)) return null;
            }
            return headers;
        }

        Dictionary<string, string>? videoHeaders = SecretHeaders(v, video.Url);
        Dictionary<string, string>? audioHeaders = SecretHeaders(a, audio.Url);
        return videoHeaders is not null && audioHeaders is not null && videoHeaders.Count == audioHeaders.Count
            && videoHeaders.All(pair => audioHeaders.TryGetValue(pair.Key, out string? value)
                && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static bool HasSafeRequestContext(MediaRepresentation r) =>
        !r.Context.HasCredentials && r.Context.Origin is null && r.Context.AcceptLanguage is null;

    /// <summary>Output container for merging <paramref name="video"/> (video-only) with <paramref name="audio"/> (audio-only), or null.</summary>
    public static string? MergeContainer(MediaRepresentation video, MediaRepresentation audio, string? preferred = null)
    {
        if (video.Content != StreamContent.VideoOnly || audio.Content != StreamContent.AudioOnly) return null;
        if (video is { Protocol: StreamProtocol.Hls, AudioGroupId: { } group }
            && (audio.Protocol != StreamProtocol.Hls || audio.AudioGroupId != group
                || video.ManifestUrl is not null && audio.ManifestUrl != video.ManifestUrl)) return null;
        string? v = video.VideoCodec, a = audio.AudioCodec;
        if (v is null || a is null) return null;

        bool segmented = IsSegmentedTrack(video) || IsSegmentedTrack(audio);
        if (segmented)
        {
            if (!IsSegmentedTrack(video) || !IsSegmentedTrack(audio) || video.Protocol != audio.Protocol) return null;
            if (video.Protocol == StreamProtocol.Hls && (video.Container == "ts" || audio.Container == "ts"))
                return video.Container == "ts" && audio.Container == "ts" && v == "h264" && a == "aac" ? "mkv" : null;
            if (video.Protocol == StreamProtocol.Dash)
                return Mp4Video.Contains(v) && Mp4Audio.Contains(a) ? "mp4"
                    : MkvVideo.Contains(v) && MkvAudio.Contains(a) ? "mkv" : null;
            bool streamVideoMp4 = video.Container == "mp4", streamAudioMp4 = audio.Container is "mp4" or "m4a";
            if (streamVideoMp4 && streamAudioMp4 && Mp4Video.Contains(v) && Mp4Audio.Contains(a)) return "mp4";
            return MkvVideo.Contains(v) && MkvAudio.Contains(a) ? "mkv" : null;
        }

        if (!IsNativeHttpTrack(video) || !IsNativeHttpTrack(audio)) return null;
        bool hasCredentials = video.Context.HasCredentials || audio.Context.HasCredentials;
        if (hasCredentials ? !HasMatchingOriginCredentials(video, audio)
            : !HasSafeRequestContext(video) || !HasSafeRequestContext(audio)) return null;
        bool videoMp4 = video.Container == "mp4", audioMp4 = audio.Container is "mp4" or "m4a";
        var options = new List<string>(3);
        if (videoMp4 && audioMp4 && Mp4Video.Contains(v) && Mp4Audio.Contains(a)) options.Add("mp4");
        if (WebmVideo.Contains(v) && WebmAudio.Contains(a) && audio.Container == "webm" && (video.Container == "webm" || v == "av1")) options.Add("webm");
        if (MkvVideo.Contains(v) && MkvAudio.Contains(a)) options.Add("mkv");
        if (options.Count == 0) return null;
        return preferred is not null && options.Contains(preferred) ? preferred : options[0];
    }

    /// <summary>Container for a native single-track audio download (remuxed, never transcoded), or null.</summary>
    public static string? AudioContainer(MediaRepresentation audio)
    {
        if (audio.Content != StreamContent.AudioOnly || !IsNativeTrack(audio)) return null;
        return audio.Container == "webm"
            ? audio.AudioCodec is "opus" or "vorbis" ? "webm" : null
            : audio.AudioCodec is "aac" or "mp3" ? "m4a" : null;
    }

    /// <summary>Output container for a single direct video object that already contains its selected tracks.</summary>
    public static string? MuxedContainer(MediaRepresentation track, string? preferred = null)
    {
        if (!IsNativeHttpTrack(track) || !track.HasVideo || track.VideoCodec is not { } video) return null;
        bool hasAudio = track.HasAudio;
        string? audio = track.AudioCodec;
        bool mp4 = track.Container == "mp4" && Mp4Video.Contains(video)
            && (!hasAudio || audio is not null && Mp4Audio.Contains(audio));
        bool matroska = MkvVideo.Contains(video) && (!hasAudio || audio is not null && MkvAudio.Contains(audio));
        if (preferred == "mp4" && mp4) return "mp4";
        if (preferred == "mkv" && matroska) return "mkv";
        return mp4 ? "mp4" : matroska ? "mkv" : null;
    }
}
