using System.Globalization;

namespace Vesper.Engine;

public sealed record HlsVariant(string Uri, long Bandwidth, int Width, int Height, string? Codecs, bool NeedsSeparateAudio, bool AudioOnly);
public sealed record HlsKey(string? Uri, byte[]? Iv);
public sealed record HlsMap(string Uri, long? Length, long? Offset);
public sealed record HlsSegment(string Uri, double Duration, long? RangeLength, long? RangeOffset, HlsKey? Key, HlsMap? Map, long Sequence);

public sealed class HlsMedia
{
    public List<HlsSegment> Segments { get; } = [];
    public bool IsVod { get; set; }
    /// <summary>Set when the playlist uses protection VESPER will not (and must not) work around.</summary>
    public string? Unsupported { get; set; }
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
        bool hasAudioRendition = text.Split('\n').Any(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)
            && Attributes(l).GetValueOrDefault("TYPE", "").Equals("AUDIO", StringComparison.OrdinalIgnoreCase) && Attributes(l).ContainsKey("URI"));

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
            bool separateAudio = a.ContainsKey("AUDIO") && hasAudioRendition && !audioOnly;
            variants.Add(new HlsVariant(new Uri(baseUri, uri).AbsoluteUri, bandwidth, width, height, codecs, separateAudio, audioOnly));
        }
        return variants;
    }

    public static HlsMedia ParseMedia(string text, Uri baseUri)
    {
        var media = new HlsMedia();
        HlsKey? key = null;
        HlsMap? map = null;
        long sequence = 0;
        double duration = 0;
        long? rangeLength = null, rangeOffset = null, nextOffset = 0;

        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) media.IsVod = true;
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.Ordinal)) media.IsVod = true;
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                double.TryParse(line["#EXTINF:".Length..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                string[] parts = line["#EXT-X-BYTERANGE:".Length..].Split('@');
                if (long.TryParse(parts[0], out long len)) rangeLength = len;
                rangeOffset = parts.Length > 1 && long.TryParse(parts[1], out long off) ? off : nextOffset;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                Dictionary<string, string> a = Attributes(line);
                string method = a.GetValueOrDefault("METHOD", "NONE");
                string format = a.GetValueOrDefault("KEYFORMAT", "identity");
                if (method == "NONE") key = null;
                else if (method == "AES-128" && format == "identity")
                    key = new HlsKey(a.GetValueOrDefault("URI") is { } k ? new Uri(baseUri, k).AbsoluteUri : null, ParseIv(a.GetValueOrDefault("IV")));
                else media.Unsupported = $"The stream uses protection that VESPER does not support ({method}/{format}).";
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                Dictionary<string, string> a = Attributes(line);
                long? length = null, offset = null;
                if (a.GetValueOrDefault("BYTERANGE")?.Split('@') is { Length: > 0 } br && long.TryParse(br[0], out long l))
                { length = l; offset = br.Length > 1 && long.TryParse(br[1], out long o) ? o : 0; }
                map = new HlsMap(new Uri(baseUri, a["URI"]).AbsoluteUri, length, offset);
            }
            else if (!line.StartsWith('#'))
            {
                media.Segments.Add(new HlsSegment(new Uri(baseUri, line).AbsoluteUri, duration, rangeLength, rangeOffset, key, map, sequence++));
                if (rangeLength is long rl && rangeOffset is long ro) nextOffset = ro + rl;
                rangeLength = rangeOffset = null;
                duration = 0;
            }
        }
        return media;
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
