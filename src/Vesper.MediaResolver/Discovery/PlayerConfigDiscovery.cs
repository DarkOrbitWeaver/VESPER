using System.Text.Json;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

/// <summary>
/// Universal, site-agnostic hydration JSON &amp; player-config graph extractor.
/// Walks arbitrary SPA state trees (__NEXT_DATA__, __NUXT_DATA__, Apollo, Relay, Redux, inline player configs)
/// in linear O(N) time with target-URL slug/ID affinity scoring so unseen sites and multi-item hydration payloads
/// resolve cleanly without site-specific adapters.
/// </summary>
internal sealed class PlayerConfigDiscovery
{
    private const int MaxDepth = 18;
    private const int MaxSourcesPerGroup = 24;
    private const int MaxGroups = 32;
    private int _remaining = 32_768;
    private bool _incomplete;
    private int _unresolvedAffinity;

    private sealed record ScoredGroup(List<MediaCandidate> Candidates, int Affinity, string? Title, double? DurationSeconds, Uri? Thumbnail, List<SubtitleTrack> Subtitles);
    private readonly List<ScoredGroup> _groups = [];

    internal bool HasEvidence => _groups.Count > 0 || _incomplete || _unresolvedAffinity > 0;

    internal string? BestTitle => PickBestGroup()?.Title;
    internal double? BestDurationSeconds => PickBestGroup()?.DurationSeconds;
    internal Uri? BestThumbnail => PickBestGroup()?.Thumbnail;
    internal IReadOnlyList<SubtitleTrack> BestSubtitles => PickBestGroup()?.Subtitles ?? [];

    internal IReadOnlyList<MediaCandidate> BestCandidates => PickBestGroup()?.Candidates ?? [];
    internal IReadOnlyList<MediaCandidate> UniqueGroup => BestCandidates;

    private ScoredGroup? PickBestGroup()
    {
        if (_incomplete || _groups.Count == 0) return null;
        var ordered = _groups.OrderByDescending(group => group.Affinity).ToArray();
        if (_unresolvedAffinity > 0 && ordered[0].Affinity <= _unresolvedAffinity) return null;
        return ordered.Length == 1 || ordered[0].Affinity > ordered[1].Affinity ? ordered[0] : null;
    }

