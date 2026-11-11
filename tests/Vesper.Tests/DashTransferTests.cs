using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class DashTransferTests
{
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));
    private static int Tag(byte[] bytes, string tag) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(tag));
    private static byte[] Expected => [.. Fixture("av-init.mp4"), .. Fixture("av-1.m4s"), .. Fixture("av-2.m4s"), .. Fixture("av-3.m4s")];

    [Fact]
    public void ClearSourceMuxedFragments_HaveBothTracksAndContinuousTimelines()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.True(mp4.HasAudio); Assert.True(mp4.HasVideo);
        for (int i = 1; i <= 3; i++) mp4.ValidateFragment(Fixture($"av-{i}.m4s"));
        mp4.Finish(requireAudio: true, requireVideo: true);
        Assert.All(mp4.Snapshot().Tracks, t => Assert.True(t.Samples > 0));
    }

    [Fact]
    public void AudioOnly_IsSupported_ButCannotPretendToContainVideo()
    {
        var mp4 = FragmentedMp4.Open(Fixture("audio-init.mp4"));
        mp4.ValidateFragment(Fixture("audio-1.m4s"));
        mp4.Finish(true, false);
        Assert.Throws<MediaContainerException>(() => mp4.Finish(true, true));
    }

    [Fact]
    public void InvalidBoxLength_IsRejected()
    {
        byte[] init = Fixture("av-init.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(init, uint.MaxValue);
        Assert.Throws<MediaContainerException>(() => FragmentedMp4.Open(init));
    }

    [Fact]
    public void AbsoluteOffsets_AreNotConcatenatedWithoutRemuxing()
    {
        byte[] fragment = Fixture("av-1.m4s");
        fragment[Tag(fragment, "tfhd") + 7] |= 1;
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void SampleOffsetsOutsideMdat_AreRejected()
    {
        byte[] fragment = Fixture("av-1.m4s");
        BinaryPrimitives.WriteInt32BigEndian(fragment.AsSpan(Tag(fragment, "trun") + 12), int.MaxValue);
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void TimelineOverlapOrGap_IsRejected()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        mp4.ValidateFragment(Fixture("av-1.m4s"));
        byte[] fragment = Fixture("av-2.m4s");
        int tfdt = Tag(fragment, "tfdt");
        BinaryPrimitives.WriteUInt64BigEndian(fragment.AsSpan(tfdt + 8), 0);
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void DuplicateOrOutOfOrderFragments_AreRejected()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        mp4.ValidateFragment(Fixture("av-1.m4s"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(Fixture("av-1.m4s")));
    }

    [Fact]
    public void RequiredTracksWithoutSamples_AreNeverSuccessful()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.Finish(true, true));
    }

    [Fact]
    public void PlansRejectPrivateTargetsAndMalformedRanges()
    {
        var plan = new DashDownloadPlan
        {
            RepresentationId = "a", Initialization = new("http://127.0.0.1/init.mp4"),
            Fragments = [new("https://public.example.com/1.m4s")]
        };
        Assert.False(DashDownloadPlan.TryParse(plan.ToJson(), false, out _, out _));
        Assert.False(DashDownloadPlan.TryParse((plan with { Initialization = new("https://public.example.com/init.mp4", -1, 10) }).ToJson(), false, out _, out _));
        Assert.False(DashDownloadPlan.TryParse((plan with { Initialization = new("https://public.example.com/init.mp4", long.MaxValue, 10) }).ToJson(), false, out _, out _));
    }

    [Fact]
    public async Task DashTransfersProduceTheExactIndependentlyValidatedSourceMuxedFile()
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server), JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
        Assert.EndsWith(".mp4", job.FinalPath);
        Assert.Equal(Expected.Length, job.CommittedBytes);
    }

    [Fact]
    public async Task ExactByteRanges_AreAssembledInOrder()
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server, ranges: true), JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
        Assert.Equal(4, server.Hits("/all.mp4"));
    }

    [Fact]
    public async Task IgnoredRanges_NeverCommitBytes()
    {
        await using var server = await FixtureServer.StartAsync(); server.IgnoreRanges = true;
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server, ranges: true), JobState.NeedsAttention);
        Assert.Equal(0, job.CommittedBytes); Assert.Null(job.FinalPath);
    }

    [Fact]
    public async Task InvalidMedia_IsNotPublishedOrCommitted()
    {
        await using var server = await FixtureServer.StartAsync(); server.InvalidFragment = true;
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server), JobState.NeedsAttention);
        Assert.Equal("invalid-container", job.Reason);
        Assert.Equal(0, job.CommittedBytes); Assert.Null(job.FinalPath);
    }

    [Fact]
    public async Task InterruptedTransfer_ResumesWithoutFetchingCommittedFragmentsAgain()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stopped.CommittedBytes > 0);
        h.Restart(); server.ExpireSecond = false; h.Engine.Resume(id);
        var done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(1, server.Hits("/av-1.m4s"));
        Assert.Equal(1, server.Hits("/av-init.mp4"));
    }

    [Fact]
    public async Task CorruptCommittedPrefix_IsKeptButNeverResumedOrPublished()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        h.Restart();
        byte[] bytes = await File.ReadAllBytesAsync(stopped.PartialPath); bytes[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(stopped.PartialPath, bytes);
        server.ExpireSecond = false; h.Engine.Resume(id);
        var refused = await h.WaitForStateAsync(id, JobState.NeedsAttention);
        Assert.Equal("dash-integrity", refused.Reason);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(refused.FinalPath);
    }

    [Fact]
    public async Task FileLinkRescue_CannotReplaceAFragmentManifest()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness(); Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        var decision = await h.Engine.ReplaceLinkAsync(id, server.Url("/different.mp4"));
        Assert.False(decision.Accepted);
        Assert.Equal(stopped.CommittedBytes, h.Engine.Snapshot(id)!.CommittedBytes);
        Assert.Equal(0, server.Hits("/different.mp4"));
    }

    [Fact]
    public async Task CancellationDuringBodyRead_IsPromptAndPublishesNothing()
    {
        await using var server = await FixtureServer.StartAsync(); server.SlowFirst = true;
        using var h = new Harness(); Guid id = Add(h, server);
        await server.FirstFragmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = System.Diagnostics.Stopwatch.StartNew(); h.Engine.Pause(id);
        var paused = await h.WaitForStateAsync(id, JobState.Paused);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2)); Assert.Null(paused.FinalPath);
    }

    [Fact]
    public async Task ConcurrentJobs_NeverOverwriteAFileWithTheSameName()
    {
        await using var server = await FixtureServer.StartAsync(); using var h = new Harness();
        Guid a = Add(h, server), b = Add(h, server, suffix: "?other=1");
        var results = await Task.WhenAll(h.WaitForStateAsync(a, JobState.Completed), h.WaitForStateAsync(b, JobState.Completed));
        Assert.NotEqual(results[0].FinalPath, results[1].FinalPath);
        foreach (var job in results) Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
    }

    private static Guid Add(Harness h, FixtureServer server, bool ranges = false, string suffix = "")
    {
        var plan = server.Plan(ranges);
        var added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd") + suffix, FileName: "fixture.mp4", Kind: JobKind.Dash,
            MediaPlanJson: plan.ToJson(), MediaProvider: "fixture", MediaId: "av", MediaFormatId: plan.RepresentationId));
        Assert.True(added.Ok, added.Message); return added.JobId;
    }

    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentDictionary<string, int> _hits = new();
        private readonly Dictionary<string, byte[]> _objects = new();
        private string _base = "";
        internal bool IgnoreRanges, InvalidFragment, ExpireSecond, SlowFirst;
        internal TaskCompletionSource FirstFragmentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url(string path) => _base + path;
        internal int Hits(string path) => _hits.GetValueOrDefault(path);
        private FixtureServer()
        {
            foreach (string name in new[] { "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s" }) _objects["/" + name] = Fixture(name);
            _objects["/all.mp4"] = Expected;
            var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            _app = builder.Build(); _app.Run(ServeAsync);
        }
        internal static async Task<FixtureServer> StartAsync()
        {
            var server = new FixtureServer(); await server._app.StartAsync(); server._base = server._app.Urls.Single(); return server;
        }
        internal DashDownloadPlan Plan(bool ranges)
        {
            var init = new DashFragment(Url("/av-init.mp4"));
            var fragments = Enumerable.Range(1, 3).Select(i => new DashFragment(Url($"/av-{i}.m4s"))).ToArray();
            if (ranges)
            {
                long offset = Fixture("av-init.mp4").Length; init = new(Url("/all.mp4"), 0, offset);
                for (int i = 0; i < 3; i++) { int size = Fixture($"av-{i + 1}.m4s").Length; fragments[i] = new(Url("/all.mp4"), offset, size); offset += size; }
            }
            return new() { RepresentationId = "av-source-muxed", Provider = "fixture", MediaId = "av", Initialization = init,
                Fragments = fragments, RequireAudio = true, RequireVideo = true };
        }
        private async Task ServeAsync(HttpContext context)
        {
            string path = context.Request.Path.Value!; _hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (path == "/av-2.m4s" && ExpireSecond) { context.Response.StatusCode = 403; return; }
            if (!_objects.TryGetValue(path, out var data)) { context.Response.StatusCode = 404; return; }
            if (path == "/av-1.m4s")
            {
                FirstFragmentStarted.TrySetResult();
                if (SlowFirst) { try { await Task.Delay(20_000, context.RequestAborted); } catch (OperationCanceledException) { return; } }
                if (InvalidFragment) data = Encoding.UTF8.GetBytes("not a media container");
            }
            int start = 0, end = data.Length - 1;
            if (!IgnoreRanges && context.Request.Headers.Range.ToString() is { Length: > 6 } range)
            {
                var parts = range[6..].Split('-'); start = int.Parse(parts[0]); end = int.Parse(parts[1]);
                context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {start}-{end}/{data.Length}";
            }
            context.Response.ContentType = "video/mp4"; context.Response.ContentLength = end - start + 1;
            await context.Response.Body.WriteAsync(data.AsMemory(start, end - start + 1), context.RequestAborted);
        }
        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
