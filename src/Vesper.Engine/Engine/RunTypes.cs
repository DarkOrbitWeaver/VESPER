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

/// <summary>A byte span owned by one worker, with the end of its current HTTP range.</summary>
internal sealed class Segment(long start, long end)
{
    public long Start { get; } = start;
    public long Next { get; set; } = start;
    public long End { get; set; } = end;
    /// <summary>End of the chunk currently being written to disk; a split may never cut below it.</summary>
    public long Claimed { get; set; } = start;
    /// <summary>Exclusive end of the currently in-flight HTTP Range request window, so splits prefer cutting above the active wire window.</summary>
    public long WireEnd { get; set; } = start;
    public bool Owned { get; set; }
    public long SampleAt { get; set; }
    public long SampleBytes { get; set; }
    public double Rate { get; set; }
    public long? LastProgressAt { get; set; }
    internal LinkedListNode<Segment>? Node { get; set; }
}

/// <summary>
/// Dynamic adjacent-segment work-stealing scheduler.
/// Splits straggling spans proportional to measured worker speed, prefers splitting beyond active HTTP wire windows
/// so the current response can finish, and lowers split granularity during the
/// final 10 % tail so idle workers close slow-tail stragglers immediately.
/// </summary>
internal sealed class SegmentBoard(long minSplitBytes, bool rateAware = false, long totalBytes = 0, TimeProvider? clock = null)
{
    private const long Alignment = 64 * 1024;
    private const long TailMinSplitBytes = 256 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly LinkedList<Segment> _segments = new();

    private long EffectiveMinSplitLocked()
    {
        if (!rateAware || totalBytes <= 0) return minSplitBytes;
        long rem = _segments.Sum(s => Math.Max(0, s.End - s.Next));
        if (rem <= totalBytes / 10)
            return Math.Min(minSplitBytes, TailMinSplitBytes);
        return minSplitBytes;
    }

    public void Add(Segment segment) { lock (_gate) segment.Node = _segments.AddLast(segment); }

    public bool HasWork { get { lock (_gate) return _segments.Any(s => s.Next < s.End); } }

    public bool HasAssignableWork
    {
        get
        {
            lock (_gate)
            {
                long splitFloor = EffectiveMinSplitLocked();
                return _segments.Any(s => !s.Owned && s.Next < s.End
                    || s.Owned && s.End - Math.Max(s.Next, s.Claimed) >= 2 * splitFloor);
            }
        }
    }

    public long RemainingBytes { get { lock (_gate) return _segments.Sum(s => Math.Max(0, s.End - s.Next)); } }

    public long Remaining(Segment segment) { lock (_gate) return Math.Max(0, segment.End - segment.Next); }

    public long PlanWireWindowEnd(Segment segment, long preferredWindowBytes, bool boundedWindow)
    {
        lock (_gate)
        {
            if (!boundedWindow)
            {
                segment.WireEnd = segment.End;
                return segment.End;
            }
            long window = Math.Max(Alignment * 4, preferredWindowBytes);
            long wireEnd = Math.Min(segment.End, segment.Next + window);
            // If remaining after this window is tiny (< 512 KiB), absorb it into one request to save a round-trip
            if (segment.End - wireEnd < 512 * 1024) wireEnd = segment.End;
            segment.WireEnd = wireEnd;
            return wireEnd;
        }
    }

    public Segment? Acquire(double callerRateHint = 0)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            // 1. Prefer an unowned free gap (in sequential file order for locality)
            Segment? free = _segments.FirstOrDefault(s => !s.Owned && s.Next < s.End);
            if (free is not null)
            {
                free.Owned = true;
                free.SampleAt = 0;
                free.LastProgressAt = now;
                free.SampleBytes = 0;
                free.Rate = callerRateHint > 0 ? callerRateHint : 0;
                return free;
            }

            long splitMin = EffectiveMinSplitLocked();
            double helperRate = callerRateHint;
            if (rateAware && helperRate <= 0)
            {
                int samples = 0;
                foreach (Segment segment in _segments)
                {
                    if (!segment.Owned || segment.Rate <= 0) continue;
                    helperRate += EffectiveRate(segment, now);
                    samples++;
                }
                if (samples > 0) helperRate /= samples;
            }

