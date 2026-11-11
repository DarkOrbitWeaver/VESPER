using System.Globalization;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Protocols.Hls;

/// <summary>RFC 8216 attribute-list tokenizer (handles quoted values containing commas).</summary>
public static class HlsAttributes
{
    public static Dictionary<string, string> Parse(string list)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0, n = list.Length;
        while (i < n)
        {
            while (i < n && (list[i] == ',' || char.IsWhiteSpace(list[i]))) i++;
            int ks = i;
            while (i < n && list[i] != '=' && list[i] != ',') i++;
            if (i >= n || list[i] != '=') { continue; }
            string key = list[ks..i].Trim();
            i++; // '='
            string value;
            if (i < n && list[i] == '"')
            {
                int end = list.IndexOf('"', i + 1);
                if (end < 0) end = n;
                value = list.Substring(i + 1, end - i - 1);
                i = Math.Min(n, end + 1);
            }
            else
            {
                int vs = i;
                while (i < n && list[i] != ',') i++;
                value = list[vs..i].Trim();
            }
            if (key.Length > 0) d[key] = value;
        }
        return d;
    }
}

/// <summary>
/// Parses master and media playlists. Hostile input is expected: line count and segment count are bounded and
/// every URI is resolved through <see cref="UrlUtil.Resolve"/> (non-http(s) schemes and credentials are rejected).
/// </summary>
public static class HlsParser
{
    private const int MaxLines = 600_000;

    public static HlsParseResult Parse(string text, Uri baseUri, int maxSegments = 100_000)
    {
        string head = text.Length > 64 ? text[..64] : text;
        if (head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal) == false)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Not an HLS playlist: missing #EXTM3U"));

        var master = new HlsMasterPlaylist();
        var media = new HlsMediaPlaylist();

