using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Rules;

public sealed record DeclarativeExtractorRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Domains { get; init; }
    public string? UrlPattern { get; init; }
    public IReadOnlyList<string> TitleJsonPaths { get; init; } = [];
    public IReadOnlyList<string> StreamJsonPaths { get; init; } = [];
    public IReadOnlyList<string> ManifestJsonPaths { get; init; } = [];
    public IReadOnlyList<string> HtmlStreamRegexes { get; init; } = [];
    public IReadOnlyList<string> ApiTemplates { get; init; } = [];
    public IReadOnlyList<string> AllowedCdnDomains { get; init; } = [];
    public bool RequireOriginHeader { get; init; }
}

public sealed record DeclarativeRulePack
{
    public const string CurrentSchema = "vesper.extractor-pack/1";

    public required string Schema { get; init; }
    public required int Version { get; init; }
    public required string PublishedAt { get; init; }
    public required IReadOnlyList<DeclarativeExtractorRule> Rules { get; init; }
}

/// <summary>
/// Hot-updatable, cryptographically verified declarative extractor rule-pack store.
/// Allows site parser healing out-of-band without shipping a new desktop binary.
/// </summary>
public sealed class RulePackStore
{
    private const int MaxPackBytes = 1024 * 1024;
    private static readonly Lazy<DeclarativeRulePack> Baseline = new(LoadEmbeddedBaseline);
    private sealed record SignedPack(string PayloadBase64, string SignatureBase64);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private DeclarativeRulePack? _active;
    private DateTimeOffset _lastDiskCheck = DateTimeOffset.MinValue;
    private readonly string? _directory;
    private readonly string? _pinnedKeySpki;

    public RulePackStore(string? directory = null, string? pinnedPublicKeySpki = null)
    {
        _directory = directory;
        _pinnedKeySpki = pinnedPublicKeySpki;
    }

    public DeclarativeRulePack Current
    {
        get
        {
            lock (_gate)
            {
                if (_directory is not null && _pinnedKeySpki is not null
                    && DateTimeOffset.UtcNow - _lastDiskCheck > TimeSpan.FromSeconds(30))
                    TryReloadFromDisk();
                return _active ?? Baseline.Value;
            }
        }
    }

    private DeclarativeRulePack? ReadSignedPack(string json)
    {
        if (string.IsNullOrWhiteSpace(_pinnedKeySpki) || _pinnedKeySpki.Length > 256
            || json.Length > 2 * MaxPackBytes) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedPack>(json, JsonOptions);
            if (envelope?.PayloadBase64 is not { Length: > 0 } || envelope.SignatureBase64 is not { Length: 88 })
                return null;
            byte[] payload = Convert.FromBase64String(envelope.PayloadBase64);
            byte[] key = Convert.FromBase64String(_pinnedKeySpki);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out int read);
            if (read != key.Length || ecdsa.KeySize != 256 || payload.Length > MaxPackBytes
                || !ecdsa.VerifyData(payload, Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256))
                return null;
            var pack = JsonSerializer.Deserialize<DeclarativeRulePack>(payload, JsonOptions);
            return pack is not null && ValidateRules(pack) ? pack : null;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool TryInstallSignedPack(string json)
    {
        if (ReadSignedPack(json) is not { } candidate) return false;
        lock (_gate)
        {
            if (candidate.Version <= Current.Version) return false;
            if (_directory is not null)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    string target = Path.Combine(_directory, "active-pack.json");
                    string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(tmp, json, Encoding.UTF8);
                        File.Move(tmp, target, overwrite: true);
                    }
                    finally { File.Delete(tmp); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return false; }
            }
            _active = candidate;
            return true;
        }
    }

    private void TryReloadFromDisk()
    {
        _lastDiskCheck = DateTimeOffset.UtcNow;
        string file = Path.Combine(_directory!, "active-pack.json");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length is <= 0 or > 2 * MaxPackBytes) return;
            var loaded = ReadSignedPack(File.ReadAllText(file, Encoding.UTF8));
            if (loaded is not null && loaded.Version > (_active ?? Baseline.Value).Version) _active = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool ValidateRules(DeclarativeRulePack pack)
    {
        if (pack.Schema != DeclarativeRulePack.CurrentSchema || pack.Version < 1 || pack.Rules is null || pack.Rules.Count > 256)
            return false;
        foreach (DeclarativeExtractorRule rule in pack.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64
                || rule.Domains is not { Count: > 0 and <= 16 }
                || rule.Domains.Any(host => Uri.CheckHostName(host) != UriHostNameType.Dns))
                return false;
            if (rule.UrlPattern is { Length: > 512 }) return false;
            if (rule.UrlPattern is not null)
            {
                try { _ = new Regex(rule.UrlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking); }
                catch { return false; }
            }
            if (rule.HtmlStreamRegexes is not { Count: <= 6 } || rule.ApiTemplates is not { Count: <= 3 }
                || rule.ApiTemplates.Any(value => value is not { Length: > 0 and <= 1024 }
                    || value.Contains("{url}", StringComparison.Ordinal)))
                return false;
            IReadOnlyList<string>?[] paths = [rule.TitleJsonPaths, rule.ManifestJsonPaths, rule.StreamJsonPaths];
            if (paths.Any(list => list is not { Count: <= 16 }
                || list.Any(path => path is not { Length: > 0 and <= 160 })))
                return false;
            foreach (string rx in rule.HtmlStreamRegexes)
            {
                if (rx is not { Length: > 0 and <= 512 }) return false;
                try { _ = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking); }
                catch { return false; }
            }
        }
        return true;
    }

    private static DeclarativeRulePack LoadEmbeddedBaseline()
    {
        using Stream? s = typeof(RulePackStore).Assembly.GetManifestResourceStream("Vesper.MediaResolver.Rules.default-extractor-pack.json");
        if (s is not null)
        {
            using var reader = new StreamReader(s, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<DeclarativeRulePack>(reader.ReadToEnd(), JsonOptions);
            if (parsed is not null && ValidateRules(parsed)) return parsed;
        }
        return new DeclarativeRulePack
        {
            Schema = DeclarativeRulePack.CurrentSchema,
            Version = 1,
            PublishedAt = "2026-01-01T00:00:00Z",
            Rules = []
        };
    }
}

