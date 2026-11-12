using System.Diagnostics;

namespace Vesper.Engine;

/// <summary>Samples unique byte progress for the ETA without changing the transfer scheduler's rate meter.</summary>
internal sealed class TransferEstimate
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private readonly Queue<(long Time, long Bytes)> _samples = new(17);
    private long? _dipStarted;
    private double? _lastSeconds;
    private double? _tailReferenceSeconds;

    public TimeSpan? Remaining { get; private set; }
    public bool Estimating { get; private set; }
    public bool WaitingForData { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        _dipStarted = null;
        _lastSeconds = null;
        _tailReferenceSeconds = null;
        Remaining = null;
        Estimating = false;
        WaitingForData = false;
    }

    public void Sample(long now, long bytes, long? total)
    {
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && Stopwatch.GetElapsedTime(_samples.Peek().Time, now) > Window)
            _samples.Dequeue();

        var first = _samples.Peek();
        double seconds = Stopwatch.GetElapsedTime(first.Time, now).TotalSeconds;
        long advanced = bytes - first.Bytes;
        WaitingForData = seconds >= 2.5 && advanced <= 0;
        Remaining = null;
        Estimating = false;
        if (total is not > 0 || bytes >= total)
        {
            _dipStarted = null;
            _lastSeconds = null;
            _tailReferenceSeconds = null;
            return;
        }

        long remaining = total.Value - bytes;
        double? estimate = seconds >= 0.5 && advanced > 0 ? remaining * seconds / advanced : null;
        bool tail = bytes >= total.Value * 0.95;
        if (tail) _tailReferenceSeconds ??= _lastSeconds ?? estimate;
        else _tailReferenceSeconds = null;
        bool tailDip = _tailReferenceSeconds is double reference
            && (estimate is null || estimate.Value > reference * 2 && estimate.Value - reference >= 5);
        bool recovered = estimate is double current && _tailReferenceSeconds is double prior
            && current <= Math.Max(prior * 2, prior + 1);
        if (tail && (tailDip || _dipStarted.HasValue && !recovered))
        {
            _dipStarted ??= now;
            if (Stopwatch.GetElapsedTime(_dipStarted.Value, now) < Window)
            {
                Estimating = true;
                return;
            }
            _tailReferenceSeconds = estimate;
        }
        else if (tail && estimate < _tailReferenceSeconds) _tailReferenceSeconds = estimate;
        _dipStarted = null;
        _lastSeconds = estimate;
        Remaining = estimate is double value && value < TimeSpan.MaxValue.TotalSeconds - 1
            ? TimeSpan.FromSeconds(value) : null;
    }
}
