using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Xunit;
using Xunit.Abstractions;

namespace Vesper.MediaResolver.Tests.LiveTests;

/// <summary>
/// OPT-IN live checks against legally accessible, freely redistributable test media. They do nothing unless the
/// environment variable VESPER_LIVE_TESTS=1 is set (then they use the real network). A silent early return means
/// "not run", never "verified". The URLs below were chosen from public documentation and have NOT been exercised yet;
/// if one has moved, update it and record the result in COMPATIBILITY.md.
/// </summary>
public class LiveTests
{
    private readonly ITestOutputHelper _out;
    private static bool Enabled => Environment.GetEnvironmentVariable("VESPER_LIVE_TESTS") == "1";

    public LiveTests(ITestOutputHelper output) => _out = output;

    private async Task<MediaDiscoveryResult> ResolveLive(string url)
    {
        using var resolver = new DefaultMediaResolver();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var res = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = new Uri(url) }, cts.Token);
        _out.WriteLine($"{url} -> {res} | requests: {resolver.NetworkRequestCount}");
        return res;
    }

    [Fact]
    public async Task DirectMp4_BlenderBigBuckBunny()
    {
        if (!Enabled) return;
        var res = await ResolveLive("https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_320x180.mp4");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.True(res.Item!.Representations[0].FileSize > 0);
    }

    [Fact]
    public async Task InternetArchive_PublicDomainItem()
    {
        if (!Enabled) return;
        var res = await ResolveLive("https://archive.org/details/Popeye_forPresident");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        var item = res.Item!;
        Assert.NotEmpty(item.Representations);
        Assert.NotNull(FormatSelector.Select(item, FormatPreferences.BestQuality).Selection);
    }

    [Fact]
    public async Task Hls_AppleAdvancedExample()
    {
        if (!Enabled) return;
        var res = await ResolveLive("https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Contains(res.Item!.Representations, r => r.Protocol == StreamProtocol.Hls && r.Height > 0);
        Assert.NotEmpty(QualityMenuBuilder.BuildDefault(res.Item));
    }

    [Fact]
    public async Task Dash_AkamaiBigBuckBunny()
    {
        if (!Enabled) return;
        var res = await ResolveLive("https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd");
        Assert.True(res.IsSuccess, res.Error?.Diagnostic);
        Assert.Contains(res.Item!.Representations, r => r.Protocol == StreamProtocol.Dash);
    }
}