/// <summary>
/// Executes hot-updatable declarative rules against matching sites, feeding all discovered streams
/// through <see cref="CandidateProcessor"/> so every URL is still HTTP/manifest verified.
/// </summary>
internal sealed class DeclarativeExtractorAdapter : IMediaSourceAdapter
{
    private readonly ResolverServices _svc;
    private readonly RulePackStore _store;
    private readonly GenericAdapter _generic;

    public DeclarativeExtractorAdapter(ResolverServices svc, RulePackStore store)
    {
        _svc = svc;
        _store = store;
        _generic = new GenericAdapter(svc);
    }

    public static AdapterDescriptor CreateDescriptor(RulePackStore store) => new(
        "declarative-pack",
        "Declarative Hot-Updatable Extractor Pack",
        uri => MatchRule(store.Current, uri) is not null,
        svc => new DeclarativeExtractorAdapter(svc, store),
        new AdapterCapabilities(
            Domains: ["*"],
            UrlPatterns: ["Signed declarative extractor rules (vesper.extractor-pack/1)"],
            SingleItem: true, Playlist: false, AudioOnly: true, Subtitles: true,
            Authentication: "Forwards browser-consented cookies and Origin headers per rule policy",
            KnownUnsupported: ["DRM streams"],
            Notes: "Hot-updatable without binary rebuilds."));

    public bool CanHandle(Uri url) => MatchRule(_store.Current, url) is not null;

    private static (DeclarativeExtractorRule Rule, Match? Match)? MatchRule(DeclarativeRulePack pack, Uri url)
    {
        foreach (DeclarativeExtractorRule rule in pack.Rules)
        {
            if (!rule.Domains.Any(d => UrlUtil.HostIs(url, d))) continue;
            if (rule.UrlPattern is null) return (rule, null);
            var rx = new Regex(rule.UrlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            var m = rx.Match(url.PathAndQuery);
            if (m.Success) return (rule, m);
        }
        return null;
    }

    public async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var matched = MatchRule(_store.Current, request.Url);
        if (matched is null) return MediaDiscoveryResult.Failure(MediaErrors.NotThisSite("No declarative rule matched"));

        var (rule, urlMatch) = matched.Value;
        string? extractedId = urlMatch?.Groups["id"] is { Success: true, Value: { Length: > 0 } v } ? v : null;
        var candidates = new List<MediaCandidate>();
        string? title = null;

        var ctx = request.AuthContext with
        {
            Referer = UrlUtil.WithoutFragment(request.Url).AbsoluteUri,
            Origin = rule.RequireOriginHeader ? UrlUtil.Origin(request.Url) : request.AuthContext.Origin
        };

        // 1. Optional templated JSON API calls (e.g. public player config APIs)
        foreach (string template in rule.ApiTemplates.Take(3))
        {
            string expanded = template
                .Replace("{id}", Uri.EscapeDataString(extractedId ?? ""), StringComparison.Ordinal)
                .Replace("{host}", request.Url.Host, StringComparison.Ordinal);
            if (!Uri.TryCreate(expanded, UriKind.Absolute, out Uri? apiUri) || apiUri.Scheme != "https"
                || apiUri.UserInfo.Length != 0 || string.IsNullOrEmpty(extractedId) && template.Contains("{id}", StringComparison.Ordinal))
                continue;
            try
            {
                var apiResp = await _svc.Http.GetStringAsync(apiUri, null, ct, _svc.Options.MaxJsonBytes, "application/json").ConfigureAwait(false);
                if (apiResp.IsSuccess && JsonExt.TryParse(apiResp.Content) is { } doc)
                {
                    using (doc) ExtractFromJson(doc.RootElement, request.Url, rule, candidates, ref title);
                }
            }
            catch (MediaResolverException) { }
        }

        // 2. HTML + Hydration JSON extraction
        if (candidates.Count == 0)
        {
            try
            {
                var page = request.PageResponse ?? await _svc.Http.GetStringAsync(request.Url, ctx, ct).ConfigureAwait(false);
                if (page.IsSuccess && page.IsText)
                {
                    request.PageResponse = page;
                    string decoded = page.Content.Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
                    foreach (string pattern in rule.HtmlStreamRegexes.Take(6))
                    {
                        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                        foreach (Match m in rx.Matches(decoded).Cast<Match>().Take(8))
                        {
                            string raw = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                            if (UrlUtil.Resolve(page.FinalUrl, raw) is { } streamUri)
                                AddRuleCandidate(candidates, streamUri);
                        }
                    }
                }
            }
            catch (MediaResolverException) { }
        }

        if (candidates.Count == 0)
            return MediaDiscoveryResult.Failure(MediaErrors.NotThisSite($"Declarative rule '{rule.Id}' found no direct candidates; deferring"));

        var result = await _generic.ResolveAsync(request, candidates, ct).ConfigureAwait(false);
        if (result.Item is { } item)
        {
            return MediaDiscoveryResult.FromItem(item with
            {
                Provider = rule.Id,
                MediaId = extractedId ?? item.MediaId,
                Title = title ?? item.Title
            }, result.Warnings, result.Notes);
        }
        return result;
    }

