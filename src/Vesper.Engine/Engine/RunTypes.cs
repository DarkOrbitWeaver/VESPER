namespace Vesper.Engine;

internal enum StopIntent { None, Pause, Shutdown, Window, MeteredNetwork, BrowserFallback, LiveStop, Cancel }

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
    public long SampleAt { get; set; }
    public long SampleBytes { get; set; }
    public double Rate { get; set; }
    internal LinkedListNode<Segment>? Node { get; set; }
}

/// <summary>
/// Hands out non-overlapping spans to connections. When no free span is left, the largest running span is
/// split in two, which adapts naturally to connections of different speed. All mutation happens under one lock.
/// </summary>
internal sealed class SegmentBoard(long minSplitBytes, bool rateAware = false)
{
    private const long Alignment = 64 * 1024;
    private readonly object _gate = new();
    private readonly LinkedList<Segment> _segments = new();

    public void Add(Segment segment) { lock (_gate) segment.Node = _segments.AddLast(segment); }

    public bool HasWork { get { lock (_gate) return _segments.Any(s => s.Next < s.End); } }

    public bool HasAssignableWork
    {
        get
        {
            lock (_gate) return _segments.Any(s => !s.Owned && s.Next < s.End
                || s.Owned && s.End - Math.Max(s.Next, s.Claimed) >= 2 * minSplitBytes);
        }
    }

    public long RemainingBytes { get { lock (_gate) return _segments.Sum(s => Math.Max(0, s.End - s.Next)); } }

    public long Remaining(Segment segment) { lock (_gate) return Math.Max(0, segment.End - segment.Next); }

    public Segment? Acquire()
    {
        lock (_gate)
        {
            Segment? free = _segments.FirstOrDefault(s => !s.Owned && s.Next < s.End);
            if (free is not null) { free.Owned = true; free.SampleAt = 0; free.SampleBytes = 0; free.Rate = 0; return free; }

            // Nothing free: split the span with the most bytes left, if it is worth a new connection.
            double helperRate = 0;
            if (rateAware)
            {
                int samples = 0;
                foreach (Segment segment in _segments)
                {
                    if (!segment.Owned || segment.Rate <= 0 || SplittableBytes(segment) < 2 * minSplitBytes) continue;
                    helperRate += segment.Rate;
                    samples++;
                }
                if (samples > 0) helperRate /= samples;
            }
            Segment? largest = null;
            double longest = 0;
            foreach (Segment segment in _segments)
            {
                long bytes = SplittableBytes(segment);
                if (!segment.Owned || bytes < 2 * minSplitBytes) continue;
                double cost = helperRate > 0 ? bytes / (segment.Rate > 0 ? segment.Rate : helperRate) : bytes;
                if (cost <= longest) continue;
                longest = cost;
                largest = segment;
            }
            if (largest is null) return null;

            long floor = Math.Max(largest.Next, largest.Claimed);
            long remaining = largest.End - floor;
            long stolen = remaining / 2;
            if (helperRate > 0 && largest.Rate > 0)
                stolen = (long)Math.Clamp(helperRate * (remaining - largest.Rate * 0.15) / (helperRate + largest.Rate), minSplitBytes, remaining - minSplitBytes);
            long mid = (largest.End - stolen) / Alignment * Alignment;
            mid = Math.Clamp(mid, floor + minSplitBytes, largest.End - minSplitBytes);

            var tail = new Segment(mid, largest.End) { Owned = true };
            largest.End = mid;
            tail.Node = _segments.AddAfter(largest.Node!, tail);
            return tail;
        }
    }

    // A still-open validated response can cover an adjacent, untouched free span.
    public bool TryExtend(Segment segment, long responseEndExclusive)
    {
        lock (_gate)
        {
            if (!segment.Owned || segment.Next != segment.End) return false;
            Segment? next = _segments.FirstOrDefault(s => !s.Owned && s.Start == segment.End
                && s.Next == s.Start && s.Claimed <= s.Start && s.End <= responseEndExclusive);
            if (next is null) return false;
            segment.End = next.End;
            Remove(next);
            return true;
        }
    }

    public void Release(Segment segment)
    {
        lock (_gate)
        {
            segment.Owned = false;
            if (segment.Next == segment.End) Remove(segment);
        }
    }

    private static long SplittableBytes(Segment segment) => segment.End - Math.Max(segment.Next, segment.Claimed);

    private void Remove(Segment segment)
    {
        if (segment.Node is not { } node) return;
        _segments.Remove(node);
        segment.Node = null;
    }

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

    public void Advance(Segment segment, int written)
    {
        lock (_gate)
        {
            segment.Next += written;
            if (!rateAware) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (segment.SampleAt == 0) segment.SampleAt = now;
            segment.SampleBytes += written;
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(segment.SampleAt, now).TotalSeconds;
            if (elapsed < 0.25) return;
            double rate = segment.SampleBytes / elapsed;
            segment.Rate = segment.Rate == 0 ? rate : segment.Rate * 0.5 + rate * 0.5;
            segment.SampleAt = now;
            segment.SampleBytes = 0;
        }
    }
}
