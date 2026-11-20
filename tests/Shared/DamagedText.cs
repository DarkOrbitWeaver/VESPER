using System.Text.RegularExpressions;

namespace Vesper.Tests.Shared;

/// <summary>
/// Seeded damage for text that arrives from a server or a paste: playlists, manifests, feeds and URL lists.
/// It truncates, swaps in single awkward characters, and replaces numbers with values at or past the usual
/// limits. The same seed always yields the same cases, so a failure label is enough to replay one.
/// </summary>
internal static class DamagedText
{
    private static readonly Regex Number = new(@"\d+", RegexOptions.CultureInvariant);

    private static readonly string[] Characters =
        ["\0", "\n", "\"", ",", "=", ":", "-", "#", "<", ">", "&", "[", "]", "%", "/", "@", " ", "\uFFFD", "\u202E"];

    private static readonly string[] Numbers =
        ["", "0", "-1", "4294967296", "9223372036854775807", "99999999999999999999", "1e309", "NaN", "0x10", "1.5.2"];

    internal static IEnumerable<(string Label, string Text)> Cases(string text, int seed, int perMutation)
    {
        var random = new Random(seed);

        foreach (int length in Distinct(Math.Min(perMutation, text.Length), () => random.Next(text.Length)))
            yield return ($"truncate to {length}", text[..length]);

        foreach ((int offset, int pick) in Distinct(Math.Min(perMutation, text.Length * Characters.Length),
            () => (random.Next(text.Length), random.Next(Characters.Length))))
            yield return ($"character {pick} at {offset}",
                string.Concat(text.AsSpan(0, offset), Characters[pick], text.AsSpan(offset + 1)));

        MatchCollection found = Number.Matches(text);
        if (found.Count == 0) yield break;
        foreach ((int index, int pick) in Distinct(Math.Min(perMutation, found.Count * Numbers.Length),
            () => (random.Next(found.Count), random.Next(Numbers.Length))))
        {
            Match number = found[index];
            yield return ($"number {pick} at {number.Index}",
                string.Concat(text.AsSpan(0, number.Index), Numbers[pick], text.AsSpan(number.Index + number.Length)));
        }
    }

    private static List<T> Distinct<T>(int count, Func<T> next)
    {
        var seen = new HashSet<T>();
        var ordered = new List<T>(count);
        while (ordered.Count < count)
        {
            T value = next();
            if (seen.Add(value)) ordered.Add(value);
        }
        return ordered;
    }
}
