using System.Text.Json;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

// Reads declared player configuration, not arbitrary JSON URLs. Multiple distinct
// players are ambiguous; repeated copies of one configuration are not.
internal sealed class PlayerConfigDiscovery
{
    private const int MaxDepth = 8, MaxSources = 24;
    private int _remaining = 512;
    private bool _incomplete;
    private readonly List<List<MediaCandidate>> _groups = [];

    internal bool HasEvidence => _incomplete || _groups.Count != 0;
    internal IReadOnlyList<MediaCandidate> UniqueGroup =>
        !_incomplete && _groups.Count == 1 ? _groups[0] : [];

    internal void Scan(JsonElement root, Uri baseUri, bool explicitPlayer = false) => Walk(root, baseUri, 0, true, explicitPlayer);

    private void Walk(JsonElement node, Uri baseUri, int depth, bool sourceList, bool player)
    {
        if (_groups.Count >= 2 || _incomplete || node.ValueKind != JsonValueKind.Object) return;
        if (depth > MaxDepth || --_remaining < 0) { _incomplete = true; return; }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (--_remaining < 0) { _incomplete = true; return; }
            string name = Normalize(property.Name);
            if (name.Length == 0 || !Relevant(name)) continue;
            // Conflicting spellings of the same field are not evidence of a unique source.
            if (!fields.TryAdd(name, property.Value))
            {
                _incomplete = true;
                return;
            }
        }

        if (sourceList)
        {
            var group = new List<MediaCandidate>();
            foreach (string key in new[] { "sources", "videosources", "audiosources" })
                if (fields.TryGetValue(key, out JsonElement list))
                    foreach (var entry in list.Items().Take(MaxSources))
                    {
                        if (group.Count >= MaxSources) break;
                        Add(group, baseUri, entry.ValueKind == JsonValueKind.String ? entry.GetString() : null,
                            null, null, null, null);
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        var descriptor = Descriptor(entry);
                        Add(group, baseUri, descriptor.Url, descriptor.Mime, descriptor.Width, descriptor.Height, descriptor.Label);
                    }
            if (player)
            {
                // Singular sources are only meaningful inside an explicit player object.
                string? mime = FirstString(fields, "mimetype", "contenttype", "type", "encodingformat");
                foreach (string key in new[] { "src", "file", "source", "contenturl", "videourl", "audiourl",
                             "playbackurl", "streamurl", "manifesturl", "hlsurl", "dashurl", "mp4url" })
                {
                    string? value = FirstString(fields, key);
                    string? hint = key switch
                    {
                        "hlsurl" => "application/vnd.apple.mpegurl",
                        "dashurl" => "application/dash+xml",
                        "mp4url" => "video/mp4",
                        "audiourl" => mime ?? "audio/*",
                        _ => mime
                    };
                    Add(group, baseUri, value, hint, Int(fields, "width"), Int(fields, "height"), FirstString(fields, "label"));
                    if (key == "source" && fields.TryGetValue(key, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                    {
                        var descriptor = Descriptor(nested);
                        Add(group, baseUri, descriptor.Url, descriptor.Mime, descriptor.Width, descriptor.Height, descriptor.Label);
                    }
                }
                // A generic "url" field is not necessarily media; require a media MIME.
                if (mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)))
                    Add(group, baseUri, FirstString(fields, "url"), mime, Int(fields, "width"), Int(fields, "height"), FirstString(fields, "label"));
            }
            if (group.Count > 0)
            {
                var urls = group.Select(c => c.Url.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
                if (!_groups.Any(existing => existing.Count == urls.Count && existing.All(c => urls.Contains(c.Url.AbsoluteUri))))
                    _groups.Add(group);
            }
        }

        foreach (var (key, value) in fields)
        {
            bool namedPlayer = IsPlayer(key);
            if (namedPlayer || IsWrapper(key))
                Walk(value, baseUri, depth + 1, namedPlayer || player, namedPlayer || player);
        }
    }

    private static (string? Url, string? Mime, int? Width, int? Height, string? Label) Descriptor(JsonElement entry)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in entry.EnumerateObject().Take(64))
        {
            string name = Normalize(property.Name);
            if (Relevant(name) && !fields.TryAdd(name, property.Value)) return default;
        }
        return (FirstString(fields, "src", "file", "url", "contenturl"),
            FirstString(fields, "mimetype", "contenttype", "type", "encodingformat"),
            Int(fields, "width"), Int(fields, "height"), FirstString(fields, "label"));
    }

    private static void Add(List<MediaCandidate> group, Uri baseUri, string? value, string? mime, int? width, int? height, string? label)
    {
        if (group.Count >= MaxSources || value is not { Length: > 0 and <= 4096 }) return;
        Uri? uri = UrlUtil.Resolve(baseUri, value);
        if (uri is null || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return;
        uri = UrlUtil.WithoutFragment(uri);
        CandidateKind kind = MediaCandidate.Classify(uri, mime);
        if (kind == CandidateKind.Unknown || group.Any(c => c.Url == uri)) return;
        group.Add(new MediaCandidate(uri, kind, mime,
            width is > 0 and <= 16384 ? width : null, height is > 0 and <= 16384 ? height : null,
            Label: label is { Length: > 0 } ? TextUtil.Clip(label, 200) : null));
    }

    private static string? FirstString(Dictionary<string, JsonElement> fields, params string[] keys)
    {
        foreach (string key in keys)
            if (fields.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text) return text;
        return null;
    }

    private static int? Int(Dictionary<string, JsonElement> fields, string key) =>
        fields.TryGetValue(key, out JsonElement value) ? value.Int() : null;
    private static string Normalize(string key) => key.Length > 64 ? "" :
        key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    private static bool IsPlayer(string key) =>
        key is "player" or "playerconfig" or "videoconfig" or "video" or "audio" or "media" or "playback";
    private static bool IsWrapper(string key) => key is "props" or "pageprops" or "data" or "config" or "state";
    private static bool Relevant(string key) => IsPlayer(key) || IsWrapper(key) || key is
        "sources" or "videosources" or "audiosources" or "src" or "file" or "url" or "source" or "contenturl"
        or "videourl" or "audiourl" or "playbackurl" or "streamurl" or "manifesturl" or "hlsurl" or "dashurl" or "mp4url"
        or "mimetype" or "contenttype" or "type" or "encodingformat" or "width" or "height" or "label";
}
