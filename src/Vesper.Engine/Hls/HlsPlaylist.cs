using System.Globalization;
using System.Text.Json.Serialization;

namespace Vesper.Engine;

public sealed record HlsVariant(string Uri, long Bandwidth, int Width, int Height, string? Codecs, bool NeedsSeparateAudio, bool AudioOnly);
public sealed record HlsKey(string? Uri, byte[]? Iv);
public sealed record HlsMap(string Uri, long? Length, long? Offset, HlsKey? Key = null);
public sealed record HlsSegment(string Uri, double Duration, long? RangeLength, long? RangeOffset, HlsKey? Key, HlsMap? Map, long Sequence)
{
    [JsonIgnore]
    public bool DiscontinuityBefore { get; init; }

    [JsonIgnore]
    public bool IsGap { get; init; }

    [JsonIgnore]
    public long DiscontinuitySequence { get; init; }
}

public sealed class HlsMedia
{
    public List<HlsSegment> Segments { get; } = [];
    public bool IsVod { get; set; }
    public long MediaSequence { get; set; }
    public long DiscontinuitySequence { get; set; }
    public int TargetDuration { get; set; }
    public string? PlaylistType { get; set; }
    /// <summary>Set when the playlist uses a feature VESPER does not support.</summary>
    public string? Unsupported { get; set; }
    public string UnsupportedReasonCode { get; set; } = "unsupported";
    public double TotalSeconds => Segments.Sum(s => s.Duration);
}

