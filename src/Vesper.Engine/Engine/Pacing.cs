namespace Vesper.Engine;

/// <summary>
/// FIFO bandwidth limiter with a bounded burst. A rate change wakes the current waiter so it can
/// recalculate using the new rate. A rate of 0 means unlimited.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly LinkedList<Waiter> _waiters = [];
    private long _bytesPerSecond;
    private long _revision;
    private long _lastRefill;
    private double _tokens;

    private const double BurstSeconds = 0.25;

    public RateLimiter() : this(TimeProvider.System) { }

    internal RateLimiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lastRefill = timeProvider.GetTimestamp();
    }

    public long BytesPerSecond => Interlocked.Read(ref _bytesPerSecond);

    internal long Revision => Interlocked.Read(ref _revision);

    public void SetRate(long bytesPerSecond)
    {
        long value = Math.Max(0, bytesPerSecond);
        lock (_gate)
        {
            long previous = _bytesPerSecond;
            if (previous == value) return;

            long now = _timeProvider.GetTimestamp();
            RefillTokensLocked(now, CapacityForHeadLocked(previous));
            Interlocked.Exchange(ref _bytesPerSecond, value);
            Interlocked.Increment(ref _revision);

            if (value == 0)
                _tokens = 0;
            else if (previous == 0)
                _tokens = BurstCapacity(value);
            else
                _tokens = Math.Min(_tokens, CapacityForHeadLocked(value));

            _lastRefill = now;
            PulseHeadLocked();
        }
    }

    public ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        if (bytes <= 0) return ValueTask.CompletedTask;
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (_waiters.Count == 0)
            {
                if (_bytesPerSecond == 0) return ValueTask.CompletedTask;
                RefillTokensLocked(_timeProvider.GetTimestamp(), BurstCapacity(_bytesPerSecond));
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return ValueTask.CompletedTask;
                }
            }
            waiter = new Waiter(bytes, ct);
            waiter.Node = _waiters.AddLast(waiter);
        }

        return WaitAsync(waiter);
    }

    private async ValueTask WaitAsync(Waiter waiter)
    {
        try
        {
            await WaitForTurnAsync(waiter).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Remove(waiter);
            throw;
        }
    }

    private async ValueTask WaitForTurnAsync(Waiter waiter)
    {
        while (true)
        {
            Task changed;
            TimeSpan delay;
            bool isHead;
            lock (_gate)
            {
                waiter.CancellationToken.ThrowIfCancellationRequested();
                if (waiter.Node is null) throw new OperationCanceledException(waiter.CancellationToken);

                isHead = ReferenceEquals(_waiters.First?.Value, waiter);
                changed = waiter.Changed.Task;
                if (!isHead)
                {
                    delay = TimeSpan.Zero;
                }
                else if (_bytesPerSecond == 0)
                {
                    _tokens = 0;
                    _lastRefill = _timeProvider.GetTimestamp();
                    RemoveLocked(waiter);
                    PulseHeadLocked();
                    return;
                }
                else
                {
                    long now = _timeProvider.GetTimestamp();
                    RefillTokensLocked(now, CapacityForLocked(_bytesPerSecond, waiter.Bytes));
                    if (_tokens >= waiter.Bytes)
                    {
                        _tokens -= waiter.Bytes;
                        _tokens = Math.Min(_tokens, BurstCapacity(_bytesPerSecond));
                        RemoveLocked(waiter);
                        PulseHeadLocked();
                        return;
                    }

                    double seconds = (waiter.Bytes - _tokens) / _bytesPerSecond;
                    if (seconds <= 0.002)
                    {
                        _tokens -= waiter.Bytes;
                        RemoveLocked(waiter);
                        PulseHeadLocked();
                        return;
                    }
                    delay = TimeSpan.FromSeconds(seconds);
                }
            }

            if (!isHead)
            {
                await changed.WaitAsync(waiter.CancellationToken).ConfigureAwait(false);
                continue;
            }

            await WaitForChangeOrDelayAsync(delay, changed, waiter.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForChangeOrDelayAsync(TimeSpan delay, Task changed, CancellationToken ct)
    {
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task timer = Task.Delay(delay, _timeProvider, timerCancellation.Token);
        Task winner = await Task.WhenAny(timer, changed).ConfigureAwait(false);
        if (winner == changed)
        {
            timerCancellation.Cancel();
            try { await timer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            return;
        }

        await timer.ConfigureAwait(false);
    }

    private void Remove(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return;
            bool wasHead = ReferenceEquals(_waiters.First?.Value, waiter);
            RemoveLocked(waiter);
            if (!wasHead) return;

            RefillTokensLocked(_timeProvider.GetTimestamp(), CapacityForLocked(_bytesPerSecond, waiter.Bytes));
            _tokens = Math.Min(_tokens, BurstCapacity(_bytesPerSecond));
            _lastRefill = _timeProvider.GetTimestamp();
            PulseHeadLocked();
        }
    }

    private void RemoveLocked(Waiter waiter)
    {
        if (waiter.Node is null) return;
        _waiters.Remove(waiter.Node);
        waiter.Node = null;
    }

    private void RefillTokensLocked(long now, double capacity)
    {
        long elapsed = Math.Max(0, now - _lastRefill);
        _tokens = Math.Min(capacity, _tokens + (double)elapsed * _bytesPerSecond / _timeProvider.TimestampFrequency);
        _lastRefill = now;
    }

    private double CapacityForLocked(long rate, int? currentRequestSize = null) =>
        Math.Max(BurstCapacity(rate), currentRequestSize ?? 0);

    private double CapacityForHeadLocked(long rate) =>
        _waiters.First is { } node ? CapacityForLocked(rate, node.Value.Bytes) : BurstCapacity(rate);

    private static double BurstCapacity(long rate) => rate * BurstSeconds;

    private void PulseHeadLocked()
    {
        if (_waiters.First is not { } node) return;
        TaskCompletionSource previous = node.Value.Changed;
        node.Value.Changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Waiter(int bytes, CancellationToken cancellationToken)
    {
        internal int Bytes { get; } = bytes;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal LinkedListNode<Waiter>? Node;
        internal TaskCompletionSource Changed = NewSignal();
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
        TimeSpan backoff = TimeSpan.FromSeconds(seconds);
        return retryAfter is { } floor && floor > backoff ? floor : backoff;
    }
}

/// <summary>
/// Decides how many connections a job should use. It only reacts to measured throughput:
/// add one when the last addition helped, back off when it did not, halve when the server throttles.
/// </summary>
public sealed class ConnectionAdvisor
{
    internal const long SmallFileMinimum = 1024 * 1024;
    internal const long SmallSplitMinimum = 256 * 1024;

    internal static bool WorthSmallFileSplit(long received, long remaining, TimeSpan elapsed)
    {
        // Measure from the first response, not from DNS/TLS or the resolving phase.
        // A stalled connection with no verified bytes does not justify more requests.
        if (elapsed < TimeSpan.FromMilliseconds(750) || received < 16 * 1024
            || remaining < 2 * SmallSplitMinimum) return false;
        return remaining / (received / elapsed.TotalSeconds) >= 3;
    }

    private int _ceiling;
    private bool _throttled;
    private long? _cruiseSinceTicks;
    private bool _preferDown;
    private readonly TimeProvider _clock;
    private double _baselineRate;
    private int _baselineCount;
    private int _probeCount;

    private static readonly TimeSpan ReprobeInterval = TimeSpan.FromSeconds(10);

    public ConnectionAdvisor(int maximum) : this(maximum, TimeProvider.System) { }

    internal ConnectionAdvisor(int maximum, TimeProvider clock)
    {
        Maximum = maximum;
        _ceiling = maximum;
        _clock = clock;
    }

    public int Maximum { get; }

    // External changes (rate limits, host slots, a draining worker) invalidate
    // the comparison. Only compare samples measured with a stable population.
    internal void ResetMeasurement()
    {
        _baselineRate = 0;
        _baselineCount = _probeCount = 0;
        _cruiseSinceTicks = null;
        _preferDown = false;
    }

    public int Recommend(int current, double bytesPerSecond, TimeSpan sinceLastChange, TimeSpan rampInterval)
    {
        current = Math.Clamp(current, 1, Math.Max(1, _ceiling));
        if (_throttled || sinceLastChange < rampInterval || !double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
            return current;

        int step = 1;
        if (_probeCount == current && _baselineCount > 0)
        {
            double gain = bytesPerSecond / _baselineRate;
            bool up = current > _baselineCount;
            // Accept more workers only for a meaningful gain; accept fewer
            // when they retain at least 95% of throughput.
            bool useful = up ? gain >= 1.10 : gain >= 0.95;
            _probeCount = 0;
            if (!useful)
            {
                _cruiseSinceTicks = _clock.GetTimestamp();
                _preferDown = up;
                return _baselineCount;
            }
            step = up && gain >= 1.35 && current < 4 ? 2 : 1;
            _baselineCount = current;
            _baselineRate = bytesPerSecond;
            if (!up || current == _ceiling)
            {
                _cruiseSinceTicks = _clock.GetTimestamp();
                _preferDown = current > 1;
                return current;
            }
        }
        else
        {
            if (_baselineCount != current)
            {
                _probeCount = 0;
                _baselineCount = current;
            }
            _baselineRate = bytesPerSecond;
        }

        bool cruising = _cruiseSinceTicks is long;
        if (_cruiseSinceTicks is long since && _clock.GetElapsedTime(since) < ReprobeInterval)
            return current;
        _cruiseSinceTicks = null;

        // After a plateau, test whether fewer workers sustain the same rate.
        // A rejected down-probe schedules an up-probe on the next cruise.
        int next = current > 1 && (current == _ceiling || cruising && _preferDown)
            ? current - 1 : Math.Min(_ceiling, current + step);
        if (next < 1 || next == current) return current;
        _probeCount = next;
        return next;
    }

    /// <summary>The server answered 429/503: cut connections in half and lock the ceiling.</summary>
    public int OnThrottled(int current)
    {
        _ceiling = Math.Max(1, current / 2);
        _throttled = true;
        return _ceiling;
    }
}

internal sealed class SegmentLookahead(EngineOptions options, RateLimiter local, RateLimiter global)
{
    private int _window = 3;
    private int _samples, _slow, _ready;

    // The buffer limiter independently bounds total memory, even across jobs.
    internal int Limit(string url)
    {
        if (local.BytesPerSecond > 0 || global.BytesPerSecond > 0
            || Uri.TryCreate(url, UriKind.Absolute, out var uri) && options.Cooldown.WasRecentlyThrottled(uri))
            return 1;
        return Math.Clamp(_window, 1, Math.Max(1, Math.Min(SegmentBufferLimiter.MaxRetainedSegments,
            Math.Min(options.MaxConnectionsPerJob, options.MaxConnectionsPerHost))));
    }

    // Measure time the ordered writer actually waited for its next object.
    // Ready objects mean lookahead is already hiding the network latency.
    internal void ObserveWait(TimeSpan wait)
    {
        if (wait >= TimeSpan.FromMilliseconds(125)) _slow++;
        else if (wait <= TimeSpan.FromMilliseconds(10)) _ready++;
        if (++_samples < 6) return;

        // A burst of ready objects between stalls is normal with parallel requests.
        // Evaluate the whole window, rather than requiring consecutive slow objects.
        if (_slow >= 2) _window = Math.Min(SegmentBufferLimiter.MaxRetainedSegments, _window + 1);
        else if (_ready >= 5 && _slow == 0) _window = Math.Max(2, _window - 1);
        _samples = _slow = _ready = 0;
    }
}
