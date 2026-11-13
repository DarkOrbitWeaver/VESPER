using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vesper.Engine;

/// <summary>
/// One source track of a merge job: a single HTTP object of exactly known size, fetched with bounded range requests.
/// <see cref="Identity"/> is provider evidence that identifies the exact encoded bytes (for YouTube: itag, last-modified
/// and content length). It is what allows a refreshed signed URL to continue committed bytes; titles or video ids never do.
/// </summary>
public sealed record MergeTrack(string Role, string FormatId, string Url, long Size, string Container,
    string? Codec = null, string? Identity = null, string? Language = null, MergeMediaSource? Source = null);

/// <summary>Plan for a segmented rendition. HLS resolves a static playlist at run time; DASH has explicit fMP4 parts.</summary>
public sealed record MergeMediaSource(string Protocol, string? PlaylistUrl = null, DashDownloadPlan? Dash = null);

public sealed record MediaMergeChapter(string Title, TimeSpan Start, TimeSpan? End = null);

public sealed record MediaMergeMetadata
{
    public string? Title { get; init; }
    public string? Uploader { get; init; }
    public string? Description { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public string? ReleaseDate { get; init; }
    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<MediaMergeChapter> Chapters { get; init; } = [];
}

/// <summary>
/// Immutable plan for a native remux job (separate video + audio, or a single track normalised into a clean container).
/// Contains no cookies, authorization headers or keys.
/// </summary>
public sealed record MediaMergePlan
{
    public const long MaxTrackBytes = 256L * 1024 * 1024 * 1024;
    private static readonly string[] Outputs = ["mp4", "m4a", "webm", "mkv"];

