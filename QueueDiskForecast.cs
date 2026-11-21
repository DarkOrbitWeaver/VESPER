using Vesper.Engine;

namespace VESPER;

internal sealed record DiskForecastVolume(
    string Root,
    long KnownRemainingBytes,
    long SafetyReserveBytes,
    long RequiredBytes,
    int UnknownJobs,
    long? AvailableBytes)
{
    public bool IsShort => AvailableBytes is long available && RequiredBytes > available;
}

internal static class QueueDiskForecast
{
    private const int MaxVolumeChecks = 16;

    private sealed class Totals
    {
        public long KnownRemainingBytes;
        public int UnknownJobs;
    }

    public static string[] VolumeRoots(IEnumerable<JobSnapshot> jobs) => jobs
        .Where(IsPending)
        .Select(job => Root(job.Destination))
        .Where(root => root is not null)
        .Select(root => root!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyDictionary<string, long?> ReadAvailable(IEnumerable<string> roots)
    {
        var available = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxVolumeChecks))
        {
            try
            {
                var drive = new DriveInfo(root);
                available[root] = drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                available[root] = null;
            }
        }
        return available;
    }

    public static IReadOnlyList<DiskForecastVolume> Calculate(IEnumerable<JobSnapshot> jobs,
        IReadOnlyDictionary<string, long?> available, long safetyReserveBytes)
    {
        var totals = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        foreach (JobSnapshot job in jobs.Where(IsPending))
        {
            string root = Root(job.Destination) ?? "Unknown location";
            if (!totals.TryGetValue(root, out Totals? total)) totals[root] = total = new Totals();

            if (CanEstimate(job))
            {
                long totalBytes = job.TotalBytes!.Value;
                long committedBytes = Math.Clamp(job.CommittedBytes, 0, totalBytes);
                long remaining = totalBytes - committedBytes;
                total.KnownRemainingBytes = AddSaturated(total.KnownRemainingBytes, remaining);
            }
            else total.UnknownJobs++;
        }

        var result = new List<DiskForecastVolume>(totals.Count);
        foreach ((string root, Totals total) in totals.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            long reserve = total.KnownRemainingBytes == 0 && total.UnknownJobs == 0 ? 0 : Math.Max(0, safetyReserveBytes);
            long required = AddSaturated(total.KnownRemainingBytes, reserve);
            available.TryGetValue(root, out long? free);
            result.Add(new DiskForecastVolume(root, total.KnownRemainingBytes, reserve, required, total.UnknownJobs, free));
        }
        return result;
    }

    public static string Summary(IReadOnlyList<DiskForecastVolume> volumes)
    {
        return volumes.Count switch
        {
            0 => "",
            1 => $"Low disk space on {volumes[0].Root.TrimEnd(Path.DirectorySeparatorChar)}",
            _ => $"Low disk space on {volumes.Count} drives"
        };
    }

    public static string Details(IReadOnlyList<DiskForecastVolume> volumes)
    {
        var lines = new List<string>();
        foreach (DiskForecastVolume volume in volumes.Take(MaxVolumeChecks))
        {
            if (volume.AvailableBytes is not long free) continue;
            string drive = volume.Root.TrimEnd(Path.DirectorySeparatorChar);
            lines.Add($"{drive} has {Formatting.Bytes(free)} free. Keep at least {Formatting.Bytes(volume.RequiredBytes)} free for this queue.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static bool IsPending(JobSnapshot job) => job.State is not (JobState.Completed or JobState.Finalizing
        or JobState.Verifying or JobState.Suspect);

    private static bool CanEstimate(JobSnapshot job) => job.Kind == JobKind.Http && job.Output == MediaOutput.Original
        && job.State != JobState.NeedsLink && !job.IsLiveRecording && job.TotalBytes is >= 0;

    private static string? Root(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)) return null;
        try { return Path.GetPathRoot(destination); }
        catch (ArgumentException) { return null; }
    }

    private static long AddSaturated(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;
}
