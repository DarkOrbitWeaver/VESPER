using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Playlists;

public sealed record UrlListParseResult(
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<(string Text, string Reason)> Rejected,
    bool Truncated);

/// <summary>Turns pasted text containing several links into a clean, de-duplicated, bounded URL list.</summary>
public static class UrlListParser
{
    private static readonly Core.ResolverOptions Defaults = new();

    public static UrlListParseResult Parse(string? text, int maxUrls = 500)
    {
        var urls = new List<Uri>();
        var rejected = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;
        if (string.IsNullOrWhiteSpace(text)) return new(urls, rejected, false);

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.Trim('<', '>', '"', '\'', ',', ';', '(', ')');
            if (t.Length == 0) continue;
            if (!UrlValidator.TryParse(t, out var uri, out var reason) || !UrlValidator.IsAllowed(uri, Defaults, out reason))
            {
                if (rejected.Count < 100) rejected.Add((TextUtil.Clip(t, 80), reason ?? "invalid"));
                continue;
            }
            var clean = UrlUtil.WithoutFragment(uri);
            if (!seen.Add(clean.AbsoluteUri)) continue;
            if (urls.Count >= maxUrls) { truncated = true; break; }
            urls.Add(clean);
        }
        return new(urls, rejected, truncated);
    }
}
