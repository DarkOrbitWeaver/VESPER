namespace Vesper.Engine;

internal enum StopIntent { None, Pause, Shutdown, Window, BrowserFallback }

/// <summary>How one run of a job ended. The engine turns it into the next durable state.</summary>
internal readonly record struct RunOutcome(JobState State, string Reason, string Message, TimeSpan? RetryAfter = null)
{
    public static RunOutcome NeedsAttention(string reason, string message) => new(JobState.NeedsAttention, reason, message);
    public static RunOutcome NeedsLink(string reason, string message) => new(JobState.NeedsLink, reason, message);
    public static RunOutcome Failed(string reason, string message) => new(JobState.Failed, reason, message);
    public static RunOutcome Retry(string reason, string message, TimeSpan? after = null) => new(JobState.WaitingForRetry, reason, message, after);
    public static RunOutcome WaitNetwork(string message) => new(JobState.WaitingForNetwork, "network", message);
    public static RunOutcome Done() => new(JobState.Completed, "", "");
}

/// <summary>A deliberate, already-classified end of a run (as opposed to an unexpected exception).</summary>
internal sealed class RunnerStop(RunOutcome outcome) : Exception(outcome.Message)
{
    public RunOutcome Outcome { get; } = outcome;
}

/// <summary>A range response contradicted what was requested. Segmentation is no longer defensible.</summary>
internal sealed class RangeProblemException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A byte span owned by one connection. Start is fixed, End shrinks when the span is split.</summary>
internal sealed class Segment(long start, long end)
{
    public long Start { get; } = start;
    public long Next { get; set; } = start;
    public long End { get; set; } = end;
    /// <summary>End of the chunk the owner is about to write; a split may never cut below it.</summary>
    public long Claimed { get; set; } = start;
    public bool Owned { get; set; }
}

/// <summary>
/// Hands out non-overlapping spans to connections. When no free span is left, the largest running span is
/// split in two, which adapts naturally to connections of different speed. All mutation happens under one lock.
/// </summary>
internal sealed class SegmentBoard(long minSplitBytes)
{
    private const long Alignment = 64 * 1024;
    private readonly object _gate = new();
    private readonly List<Segment> _segments = [];

    public void Add(Segment segment) { lock (_gate) _segments.Add(segment); }

    public bool HasWork { get { lock (_gate) return _segments.Any(s => s.Next < s.End); } }

    public long RemainingBytes { get { lock (_gate) return _segments.Sum(s => Math.Max(0, s.End - s.Next)); } }

    public long Remaining(Segment segment) { lock (_gate) return Math.Max(0, segment.End - segment.Next); }

    public Segment? Acquire()
    {
        lock (_gate)
        {
            Segment? free = _segments.FirstOrDefault(s => !s.Owned && s.Next < s.End);
            if (free is not null) { free.Owned = true; return free; }

            // Nothing free: split the span with the most bytes left, if it is worth a new connection.
            Segment? largest = _segments.Where(s => s.Owned && s.Next < s.End).MaxBy(s => s.End - Math.Max(s.Next, s.Claimed));
            if (largest is null) return null;

            long floor = Math.Max(largest.Next, largest.Claimed);
            long remaining = largest.End - floor;
            if (remaining < 2 * minSplitBytes) return null;

            long mid = (floor + remaining / 2) / Alignment * Alignment;
            if (mid <= floor || mid >= largest.End) return null;

            var tail = new Segment(mid, largest.End) { Owned = true };
            largest.End = mid;
            _segments.Add(tail);
            return tail;
        }
    }

    public void Release(Segment segment) { lock (_gate) segment.Owned = false; }

    /// <summary>Reserves up to <paramref name="want"/> bytes for the next write. Returns 0 if the span has shrunk away.</summary>
    public int ClaimChunk(Segment segment, int want)
    {
        lock (_gate)
        {
            int n = (int)Math.Min(want, Math.Max(0, segment.End - segment.Next));
            segment.Claimed = segment.Next + n;
            return n;
        }
    }

    public void Advance(Segment segment, int written) { lock (_gate) segment.Next += written; }
}
