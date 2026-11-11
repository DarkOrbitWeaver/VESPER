using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

internal enum CandidateKind { Unknown, Direct, Hls, Dash }

/// <summary>A URL that might be media (from page markup or from the browser). It is verified before it becomes a representation.</summary>
internal sealed record MediaCandidate(
    Uri Url,
    CandidateKind Kind,
    string? MimeType = null,
    int? Width = null,
    int? Height = null,
    long? Bitrate = null,
    double? DurationSeconds = null,
    string? Label = null)
{
    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "webm", "mkv", "mov", "avi", "flv", "ts", "mpg", "mpeg", "3gp", "ogv", "wmv",
        "mp3", "m4a", "aac", "ogg", "oga", "opus", "flac", "wav", "wma"
    };

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "aac", "ogg", "oga", "opus", "flac", "wav", "wma"
    };

    public static CandidateKind Classify(Uri url, string? mime)
    {
        string? ext = UrlUtil.Extension(url);
        string m = mime?.ToLowerInvariant() ?? "";
        if (ext is "m3u8" or "m3u" || m.Contains("mpegurl")) return CandidateKind.Hls;
        if (ext == "mpd" || m.Contains("dash+xml")) return CandidateKind.Dash;
        if ((ext is not null && MediaExtensions.Contains(ext)) || m.StartsWith("video/") || m.StartsWith("audio/")) return CandidateKind.Direct;
        return CandidateKind.Unknown;
    }
}
