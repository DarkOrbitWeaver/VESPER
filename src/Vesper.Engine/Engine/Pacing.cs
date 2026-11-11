using System.Diagnostics;

namespace Vesper.Engine;

/// <summary>
/// Bandwidth limiter based on booking time slots: each caller reserves the next free slot for its
/// bytes and sleeps until that slot ends. That keeps competing callers (jobs sharing the global
/// limiter) in first-come order without a background thread. A rate of 0 means unlimited.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private long _nextFree;   // Stopwatch ticks

    public long BytesPerSecond => Interlocked.Read(ref _bytesPerSecond);

    public void SetRate(long bytesPerSecond) => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, bytesPerSecond));

    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        long rate = BytesPerSecond;
        if (rate <= 0 || bytes <= 0) return;

        long waitTicks;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            long burstCredit = Stopwatch.Frequency / 4;          // idle time banks at most 250 ms
            long start = Math.Max(_nextFree, now - burstCredit);
            _nextFree = start + (long)((double)bytes * Stopwatch.Frequency / rate);
            waitTicks = _nextFree - now;
        }
        if (waitTicks <= 0) return;
        TimeSpan delay = TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency);
        if (delay > TimeSpan.FromMilliseconds(2)) await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}

/// <summary>Exponential backoff with jitter that honours Retry-After as a floor.</summary>
public sealed class RetryPolicy(RetryOptions options, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;

    public bool ShouldGiveUp(int consecutiveFailures) => consecutiveFailures >= options.MaxAttempts;

    public TimeSpan Delay(int consecutiveFailures, TimeSpan? retryAfter = null)
    {
        double exponent = Math.Min(consecutiveFailures, 20);
        double ceiling = Math.Min(options.MaxDelay.TotalSeconds, options.BaseDelay.TotalSeconds * Math.Pow(2, exponent));
        // "Equal jitter": half fixed, half random. Avoids both thundering herds and near-zero delays.
        double seconds = ceiling / 2 + _random.NextDouble() * ceiling / 2;
        if (retryAfter is { } floor) seconds = Math.Max(seconds, floor.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// Decides how many connections a job should use. It only reacts to measured throughput:
/// add one when the last addition helped, back off when it did not, halve when the server throttles.
/// </summary>
public sealed class ConnectionAdvisor
{
    private int _ceiling;
    private double _throughputAtLastChange;
    private int _countAtLastChange = 1;

    public ConnectionAdvisor(int maximum)
    {
        Maximum = maximum;
        _ceiling = maximum;
    }

    public int Maximum { get; }

    public int Recommend(int current, double bytesPerSecond, TimeSpan sinceLastChange, TimeSpan rampInterval)
    {
        current = Math.Clamp(current, 1, Math.Max(1, _ceiling));
        if (sinceLastChange < rampInterval) return current;

        int next = current;
        if (current == 1)
        {
            next = 2;                                                   // always try one more once running
        }
        else if (bytesPerSecond > _throughputAtLastChange * 1.15)
        {
            next = current + 1;                                         // the last connection paid for itself
        }
        else if (bytesPerSecond < _throughputAtLastChange * 0.9 && current > 1)
        {
            next = current - 1;                                         // the last connection hurt: stop growing
            _ceiling = next;
        }
        else
        {
            _ceiling = current;                                         // plateau
        }

        next = Math.Clamp(next, 1, Math.Max(1, _ceiling));
        if (next != current || next == _countAtLastChange) { _throughputAtLastChange = bytesPerSecond; _countAtLastChange = next; }
        return next;
    }

    /// <summary>The server answered 429/503: cut connections in half and stop probing upward.</summary>
    public int OnThrottled(int current)
    {
        _ceiling = Math.Max(1, current / 2);
        return _ceiling;
    }
}
