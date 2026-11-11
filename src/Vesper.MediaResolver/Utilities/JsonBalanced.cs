using System.Text.Json;

namespace Vesper.MediaResolver.Utilities;

/// <summary>
/// Extracts a JSON object literal that follows a marker in HTML/JS text, e.g. <c>var ytInitialPlayerResponse = {...};</c>.
/// Uses a real brace/string scanner (no regex) and never executes script code.
/// </summary>
public static class JsonBalanced
{
    public static JsonDocument? ExtractAfter(string text, string marker, int maxChars = 4_000_000)
    {
        int m = text.IndexOf(marker, StringComparison.Ordinal);
        while (m >= 0)
        {
            int p = m + marker.Length;
            while (p < text.Length && (text[p] is ' ' or '=' or '\t' or '\n' or '\r' or ':')) p++;
            if (p < text.Length && text[p] == '{')
            {
                string? slice = ReadObject(text, p, maxChars);
                if (slice is not null)
                {
                    var doc = JsonExt.TryParse(slice);
                    if (doc is not null) return doc;
                }
            }
            m = text.IndexOf(marker, m + marker.Length, StringComparison.Ordinal);
        }
        return null;
    }

    internal static string? ReadObject(string t, int start, int maxChars)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        int end = Math.Min(t.Length, start + maxChars);
        for (int i = start; i < end; i++)
        {
            char c = t[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return t.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }
}
