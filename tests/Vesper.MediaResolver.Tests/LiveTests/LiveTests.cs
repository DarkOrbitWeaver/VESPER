using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Xunit;
using Xunit.Abstractions;

namespace Vesper.MediaResolver.Tests.LiveTests;

/// <summary>
/// Opt-in live checks against public test media. Disabled checks are reported
/// as skipped, never as passing. An unavailable fixture is a failed live check,
/// not a successful resolver result.
/// </summary>
public class LiveTests
{
    private readonly ITestOutputHelper _out;
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_LIVE_TESTS") != "1")
                Skip = "Set VESPER_LIVE_TESTS=1 to run public network checks.";
        }
    }

    public sealed class LiveTheoryAttribute : TheoryAttribute
    {
        public LiveTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_LIVE_TESTS") != "1")
                Skip = "Set VESPER_LIVE_TESTS=1 to run public network checks.";
        }
    }

    public LiveTests(ITestOutputHelper output) => _out = output;

    private async Task<MediaDiscoveryResult> ResolveLive(string url)
    {
        using var resolver = new DefaultMediaResolver();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var res = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = new Uri(url) }, cts.Token);
        _out.WriteLine($"{url} -> {res} | requests: {resolver.NetworkRequestCount}");
        return res;
    }

    [LiveTheory]
    [InlineData("https://mdn.github.io/shared-assets/videos/flower.mp4")]
    [InlineData("https://mdn.github.io/shared-assets/videos/flower.webm")]
    [InlineData("https://media.w3.org/2010/05/bunny/trailer.mp4")]
    public async Task DirectPublicMedia(string url)
    {
        var res = await ResolveLive(url);
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Contains(res.Item!.Representations, r => r.Protocol == StreamProtocol.Http && r.FileSize > 0);
    }

    [LiveFact]
    public async Task InternetArchive_PublicDomainItem()
    {
        var res = await ResolveLive("https://archive.org/details/Popeye_forPresident");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.NotEmpty(item.Representations);
        Assert.NotNull(FormatSelector.Select(item, FormatPreferences.BestQuality).Selection);
    }

    [LiveFact]
    public async Task Hls_AppleAdvancedExample()
    {
        var res = await ResolveLive("https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Contains(res.Item!.Representations, r => r.Protocol == StreamProtocol.Hls && r.Height > 0);
        Assert.NotEmpty(QualityMenuBuilder.BuildDefault(res.Item));
    }

    [LiveFact]
    public async Task Dash_AkamaiBigBuckBunny()
    {
        var res = await ResolveLive("https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Contains(res.Item!.Representations, r => r.Protocol == StreamProtocol.Dash);
    }
}