        Dictionary<string, string>? pendingStreamInf = null;
        double pendingDuration = -1;
        ByteRange? pendingRange = null;
        bool pendingDiscontinuity = false;
        HlsKey? currentKey = null;
        HlsMap? currentMap = null;
        long nextOffset = 0;
        Uri? lastRangeUri = null;
        int lines = 0;

        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            if (++lines > MaxLines) throw new MediaResolverException(MediaErrors.TooLarge("Playlist has too many lines"));
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '#')
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                    pendingStreamInf = HlsAttributes.Parse(line["#EXT-X-STREAM-INF:".Length..]);
                else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                    AddRendition(master, HlsAttributes.Parse(line["#EXT-X-MEDIA:".Length..]), baseUri);
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    string d = line["#EXTINF:".Length..];
                    int comma = d.IndexOf(',');
                    if (comma >= 0) d = d[..comma];
                    pendingDuration = double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) && dur >= 0 ? dur : 0;
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                    pendingRange = ParseRangeToken(line["#EXT-X-BYTERANGE:".Length..], ref nextOffset, lastRangeUri, null);
                else if (line == "#EXT-X-DISCONTINUITY")
                {
                    pendingDiscontinuity = true;
                    media.HasDiscontinuities = true;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    currentKey = ParseKey(HlsAttributes.Parse(line["#EXT-X-KEY:".Length..]), baseUri);
                    if (currentKey.Protection > media.Protection) media.Protection = currentKey.Protection;
                }
                else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                {
                    var k = ParseKey(HlsAttributes.Parse(line["#EXT-X-SESSION-KEY:".Length..]), baseUri);
                    if (k.Protection > master.SessionProtection) master.SessionProtection = k.Protection;
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var a = HlsAttributes.Parse(line["#EXT-X-MAP:".Length..]);
                    var mapUri = a.TryGetValue("URI", out var mu) ? UrlUtil.Resolve(baseUri, mu) : null;
                    if (mapUri is null) throw new MediaResolverException(MediaErrors.UnsupportedManifest("EXT-X-MAP has an invalid URI"));
                    ByteRange? mr = null;
                    if (a.TryGetValue("BYTERANGE", out var brs)) { long dummy = 0; mr = ParseRangeToken(brs, ref dummy, null, mapUri); }
                    currentMap = new HlsMap(mapUri, mr);
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                    media.TargetDuration = double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var td) ? td : 0;
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                    media.MediaSequence = long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
                else if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal))
                    media.Version = int.TryParse(line["#EXT-X-VERSION:".Length..], out var ver) ? ver : 0;
                else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
                    media.PlaylistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim();
                else if (line == "#EXT-X-ENDLIST")
                    media.IsEndList = true;
                else if (line.StartsWith("#EXT-X-DEFINE", StringComparison.Ordinal))
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("EXT-X-DEFINE variable substitution is not supported"));
                continue;
            }

            // URI line
            var uri = UrlUtil.Resolve(baseUri, line)
                ?? throw new MediaResolverException(MediaErrors.UnsupportedManifest("Playlist contains a non-http(s) or invalid URI"));

            if (pendingStreamInf is not null)
            {
                master.Variants.Add(BuildVariant(pendingStreamInf, uri));
                pendingStreamInf = null;
                if (master.Variants.Count > 10_000) throw new MediaResolverException(MediaErrors.TooLarge("Too many variants"));
            }
            else if (pendingDuration >= 0)
            {
                if (media.Segments.Count >= maxSegments)
                    throw new MediaResolverException(MediaErrors.TooLarge("Playlist exceeds the segment limit"));

                ByteRange? range = pendingRange;
                if (range is { } r)
                {
                    media.HasByteRanges = true;
                    // A length-only BYTERANGE continues the previous range of the same resource.
                    if (r.Offset < 0) range = new ByteRange(lastRangeUri == uri ? nextOffset : 0, r.Length);
                    nextOffset = range!.Value.Offset + range.Value.Length;
                    lastRangeUri = uri;
                }
                media.Segments.Add(new HlsSegment(uri, pendingDuration, range, pendingDiscontinuity,
                    currentKey is { Protection: not HlsProtection.None } ? currentKey : null, currentMap));
                media.TotalDuration += pendingDuration;
                pendingDuration = -1;
                pendingRange = null;
                pendingDiscontinuity = false;
            }
        }

        if (master.Variants.Count > 0 && media.Segments.Count > 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Playlist mixes variant and segment entries"));
        if (master.Variants.Count == 0 && media.Segments.Count == 0 && master.Renditions.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Playlist contains no media"));

        return master.Variants.Count > 0 || media.Segments.Count == 0
            ? new HlsParseResult { Master = master }
            : new HlsParseResult { Media = media };
    }

    // "n[@o]" -> ByteRange; offset -1 marks "continue from previous range" (resolved by the caller).
    private static ByteRange? ParseRangeToken(string token, ref long nextOffset, Uri? lastUri, Uri? explicitUri)
    {
        var parts = token.Trim().Split('@');
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len) || len <= 0) return null;
        if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var off) && off >= 0)
            return new ByteRange(off, len);
        return explicitUri is not null ? new ByteRange(0, len) : new ByteRange(-1, len);
    }

    private static HlsKey ParseKey(Dictionary<string, string> a, Uri baseUri)
    {
        string method = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
        string? keyFormat = a.GetValueOrDefault("KEYFORMAT");
        a.TryGetValue("IV", out var iv);
        Uri? keyUri = null;
        bool hasUri = a.TryGetValue("URI", out var uriText);
        if (hasUri) keyUri = UrlUtil.Resolve(baseUri, uriText);

        HlsProtection p;
        if (method == "NONE") p = HlsProtection.None;
        else if (!string.Equals(keyFormat ?? "identity", "identity", StringComparison.OrdinalIgnoreCase)) p = HlsProtection.Drm;
        else if (keyUri is null) p = HlsProtection.Drm;           // missing or non-http (data:, skd:) key URI
        else p = method switch
        {
            "AES-128" => HlsProtection.Aes128,
            "SAMPLE-AES" => HlsProtection.SampleAes,
            _ => HlsProtection.Drm                                // SAMPLE-AES-CTR, SAMPLE-AES-CENC, unknown
        };
        return new HlsKey(method, keyUri, iv, keyFormat, p);
    }

    private static HlsVariant BuildVariant(Dictionary<string, string> a, Uri url)
    {
        int? width = null, height = null;
        if (a.TryGetValue("RESOLUTION", out var res))
        {
            var wh = res.Split('x', 'X');
            if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h) && w > 0 && h > 0 && w <= 16384 && h <= 16384)
            { width = w; height = h; }
        }
        return new HlsVariant(
            url,
            a.TryGetValue("BANDWIDTH", out var bw) && int.TryParse(bw, out var b) ? b : 0,
            a.TryGetValue("AVERAGE-BANDWIDTH", out var ab) && int.TryParse(ab, out var avg) ? avg : null,
            width, height,
            a.TryGetValue("FRAME-RATE", out var fr) && double.TryParse(fr, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null,
            a.GetValueOrDefault("CODECS"),
            a.GetValueOrDefault("AUDIO"),
            a.GetValueOrDefault("SUBTITLES"),
            a.GetValueOrDefault("VIDEO-RANGE"));
    }

    private static void AddRendition(HlsMasterPlaylist m, Dictionary<string, string> a, Uri baseUri)
    {
        if (!a.TryGetValue("TYPE", out var type) || !a.TryGetValue("GROUP-ID", out var group)) return;
        Uri? uri = a.TryGetValue("URI", out var u) ? UrlUtil.Resolve(baseUri, u) : null;
        m.Renditions.Add(new HlsRendition(
            type.ToUpperInvariant(), group, a.GetValueOrDefault("NAME"), a.GetValueOrDefault("LANGUAGE"), uri,
            string.Equals(a.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase),
            string.Equals(a.GetValueOrDefault("AUTOSELECT"), "YES", StringComparison.OrdinalIgnoreCase),
            a.GetValueOrDefault("CHANNELS"), a.GetValueOrDefault("CHARACTERISTICS")));
    }
}
