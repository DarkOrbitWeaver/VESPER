using System.Text;

namespace Vesper.MediaResolver.Security;

/// <summary>Turns untrusted titles into safe single-segment Windows file names (no traversal, no reserved names).</summary>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const string InvalidChars = "<>:\"/\\|?*";

    public static string Sanitize(string? name, string fallback = "media", int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        try { name = name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { /* invalid surrogates: keep as-is, they are replaced below */ }

        var sb = new StringBuilder(Math.Min(name.Length, maxLength + 8));
        foreach (char ch in name)
        {
            if (ch < 32 || ch == 127 || InvalidChars.IndexOf(ch) >= 0) sb.Append('_');
            else if (ch is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\uFEFF') continue; // zero-width / bidi tricks
            else if (char.IsSurrogate(ch)) sb.Append(ch);
            else sb.Append(ch);
        }

        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);

        if (s.Length > maxLength)
        {
            int cut = maxLength;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            s = s[..cut].TrimEnd('.', ' ');
        }

        if (s.Length == 0 || s.All(c => c == '.' || c == '_' || c == ' ')) return fallback;
        string stem = s.Split('.')[0].Trim();
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    public static string BuildFileName(string? title, string? id, string? extension)
    {
        string stem = Sanitize(title, string.IsNullOrWhiteSpace(id) ? "media" : Sanitize(id), 110);
        string ext = new string((extension ?? "").Where(char.IsAsciiLetterOrDigit).Take(5).ToArray()).ToLowerInvariant();
        return ext.Length == 0 ? stem : $"{stem}.{ext}";
    }
}
