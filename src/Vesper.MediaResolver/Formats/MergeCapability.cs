using Vesper.MediaResolver.Models;

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

    private static bool HasSafeRequestContext(MediaRepresentation r) =>
        !r.Context.HasCredentials && r.Context.Origin is null && r.Context.AcceptLanguage is null;

    /// <summary>Output container for merging <paramref name="video"/> (video-only) with <paramref name="audio"/> (audio-only), or null.</summary>
    public static string? MergeContainer(MediaRepresentation video, MediaRepresentation audio, string? preferred = null)
    {
        if (video.Content != StreamContent.VideoOnly || audio.Content != StreamContent.AudioOnly) return null;
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

        if (!IsNativeTrack(video) || !IsNativeTrack(audio)) return null;
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
}
