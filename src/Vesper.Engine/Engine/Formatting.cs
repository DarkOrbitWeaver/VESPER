using System.Globalization;

namespace Vesper.Engine;

/// <summary>Human-readable sizes, rates and times shared by messages and the UI.</summary>
public static class Formatting
{
    private static readonly string[] SizeUnits = ["B", "KiB", "MiB", "GiB", "TiB"];
    private static readonly string[] RateUnits = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];

    public static string Bytes(long bytes) => Quantity(bytes, 1024, SizeUnits);

    public static string Rate(double bytesPerSecond) => Quantity(bytesPerSecond, 1000, RateUnits);

    private static string Quantity(double value, int scale, string[] units)
    {
        if (!double.IsFinite(value) || value < 0) value = 0;
        int unit = 0;
        while (value >= scale && unit < units.Length - 1) { value /= scale; unit++; }
        string format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Rounded in steps so the displayed value does not flicker on every update.</summary>
    public static string Eta(TimeSpan eta)
    {
        double seconds = eta.TotalSeconds;
        if (double.IsNaN(seconds) || seconds < 0) return "";
        if (seconds < 10) return "a few seconds left";
        if (seconds < 120) return $"{Math.Round(seconds / 5) * 5:0} sec left";
        if (seconds < 3600) return $"{Math.Round(seconds / 60):0} min left";
        double hours = Math.Floor(seconds / 3600);
        double minutes = Math.Round(seconds % 3600 / 300) * 5;
        if (minutes >= 60) { hours++; minutes = 0; }
        return minutes == 0 ? $"{hours:0} h left" : $"{hours:0} h {minutes:0} min left";
    }

    public static string RateLimit(long bytesPerSecond) => bytesPerSecond <= 0
        ? "Unlimited"
        : bytesPerSecond % (1000 * 1000) == 0 ? $"{bytesPerSecond / 1_000_000} MB/s" : $"{bytesPerSecond / 1000} KB/s";
}
