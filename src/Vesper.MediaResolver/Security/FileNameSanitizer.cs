using System.Text;
using Vesper.Shared;

namespace Vesper.MediaResolver.Security;

/// <summary>Turns untrusted titles into safe single-segment Windows file names (no traversal, no reserved names).</summary>
public static class FileNameSanitizer
{
    public static string Sanitize(string? name, string fallback = "media", int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        try { name = name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { /* invalid surrogates: keep as-is, they are replaced below */ }

        var sb = new StringBuilder(Math.Min(name.Length, maxLength + 8));
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (FileNameRules.IsWindowsInvalid(rune)) sb.Append('_');
            else if (FileNameRules.IsHidden(rune)) continue;    // zero-width / bidi tricks
            else sb.Append(rune.ToString());
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
        if (FileNameRules.IsReservedStem(stem)) s = "_" + s;
        return s;
    }

    public static string BuildFileName(string? title, string? id, string? extension)
    {
        string stem = Sanitize(title, string.IsNullOrWhiteSpace(id) ? "media" : Sanitize(id), 110);
        string ext = new string((extension ?? "").Where(char.IsAsciiLetterOrDigit).Take(5).ToArray()).ToLowerInvariant();
        return ext.Length == 0 ? stem : $"{stem}.{ext}";
    }
}
