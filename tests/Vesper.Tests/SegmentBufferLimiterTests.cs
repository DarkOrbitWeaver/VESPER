using Vesper.Engine;

namespace Vesper.Tests;

public sealed class SegmentBufferLimiterTests
{
    [Fact]
    public async Task ShrinkingAReservationUnblocksTheNextBuffer()
    {
        var limiter = new SegmentBufferLimiter(maxSlots: 3, maxBytes: 10);
        SegmentBufferLease first = await limiter.AcquireAsync(8, CancellationToken.None);
        Task<SegmentBufferLease> waiting = limiter.AcquireAsync(6, CancellationToken.None).AsTask();

        Assert.False(waiting.IsCompleted);
        first.Resize(4);
        using SegmentBufferLease second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(10, limiter.ActiveBytes);
        Assert.Equal(2, limiter.ActiveSlots);

        second.Dispose();
        second.Dispose();
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, limiter.ActiveBytes);
        Assert.Equal(0, limiter.ActiveSlots);
    }

    [Fact]
    public async Task CancellingAQueuedBufferDoesNotLeakCapacity()
    {
        var limiter = new SegmentBufferLimiter(maxSlots: 3, maxBytes: 10);
        SegmentBufferLease held = await limiter.AcquireAsync(10, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        Task<SegmentBufferLease> waiting = limiter.AcquireAsync(1, cancel.Token).AsTask();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        using SegmentBufferLease replacement = await limiter.AcquireAsync(10, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(10, limiter.ActiveBytes);
    }

    [Fact]
    public async Task SlotAndByteLimitsAreBothEnforced()
    {
        var limiter = new SegmentBufferLimiter(maxSlots: 1, maxBytes: 20);
        using SegmentBufferLease first = await limiter.AcquireAsync(5, CancellationToken.None);
        Task<SegmentBufferLease> waiting = limiter.AcquireAsync(1, CancellationToken.None).AsTask();

        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using SegmentBufferLease second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, limiter.ActiveSlots);
        Assert.Equal(1, limiter.ActiveBytes);
    }

    [Fact]
    public void ReservationsCoverGrowthAndDecryptionCopies()
    {
        Assert.Equal(128L * 1024 * 1024, SegmentBufferLimiter.Reservation(64L * 1024 * 1024, null, copies: 2));
        Assert.Equal(128L * 1024, SegmentBufferLimiter.Reservation(64L * 1024 * 1024, 1, copies: 2));
        Assert.Equal(128L * 1024, SegmentBufferLimiter.CapacityReservation(64L * 1024, copies: 2));
    }
}
