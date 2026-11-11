using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

/// <summary>Codec-string normalisation and container-compatibility rules (pure functions, no I/O).</summary>
public static class CodecInfo
{
    public static string? NormalizeVideo(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        string c = codec.Trim().ToLowerInvariant();
        if (c.StartsWith("avc1") || c.StartsWith("avc3") || c is "h264" or "h.264" or "avc") return "h264";
        if (c.StartsWith("hvc1") || c.StartsWith("hev1") || c.StartsWith("dvh1") || c.StartsWith("dvhe") || c is "h265" or "h.265" or "hevc" or "bytevc1") return "h265";
        if (c.StartsWith("vp09") || c.StartsWith("vp9") || c == "vp9") return "vp9";
        if (c.StartsWith("vp08") || c == "vp8") return "vp8";
        if (c.StartsWith("av01") || c == "av1") return "av1";
        if (c.StartsWith("mp4v")) return "mpeg4";
        return null;
    }

    public static string? NormalizeAudio(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        string c = codec.Trim().ToLowerInvariant();
        if (c is "mp4a.40.34" or "mp4a.6b" or "mp4a.69" or "mp3") return "mp3";
        if (c.StartsWith("mp4a") || c is "aac" or "aac-lc" or "he-aac") return "aac";
        if (c.StartsWith("opus") || c == "opus") return "opus";
        if (c.StartsWith("vorbis")) return "vorbis";
        if (c is "ac-3" or "ac3") return "ac3";
        if (c is "ec-3" or "eac3" or "e-ac-3") return "eac3";
        if (c.StartsWith("flac")) return "flac";
        if (c.StartsWith("alac")) return "alac";
        return null;
    }

    /// <summary>Splits an RFC 6381 codecs list ("avc1.640028, mp4a.40.2") into normalised video/audio families.</summary>
    public static void Split(string? codecs, out string? video, out string? audio, out HdrKind hdr)
    {
        video = audio = null;
        hdr = HdrKind.Sdr;
        if (string.IsNullOrWhiteSpace(codecs)) return;
        foreach (var raw in codecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string lower = raw.ToLowerInvariant();
            if (lower.StartsWith("dvh1") || lower.StartsWith("dvhe")) hdr = HdrKind.DolbyVision;
            video ??= NormalizeVideo(raw);
            audio ??= NormalizeAudio(raw);
        }
    }

    /// <summary>Higher = smaller files at equal quality.</summary>
    public static int EfficiencyRank(string? videoCodec) => videoCodec switch
    {
        "av1" => 5, "h265" => 4, "vp9" => 3, "h264" => 2, "vp8" => 1, _ => 0
    };

    /// <summary>Higher = plays on more devices without transcoding.</summary>
    public static int CompatibilityRank(string? videoCodec) => videoCodec switch
    {
        "h264" => 5, "vp9" => 3, "h265" => 2, "av1" => 2, "vp8" => 2, "mpeg4" => 1, _ => 0
    };

    public static bool FitsContainer(string container, string? video, string? audio)
    {
        bool v = video is null || container switch
        {
            "mp4" => video is "h264" or "h265" or "av1" or "vp9" or "mpeg4",
            "webm" => video is "vp8" or "vp9" or "av1",
            _ => true
        };
        bool a = audio is null || container switch
        {
            "mp4" => audio is "aac" or "mp3" or "ac3" or "eac3" or "opus" or "flac" or "alac",
            "webm" => audio is "opus" or "vorbis",
            _ => true
        };
        return v && a;
    }

    /// <summary>
    /// Container a stream-copy mux of the two codecs could use. Honours <paramref name="preferred"/> when valid.
    /// Matroska accepts everything, so it is the last resort.
    /// </summary>
    public static string ChooseMuxContainer(string? video, string? audio, string? preferred)
    {
        if (preferred is "mp4" or "webm" or "mkv" && (preferred == "mkv" || FitsContainer(preferred, video, audio)))
            return preferred;
        if (video is not null && audio is not null)
        {
            if (FitsContainer("mp4", video, audio)) return "mp4";
            if (FitsContainer("webm", video, audio)) return "webm";
        }
        return "mkv";
    }

    public static string? ContainerFromMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        string m = mime.Split(';')[0].Trim().ToLowerInvariant();
        return m switch
        {
            "video/mp4" or "application/mp4" => "mp4",
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "m4a",
            "video/webm" or "audio/webm" => "webm",
            "video/x-matroska" => "mkv",
            "video/quicktime" => "mov",
            "video/mp2t" => "ts",
            "video/ogg" or "audio/ogg" or "application/ogg" => "ogg",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/aac" or "audio/aacp" => "aac",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/opus" => "opus",
            "video/x-flv" => "flv",
            _ => null
        };
    }
}
