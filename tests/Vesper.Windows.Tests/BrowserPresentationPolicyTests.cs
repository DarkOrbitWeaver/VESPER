using VESPER;

namespace Vesper.Windows.Tests;

public sealed class BrowserPresentationPolicyTests
{
    [Fact]
    public void Automatic_burst_reveals_once_and_never_reopens_for_replayed_receipts()
    {
        var policy = new BrowserPresentationPolicy();
        Assert.True(policy.ShouldPresent("a", false, 0));
        Assert.False(policy.ShouldPresent("b", false, 10_000));
        Assert.False(policy.ShouldPresent("a", false, 70_000));
        Assert.True(policy.ShouldPresent("c", false, 70_000));
    }

    [Fact]
    public void Explicit_new_download_reveals_even_during_an_automatic_burst()
    {
        var policy = new BrowserPresentationPolicy();
        Assert.True(policy.ShouldPresent("a", false, 0));
        Assert.True(policy.ShouldPresent("b", true, 100));
        Assert.False(policy.ShouldPresent("b", true, 200));
        Assert.False(policy.ShouldPresent("c", false, 300));
    }
}
