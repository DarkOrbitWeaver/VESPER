namespace Vesper.Engine;

public enum ProxyMode { Direct, System, Custom }

public sealed class RetryOptions
{
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Consecutive failed runs without any committed progress before a job is marked Failed.</summary>
    public int MaxAttempts { get; init; } = 8;
}

/// <summary>Optional download window. Outside it nothing starts and running jobs are re-queued.</summary>
public sealed record SchedulePolicy(bool Enabled, TimeOnly Start, TimeOnly End)
{
    public static SchedulePolicy Always { get; } = new(false, new TimeOnly(0, 0), new TimeOnly(0, 0));

    public bool IsOpen(DateTime local)
    {
        if (!Enabled || Start == End) return true;
        TimeOnly now = TimeOnly.FromDateTime(local);
        return Start < End ? now >= Start && now < End : now >= Start || now < End; // window may wrap midnight
    }

    /// <summary>Next moment the window opens or closes, so a single timer can be armed instead of polling.</summary>
    public DateTime? NextBoundary(DateTime local)
    {
        if (!Enabled || Start == End) return null;
        DateTime a = local.Date + Start.ToTimeSpan();
        DateTime b = local.Date + End.ToTimeSpan();
        return new[] { a, b, a.AddDays(1), b.AddDays(1) }.Where(t => t > local).Min();
    }
}

public sealed class EngineOptions
{
    public required string DataDirectory { get; init; }
    public string DefaultFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string? BrowserDownloadsFolder { get; init; }

    public int MaxConcurrentJobs { get; set; } = 3;
    public int MaxConnectionsPerJob { get; set; } = 4;
    public int MaxJobsPerHost { get; set; } = 2;
    public int MaxConnectionsPerHost { get; set; } = 8;
    public long GlobalLimitBytesPerSecond { get; set; }
    public bool CategorySubfolders { get; set; }
    public SchedulePolicy Schedule { get; set; } = SchedulePolicy.Always;
    public bool PreventSleepWhileActive { get; set; }

    /// <summary>Loopback/private targets are blocked unless the user explicitly allows them.</summary>
    public bool AllowPrivateNetworks { get; init; }
    public ProxyMode Proxy { get; init; } = ProxyMode.Direct;
    public string? ProxyAddress { get; init; }
    public OriginCooldown Cooldown { get; } = new();

    public RetryOptions Retry { get; init; } = new();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Durability checkpoint cadence: whichever limit is reached first.</summary>
    public long CheckpointBytes { get; init; } = 4L * 1024 * 1024;
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);

    public long MinSegmentedSize { get; init; } = 8L * 1024 * 1024;
    public long MinSplitBytes { get; init; } = 2L * 1024 * 1024;
    public TimeSpan RampInterval { get; init; } = TimeSpan.FromSeconds(3);
    public long FreeSpaceReserve { get; init; } = 64L * 1024 * 1024;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    internal Action<string, string, string, MediaOutput, CancellationToken>? ConvertMedia { get; init; }

    /// <summary>
    /// Fault injection at durability boundaries. A callback may throw <see cref="FailpointCrash"/>
    /// to simulate the process dying at exactly that point; the engine then persists nothing further.
    /// </summary>
    public Action<string>? Failpoint { get; init; }
}

/// <summary>Simulates abrupt process death at a failpoint. Never caught by engine error handling.</summary>
public sealed class FailpointCrash(string point) : Exception($"Simulated crash at {point}")
{
    public string Point { get; } = point;
}
