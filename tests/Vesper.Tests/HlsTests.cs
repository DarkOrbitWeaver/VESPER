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
    public async Task Repeated_equal_key_and_map_tags_keep_one_initialization_identity()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        byte[] initialization = DashTransferTests.Fixture("av-init.mp4");
        byte[][] fragments = [DashTransferTests.Fixture("av-1.m4s"), DashTransferTests.Fixture("av-2.m4s"), DashTransferTests.Fixture("av-3.m4s")];
        byte[] Encrypt(byte[] bytes)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = FaultServer.HlsKey;
            return aes.EncryptCbc(bytes, FaultServer.HlsIv, System.Security.Cryptography.PaddingMode.PKCS7);
        }

        server.HlsFiles["/hls/repeated/key.bin"] = FaultServer.HlsKey;
        server.HlsFiles["/hls/repeated/init.mp4"] = Encrypt(initialization);
        for (int i = 0; i < fragments.Length; i++)
            server.HlsFiles[$"/hls/repeated/s{i}.m4s"] = Encrypt(fragments[i]);

        string key = "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x" + Convert.ToHexString(FaultServer.HlsIv);
        server.HlsPlaylists["/hls/repeated/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-VERSION:7", key, "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXTINF:1,", "s0.m4s", key, "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXTINF:1,", "s1.m4s", "#EXTINF:1,", "s2.m4s", "#EXT-X-ENDLIST", "");

        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, "/hls/repeated/index.m3u8"), JobState.Completed);
        byte[] expected = [.. initialization, .. fragments.SelectMany(fragment => fragment)];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Unsupported_hls_features_are_not_reported_as_drm()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/discontinuous/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns0.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:4,\ns1.ts\n#EXT-X-ENDLIST\n";
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, "/hls/discontinuous/index.m3u8"),
            state => state.State == JobState.NeedsAttention);
        Assert.Equal("unsupported", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
    }

    [Fact]
    public async Task Master_session_key_detection_checks_every_key_format()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/session-keys/index.m3u8";
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U",
            "#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"key.bin\",KEYFORMAT=\"identity\"",
            "#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000", "variant.m3u8", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, path), state => state.State == JobState.NeedsAttention);

        Assert.Equal("drm", stopped.Reason);
    }

    [Fact]
    public async Task Invalid_hls_key_uri_is_rejected_before_any_media_request()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/invalid-key/index.m3u8";
        server.HlsPlaylists[path] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=AES-128,URI=\"file:///C:/Windows/win.ini\"\n#EXTINF:4,\ns0.ts\n#EXT-X-ENDLIST\n";
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, path), state => state.State == JobState.NeedsAttention);
        Assert.Equal("key", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Equal(1, server.Hits(path));
        Assert.Equal(0, server.Hits("/hls/invalid-key/s0.ts"));
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
    [Fact]
    public async Task FragmentedMp4Hls_UsesValidatedInitAndFragmentsInsteadOfArbitraryConcatenation()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var done = await h.WaitForStateAsync(AddHls(h, server, "/hls/fmp4/index.m3u8"), JobState.Completed);
        byte[] expected = [.. DashTransferTests.Fixture("av-init.mp4"), .. DashTransferTests.Fixture("av-1.m4s"),
            .. DashTransferTests.Fixture("av-2.m4s"), .. DashTransferTests.Fixture("av-3.m4s")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!)); Assert.EndsWith(".mp4", done.FinalPath);
    }

}
