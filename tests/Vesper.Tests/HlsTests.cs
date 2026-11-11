using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>HLS end to end: playlist parsing, optional AES-128, segment commits and resume at a segment boundary.</summary>
public class HlsTests
{
    private static Guid AddHls(Harness h, FaultServer s, string path) =>
        h.Engine.Add(new AddRequest(s.Url(path), FileName: "video", Kind: JobKind.Hls)).JobId;

    [Fact]
    public async Task A_plain_VOD_playlist_becomes_one_transport_stream_file()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, "/hls/plain/index.m3u8"), JobState.Completed);
        Assert.Equal(FaultServer.HlsExpected(), await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.EndsWith(".ts", done.FinalPath);               // honest container: concatenated TS is a valid .ts, not a pretend .mp4
    }

    [Fact]
    public async Task AES128_segments_with_an_openly_served_key_are_decrypted()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, "/hls/enc/index.m3u8"), JobState.Completed);
        Assert.Equal(FaultServer.HlsExpected(), await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task A_master_playlist_downloads_the_best_quality()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, "/hls/master.m3u8"), JobState.Completed);
        Assert.Equal(FaultServer.HlsExpected(), await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(0, server.Hits("/hls/low/WRONG-QUALITY.ts"));
    }

    [Fact]
    public async Task Protected_and_live_streams_are_refused_with_an_honest_reason()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot drm = await h.WaitAsync(AddHls(h, server, "/hls/drm/index.m3u8"), s => s.State == JobState.NeedsAttention);
        Assert.Equal("drm", drm.Reason);
        JobSnapshot live = await h.WaitAsync(AddHls(h, server, "/hls/live/index.m3u8"), s => s.State == JobState.NeedsAttention);
        Assert.Equal("live", live.Reason);
        Assert.Equal(0, drm.CommittedBytes);
    }

    [Fact]
    public async Task Hls_wrong_content_range_never_commits_segment_bytes()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, "/hls/range-bad/index.m3u8"),
            state => state.State == JobState.NeedsAttention);
        Assert.Equal("invalid-range", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
    }

    [Fact]
    public async Task A_crash_part_way_resumes_at_a_segment_boundary_and_the_result_is_exact()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        int commits = 0;
        using var h = new Harness(point => { if (point == "checkpoint.afterCommit" && Interlocked.Increment(ref commits) == 3) throw new FailpointCrash(point); });
        Guid id = AddHls(h, server, "/hls/plain/index.m3u8");
        await Task.Delay(1200);
        h.Restart();

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 30);
        Assert.Equal(FaultServer.HlsExpected(), await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.True(server.Hits("/hls/plain/s0.ts") <= 1, "a committed segment must not be downloaded again");
    }
}
