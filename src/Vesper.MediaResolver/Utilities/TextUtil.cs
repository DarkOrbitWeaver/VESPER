using System.Globalization;

namespace Vesper.MediaResolver.Utilities;

public static class TextUtil
{
    /// <summary>Parses "123.5", "mm:ss" or "hh:mm:ss". Returns null for anything else.</summary>
    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return secs is >= 0 and < 8_640_000 ? TimeSpan.FromSeconds(secs) : null;

        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total < 8_640_000 ? TimeSpan.FromSeconds(total) : null;
    }

    public static TimeSpan? Seconds(double? s) => s is { } v and >= 0 and < 8_640_000 ? TimeSpan.FromSeconds(v) : null;

    public static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    public static string NormalizeLang(string? lang) => string.IsNullOrWhiteSpace(lang) ? "und" : lang.Trim().Replace('_', '-').ToLowerInvariant();

    /// <summary>Primary language subtag: "en-US" -> "en".</summary>
    public static string PrimaryLang(string? lang)
    {
        string l = NormalizeLang(lang);
        int dash = l.IndexOf('-');
        return dash < 0 ? l : l[..dash];
    }

    /// <summary>"1:23" / "12:03:04" at the start of a line -> time.</summary>
    public static bool TryParseTimestamp(string s, out TimeSpan t)
    {
        t = default;
        var ts = ParseDuration(s);
        if (ts is null || !s.Contains(':')) return false;
        t = ts.Value;
        return true;
    }
}
