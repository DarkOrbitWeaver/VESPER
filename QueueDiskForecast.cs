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
        if (volumes.Count == 0) return "";
        if (volumes.Count == 1)
        {
            DiskForecastVolume volume = volumes[0];
            string prefix = volume.IsShort ? "Low disk" : "Disk";
            string needed = volume.UnknownJobs == 0
                ? Formatting.Bytes(volume.RequiredBytes) + " needed"
                : volume.KnownRemainingBytes == 0
                    ? $"{volume.UnknownJobs} size{(volume.UnknownJobs == 1 ? "" : "s")} unknown"
                    : $"at least {Formatting.Bytes(volume.RequiredBytes)} known + {volume.UnknownJobs} unknown";
            string free = volume.AvailableBytes is long bytes ? $" · {Formatting.Bytes(bytes)} free" : " · free space unavailable";
            return $"{prefix}: {needed} on {volume.Root}{free}";
        }

        long knownNeeded = 0;
        int unknown = 0;
        int shortVolumes = 0;
        int uncheckedVolumes = 0;
        foreach (DiskForecastVolume volume in volumes)
        {
            knownNeeded = AddSaturated(knownNeeded, volume.RequiredBytes);
            unknown += volume.UnknownJobs;
            if (volume.IsShort) shortVolumes++;
            if (volume.AvailableBytes is null) uncheckedVolumes++;
        }
        string prefixMany = shortVolumes > 0 ? "Low disk" : "Disk";
        return $"{prefixMany}: {Formatting.Bytes(knownNeeded)} baseline across {volumes.Count} locations"
            + (unknown > 0 ? $" · {unknown} size{(unknown == 1 ? "" : "s")} unknown" : "")
            + (shortVolumes > 0 ? $" · {shortVolumes} below estimate" : "")
            + (uncheckedVolumes > 0 ? $" · free space unknown on {uncheckedVolumes}" : "");
    }

    public static string Details(IReadOnlyList<DiskForecastVolume> volumes)
    {
        if (volumes.Count == 0) return "";
        var lines = new List<string>
        {
            "Estimated remaining storage by destination volume:"
        };
        foreach (DiskForecastVolume volume in volumes.Take(MaxVolumeChecks))
        {
            string reserve = volume.SafetyReserveBytes > 0 ? $"{Formatting.Bytes(volume.SafetyReserveBytes)} reserve" : "no reserve";
            string known = volume.KnownRemainingBytes > 0
                ? $"at least {Formatting.Bytes(volume.KnownRemainingBytes)} known" : "no known sizes";
            string needed = volume.UnknownJobs == 0
                ? $"{Formatting.Bytes(volume.KnownRemainingBytes)} remaining + {reserve}"
                : $"{known} + {reserve}; {volume.UnknownJobs} size{(volume.UnknownJobs == 1 ? "" : "s")} unknown";
            string free = volume.AvailableBytes is long bytes ? $"{Formatting.Bytes(bytes)} available" : "free space unavailable";
            lines.Add($"{volume.Root}: {needed}; {free}.");
        }
        if (volumes.Count > MaxVolumeChecks)
            lines.Add($"Free space was not checked for {volumes.Count - MaxVolumeChecks} additional locations.");
        lines.Add("Converted and segmented media sizes are counted as unknown when their final size cannot be estimated. Other applications can also use disk space.");
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
