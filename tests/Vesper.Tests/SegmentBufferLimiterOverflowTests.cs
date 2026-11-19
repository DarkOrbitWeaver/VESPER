using Vesper.Engine;

namespace Vesper.Tests;

public sealed class SegmentBufferLimiterOverflowTests
{
    [Fact]
    public void ReservationGrowthDoesNotOverflowAtLargeCapacities()
    {
        Assert.Equal(long.MaxValue, SegmentBufferLimiter.Reservation(long.MaxValue, long.MaxValue));
    }
}
