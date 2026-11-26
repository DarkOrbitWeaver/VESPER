using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols.Dash;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

internal sealed class PageMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public Uri? Thumbnail { get; set; }
    public TimeSpan? Duration { get; set; }
    public List<MediaCandidate> Candidates { get; } = new();
    public List<Uri> Embeds { get; } = new();
    public List<SubtitleTrack> Subtitles { get; } = new();
}

/// <summary>
/// Finds media references in a page without executing anything: HTML5 video/audio/source/track, Open Graph and Twitter
/// player meta, schema.org JSON-LD, iframes, and manifest URLs inside script text (linear-time regex).
/// </summary>
internal static class PageScanner
{
    // NonBacktracking guarantees linear-time matching on hostile script text.
    // A bounded fallback for inline player configuration; do not scan structured
    // JSON feeds or treat an arbitrary URL key as a playable source.
    private static readonly Regex ScriptMediaRegex = new(
        @"https?://[^\s""'<>\\]+?\.(?:mp4|m4v|webm|mov|m4a|mp3|ogg|opus|flac|wav|m3u8|mpd)(?:\?[^\s""'<>\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PlayerMediaKeyRegex = new(
        @"[""']?(?:file|video_url|videoUrl|hlsUrl|hls_url|dashUrl|dash_url|mp4Url|mp4_url|contentUrl|stream_url|streamUrl)[""']?\s*[:=]\s*[""']((?:https?:)?//[^""'\s<>]+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ManifestRegex = new(
        @"https?://[^\s""'<>\\]+?\.(?:m3u8|mpd)(?:\?[^\s""'<>\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static PageMetadata Scan(string html, Uri pageUrl, int maxCandidates) => new State(pageUrl, maxCandidates).Run(html);

    private sealed class State
    {
        private readonly PageMetadata _meta = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Uri _page;
        private readonly int _max;
        private Uri _base;
        private string? _ldTitle;
        private readonly PlayerConfigDiscovery _configs = new();
        private int _lazyBudget = 400;
        private readonly List<string> _scriptFallbacks = new();

        public State(Uri page, int max)
        {
            _page = page;
            _base = page;
            _max = max;
        }

        public PageMetadata Run(string html)
        {
            string? docTitle = null;
            var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ogVideos = new List<string>();
            var ogAudios = new List<string>();
            string? currentMedia = null;
            bool mutedLoop = false;

            foreach (var tag in HtmlScanner.Scan(html))
            {
                if (tag.IsClosing)
                {
                    if (tag.Name is "video" or "audio") currentMedia = null;
                    continue;
                }

                if (tag.Name is "div" or "a" or "span" && _lazyBudget-- > 0)
                    ScanLazyMedia(tag);
                switch (tag.Name)
                {
                    case "base":
                        _base = UrlUtil.Resolve(_page, tag.Attr("href")) ?? _base;
                        break;
                    case "title":
                        docTitle ??= Clean(tag.Body);
                        break;
                    case "meta":
                    {
                        string? key = tag.Attr("property") ?? tag.Attr("name") ?? tag.Attr("itemprop");
                        string? content = tag.Attr("content");
                        if (key is null) break;
                        if (key.Equals("contentURL", StringComparison.OrdinalIgnoreCase))
                            AddCandidate(content ?? tag.Attr("href"), tag.Attr("type"), null, null, null);
                        else if (key.Equals("embedURL", StringComparison.OrdinalIgnoreCase))
                            AddEmbed(content ?? tag.Attr("href"));
                        else if (key.Equals("thumbnailUrl", StringComparison.OrdinalIgnoreCase))
                            _meta.Thumbnail ??= UrlUtil.Resolve(_base, content);
                        else if (key.Equals("duration", StringComparison.OrdinalIgnoreCase) && _meta.Duration is null && content is not null)
                            _meta.Duration = DashParser.ParseIsoDuration(content);
                        if (content is null) break;
                        key = key.ToLowerInvariant();
                        if (key is "og:video" or "og:video:url" or "og:video:secure_url" or "twitter:player:stream") ogVideos.Add(content);
                        else if (key is "og:audio" or "og:audio:url" or "og:audio:secure_url") ogAudios.Add(content);
                        else metas.TryAdd(key, content);
                        break;
                    }
                    case "video":
                    case "audio":
                    {
                        currentMedia = tag.Name;
                        mutedLoop = tag.Name == "video" && tag.Attr("muted") is not null && tag.Attr("autoplay") is not null
                            && tag.Attr("loop") is not null && tag.Attr("controls") is null;
                        string? src = tag.Attr("src");
                        string? mime = tag.Name == "audio" ? "audio/*" : tag.Attr("type");
                        AddCandidate(src ?? tag.Attr("data-src") ?? tag.Attr("data-video-src")
                            ?? tag.Attr("data-hls") ?? tag.Attr("data-dash") ?? tag.Attr("data-mp4"),
                            mime, Dim(tag.Attr("width")), Dim(tag.Attr("height")), null, mutedLoop && src is not null);
                        if (string.IsNullOrWhiteSpace(src) && tag.Attr("data-setup") is { Length: > 2 and <= 8192 } setup)
                        {
                            using var config = JsonExt.TryParse(setup);
                            if (config is not null)
                                foreach (var source in config.RootElement.P("sources").Items().Take(6))
                                    AddCandidate(source.P("src").Str(), source.P("type").Str() ?? mime, null, null, null);
                        }
                        if (_meta.Thumbnail is null && tag.Attr("poster") is { } poster) _meta.Thumbnail = UrlUtil.Resolve(_base, poster);
                        break;
                    }
                    case "source" when currentMedia is not null:
                        AddCandidate(tag.Attr("src") ?? tag.Attr("data-src"), tag.Attr("type") ?? (currentMedia == "audio" ? "audio/*" : null),
                            Dim(tag.Attr("width")), Dim(tag.Attr("height")), tag.Attr("label") ?? tag.Attr("title"),
                            mutedLoop && tag.Attr("src") is not null);
                        break;
                    case "track":
                    {
                        string kind = (tag.Attr("kind") ?? "subtitles").ToLowerInvariant();
                        var u = UrlUtil.Resolve(_base, tag.Attr("src"));
                        if (u is not null && kind is "subtitles" or "captions" && _meta.Subtitles.Count < 32)
                            _meta.Subtitles.Add(new SubtitleTrack(u, TextUtil.NormalizeLang(tag.Attr("srclang")), tag.Attr("label"), UrlUtil.Extension(u) ?? "vtt"));
                        break;
                    }
                    case "link":
                        if ((tag.Attr("rel") ?? "").Split(' ').Contains("preload", StringComparer.OrdinalIgnoreCase)
                            && tag.Attr("as") is "video" or "audio")
                            AddCandidate(tag.Attr("href"), tag.Attr("type"), null, null, null);
                        break;
                    case "iframe":
                        AddEmbed(tag.Attr("src") ?? tag.Attr("data-src"));
                        break;
                    case "script":
                        HandleScript(tag);
                        break;
                }
            }

            string? ogType = metas.GetValueOrDefault("og:video:type");
            foreach (var v in ogVideos)
            {
                if (ogType is not null && (ogType.Contains("html", StringComparison.OrdinalIgnoreCase) || ogType.Contains("flash", StringComparison.OrdinalIgnoreCase)))
                    AddEmbed(v);
                else
                    AddCandidate(v, ogType, Dim(metas.GetValueOrDefault("og:video:width")), Dim(metas.GetValueOrDefault("og:video:height")), null);
            }
            foreach (var a in ogAudios) AddCandidate(a, metas.GetValueOrDefault("og:audio:type") ?? "audio/*", null, null, null);

            if (_meta.Candidates.Count == 0)
            {
                foreach (MediaCandidate candidate in _configs.BestCandidates)
                    AddCandidate(candidate.Url.AbsoluteUri, candidate.MimeType, candidate.Width, candidate.Height, candidate.Label);
                foreach (SubtitleTrack sub in _configs.BestSubtitles)
                    if (_meta.Subtitles.Count < 32 && !_meta.Subtitles.Any(s => s.Url == sub.Url))
                        _meta.Subtitles.Add(sub);
            }
            if (_meta.Candidates.Count == 0 && !_configs.HasEvidence
                && _scriptFallbacks.Distinct(StringComparer.Ordinal).Take(2).Count() == 1)
                AddCandidate(_scriptFallbacks[0], null, null, null, null);
            _meta.Title = Clean(_ldTitle) ?? Clean(_configs.BestTitle) ?? Clean(metas.GetValueOrDefault("og:title")) ?? Clean(metas.GetValueOrDefault("twitter:title")) ?? Clean(docTitle);
            _meta.Description ??= Clean(metas.GetValueOrDefault("og:description")) ?? Clean(metas.GetValueOrDefault("description"));
            _meta.Thumbnail ??= _configs.BestThumbnail ?? UrlUtil.Resolve(_base, metas.GetValueOrDefault("og:image") ?? metas.GetValueOrDefault("twitter:image"));
            if (_meta.Duration is null && _configs.BestDurationSeconds is { } cfgDuration)
                _meta.Duration = TimeSpan.FromSeconds(cfgDuration);
            return _meta;
        }

        private void ScanLazyMedia(HtmlTag tag)
        {
            foreach (string attribute in new[] { "data-video-src", "data-video", "data-mp4", "data-hls", "data-dash", "data-file" })
            {
                var uri = UrlUtil.Resolve(_base, tag.Attr(attribute));
                if (uri is null || MediaCandidate.Classify(uri, tag.Attr("type")) == CandidateKind.Unknown) continue;
                if (_scriptFallbacks.Count < 16) _scriptFallbacks.Add(uri.AbsoluteUri);
            }
        }

        private void HandleScript(HtmlTag tag)
        {
            string? body = tag.Body;
            if (string.IsNullOrWhiteSpace(body) || body.Length > 1_500_000) return;

            if ((tag.Attr("type") ?? "").Contains("ld+json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonExt.TryParse(body);
                if (doc is not null) WalkLd(doc.RootElement, 0);
                return;
            }

            // JSON-only bootstrap data. No script evaluation, globals, API calls
            // or recursive crawling of URLs. A config must declare a source list
            // or an explicit video/audio source with a media type.
            string type = tag.Attr("type") ?? "";
            string id = tag.Attr("id") ?? "";
            bool explicitPlayer = id.Equals("player-config", StringComparison.OrdinalIgnoreCase)
                || id.Equals("video-config", StringComparison.OrdinalIgnoreCase)
                || id.Equals("playerConfig", StringComparison.OrdinalIgnoreCase);
            string? json = type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || tag.Attr("data-sjs") is not null
                || id is "__NEXT_DATA__" or "__NUXT_DATA__" or "__UNIVERSAL_DATA_FOR_REHYDRATION__" or "SIGI_STATE" or "__INITIAL_STATE__" ? body : null;
            if (json is null)
            {
                string trimmed = body.Trim();
                foreach (string prefix in new[] { "window.playerConfig =", "window.videoConfig =", "window.__INITIAL_STATE__ =", "window.__PRELOADED_STATE__ =", "window.__DATA__ =", "const playerConfig =", "var playerConfig =" })
                    if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    { json = trimmed[prefix.Length..].Trim().TrimEnd(';'); explicitPlayer = prefix.Contains("Config", StringComparison.Ordinal); break; }
            }
            if (json is not null)
            {
                using var config = JsonExt.TryParse(json);
                if (config is not null) _configs.Scan(config.RootElement, _base, explicitPlayer);
                // Do not also regex-scan structured JSON: that would bypass the
                // single-player requirement and include ads/recommendations.
                return;
            }

            if (_scriptFallbacks.Count < 16 && string.IsNullOrWhiteSpace(tag.Attr("src")))
            {
                string cleaned = WebUtility.HtmlDecode(body.Replace("\\/", "/", StringComparison.Ordinal)
                    .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                    .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase));
                foreach (var match in PlayerMediaKeyRegex.Matches(cleaned).Cast<Match>().Take(8))
                {
                    var uri = UrlUtil.Resolve(_base, match.Groups[1].Value);
                    if (uri is not null && _scriptFallbacks.Count < 16) _scriptFallbacks.Add(uri.AbsoluteUri);
                }
                foreach (var match in ScriptMediaRegex.Matches(cleaned).Cast<Match>().Take(8))
                    if (_scriptFallbacks.Count < 16) _scriptFallbacks.Add(match.Value);
            }

            if (body.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || body.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
            {
                string cleaned = body.Replace("\\/", "/", StringComparison.Ordinal)
                                     .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                                     .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
                int taken = 0;
                foreach (Match m in ManifestRegex.Matches(cleaned))
                {
                    AddCandidate(m.Value, null, null, null, null);
                    if (++taken >= 6) break;
                }
            }
        }

        private void WalkLd(JsonElement e, int depth)
        {
            if (depth > 6) return;
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray().Take(50)) WalkLd(x, depth + 1);
                return;
            }
            if (e.ValueKind != JsonValueKind.Object) return;

            if (e.P("@graph") is { } graph) WalkLd(graph, depth + 1);

            var typeEl = e.P("@type");
            IEnumerable<string?> types = typeEl is { ValueKind: JsonValueKind.Array }
                ? typeEl.Items().Select(t => t.Str())
                : new[] { typeEl.Str() };
            if (!types.Any(t => t is "VideoObject" or "AudioObject")) return;

            AddCandidate(e.P("contentUrl").Str(), e.P("encodingFormat").Str(), e.P("width").Int(), e.P("height").Int(), null);
            AddEmbed(e.P("embedUrl").Str());
            _ldTitle ??= e.P("name").Str();
            _meta.Description ??= e.P("description").Str();

            var thumb = e.P("thumbnailUrl");
            string? thumbStr = thumb is { ValueKind: JsonValueKind.Array } ? thumb.At(0).Str() : thumb.Str();
            if (thumbStr is null && thumb is { ValueKind: JsonValueKind.Object }) thumbStr = thumb.P("url").Str();
            if (thumbStr is not null) _meta.Thumbnail ??= UrlUtil.Resolve(_base, thumbStr);

            if (_meta.Duration is null && e.P("duration").Str() is { } iso) _meta.Duration = DashParser.ParseIsoDuration(iso);
        }

        private void AddCandidate(string? src, string? mime, int? w, int? h, string? label, bool mutedLoop = false)
        {
            if (_meta.Candidates.Count >= _max || string.IsNullOrWhiteSpace(src)) return;
            var u = UrlUtil.Resolve(_base, WebUtility.HtmlDecode(src));
            if (u is null) return;
            u = UrlUtil.WithoutFragment(u);
            if (!_seen.Add(u.AbsoluteUri)) return;
            string? cleanMime = string.IsNullOrWhiteSpace(mime) ? null : mime.Trim();
            _meta.Candidates.Add(new MediaCandidate(u, MediaCandidate.Classify(u, cleanMime), cleanMime, w, h, null, null, label, mutedLoop));
        }

        private void AddEmbed(string? src)
        {
            if (_meta.Embeds.Count >= 8) return;
            var u = UrlUtil.Resolve(_base, src);
            if (u is not null && !_meta.Embeds.Contains(u)) _meta.Embeds.Add(u);
        }

        private static int? Dim(string? s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 16384 ? v : null;

        private static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string t = WebUtility.HtmlDecode(s).Trim();
            return t.Length == 0 ? null : TextUtil.Clip(t.Replace('\n', ' ').Replace('\r', ' '), 300);
        }
    }
}
