using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

/// <summary>
/// Optional, strictly loopback-only semantic DOM/script extractor (e.g. local Ollama / llama.cpp endpoint).
/// Zero vaporware: when unconfigured (the default), this does nothing. When explicitly configured by the user,
/// it receives ONLY a redacted structural skeleton (&lt;= 12 KiB, query tokens stripped from non-candidate text)
/// and any URLs it extracts still pass through <see cref="CandidateProcessor"/> byte/manifest verification.
/// </summary>
internal static class SemanticPageSynthesizer
{
    private const int MaxSkeletonChars = 12_000;
    private static readonly Regex UrlCandidateRegex = new(
        @"https?://[^\s""'<>\\]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task<IReadOnlyList<MediaCandidate>> TrySynthesizeAsync(
        string html,
        Uri pageUrl,
        ResolverOptions options,
        CancellationToken ct)
    {
        if (options.LocalSemanticEndpoint is not { IsAbsoluteUri: true } endpoint
            || endpoint.Scheme is not ("http" or "https")
            || !endpoint.IsLoopback || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return [];

        string skeleton = BuildCompactMediaSkeleton(html);
        if (skeleton.Length == 0) return [];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            using var loopbackClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(6),
                MaxResponseContentBufferSize = 64 * 1024
            };
            var payload = new
            {
                model = options.LocalSemanticModel,
                stream = false,
                prompt = "Extract all playable video, audio, HLS (.m3u8), or DASH (.mpd) stream URLs from this page snippet. Output ONLY raw JSON: {\"urls\": [\"https://...\"]}.\n\n" + skeleton
            };
            using var resp = await loopbackClient.PostAsJsonAsync(endpoint, payload, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            string raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (raw.Length > 64 * 1024) return [];

            var discovered = new List<MediaCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in UrlCandidateRegex.Matches(raw.Replace("\\/", "/", StringComparison.Ordinal)))
            {
                if (discovered.Count >= 8) break;
                if (UrlUtil.Resolve(pageUrl, m.Value.TrimEnd('"', '\'', ',', ']', '}')) is not { Scheme: "http" or "https" } u) continue;
                u = UrlUtil.WithoutFragment(u);
                if (PlayerConfigDiscovery.IsAdOrTrackingHost(u.Host) || !seen.Add(u.AbsoluteUri)) continue;
                var kind = MediaCandidate.Classify(u, null);
                discovered.Add(new MediaCandidate(u, kind == CandidateKind.Unknown ? CandidateKind.Direct : kind));
            }
            return discovered;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    internal static string BuildCompactMediaSkeleton(string html)
    {
        var sb = new StringBuilder(capacity: 4096);
        foreach (HtmlTag tag in HtmlScanner.Scan(html))
        {
            if (tag.IsClosing || tag.Name is not ("video" or "audio" or "source" or "iframe" or "script"))
                continue;
            string? raw = tag.Name == "script" ? tag.Body : tag.Attr("src") ?? tag.Attr("data-src");
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (Match match in UrlCandidateRegex.Matches(raw.Replace("\\/", "/", StringComparison.Ordinal)))
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? url) || url.UserInfo.Length != 0) continue;
                string path = url.GetLeftPart(UriPartial.Path);
                if (path.Length > 1024 || MediaCandidate.Classify(url, null) == CandidateKind.Unknown) continue;
                if (sb.Length + path.Length + tag.Name.Length + 2 > MaxSkeletonChars) return sb.ToString();
                sb.Append(tag.Name).Append(' ').Append(path).Append('\n');
            }
        }
        return sb.ToString();
    }
}