/// <summary>
/// Parses the subset of RFC 8216 needed for non-DRM VOD downloads. Anything outside that subset is reported
/// through <see cref="HlsMedia.Unsupported"/> rather than being guessed at.
/// </summary>
public static class HlsParser
{
    public static bool IsPlaylist(string text) => text.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal);
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var variants = new List<HlsVariant>();
        var externalAudioGroups = text.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)).Select(Attributes)
            .Where(a => a.GetValueOrDefault("TYPE") == "AUDIO" && a.ContainsKey("URI"))
            .Select(a => a.GetValueOrDefault("GROUP-ID", "")).ToHashSet(StringComparer.Ordinal);

        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            Dictionary<string, string> a = Attributes(lines[i]);
            string uri = lines[i + 1].Trim();
            if (uri.Length == 0 || uri.StartsWith('#')) continue;

            int width = 0, height = 0;
            if (a.TryGetValue("RESOLUTION", out string? res) && res.Split('x') is [var w, var h])
            { int.TryParse(w, out width); int.TryParse(h, out height); }
            long.TryParse(a.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out long bandwidth);
            string? codecs = a.GetValueOrDefault("CODECS");
            bool audioOnly = height == 0 && codecs is not null && !codecs.Contains("avc", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("hev", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("vp0", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("av01", StringComparison.OrdinalIgnoreCase);
            // A variant that points at an audio group carries picture only; joining needs a muxer VESPER does not ship.
            bool separateAudio = a.TryGetValue("AUDIO", out var group) && externalAudioGroups.Contains(group) && !audioOnly;
            if (variants.Count >= 1000) break;
            variants.Add(new HlsVariant(new Uri(baseUri, uri).AbsoluteUri, bandwidth, width, height, codecs, separateAudio, audioOnly));
        }
        return variants;
    }

    public static HlsMedia ParseMedia(string text, Uri baseUri)
        => ParseMedia(text, baseUri, allowTimelineChanges: false);

    internal static HlsMedia ParseMedia(string text, Uri baseUri, bool allowTimelineChanges)
    {
        var media = new HlsMedia();
        HlsKey? key = null;
        HlsMap? map = null;
        long sequence = 0, discontinuitySequence = 0;
        double duration = 0;
        long? rangeLength = null, rangeOffset = null, nextOffset = null;
        string? previousResource = null;
        bool implicitRange = false, discontinuityBefore = false, gap = false;

        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (!int.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int target)
                    || target <= 0)
                    SetUnsupported(media, "The playlist has an invalid target duration.");
                else media.TargetDuration = target;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (media.Segments.Count > 0
                    || !long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
                    SetUnsupported(media, "The playlist has an invalid media sequence.");
                else media.MediaSequence = sequence;
            }
            else if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal))
            {
                if (media.Segments.Count > 0
                    || !long.TryParse(line["#EXT-X-DISCONTINUITY-SEQUENCE:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out discontinuitySequence))
                    SetUnsupported(media, "The playlist has an invalid discontinuity sequence.");
                else media.DiscontinuitySequence = discontinuitySequence;
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                string type = line["#EXT-X-PLAYLIST-TYPE:".Length..];
                if (type is not ("EVENT" or "VOD")) SetUnsupported(media, "The playlist type is unsupported.");
                else media.PlaylistType = type;
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) media.IsVod = true;
            else if (line == "#EXT-X-DISCONTINUITY")
            {
                if (discontinuitySequence == long.MaxValue) SetUnsupported(media, "The playlist discontinuity sequence overflows.");
                else discontinuitySequence++;
                discontinuityBefore = true;
                if (!allowTimelineChanges)
                    SetUnsupported(media, "Discontinuous, missing or I-frame-only media requires processing that is not supported.");
            }
            else if (line == "#EXT-X-GAP")
            {
                gap = true;
                if (!allowTimelineChanges)
                    SetUnsupported(media, "Discontinuous, missing or I-frame-only media requires processing that is not supported.");
            }
            else if (line == "#EXT-X-I-FRAMES-ONLY")
                SetUnsupported(media, "Discontinuous, missing or I-frame-only media requires processing that is not supported.");
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                double.TryParse(line["#EXTINF:".Length..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                string[] parts = line["#EXT-X-BYTERANGE:".Length..].Split('@');
                if (parts.Length > 2 || !long.TryParse(parts[0], out long len) || len <= 0)
                    SetUnsupported(media, "The playlist contains an invalid byte-range length.");
                else rangeLength = len;
                implicitRange = parts.Length == 1;
                if (implicitRange) rangeOffset = nextOffset;
                else if (long.TryParse(parts[1], out long off) && off >= 0) rangeOffset = off;
                else SetUnsupported(media, "The playlist contains an invalid byte-range offset.");
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                Dictionary<string, string> a = Attributes(line);
                string method = a.GetValueOrDefault("METHOD", "NONE");
                string format = a.GetValueOrDefault("KEYFORMAT", "identity");
                if (method == "NONE") key = null;
                else if (method == "AES-128" && format == "identity")
                {
                    byte[]? iv = ParseIv(a.GetValueOrDefault("IV"));
                    if (a.ContainsKey("IV") && iv is null)
                        SetUnsupported(media, "The AES-128 IV is invalid.", "key");
                    string? keyUri = null;
                    if (a.GetValueOrDefault("URI") is not { } value || !TryResolveHttpUri(baseUri, value, out keyUri))
                        SetUnsupported(media, "The AES-128 key address is invalid or missing.", "key");
                    key = new HlsKey(keyUri, iv);
                }
                else
                {
                    bool drm = !string.Equals(format, "identity", StringComparison.OrdinalIgnoreCase);
                    SetUnsupported(media,
                        $"The stream uses protection that VESPER does not support ({method}/{format}).",
                        drm ? "drm" : "unsupported");
                }
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                Dictionary<string, string> a = Attributes(line);
                long? length = null, offset = null;
                if (a.GetValueOrDefault("BYTERANGE")?.Split('@') is { } br)
                {
                    if (br.Length != 2 || !long.TryParse(br[0], out long l) || l <= 0 || !long.TryParse(br[1], out long o) || o < 0 || o > long.MaxValue - l)
                        SetUnsupported(media, "An initialization byte range needs a valid explicit offset.");
                    else { length = l; offset = o; }
                }
                if (!a.TryGetValue("URI", out string? mapUrl)
                    || !TryResolveHttpUri(baseUri, mapUrl, out string resolvedMapUrl))
                    SetUnsupported(media, "Initialization has an invalid or missing URI.");
                else map = new HlsMap(resolvedMapUrl, length, offset, key);
                if (key is { Iv: null }) SetUnsupported(media, "Encrypted initialization requires an explicit AES IV.", "key");
            }
            else if (!line.StartsWith('#'))
            {
                if (!TryResolveHttpUri(baseUri, line, out string resource))
                {
                    SetUnsupported(media, "A media segment address is invalid.");
                    break;
                }
                if (media.Segments.Count >= 50_000) { SetUnsupported(media, "The playlist exceeds the segment limit."); break; }
                if (duration <= 0 || !double.IsFinite(duration)) SetUnsupported(media, "A segment has no valid finite duration.");
                if (implicitRange && (previousResource != resource || nextOffset is null))
                    SetUnsupported(media, "An implicit byte range must follow a range of the same resource.");
                if (sequence == long.MaxValue)
                {
                    SetUnsupported(media, "The media sequence number overflows.");
                    break;
                }
                if (rangeLength is long rl && rangeOffset is long ro)
                {
                    if (ro > long.MaxValue - rl) SetUnsupported(media, "A byte range overflows.");
                    else nextOffset = ro + rl;
                }
                else nextOffset = null;
                media.Segments.Add(new HlsSegment(resource, duration, rangeLength, rangeOffset, key, map, sequence++)
                {
                    DiscontinuityBefore = discontinuityBefore,
                    IsGap = gap,
                    DiscontinuitySequence = discontinuitySequence
                });
                previousResource = resource;
                rangeLength = rangeOffset = null; implicitRange = false;
                discontinuityBefore = gap = false;
                duration = 0;
            }
        }
        return media;
    }

    public static bool IsDrmSessionKey(string line)
    {
        Dictionary<string, string> attributes = Attributes(line);
        return attributes.TryGetValue("KEYFORMAT", out string? format)
            && !string.Equals(format, "identity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveHttpUri(Uri baseUri, string value, out string absoluteUri)
    {
        absoluteUri = "";
        if (!Uri.TryCreate(baseUri, value, out Uri? resolved)
            || !UrlPolicy.TryParse(resolved.AbsoluteUri, out _, out _))
            return false;

        absoluteUri = resolved.AbsoluteUri;
        return true;
    }

    private static void SetUnsupported(HlsMedia media, string reason, string code = "unsupported")
    {
        if (media.Unsupported is not null && code != "drm") return;
        media.Unsupported = reason;
        media.UnsupportedReasonCode = code;
    }

    private static byte[]? ParseIv(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        text = text.PadLeft(32, '0');
        return text.Length == 32 && text.All(Uri.IsHexDigit) ? Convert.FromHexString(text) : null;
    }

    /// <summary>Splits an attribute list, honouring quoted values that contain commas.</summary>
    public static Dictionary<string, string> Attributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string list = line[(line.IndexOf(':') + 1)..];
        int i = 0;
        while (i < list.Length)
        {
            int eq = list.IndexOf('=', i);
            if (eq < 0) break;
            string name = list[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < list.Length && list[i] == '"')
            {
                int close = list.IndexOf('"', i + 1);
                if (close < 0) close = list.Length;
                value = list[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                int comma = list.IndexOf(',', i);
                if (comma < 0) comma = list.Length;
                value = list[i..comma];
                i = comma;
            }
            if (i < list.Length && list[i] == ',') i++;
            result[name] = value;
        }
        return result;
    }
}
