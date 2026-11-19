namespace Vesper.Engine;

/// <summary>Bounds retained whole-segment buffers across concurrent media jobs.</summary>
internal sealed class SegmentBufferLimiter
{
    private const long DefaultBufferCapacity = 64 * 1024;
    private readonly int _maxSlots;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _activeSlots;
    private long _activeBytes;

    internal SegmentBufferLimiter(int maxSlots = 3, long maxBytes = 256L * 1024 * 1024)
    {
        if (maxSlots < 1) throw new ArgumentOutOfRangeException(nameof(maxSlots));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxSlots = maxSlots;
        _maxBytes = maxBytes;
    }

    internal long ActiveBytes { get { lock (_gate) return _activeBytes; } }
    internal int ActiveSlots { get { lock (_gate) return _activeSlots; } }

    internal static long Reservation(long maxCapacity, long? knownCapacity, int copies = 1)
    {
        if (maxCapacity <= 0 || copies <= 0) throw new ArgumentOutOfRangeException();
        if (knownCapacity is < 0) throw new ArgumentOutOfRangeException(nameof(knownCapacity));
        if (knownCapacity is null) return CapacityReservation(maxCapacity, copies);
        long wanted = Math.Max(knownCapacity.Value, Math.Min(maxCapacity, DefaultBufferCapacity));
        long capacity = Math.Min(maxCapacity, DefaultBufferCapacity);
        while (capacity < wanted && capacity < maxCapacity)
            capacity = capacity >= maxCapacity / 2 ? maxCapacity : capacity * 2;
        return CapacityReservation(capacity, copies);
    }

    internal static long ContentLengthReservation(long maxCapacity, long contentLength, int copies = 1)
    {
        if (maxCapacity <= 0 || contentLength < 0 || contentLength > maxCapacity || copies <= 0)
            throw new ArgumentOutOfRangeException();
        return CapacityReservation(contentLength > 0 ? contentLength : Math.Min(maxCapacity, DefaultBufferCapacity), copies);
    }

    internal static long CapacityReservation(long capacity, int copies = 1)
    {
        if (capacity < 0 || copies <= 0) throw new ArgumentOutOfRangeException();
        return checked(capacity * copies);
    }

    internal ValueTask<SegmentBufferLease> AcquireAsync(long bytes, CancellationToken ct)
    {
        if (bytes < 0 || bytes > _maxBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanGrant(bytes))
            {
                _activeSlots++;
                _activeBytes += bytes;
                return ValueTask.FromResult(new SegmentBufferLease(this, bytes));
            }
            waiter = new Waiter(bytes, ct);
            waiter.Node = _waiters.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
            waiter.Registration = ct.Register(static state =>
            {
                var (limiter, pending) = ((SegmentBufferLimiter, Waiter))state!;
                limiter.Cancel(pending);
            }, (this, waiter));
        return new ValueTask<SegmentBufferLease>(WaitAsync(waiter));
    }

    private async Task<SegmentBufferLease> WaitAsync(Waiter waiter)
    {
        try { return await waiter.Completion.Task.ConfigureAwait(false); }
        finally { waiter.Registration.Dispose(); }
    }

    private bool CanGrant(long bytes) => _activeSlots < _maxSlots && bytes <= _maxBytes - _activeBytes;

    private void Cancel(Waiter waiter)
    {
        List<Waiter> granted;
        lock (_gate)
        {
            if (waiter.Node is null) return;
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            granted = GrantWaitersLocked();
        }
        waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        Complete(granted);
    }

    internal void Release(SegmentBufferLease lease)
    {
        List<Waiter> granted;
        lock (_gate)
        {
            if (!lease.Detach(this, out long bytes)) return;
            _activeSlots--;
            _activeBytes -= bytes;
            granted = GrantWaitersLocked();
        }
        Complete(granted);
    }

    internal void Resize(SegmentBufferLease lease, long bytes)
    {
        List<Waiter> granted;
        lock (_gate)
        {
            if (!lease.TryGetBytes(this, out long current)) throw new ObjectDisposedException(nameof(SegmentBufferLease));
            if (bytes < 0 || bytes > current) throw new ArgumentOutOfRangeException(nameof(bytes));
            lease.SetBytes(bytes);
            _activeBytes -= current - bytes;
            granted = GrantWaitersLocked();
        }
        Complete(granted);
    }

    private List<Waiter> GrantWaitersLocked()
    {
        List<Waiter> granted = [];
        while (_waiters.First is { } node)
        {
            Waiter waiter = node.Value;
            if (waiter.CancellationToken.IsCancellationRequested)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                waiter.Cancelled = true;
                granted.Add(waiter);
                continue;
            }
            if (!CanGrant(waiter.Bytes)) break;
            _waiters.RemoveFirst();
            waiter.Node = null;
            _activeSlots++;
            _activeBytes += waiter.Bytes;
            granted.Add(waiter);
        }
        return granted;
    }

    private void Complete(List<Waiter> waiters)
    {
        foreach (Waiter waiter in waiters)
        {
            if (waiter.Cancelled) waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            else waiter.Completion.TrySetResult(new SegmentBufferLease(this, waiter.Bytes));
        }
    }

    private sealed class Waiter(long bytes, CancellationToken cancellationToken)
    {
        internal long Bytes { get; } = bytes;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource<SegmentBufferLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal LinkedListNode<Waiter>? Node;
        internal bool Cancelled;
    }

}

internal sealed class SegmentBufferLease(SegmentBufferLimiter owner, long bytes) : IDisposable
{
    private long _bytes = bytes;
    private bool _disposed;

    internal long ReservedBytes => Interlocked.Read(ref _bytes);
    internal void Resize(long bytes) => owner.Resize(this, bytes);
    public void Dispose() => owner.Release(this);

    internal bool TryGetBytes(SegmentBufferLimiter expectedOwner, out long currentBytes)
    {
        currentBytes = _bytes;
        return ReferenceEquals(expectedOwner, owner) && !_disposed;
    }

    internal void SetBytes(long newBytes) => Interlocked.Exchange(ref _bytes, newBytes);

    internal bool Detach(SegmentBufferLimiter expectedOwner, out long currentBytes)
    {
        currentBytes = _bytes;
        if (!ReferenceEquals(expectedOwner, owner) || _disposed) return false;
        _disposed = true;
        return true;
    }
}
