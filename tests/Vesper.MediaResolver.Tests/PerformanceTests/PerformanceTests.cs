using System.Diagnostics;
using System.Text;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols.Dash;
using Vesper.MediaResolver.Protocols.Hls;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vesper.MediaResolver.Tests.PerformanceTests;

/// <summary>
/// Regression guards, not benchmarks: thresholds are deliberately loose so they only fail on algorithmic blow-ups
/// (quadratic parsing, runaway allocation, ignored cancellation). Real numbers come from the Benchmarks project.
/// </summary>
public class PerformanceTests
{
    private readonly ITestOutputHelper _out;

    public PerformanceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Hls_TwentyThousandSegments_ParsesQuicklyWithBoundedAllocation()
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (int i = 0; i < 20_000; i++) sb.Append("#EXTINF:6.000,\nsegment-").Append(i).Append(".ts\n");
        sb.Append("#EXT-X-ENDLIST\n");
        string text = sb.ToString();

        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var parsed = HlsParser.Parse(text, new Uri("https://cdn.example.com/p/index.m3u8"));
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        _out.WriteLine($"HLS 20k segments: {sw.ElapsedMilliseconds} ms, {allocated / 1024 / 1024.0:F1} MiB allocated (input {text.Length / 1024} KiB)");
        Assert.Equal(20_000, parsed.Media!.Segments.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000);
        Assert.True(allocated < 200L * 1024 * 1024);
    }

    [Fact]
    public void Dash_TenThousandSegmentTimeline_ParsesQuickly()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10000S">
  <Period><AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" initialization="i.mp4" media="s$Number$.m4s"><SegmentTimeline><S t="0" d="1" r="9999"/></SegmentTimeline></SegmentTemplate>
    <Representation id="a" bandwidth="1000000" width="640" height="360" codecs="avc1.4d401e"/>
  </AdaptationSet></Period>
</MPD>
""";
        var sw = Stopwatch.StartNew();
        var m = DashParser.Parse(mpd, new Uri("https://example.com/m.mpd"));
        sw.Stop();
        _out.WriteLine($"DASH 10k segments: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(10_000, m.Representations[0].Segments!.Segments.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public void Selection_HundredItemsThirtyRepresentationsEach_IsFast()
    {
        var items = Enumerable.Range(0, 100).Select(n => T.Item(
            Enumerable.Range(0, 24).Select(i => T.Video($"v{n}-{i}", 144 + 144 * (i % 12), i % 2 == 0 ? "h264" : "vp9", withAudio: false, kbps: 200 + i * 150))
                .Concat(Enumerable.Range(0, 6).Select(i => T.Audio($"a{n}-{i}", lang: i % 2 == 0 ? "en" : "es", kbps: 64 + i * 32)))
                .ToArray())).ToList();

        var sw = Stopwatch.StartNew();
        foreach (var item in items)
            Assert.True(FormatSelector.Select(item, FormatPreferences.Height(1080)).IsSuccess);
        sw.Stop();
        _out.WriteLine($"100 x 30 selection: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public async Task Cancellation_StopsAnInFlightResolveQuickly()
    {
        var handler = new FakeHttpHandler { Delay = TimeSpan.FromSeconds(10) }.Add("https://slow.example.com/page", "x");
        using var resolver = T.Resolver(handler);
        using var cts = new CancellationTokenSource();

        var task = resolver.ResolveAsync(T.Req("https://slow.example.com/page"), cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();

        _out.WriteLine($"Cancellation latency: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }

    [Fact]
    public async Task PageWithHundredsOfMediaTags_IsBoundedToTheCandidateLimit()
    {
        var html = new StringBuilder("<html><body>");
        for (int i = 0; i < 500; i++) html.Append($"<video src=\"https://cdn.example.com/{i}.mp4\"></video>");
        var handler = new FakeHttpHandler().Add("https://www.example.com/many", html.ToString());
        using var resolver = T.Resolver(handler);

        var res = await resolver.ResolveAsync(T.Req("https://www.example.com/many"), CancellationToken.None);

        Assert.False(res.IsSuccess);                                       // none of the routes exist
        Assert.True(handler.Requests.Count <= 1 + 24 * 2);                 // page + at most (HEAD + ranged GET) per candidate
    }

    [Fact]
    public async Task HostileScriptAndMarkup_DoNotCauseSuperlinearWork()
    {
        var sb = new StringBuilder("<html><script>var u = \".m3u8 \"; var x = \"https://");
        sb.Append('a', 900_000).Append("\";</script>");
        sb.Append('<', 100_000).Append("<div a='b").Append('"', 50_000);
        var handler = new FakeHttpHandler().Add("https://www.example.com/hostile", sb.ToString());
        using var resolver = T.Resolver(handler);

        var sw = Stopwatch.StartNew();
        var res = await resolver.ResolveAsync(T.Req("https://www.example.com/hostile"), CancellationToken.None);
        sw.Stop();

        _out.WriteLine($"Hostile page: {sw.ElapsedMilliseconds} ms");
        Assert.False(res.IsSuccess);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void ResolverConstruction_IsCheapAndLazy()
    {
        var handler = new FakeHttpHandler();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) new DefaultMediaResolver(new ResolverOptions(), handler).Dispose();
        sw.Stop();
        _out.WriteLine($"1000 constructions: {sw.ElapsedMilliseconds} ms");
        Assert.Empty(handler.Requests);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }
}
