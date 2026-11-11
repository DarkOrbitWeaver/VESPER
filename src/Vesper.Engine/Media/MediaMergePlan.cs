using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// One source track of a merge job: a single HTTP object of exactly known size, fetched with bounded range requests.
/// <see cref="Identity"/> is provider evidence that identifies the exact encoded bytes (for YouTube: itag, last-modified
/// and content length). It is what allows a refreshed signed URL to continue committed bytes; titles or video ids never do.
/// </summary>
public sealed record MergeTrack(string Role, string FormatId, string Url, long Size, string Container,
    string? Codec = null, string? Identity = null, string? Language = null);

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

    public string ToJson() => JsonSerializer.Serialize(this);
    public long TotalBytes => Tracks.Sum(t => t.Size);

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
            foreach (var t in Tracks)
                sb.Append("|T:").Append(t.Role).Append(',').Append(t.FormatId).Append(',').Append(t.Size).Append(',')
                  .Append(t.Container).Append(',').Append(t.Codec).Append(',').Append(t.Identity).Append(',').Append(t.Language);
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
        if (json is null || json.Length > 256 * 1024) return false;
        try
        {
            var value = JsonSerializer.Deserialize<MediaMergePlan>(json, new JsonSerializerOptions { MaxDepth = 8 });
            if (value is null || value.Version != 1 || !Outputs.Contains(value.OutputContainer)
                || value.Tracks is not { Count: >= 1 and <= 4 }
                || (value.Provider?.Length ?? 0) > 80 || (value.MediaId?.Length ?? 0) > 256) return false;
            int videos = 0, audios = 0;
            foreach (var t in value.Tracks)
            {
                if (t is null || t.Role is not ("video" or "audio") || t.FormatId is not { Length: > 0 and <= 256 }
                    || t.Size <= 0 || t.Size > MaxTrackBytes || t.Container is not ("mp4" or "m4a" or "webm")
                    || (t.Identity?.Length ?? 0) > 512 || (t.Codec?.Length ?? 0) > 64 || (t.Language?.Length ?? 0) > 35
                    || t.Url is not { Length: > 0 and <= 16384 } || !UrlPolicy.TryParse(t.Url, out var uri, out _)) return false;
                UrlPolicy.EnsureAllowed(uri, allowPrivate);
                if (t.Role == "video") videos++; else audios++;
            }
            if (videos > 1 || (value.RequireVideo && videos == 0) || (value.RequireAudio && audios == 0)) return false;
            if (value.OutputContainer == "m4a" && videos > 0) return false;
            plan = value;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or BlockedAddressException or NotSupportedException) { return false; }
    }
}