    private static HashSet<string> ExtractPageTokens(Uri pageUri)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string seg in pageUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string decoded = Uri.UnescapeDataString(seg);
            if (decoded.Length is >= 5 and <= 80 && !IsGenericPathSegment(decoded))
                tokens.Add(decoded);
        }
        if (!string.IsNullOrEmpty(pageUri.Query))
        {
            foreach (string pair in pageUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0 || eq + 1 >= pair.Length) continue;
                string key = pair[..eq].ToLowerInvariant();
                string val = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (key is "v" or "id" or "video" or "video_id" or "clip" or "bvid" or "aid" or "shortcode" or "item_id"
                    && val.Length is >= 4 and <= 80)
                    tokens.Add(val);
            }
        }
        return tokens;
    }

    private static bool IsGenericPathSegment(string value) =>
        value.ToLowerInvariant() is "watch" or "video" or "videos" or "reel" or "reels" or "shorts" or "embed"
            or "player" or "status" or "posts" or "post" or "comments" or "media" or "stream" or "episodes" or "episode";

    internal void Scan(JsonElement root, Uri baseUri, bool explicitPlayer = false)
    {
        HashSet<string> pageTokens = ExtractPageTokens(baseUri);
        Walk(root, baseUri, pageTokens, depth: 0, inPlayerContext: explicitPlayer, inheritedAffinity: 0);
    }

    private void Walk(JsonElement node, Uri baseUri, HashSet<string> pageTokens, int depth, bool inPlayerContext, int inheritedAffinity)
    {
        if (_groups.Count >= MaxGroups || depth > MaxDepth || --_remaining <= 0)
        {
            _incomplete = true;
            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement item in node.EnumerateArray())
            {
                if (++idx > 64 || _remaining <= 0)
                {
                    _incomplete = true;
                    break;
                }
                Walk(item, baseUri, pageTokens, depth + 1, inPlayerContext, inheritedAffinity);
            }
            return;
        }

        if (node.ValueKind != JsonValueKind.Object) return;

        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        int localAffinity = inheritedAffinity;
        string? title = null;
        double? duration = null;
        Uri? thumbnail = null;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (--_remaining <= 0) { _incomplete = true; return; }
            string norm = Normalize(prop.Name);
            if (norm.Length > 0 && !props.TryAdd(norm, prop.Value)
                && (DirectMediaKeys.Contains(norm) || StreamArrayKeys.Contains(norm) || norm is "src" or "url" or "file")
                && props[norm].GetRawText() != prop.Value.GetRawText())
            {
                _incomplete = true;
                return;
            }

            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 and <= 300 } strVal)
            {
                if (norm is "id" or "videoid" or "shortcode" or "code" or "slug" or "awemeid" or "bvid" or "mediaid" or "clipid" or "postid" or "itemid")
                {
                    if (pageTokens.Contains(strVal)) localAffinity += 30;
                }
                else if (norm is "title" or "name" or "caption" or "desc")
                {
                    title ??= strVal;
                }
                else if (norm is "thumbnail" or "thumbnailurl" or "poster" or "cover" or "displayurl" or "pic")
                {
                    thumbnail ??= UrlUtil.Resolve(baseUri, strVal);
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Number && norm is "id" or "videoid" or "mediaid" or "clipid" or "postid" or "itemid")
            {
                // Preserve exact decimal IDs, even beyond JavaScript's safe integer range.
                string id = prop.Value.GetRawText();
                if (id.Length is >= 4 and <= 80 && id.All(char.IsAsciiDigit) && pageTokens.Contains(id))
                    localAffinity += 30;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Number && norm is "duration" or "videoduration" or "lengthseconds" or "durationms")
            {
                if (prop.Value.TryGetDouble(out double d) && double.IsFinite(d) && d > 0)
                    duration ??= norm == "durationms" ? d / 1000.0 : d;
            }
        }

        var group = new List<MediaCandidate>();
        var subs = new List<SubtitleTrack>();

        // 1. Array-valued stream collections (sources, video_versions,progressive, dash/hls lists, formats, renditions)
        foreach (string listKey in StreamArrayKeys)
        {
            if (!props.TryGetValue(listKey, out JsonElement list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (group.Count >= MaxSourcesPerGroup) break;
                if (entry.ValueKind == JsonValueKind.String)
                    AddCandidate(group, baseUri, entry.GetString(), HintMime(listKey, null), null, null, null, duration);
                else if (entry.ValueKind == JsonValueKind.Object)
                    ExtractStreamObject(group, baseUri, entry, listKey, duration);
            }
        }

        // 2. Direct stream/manifest keys on player/video/media objects
        string? declaredMime = FirstString(props, "mimetype", "contenttype", "type", "encodingformat", "format");
        int? objWidth = Int(props, "width");
        int? objHeight = Int(props, "height");
        string? objLabel = FirstString(props, "label", "quality", "qualitylabel", "gearname");

        foreach (string urlKey in DirectMediaKeys)
        {
            if (!props.TryGetValue(urlKey, out JsonElement val)) continue;
            string? hint = HintMime(urlKey, declaredMime);
            if (val.ValueKind == JsonValueKind.String)
            {
                AddCandidate(group, baseUri, val.GetString(), hint, objWidth, objHeight, objLabel, duration);
            }
            else if (val.ValueKind == JsonValueKind.Object)
            {
                ExtractStreamObject(group, baseUri, val, urlKey, duration);
                // Handle nested UrlList / url_list (TikTok / ByteDance / generic CDN mirror objects)
                foreach (JsonProperty sub in val.EnumerateObject())
                {
                    if (Normalize(sub.Name) is "urllist" or "urls" or "mirrors" or "backupurl" && sub.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement u in sub.Value.EnumerateArray())
                            if (u.ValueKind == JsonValueKind.String)
                                AddCandidate(group, baseUri, u.GetString(), hint ?? "video/mp4", objWidth, objHeight, objLabel, duration);
                    }
                }
            }
        }

        // Generic "url" / "src" when accompanied by media MIME or explicit player context + dimensions/bitrate
        if ((inPlayerContext || IsMediaMime(declaredMime)) && FirstString(props, "src", "file", "url") is { } genericUrl)
        {
            if (IsMediaMime(declaredMime) || MediaCandidate.Classify(UrlUtil.Resolve(baseUri, genericUrl) ?? baseUri, declaredMime) != CandidateKind.Unknown)
                AddCandidate(group, baseUri, genericUrl, declaredMime, objWidth, objHeight, objLabel, duration);
        }

        // 3. Subtitles / captions arrays
        foreach (string subKey in new[] { "subtitles", "captions", "tracks", "texttracks", "subtitleinfos" })
        {
            if (!props.TryGetValue(subKey, out JsonElement subList) || subList.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement track in subList.EnumerateArray())
            {
                if (subs.Count >= 16 || track.ValueKind != JsonValueKind.Object) continue;
                var subProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in track.EnumerateObject()) subProps.TryAdd(Normalize(p.Name), p.Value);
                string? kind = FirstString(subProps, "kind", "type");
                if (kind is not null && kind.ToLowerInvariant() is not ("subtitles" or "captions" or "subtitle" or "caption" or "asr"))
                    continue;
                string? subUrlText = FirstString(subProps, "src", "url", "file", "vtturl", "webvtturl");
                if (UrlUtil.Resolve(baseUri, subUrlText) is { Scheme: "http" or "https" } subUri)
                {
                    string lang = TextUtil.NormalizeLang(FirstString(subProps, "srclang", "lang", "language", "languagecode", "locale"));
                    string? label = FirstString(subProps, "label", "name", "languagename");
                    string fmt = FirstString(subProps, "format") ?? UrlUtil.Extension(subUri) ?? "vtt";
                    subs.Add(new SubtitleTrack(subUri, lang, label, fmt));
                }
            }
        }

        // An explicitly matched program with an unusable declared source must
        // not silently fall back to an unrelated neighbouring program.
        if (group.Count == 0 && localAffinity > inheritedAffinity
            && (DirectMediaKeys.Any(props.ContainsKey) || StreamArrayKeys.Any(props.ContainsKey)))
            _unresolvedAffinity = Math.Max(_unresolvedAffinity, localAffinity);

        if (group.Count > 0)
        {
            int score = localAffinity;
            var urlSet = group.Select(c => c.Url.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
            int existingIdx = _groups.FindIndex(g => g.Candidates.Count == urlSet.Count && g.Candidates.All(c => urlSet.Contains(c.Url.AbsoluteUri)));
            if (existingIdx >= 0)
            {
                if (score > _groups[existingIdx].Affinity)
                    _groups[existingIdx] = new ScoredGroup(group, score, title ?? _groups[existingIdx].Title, duration ?? _groups[existingIdx].DurationSeconds, thumbnail ?? _groups[existingIdx].Thumbnail, subs);
            }
            else
            {
                _groups.Add(new ScoredGroup(group, score, title, duration, thumbnail, subs));
            }
        }

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
            string norm = Normalize(prop.Name);
            if (IsIgnoredBranch(norm) || StreamArrayKeys.Contains(norm) || DirectMediaKeys.Contains(norm)) continue;
            bool childPlayer = inPlayerContext || IsPlayerKey(norm);
            int childAffinity = localAffinity + (pageTokens.Contains(prop.Name) ? 25 : 0);
            Walk(prop.Value, baseUri, pageTokens, depth + 1, childPlayer, childAffinity);
        }
    }

    private static void ExtractStreamObject(List<MediaCandidate> group, Uri baseUri, JsonElement obj, string parentKey, double? fallbackDuration)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
            fields.TryAdd(Normalize(p.Name), p.Value);

        string? mime = HintMime(parentKey, FirstString(fields, "mimetype", "contenttype", "type", "encodingformat", "format"));
        string? url = FirstString(fields, "src", "file", "url", "contenturl", "baseurl", "playurl", "hlsurl", "dashurl", "mp4url", "streamurl", "playbackurl");
        int? width = Int(fields, "width");
        int? height = Int(fields, "height");
        long? bitrate = Long(fields, "bitrate", "bandwidth", "tbr");
        string? label = FirstString(fields, "label", "quality", "qualitylabel", "gearname");
        AddCandidate(group, baseUri, url, mime, width, height, label, fallbackDuration, bitrate);
    }

    private static void AddCandidate(List<MediaCandidate> group, Uri baseUri, string? value, string? mime, int? width, int? height,
        string? label, double? duration = null, long? bitrate = null)
    {
        if (group.Count >= MaxSourcesPerGroup || value is not { Length: > 0 and <= 4096 }) return;
        string cleaned = value.Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
        Uri? uri = UrlUtil.Resolve(baseUri, cleaned);
        if (uri is null || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return;
        uri = UrlUtil.WithoutFragment(uri);
        if (IsAdOrTrackingHost(uri.Host)) return;
        CandidateKind kind = MediaCandidate.Classify(uri, mime);
        if (kind == CandidateKind.Unknown)
        {
            if (IsMediaMime(mime)) kind = CandidateKind.Direct;
            else return;
        }
        if (group.Any(c => c.Url == uri)) return;
        group.Add(new MediaCandidate(
            uri,
            kind,
            mime,
            width is > 0 and <= 16384 ? width : null,
            height is > 0 and <= 16384 ? height : null,
            bitrate is > 0 and < 10_000_000_000 ? bitrate : null,
            duration is > 0 and < 8_640_000 ? duration : null,
            label is { Length: > 0 } ? TextUtil.Clip(label, 120) : null));
    }

    internal static bool IsAdOrTrackingHost(string host)
    {
        string h = host.ToLowerInvariant();
        return h.Contains("doubleclick.") || h.Contains("googlesyndication.") || h.Contains("googleadservices.")
            || h.Contains("imasdk.googleapis.") || h.StartsWith("ads.") || h.Contains(".adnxs.");
    }

    private static string? HintMime(string key, string? declared) => key switch
    {
        "hls" or "hlsurl" or "hlsmanifesturl" or "m3u8" or "m3u8url" => "application/vnd.apple.mpegurl",
        "dash" or "dashurl" or "dashmanifesturl" or "mpd" or "mpdurl" => "application/dash+xml",
        "mp4" or "mp4url" or "videoversions" or "playaddr" or "downloadaddr" or "progressive" => declared ?? "video/mp4",
        "videourl" => declared ?? "video/*",
        "audiourl" or "audiosources" => declared ?? "audio/mp4",
        _ => declared
    };

    private static bool IsMediaMime(string? mime) =>
        mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] StreamArrayKeys =
    [
        "sources", "videosources", "audiosources", "videoversions", "progressive", "formats",
        "adaptiveformats", "renditions", "streams", "files", "bitrateinfo", "durl"
    ];

    private static readonly string[] DirectMediaKeys =
    [
        "contenturl", "videourl", "audiourl", "playbackurl", "streamurl", "manifesturl",
        "hlsurl", "hlsmanifesturl", "dashurl", "dashmanifesturl", "mp4url", "m3u8url", "mpdurl",
        "playaddr", "downloadaddr", "fallbackurl"
    ];

    private static bool IsPlayerKey(string norm) => norm is
        "player" or "playerconfig" or "videoconfig" or "video" or "audio" or "media" or "playback"
        or "streamingdata" or "itemstruct" or "clip" or "episode" or "shortcode_media" or "xdtshortcode_media";

    private static bool IsIgnoredBranch(string norm) => norm is
        "comments" or "related" or "recommendations" or "suggested" or "ads" or "adbreaks" or "advertisements"
        or "translations" or "i18n" or "locales" or "stylesheets" or "analytics" or "telemetry";

    private static string? FirstString(Dictionary<string, JsonElement> fields, params string[] keys)
    {
        foreach (string key in keys)
            if (fields.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text) return text;
        return null;
    }

    private static int? Int(Dictionary<string, JsonElement> fields, string key) =>
        fields.TryGetValue(key, out JsonElement value) ? value.Int() : null;

    private static long? Long(Dictionary<string, JsonElement> fields, params string[] keys)
    {
        foreach (string key in keys)
            if (fields.TryGetValue(key, out JsonElement value) && value.Long() is { } l) return l;
        return null;
    }

    private static string Normalize(string key) => key.Length > 64 ? "" :
        key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