    public int Version { get; init; } = 1;
    public string? Provider { get; init; }
    public string? MediaId { get; init; }
    public required string OutputContainer { get; init; }
    public required IReadOnlyList<MergeTrack> Tracks { get; init; }
    public bool RequireAudio { get; init; } = true;
    public bool RequireVideo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaMergeMetadata? Metadata { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaTimeRange? ClipRange { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);
    public long TotalBytes => Tracks.Where(t => t.Source is null).Sum(t => t.Size);

    internal bool IsSegmented => Tracks.Any(track => track.Source is not null);

    internal long OffsetOf(int index)
    {
        long offset = 0;
        for (int i = 0; i < index; i++) offset += Tracks[i].Size;
        return offset;
    }

    /// <summary>
    /// Identity of the bytes being assembled. URLs are deliberately excluded: only a replacement that matches every
    /// role, format id, size, codec and provider identity may reuse committed bytes.
    /// </summary>
    internal string Fingerprint
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Version).Append('|').Append(Provider).Append('|').Append(MediaId).Append('|').Append(OutputContainer)
              .Append('|').Append(RequireAudio).Append('|').Append(RequireVideo);
            if (ClipRange is not null) sb.Append("|C:").Append(JsonSerializer.Serialize(ClipRange));
            foreach (var t in Tracks)
            {
                sb.Append("|T:").Append(t.Role).Append(',').Append(t.FormatId).Append(',').Append(t.Size).Append(',')
                  .Append(t.Container).Append(',').Append(t.Codec).Append(',').Append(t.Identity).Append(',').Append(t.Language);
                if (t.Source is not null)
                    sb.Append(",S:").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t.Source)))));
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }

    /// <summary>True only when every track carries byte identity and the replacement describes the same bytes.</summary>
    internal bool CanContinueWith(MediaMergePlan replacement, out string why)
    {
        if (Tracks.Any(t => string.IsNullOrEmpty(t.Identity)))
        {
            why = "The original plan has no byte-level identity for every track, so saved data cannot be continued from a new link.";
            return false;
        }
        if (replacement.Fingerprint != Fingerprint)
        {
            why = "The newly resolved representation differs (format, size or encoding identity). Saved data was kept; restart as a separate copy.";
            return false;
        }
        why = "";
        return true;
    }

    internal static bool TryParse(string? json, bool allowPrivate, out MediaMergePlan plan, out string error)
    {
        plan = null!;
        error = "The media merge plan is missing or invalid. Resolve the source again; saved data was kept.";
        if (json is null || json.Length > 24 * 1024 * 1024) return false;
        try
        {
            var value = JsonSerializer.Deserialize<MediaMergePlan>(json, new JsonSerializerOptions { MaxDepth = 12 });
            if (value is null || value.Version is not (1 or 2) || !Outputs.Contains(value.OutputContainer)
                || value.Tracks is not { Count: >= 1 and <= 4 }
                || (value.Provider?.Length ?? 0) > 80 || (value.MediaId?.Length ?? 0) > 256) return false;
            if (value.Metadata is { } metadata && !ValidMetadata(metadata)) return false;
            bool segmented = value.Tracks.Any(track => track?.Source is not null);
            if (value.ClipRange is { IsValid: false } || value.ClipRange is not null && !segmented) return false;
            if (value.ClipRange is not null && value.Tracks.Any(track => track.Source?.Dash?.Fragments.Any(fragment => fragment.DurationSeconds is null) == true)) return false;
            if (segmented != (value.Version == 2) || (segmented && value.Tracks.Any(track => track?.Source is null))) return false;
            int videos = 0, audios = 0;
            foreach (var t in value.Tracks)
            {
                if (t is null || t.Role is not ("video" or "audio") || t.FormatId is not { Length: > 0 and <= 256 }
                    || (t.Source is null ? t.Size <= 0 || t.Size > MaxTrackBytes : t.Size != 0)
                    || t.Container is not ("mp4" or "m4a" or "webm" or "ts")
                    || (t.Identity?.Length ?? 0) > 512 || (t.Codec?.Length ?? 0) > 64 || (t.Language?.Length ?? 0) > 35
                    || t.Url is not { Length: > 0 and <= 16384 } || !UrlPolicy.TryParse(t.Url, out var uri, out _)) return false;
                UrlPolicy.EnsureAllowed(uri, allowPrivate);
                if (t.Source is { } source && !ValidSource(t, source, allowPrivate)) return false;
                if (t.Role == "video") videos++; else audios++;
            }
            if (videos > 1 || (value.RequireVideo && videos == 0) || (value.RequireAudio && audios == 0)) return false;
            if (segmented && (videos != 1 || audios != 1 || !value.RequireVideo || !value.RequireAudio
                || value.Tracks.Select(t => t.Source!.Protocol).Distinct(StringComparer.Ordinal).Count() != 1)) return false;
            if (value.OutputContainer == "m4a" && videos > 0) return false;
            plan = value;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or BlockedAddressException or NotSupportedException) { return false; }
    }

    private static bool ValidSource(MergeTrack track, MergeMediaSource source, bool allowPrivate)
    {
        if (source.Protocol == "hls")
        {
            if (source.PlaylistUrl is not { Length: > 0 and <= 16384 } playlist || source.Dash is not null
                || !string.Equals(playlist, track.Url, StringComparison.Ordinal)
                || !UrlPolicy.TryParse(playlist, out var uri, out _)) return false;
            UrlPolicy.EnsureAllowed(uri, allowPrivate);
            return track.Container is "mp4" or "m4a" or "ts";
        }
        if (source.Protocol != "dash" || source.PlaylistUrl is not null || source.Dash is not { } dash
            || track.Container is not ("mp4" or "m4a")
            || dash.RepresentationId != track.FormatId
            || dash.RequireAudio != (track.Role == "audio")
            || dash.RequireVideo != (track.Role == "video")) return false;
        return DashDownloadPlan.TryParse(JsonSerializer.Serialize(dash), allowPrivate, out _, out _);
    }

    private static bool ValidMetadata(MediaMergeMetadata metadata)
    {
        if ((metadata.Title?.Length ?? 0) > 512 || (metadata.Uploader?.Length ?? 0) > 256
            || (metadata.Description?.Length ?? 0) > 8192 || metadata.Chapters is not { Count: <= 200 }
            || (metadata.Artist?.Length ?? 0) > 256 || (metadata.Album?.Length ?? 0) > 512
            || (metadata.AlbumArtist?.Length ?? 0) > 256
            || metadata.TrackNumber is < 1 or > 999 || metadata.DiscNumber is < 1 or > 999
            || !ValidReleaseDate(metadata.ReleaseDate)
            || metadata.Duration is { } duration && (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(365))) return false;
        TimeSpan previousStart = TimeSpan.MinValue;
        TimeSpan? previousEnd = null;
        foreach (MediaMergeChapter? chapter in metadata.Chapters)
        {
            if (chapter is null || string.IsNullOrWhiteSpace(chapter.Title) || chapter.Title.Length > 256
                || chapter.Start < TimeSpan.Zero || chapter.Start > TimeSpan.FromDays(365) || chapter.Start <= previousStart
                || metadata.Duration is { } mediaDuration && chapter.Start >= mediaDuration
                || chapter.End is { } end && (end <= chapter.Start || end > TimeSpan.FromDays(365)
                    || metadata.Duration is { } boundedDuration && end > boundedDuration)) return false;
            if (previousEnd is { } prior && chapter.Start < prior) return false;
            previousStart = chapter.Start;
            previousEnd = chapter.End;
        }
        return true;
    }

    private static bool ValidReleaseDate(string? value) => value is null
        || value.Length <= 10 && (DateOnly.TryParseExact(value, "yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)
            || DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _));
}
