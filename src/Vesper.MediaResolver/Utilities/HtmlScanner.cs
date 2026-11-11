using System.Net;

namespace Vesper.MediaResolver.Utilities;

public sealed class HtmlTag
{
    public required string Name { get; init; }
    public required Dictionary<string, string> Attributes { get; init; }
    public bool IsClosing { get; init; }

    /// <summary>Raw text for script/style/title/textarea start tags.</summary>
    public string? Body { get; init; }

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Minimal, forgiving, linear-time tag tokenizer. It does not build a DOM and never evaluates anything;
/// it exists so adapters can read meta/video/source/script tags without fragile regexes.
/// </summary>
public static class HtmlScanner
{
    public static IEnumerable<HtmlTag> Scan(string html, int maxTags = 30_000)
    {
        int i = 0, n = html.Length, count = 0;
        while (i < n && count < maxTags)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) yield break;

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int endC = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (endC < 0) yield break;
                i = endC + 3;
                continue;
            }

            char c = html[lt + 1];
            if (c == '!' || c == '?')
            {
                int gt0 = html.IndexOf('>', lt);
                if (gt0 < 0) yield break;
                i = gt0 + 1;
                continue;
            }

            bool closing = c == '/';
            int p = lt + (closing ? 2 : 1);
            int nameStart = p;
            while (p < n && (char.IsAsciiLetterOrDigit(html[p]) || html[p] is '-' or ':' or '_')) p++;
            if (p == nameStart) { i = lt + 1; continue; }
            string name = html.Substring(nameStart, p - nameStart).ToLowerInvariant();

            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (p < n)
            {
                while (p < n && (char.IsWhiteSpace(html[p]) || html[p] == '/')) p++;
                if (p >= n || html[p] == '>') break;

                int aStart = p;
                while (p < n && !char.IsWhiteSpace(html[p]) && html[p] is not ('=' or '>' or '/')) p++;
                string aName = html.Substring(aStart, p - aStart);
                while (p < n && char.IsWhiteSpace(html[p])) p++;

                string aVal = "";
                if (p < n && html[p] == '=')
                {
                    p++;
                    while (p < n && char.IsWhiteSpace(html[p])) p++;
                    if (p < n && html[p] is '"' or '\'')
                    {
                        char q = html[p++];
                        int ve = html.IndexOf(q, p);
                        if (ve < 0) ve = n;
                        aVal = html.Substring(p, ve - p);
                        p = Math.Min(n, ve + 1);
                    }
                    else
                    {
                        int vs = p;
                        while (p < n && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                        aVal = html.Substring(vs, p - vs);
                    }
                }
                if (aName.Length > 0 && !attrs.ContainsKey(aName)) attrs[aName] = WebUtility.HtmlDecode(aVal);
            }
            if (p < n) p++; // consume '>'

            string? body = null;
            if (!closing && name is "script" or "style" or "title" or "textarea")
            {
                int close = html.IndexOf("</" + name, p, StringComparison.OrdinalIgnoreCase);
                if (close < 0) { body = html.Substring(p); p = n; }
                else
                {
                    body = html.Substring(p, close - p);
                    int gt = html.IndexOf('>', close);
                    p = gt < 0 ? n : gt + 1;
                }
            }

            i = p;
            count++;
            yield return new HtmlTag { Name = name, Attributes = attrs, IsClosing = closing, Body = body };
        }
    }
}