    private static void ExtractFromJson(JsonElement root, Uri baseUri, DeclarativeExtractorRule rule, List<MediaCandidate> candidates, ref string? title)
    {
        foreach (string path in rule.TitleJsonPaths)
            if (EvaluateJsonPath(root, path).FirstOrDefault(e => e.ValueKind == JsonValueKind.String) is { ValueKind: JsonValueKind.String } node
                && node.GetString() is { Length: > 0 } t)
            { title ??= t; break; }

        foreach (string path in rule.ManifestJsonPaths)
            foreach (JsonElement el in EvaluateJsonPath(root, path))
                if (el.ValueKind == JsonValueKind.String && UrlUtil.Resolve(baseUri, el.GetString()) is { } u)
                    AddRuleCandidate(candidates, u, isManifest: true);

        foreach (string path in rule.StreamJsonPaths)
            foreach (JsonElement el in EvaluateJsonPath(root, path))
                if (el.ValueKind == JsonValueKind.String && UrlUtil.Resolve(baseUri, el.GetString()) is { } u)
                    AddRuleCandidate(candidates, u);

        // Also run universal config discovery on the API JSON
        var universal = new PlayerConfigDiscovery();
        universal.Scan(root, baseUri, explicitPlayer: true);
        foreach (var c in universal.BestCandidates)
            if (!candidates.Any(existing => existing.Url == c.Url))
                candidates.Add(c);
        title ??= universal.BestTitle;
    }

    private static void AddRuleCandidate(List<MediaCandidate> list, Uri uri, bool isManifest = false)
    {
        if (list.Count >= 24 || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || PlayerConfigDiscovery.IsAdOrTrackingHost(uri.Host)) return;
        uri = UrlUtil.WithoutFragment(uri);
        if (list.Any(c => c.Url == uri)) return;
        CandidateKind kind = MediaCandidate.Classify(uri, null);
        if (kind == CandidateKind.Unknown)
            kind = isManifest ? (uri.AbsolutePath.Contains("mpd", StringComparison.OrdinalIgnoreCase) ? CandidateKind.Dash : CandidateKind.Hls) : CandidateKind.Direct;
        list.Add(new MediaCandidate(uri, kind));
    }

    private static IEnumerable<JsonElement> EvaluateJsonPath(JsonElement current, string path)
    {
        string[] steps = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        List<JsonElement> frontier = [current];
        foreach (string step in steps.Take(8))
        {
            var next = new List<JsonElement>();
            bool arrayWildcard = step.EndsWith("[]", StringComparison.Ordinal);
            string propName = arrayWildcard ? step[..^2] : step;
            foreach (JsonElement node in frontier)
            {
                JsonElement target = node;
                if (propName.Length > 0)
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(propName, out target))
                        continue;
                }
                if (next.Count >= 24) break;
                if (arrayWildcard)
                {
                    if (target.ValueKind == JsonValueKind.Array)
                        next.AddRange(target.EnumerateArray().Take(24 - next.Count));
                }
                else next.Add(target);
            }
            frontier = next;
            if (frontier.Count == 0) break;
        }
        return frontier;
    }
}
