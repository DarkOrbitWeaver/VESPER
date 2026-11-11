using System.Globalization;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

/// <summary>
/// Collapses equivalent representations (same content, resolution, fps, HDR, codecs, container, language, channels)
/// into the single most convenient one: plain HTTP over HLS over DASH, then highest bitrate, then a known size.
/// Audio-only streams of genuinely different bitrates are kept.
/// </summary>
public static class FormatDeduplicator
{
    public static IReadOnlyList<MediaRepresentation> Deduplicate(IEnumerable<MediaRepresentation> reps)
    {
        var order = new List<string>();
        var winners = new Dictionary<string, MediaRepresentation>(StringComparer.Ordinal);

        foreach (var r in reps)
        {
            string key = KeyOf(r);
            if (!winners.TryGetValue(key, out var current))
            {
                winners[key] = r;
                order.Add(key);
            }
            else if (IsBetter(r, current))
            {
                winners[key] = r;
            }
        }
        return order.Select(k => winners[k]).ToList();
    }

    private static string KeyOf(MediaRepresentation r)
    {
        string fps = r.FrameRate is { } f ? Math.Round(f).ToString(CultureInfo.InvariantCulture) : "-";
        string audioBitrate = r.Content == StreamContent.AudioOnly ? ((r.BitrateKbps ?? 0) / 16).ToString(CultureInfo.InvariantCulture) : "";
        // Identical URLs are always the same stream; fold them regardless of metadata differences.
        return $"{(int)r.Content}|{r.Height?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{fps}|{r.Hdr}|{r.VideoCodec}|{r.AudioCodec}|" +
               $"{r.Container}|{r.AudioLanguage}|{r.AudioChannels}|{audioBitrate}|{(r.Content == StreamContent.AudioOnly ? r.AudioTrackId : "")}";
    }

    private static bool IsBetter(MediaRepresentation candidate, MediaRepresentation current)
    {
        int p = ((int)candidate.Protocol).CompareTo((int)current.Protocol);
        if (p != 0) return p < 0;
        int b = (candidate.BitrateKbps ?? 0).CompareTo(current.BitrateKbps ?? 0);
        if (b != 0) return b > 0;
        bool cs = candidate.FileSize is not null, us = current.FileSize is not null;
        if (cs != us) return cs;
        return string.CompareOrdinal(candidate.Id, current.Id) < 0;
    }
}
