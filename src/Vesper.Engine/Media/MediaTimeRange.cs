using System.Text.Json;

namespace Vesper.Engine;

/// <summary>A user-requested time window for segment-aligned media extraction.</summary>
public sealed record MediaTimeRange(double StartSeconds, double EndSeconds, int Version = 1)
{
    private const double MaxSeconds = 31_536_000;

    internal bool IsValid => Version == 1 && double.IsFinite(StartSeconds) && double.IsFinite(EndSeconds)
        && StartSeconds >= 0 && EndSeconds > StartSeconds && EndSeconds <= MaxSeconds;

    internal static bool TryParse(string? json, out MediaTimeRange range)
    {
        range = null!;
        if (json is null || json.Length > 1024) return false;
        try
        {
            MediaTimeRange? value = JsonSerializer.Deserialize<MediaTimeRange>(json);
            if (value is not { IsValid: true }) return false;
            range = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal bool TrySelect(IReadOnlyList<double> durations, out int first, out int count,
        out double actualStart, out double actualEnd) =>
        TrySelect(durations, starts: null, out first, out count, out actualStart, out actualEnd);

    internal bool TrySelect(IReadOnlyList<double> durations, IReadOnlyList<double?>? starts,
        out int first, out int count, out double actualStart, out double actualEnd)
    {
        first = count = 0;
        actualStart = actualEnd = 0;
        if (!IsValid || durations.Count is 0 or > 100_000 || starts is not null && starts.Count != durations.Count) return false;
        if (starts is not null && starts.Any(start => start is null) && starts.Any(start => start is not null)) return false;

        double position = 0;
        bool hasPosition = false;
        for (int i = 0; i < durations.Count; i++)
        {
            double duration = durations[i];
            if (starts?[i] is double explicitStart) position = explicitStart;
            else if (!hasPosition) position = 0;
            if (!double.IsFinite(position) || position < 0 || !double.IsFinite(duration)
                || duration <= 0 || position > MaxSeconds - duration) return false;
            double end = position + duration;
            if (end > StartSeconds && position < EndSeconds)
            {
                if (count > 0 && Math.Abs(position - actualEnd) > 0.001) return false;
                if (count == 0) { first = i; actualStart = position; }
                count++;
                actualEnd = end;
            }
            position = end;
            hasPosition = true;
        }
        return count > 0 && actualStart <= StartSeconds + 0.001 && actualEnd + 0.001 >= EndSeconds;
    }

    internal static string Describe(double start, double end) =>
        $"Segment-aligned {Clock(start)}–{Clock(end)}";

    private static string Clock(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(seconds);
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }
}