            // 2. Steal from the highest-ETA straggler (preferring clean splits above WireEnd so the victim's HTTP response drains cleanly!)
            Segment? victim = null;
            double highestEta = 0;
            double selectedRate = 0;
            foreach (Segment segment in _segments)
            {
                long bytes = SplittableBytes(segment);
                if (!segment.Owned || bytes < 2 * splitMin) continue;
                double measuredRate = EffectiveRate(segment, now);
                double victimRate = measuredRate > 0 ? measuredRate : (helperRate > 0 ? helperRate : 1.0);
                double eta = rateAware ? bytes / victimRate : bytes;
                // Slight bonus if we can split cleanly past the victim's active HTTP wire window without truncating its in-flight stream
                if (segment.WireEnd > segment.Next && segment.End - segment.WireEnd >= splitMin)
                    eta *= 1.15;
                if (eta > highestEta)
                {
                    highestEta = eta;
                    victim = segment;
                    selectedRate = measuredRate;
                }
            }
            if (victim is null) return null;

            long floor = Math.Max(victim.Next, victim.Claimed);
            long remaining = victim.End - floor;
            long stolen = remaining / 2;
            if (helperRate > 0 && selectedRate > 0)
            {
                stolen = (long)Math.Clamp(
                    helperRate * remaining / (helperRate + selectedRate),
                    splitMin,
                    remaining - splitMin);
            }
            long mid = (victim.End - stolen) / Alignment * Alignment;
            // Snap split point at or above victim.WireEnd when possible so victim's in-flight HTTP response finishes cleanly for keep-alive reuse!
            if (victim.WireEnd > floor + splitMin && victim.WireEnd <= victim.End - splitMin && Math.Abs(mid - victim.WireEnd) <= 4 * 1024 * 1024)
            {
                mid = victim.WireEnd;
            }
            mid = Math.Clamp(mid, floor + splitMin, victim.End - splitMin);

            var tail = new Segment(mid, victim.End)
            {
                Owned = true,
                LastProgressAt = now,
                Rate = callerRateHint > 0 ? callerRateHint : 0
            };
            victim.End = mid;
            if (victim.WireEnd > mid) victim.WireEnd = mid;
            tail.Node = _segments.AddAfter(victim.Node!, tail);
            return tail;
        }
    }

    // Absorbs an immediately adjacent unowned span so a warm worker continues sequentially without fragmenting extents.
    public bool TryExtend(Segment segment, long responseEndExclusive)
    {
        lock (_gate)
        {
            if (!segment.Owned || segment.Next != segment.End) return false;
            Segment? next = segment.Node?.Next?.Value;
            if (next is null || next.Owned || next.Start != segment.End
                || next.Next != next.Start || next.Claimed > next.Start)
                return false;
            // If the open response covers only part of an untouched adjacent
            // span, reclaim exactly that prefix and leave the remaining gap
            // available to other workers. Never reclaim committed/claimed bytes.
            long end = Math.Min(next.End, responseEndExclusive);
            if (end <= next.Start) return false;
            if (end < next.End)
            {
                var remaining = new Segment(end, next.End);
                remaining.Node = _segments.AddAfter(next.Node!, remaining);
            }
            segment.End = end;
            Remove(next);
            return true;
        }
    }

    public void Release(Segment segment)
    {
        lock (_gate)
        {
            segment.Owned = false;
            segment.WireEnd = segment.Next;
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

    public int ClaimChunk(Segment segment, int want)
    {
        lock (_gate)
        {
            int n = (int)Math.Min(want, Math.Max(0, segment.End - segment.Next));
            segment.Claimed = segment.Next + n;
            return n;
        }
    }

    // A formerly fast connection can stop delivering. Do not rank a stalled
    // span using an indefinitely fresh throughput sample. Keep a nonzero
    // floor to avoid pathological split proportions; claims are unchanged.
    private double EffectiveRate(Segment segment, long now)
    {
        if (!rateAware || segment.Rate <= 0 || segment.LastProgressAt is not long last)
            return segment.Rate;
        double idle = _clock.GetElapsedTime(last, now).TotalSeconds;
        return segment.Rate / Math.Clamp(idle, 1.0, 20.0);
    }

    public void Advance(Segment segment, int written)
    {
        lock (_gate)
        {
            segment.Next += written;
            if (!rateAware) return;
            long now = _clock.GetTimestamp();
            if (written > 0) segment.LastProgressAt = now;
            if (segment.SampleAt == 0) segment.SampleAt = now;
            segment.SampleBytes += written;
            double elapsed = _clock.GetElapsedTime(segment.SampleAt, now).TotalSeconds;
            if (elapsed < 0.2) return;
            double rate = segment.SampleBytes / elapsed;
            segment.Rate = segment.Rate == 0 ? rate : segment.Rate * 0.55 + rate * 0.45;
            segment.SampleAt = now;
            segment.SampleBytes = 0;
        }
    }
}
