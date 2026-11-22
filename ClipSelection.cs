using System.Globalization;
using Vesper.Engine;

namespace VESPER;

// How a chosen format can be trimmed to a time range.
internal enum ClipMode
{
    None,       // this format cannot be cut
    Segments,   // HLS/DASH: only the needed segments are downloaded, so the cut lands on segment edges
    FullFile    // the full source is downloaded, then trimmed locally
}

internal static class ClipSelection
{
    internal const double LongestSeconds = 31_536_000;
    internal const string SegmentNotice = "Downloads only the segments in this range; the cut may shift slightly.";
    internal const string FullFileNotice = "Downloads the full file, then trims to this range.";

    internal static ClipMode ModeFor(MediaDownloadChoice choice)
    {
        if (!MediaTransferBridge.SupportsTimeRange(choice)) return ClipMode.None;
        if (choice.Output == MediaOutput.Wav || choice.CanUseFullFileClip) return ClipMode.FullFile;
        return ModeFor(choice.Output, choice.Kind);
    }

    internal static ClipMode ModeFor(MediaOutput output, JobKind kind) =>
        output == MediaOutput.Wav && (kind is JobKind.Http or JobKind.Merge) ? ClipMode.FullFile : ClipMode.Segments;

    internal static string Notice(ClipMode mode) => mode == ClipMode.FullFile ? FullFileNotice : SegmentNotice;

    // Why trimming is unavailable, or null when it can be used.
    internal static string? UnavailableReason(bool isLive, TimeSpan? duration, ClipMode mode)
    {
        if (isLive) return "Trim isn't available for live streams.";
        if (duration is not { TotalSeconds: > 0 and <= LongestSeconds })
            return "Trim needs a known media length, and this source doesn't report one.";
        if (mode == ClipMode.None)
            return "This format can't be trimmed. Choose another format or turn Trim off.";
        return null;
    }

    internal static bool TryParseTime(string? text, out TimeSpan value) => TimeSpan.TryParseExact((text ?? "").Trim(),
        ["m\\:ss", "mm\\:ss", "h\\:mm\\:ss", "hh\\:mm\\:ss", "d\\.hh\\:mm\\:ss",
         "m\\:ss\\.FFFFFFF", "mm\\:ss\\.FFFFFFF", "h\\:mm\\:ss\\.FFFFFFF", "hh\\:mm\\:ss\\.FFFFFFF", "d\\.hh\\:mm\\:ss\\.FFFFFFF"],
        CultureInfo.InvariantCulture, TimeSpanStyles.None, out value);

    internal static string TimeText(TimeSpan value) => value.TotalHours >= 24
        ? value.ToString("d\\.hh\\:mm\\:ss\\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.')
        : value.ToString("hh\\:mm\\:ss\\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');

    internal static string TimeText(double seconds) =>
        TimeText(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)));

    internal static bool TryGetRange(string? fromText, string? toText, TimeSpan? duration,
        out double start, out double end, out string error)
    {
        start = end = 0;
        if (duration is not { } length)
        {
            error = "Trim needs a known media length, and this source doesn't report one.";
            return false;
        }
        if (!TryParseTime(fromText, out TimeSpan from) || !TryParseTime(toText, out TimeSpan to))
        {
            error = "Enter Start and End as mm:ss or hh:mm:ss.";
            return false;
        }
        if (to <= from)
        {
            error = "End must be later than Start.";
            return false;
        }
        if (to > length || to.TotalSeconds > LongestSeconds)
        {
            error = $"End is past the media length ({TimeText(length)}).";
            return false;
        }

        start = from.TotalSeconds;
        end = to.TotalSeconds;
        error = "";
        return true;
    }

    internal static string Summary(double start, double end, double duration) =>
        $"{TimeText(end - start)} selected of {TimeText(duration)}";

    // Time bar geometry. The track runs from trackLeft for trackWidth pixels.
    internal static double SecondsAt(int x, int trackLeft, int trackWidth, double duration)
    {
        if (trackWidth <= 0 || duration <= 0) return 0;
        double share = Math.Clamp((x - trackLeft) / (double)trackWidth, 0, 1);
        return share == 1 ? duration : Math.Min(duration, Math.Round(share * duration));
    }

    internal static int PixelAt(double seconds, int trackLeft, int trackWidth, double duration)
    {
        if (trackWidth <= 0 || duration <= 0) return trackLeft;
        return trackLeft + (int)Math.Round(Math.Clamp(seconds / duration, 0, 1) * trackWidth);
    }

    // Moves one handle and keeps at least one second between the two.
    internal static (double Start, double End) MoveHandle(bool startHandle, double seconds,
        double start, double end, double duration)
    {
        double gap = Math.Min(1, duration);
        if (startHandle) start = Math.Clamp(seconds, 0, Math.Max(0, end - gap));
        else end = Math.Clamp(seconds, Math.Min(duration, start + gap), duration);
        return (start, end);
    }
}
