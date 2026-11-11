using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

public enum QualityOptionKind { BestWithAudio, Balanced, Smallest, AudioOnly }

/// <summary>One row of VESPER's default quality picker.</summary>
public sealed record QualityOption(QualityOptionKind Kind, string Label, string Description, FormatSelection Selection);

/// <summary>What the source really offers, for building advanced pickers. Nothing here is invented.</summary>
public sealed record QualityCapabilities(
    IReadOnlyList<int> Heights,
    IReadOnlyList<double> FrameRates,
    bool HasHdr,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> Containers,
    bool HasCompleteStreams,
    bool HasSeparateStreams,
    bool HasAudioOnly);

public static class QualityMenuBuilder
{
    /// <summary>Best with audio, Balanced, Smallest suitable and Audio only - duplicates removed, unavailable options omitted.</summary>
    public static IReadOnlyList<QualityOption> BuildDefault(MediaItem item, FormatPreferences? baseline = null)
    {
        baseline ??= new FormatPreferences();
        var options = new List<QualityOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(QualityOptionKind kind, QualityMode mode, string title)
        {
            var r = FormatSelector.Select(item, baseline with { Mode = mode, TargetHeight = null });
            if (r.Selection is not { } sel) return;
            string sig = $"{sel.Video?.Id}|{sel.Audio?.Id}";
            if (!seen.Add(sig)) return;

            string size = sel.EstimatedSize is { } s ? $" · {(sel.EstimatedSizeIsEstimate ? "~" : "")}{FormatSize(s)}" : "";
            string note = sel.NeedsMuxing ? " · needs merge tool" : "";
            options.Add(new QualityOption(kind, $"{title} ({sel.Summary}){size}", $"{sel.Summary}{size}{note}", sel));
        }

        TryAdd(QualityOptionKind.BestWithAudio, QualityMode.Best, "Best available");
        TryAdd(QualityOptionKind.Balanced, QualityMode.Balanced, "Balanced");
        TryAdd(QualityOptionKind.Smallest, QualityMode.Smallest, "Smallest suitable");

        // Audio-only is a different kind of output, so it is exempt from the visual-duplicate check.
        var audio = FormatSelector.Select(item, baseline with { Mode = QualityMode.AudioOnly });
        if (audio.Selection is { } a && !a.IsFallback)
            options.Add(new QualityOption(QualityOptionKind.AudioOnly, $"Audio only ({a.Summary})", a.Summary, a));

        return options;
    }

    public static QualityCapabilities Describe(MediaItem item)
    {
        var reps = item.Representations.Where(r => !r.IsLive).ToList();
        var video = reps.Where(r => r.HasVideo).ToList();
        var audio = reps.Where(r => r.HasAudio && r.AudioCodec is not null).ToList();
        return new QualityCapabilities(
            video.Where(r => r.Height is not null).Select(r => r.Height!.Value).Distinct().OrderByDescending(h => h).ToList(),
            video.Where(r => r.FrameRate is not null).Select(r => Math.Round(r.FrameRate!.Value)).Distinct().OrderByDescending(f => f).ToList(),
            video.Any(r => r.Hdr != HdrKind.Sdr),
            video.Select(r => r.VideoCodec).OfType<string>().Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            audio.Select(r => r.AudioCodec).OfType<string>().Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            reps.Select(r => r.AudioLanguage).OfType<string>().Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            reps.Select(r => r.Container).OfType<string>().Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
            reps.Any(r => r.Content == StreamContent.VideoAndAudio),
            reps.Any(r => r.Content == StreamContent.VideoOnly),
            reps.Any(r => r.Content == StreamContent.AudioOnly));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
