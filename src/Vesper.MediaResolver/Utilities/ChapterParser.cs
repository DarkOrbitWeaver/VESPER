using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Utilities;

/// <summary>Reads chapters from description timestamps using the common platform convention (list starts at 0:00, at least 3 entries).</summary>
public static class ChapterParser
{
    public static IReadOnlyList<Chapter> FromDescription(string? description, TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(description)) return [];
        var found = new List<(TimeSpan Start, string Title)>();

        foreach (var raw in description.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 4 || !char.IsAsciiDigit(line[0]) && line[0] != '(') continue;

            int space = line.IndexOf(' ');
            if (space < 0) continue;
            string token = line[..space].Trim('(', ')', '[', ']');
            if (!TextUtil.TryParseTimestamp(token, out var start)) continue;

            string title = line[(space + 1)..].Trim().TrimStart('-', '–', '—', ':', '|', ' ').Trim();
            if (title.Length == 0) continue;
            found.Add((start, TextUtil.Clip(title, 200)));
            if (found.Count >= 200) break;
        }

        if (found.Count < 3 || found[0].Start != TimeSpan.Zero) return [];
        for (int i = 1; i < found.Count; i++)
            if (found[i].Start <= found[i - 1].Start) return [];
        if (duration is { } d && found[^1].Start >= d) return [];

        var chapters = new List<Chapter>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            TimeSpan? end = i + 1 < found.Count ? found[i + 1].Start : duration;
            chapters.Add(new Chapter(found[i].Title, found[i].Start, end));
        }
        return chapters;
    }
}
