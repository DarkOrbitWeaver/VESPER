using System.Diagnostics;
using Vesper.Engine;
using Xunit;

namespace Vesper.Tests;

public sealed class TransferEstimateTests
{
    private const int Total = 100_000_000;

    [Fact]
    public void A_brief_final_range_dip_does_not_flash_a_long_eta()
    {
        var estimate = new TransferEstimate();
        Sample(estimate, 0, 89_000_000);
        Sample(estimate, .5, 94_000_000);
        Sample(estimate, 1, 96_000_000);
        Assert.True(estimate.Remaining < TimeSpan.FromSeconds(2));
        Sample(estimate, 3.5, 96_100_000);
        Sample(estimate, 4, 96_150_000);
        Assert.True(estimate.Estimating);
        Assert.Null(estimate.Remaining);
        Sample(estimate, 4.5, 99_000_000);
        Assert.False(estimate.Estimating);
        Assert.True(estimate.Remaining < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_variable_tail_stays_in_one_estimating_state_until_it_settles()
    {
        var estimate = new TransferEstimate();
        Sample(estimate, 0, 90_000_000);
        Sample(estimate, .5, 94_000_000);
        Sample(estimate, 1, 96_000_000);
        Sample(estimate, 3.5, 96_500_000);
        Sample(estimate, 4, 96_700_000);
        Assert.True(estimate.Estimating);
        Sample(estimate, 4.5, 97_300_000);
        Assert.True(estimate.Estimating);
        Assert.Null(estimate.Remaining);
    }

    [Fact]
    public void A_sustained_slow_tail_displays_the_new_eta_and_stalled_data_clears_it()
    {
        var estimate = new TransferEstimate();
        Sample(estimate, 0, 91_000_000);
        Sample(estimate, 1, 96_000_000);
        for (int second = 2; second <= 8; second++)
            Sample(estimate, second, 96_000_000 + (second - 1) * 100_000);
        Assert.False(estimate.Estimating);
        Assert.True(estimate.Remaining > TimeSpan.FromSeconds(20));

        for (int second = 9; second <= 12; second++)
            Sample(estimate, second, 96_700_000);
        Assert.True(estimate.WaitingForData);
        Assert.Null(estimate.Remaining);

        estimate.Reset();
        Assert.False(estimate.WaitingForData);
        Assert.False(estimate.Estimating);
        Assert.Null(estimate.Remaining);
    }

    private static void Sample(TransferEstimate estimate, double seconds, long bytes) =>
        estimate.Sample((long)(seconds * Stopwatch.Frequency), bytes, Total);
}
