using System.Text.Json;
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
    public void Live_playlist_retains_sequence_target_duration_and_discontinuity_metadata()
    {
        var media = HlsParser.ParseMedia(string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:6", "#EXT-X-MEDIA-SEQUENCE:41",
            "#EXT-X-DISCONTINUITY-SEQUENCE:3", "#EXTINF:6,", "first.ts",
            "#EXT-X-DISCONTINUITY", "#EXT-X-GAP", "#EXTINF:5,", "second.ts", ""),
            new Uri("https://media.example.com/live/index.m3u8"), allowTimelineChanges: true);

        Assert.False(media.IsVod);
        Assert.Equal(6, media.TargetDuration);
        Assert.Equal(41, media.MediaSequence);
        Assert.Equal(3, media.DiscontinuitySequence);
        Assert.Collection(media.Segments,
            first =>
            {
                Assert.Equal(41, first.Sequence);
                Assert.False(first.DiscontinuityBefore);
                Assert.False(first.IsGap);
                Assert.Equal(3, first.DiscontinuitySequence);
            },
            second =>
            {
                Assert.Equal(42, second.Sequence);
                Assert.True(second.DiscontinuityBefore);
                Assert.True(second.IsGap);
                Assert.Equal(4, second.DiscontinuitySequence);
            });
        Assert.Null(media.Unsupported);

        string previousPlanShape = JsonSerializer.Serialize(media.Segments.Select(segment => new
        {
            segment.Uri, segment.Duration, segment.RangeLength, segment.RangeOffset, segment.Key, segment.Map, segment.Sequence
        }));
        Assert.Equal(previousPlanShape, JsonSerializer.Serialize(media.Segments));
    }

    [Fact]
    public async Task VOD_with_a_gap_is_not_published_as_complete()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/gap/index.m3u8";
        server.HlsPlaylists[path] = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-GAP\n#EXTINF:4,\ns0.ts\n#EXT-X-ENDLIST\n";
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, path), state => state.State == JobState.NeedsAttention);

        Assert.Equal("unsupported", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Equal(0, server.Hits("/hls/gap/s0.ts"));
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
    public async Task Protected_streams_are_refused_and_live_recordings_can_be_stopped_and_kept()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot drm = await h.WaitAsync(AddHls(h, server, "/hls/drm/index.m3u8"), s => s.State == JobState.NeedsAttention);
        Assert.Equal("drm", drm.Reason);
        Assert.Equal(0, drm.CommittedBytes);

        Guid id = AddHls(h, server, "/hls/live/index.m3u8");
        byte[] expected = FaultServer.HlsExpected();
        JobSnapshot recording = await h.WaitAsync(id, s => s.IsLiveRecording && s.State == JobState.Downloading
            && s.CommittedBytes == expected.Length);
        Assert.Null(recording.Fraction);
        Assert.StartsWith("Recording live", recording.Message);
        Assert.True(h.Engine.StopAndKeepLive(id));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("live-stopped", saved.Reason);
        Assert.Equal(expected, await File.ReadAllBytesAsync(saved.FinalPath!));
    }

    [Fact]
    public async Task LiveHls_RestartsAtTheNextSequenceAndAcceptsKeyRotation()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/live-recovery/index.m3u8";
        byte[] keyA = FaultServer.HlsKey;
        byte[] keyB = Payloads.Make(16, 78);
        server.HlsFiles["/hls/live-recovery/key-a.bin"] = keyA;
        server.HlsFiles["/hls/live-recovery/key-b.bin"] = keyB;

        byte[] Encrypt(byte[] bytes, byte[] key, long sequence)
        {
            var iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(bytes, iv, System.Security.Cryptography.PaddingMode.PKCS7);
        }

        server.HlsFiles["/hls/live-recovery/s40.ts"] = Encrypt(FaultServer.HlsSegment(0), keyA, 40);
        server.HlsFiles["/hls/live-recovery/s41.ts"] = Encrypt(FaultServer.HlsSegment(1), keyB, 41);
        string first = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:40",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key-a.bin\"", "#EXTINF:1,", "s40.ts", "");
        string expanded = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:40",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key-a.bin\"", "#EXTINF:1,", "s40.ts",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key-b.bin\"", "#EXTINF:1,", "s41.ts", "");
        server.HlsPlaylists[path] = first;

        var crashed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int commits = 0;
        using var h = new Harness(point =>
        {
            if (point == "checkpoint.afterCommit" && Interlocked.Increment(ref commits) == 1)
            {
                crashed.TrySetResult(true);
                throw new FailpointCrash(point);
            }
        });
        Guid id = AddHls(h, server, path);
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        server.HlsPlaylists[path] = expanded;
        h.Restart();

        JobSnapshot resumed = await h.WaitAsync(id, job => job.State == JobState.Downloading
            && job.IsLiveRecording && job.CommittedBytes > FaultServer.HlsSegment(0).Length);
        Assert.True(h.Engine.StopAndKeepLive(id));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("live-stopped", saved.Reason);
        byte[] expected = [.. FaultServer.HlsSegment(0), .. FaultServer.HlsSegment(1)];
        Assert.Equal(expected, await File.ReadAllBytesAsync(saved.FinalPath!));
        Assert.Equal(1, server.Hits("/hls/live-recovery/s40.ts"));
        Assert.Equal(1, server.Hits("/hls/live-recovery/s41.ts"));
        Assert.Equal(1, server.Hits("/hls/live-recovery/key-a.bin"));
        Assert.Equal(1, server.Hits("/hls/live-recovery/key-b.bin"));
        Assert.True(resumed.IsLiveRecording);
    }

    [Fact]
    public async Task LiveHls_MissingSequencesAreReportedAndCanSaveThePlayablePrefix()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/live-gap/index.m3u8";
        server.HlsFiles["/hls/live-gap/s70.ts"] = FaultServer.HlsSegment(0);
        server.HlsFiles["/hls/live-gap/s72.ts"] = FaultServer.HlsSegment(2);
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:70",
            "#EXTINF:1,", "s70.ts", "");
        using var h = new Harness();
        Guid id = AddHls(h, server, path);
        JobSnapshot recording = await h.WaitAsync(id, job => job.IsLiveRecording && job.CommittedBytes > 0);

        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:72",
            "#EXTINF:1,", "s72.ts", "");
        JobSnapshot gap = await h.WaitAsync(id, job => job.State == JobState.NeedsAttention);
        Assert.Equal("live-gap", gap.Reason);
        Assert.Equal(recording.CommittedBytes, gap.CommittedBytes);
        Assert.Equal(0, server.Hits("/hls/live-gap/s72.ts"));

        Assert.True(h.Engine.StopAndKeepLive(id));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal("live-gaps", saved.Reason);
        Assert.Equal(FaultServer.HlsSegment(0), await File.ReadAllBytesAsync(saved.FinalPath!));
    }

    [Fact]
    public async Task LiveHls_ChangedKeyAtTheSameAddressIsRejectedAfterResume()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/live-key-change/index.m3u8";
        const string keyPath = "/hls/live-key-change/key.bin";
        byte[] keyA = FaultServer.HlsKey;
        byte[] keyB = Payloads.Make(16, 79);
        server.HlsFiles[keyPath] = keyA;

        byte[] Encrypt(byte[] bytes, byte[] key, long sequence)
        {
            var iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(bytes, iv, System.Security.Cryptography.PaddingMode.PKCS7);
        }

        server.HlsFiles["/hls/live-key-change/s90.ts"] = Encrypt(FaultServer.HlsSegment(0), keyA, 90);
        server.HlsFiles["/hls/live-key-change/s91.ts"] = Encrypt(FaultServer.HlsSegment(1), keyB, 91);
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:90",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"", "#EXTINF:1,", "s90.ts", "");
        using var h = new Harness();
        Guid id = AddHls(h, server, path);
        JobSnapshot recording = await h.WaitAsync(id, job => job.IsLiveRecording && job.CommittedBytes > 0);
        h.Engine.Pause(id);
        await h.WaitForStateAsync(id, JobState.Paused);

        server.HlsFiles[keyPath] = keyB;
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:90",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"", "#EXTINF:1,", "s90.ts",
            "#EXTINF:1,", "s91.ts", "");
        h.Engine.Resume(id);
        JobSnapshot refused = await h.WaitForStateAsync(id, JobState.NeedsAttention);

        Assert.Equal("key-changed", refused.Reason);
        Assert.Equal(recording.CommittedBytes, refused.CommittedBytes);
        Assert.Equal(1, server.Hits("/hls/live-key-change/s90.ts"));
        Assert.Equal(1, server.Hits("/hls/live-key-change/s91.ts"));
        Assert.True(h.Engine.StopAndKeepLive(id));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(FaultServer.HlsSegment(0), await File.ReadAllBytesAsync(saved.FinalPath!));
    }

    [Fact]
    public async Task LiveHls_EndListFinalizesWithoutAStopRequest()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/live-end/index.m3u8";
        server.HlsFiles["/hls/live-end/s80.ts"] = FaultServer.HlsSegment(0);
        server.HlsFiles["/hls/live-end/s81.ts"] = FaultServer.HlsSegment(1);
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:80",
            "#EXTINF:1,", "s80.ts", "");
        using var h = new Harness();
        Guid id = AddHls(h, server, path);
        await h.WaitAsync(id, job => job.IsLiveRecording && job.CommittedBytes > 0);

        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:1", "#EXT-X-MEDIA-SEQUENCE:80",
            "#EXTINF:1,", "s80.ts", "#EXTINF:1,", "s81.ts", "#EXT-X-ENDLIST", "");
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("", saved.Reason);
        byte[] expected = [.. FaultServer.HlsSegment(0), .. FaultServer.HlsSegment(1)];
        Assert.Equal(expected, await File.ReadAllBytesAsync(saved.FinalPath!));
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

    [Fact]
    public async Task HlsTimeRange_UsesWholeSegmentsAndKeepsTheInitializationSegment()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        const string root = "/hls/clip";
        string path = root + "/index.m3u8";
        string[] names = ["av-1.m4s", "av-2.m4s", "av-3.m4s"];
        server.HlsFiles[root + "/init.mp4"] = DashTransferTests.Fixture("av-init.mp4");
        foreach (string name in names) server.HlsFiles[root + "/" + name] = DashTransferTests.Fixture(name);
        server.HlsPlaylists[path] = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n"
            + string.Concat(names.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";

        AddResult added = h.Engine.Add(new AddRequest(server.Url(path), FileName: "clip.mp4", Kind: JobKind.Hls,
            MediaPlanJson: JsonSerializer.Serialize(new MediaTimeRange(1.1, 1.8))));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention);

        Assert.Equal(JobState.Completed, done.State);
        byte[] expected = [.. DashTransferTests.Fixture("av-init.mp4"), .. DashTransferTests.Fixture("av-2.m4s")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(0, server.Hits(root + "/av-1.m4s"));
        Assert.Equal(1, server.Hits(root + "/av-2.m4s"));
        Assert.Equal(0, server.Hits(root + "/av-3.m4s"));
        Assert.Contains("Segment-aligned 00:00:01.000–00:00:02.000", done.MediaQuality);
    }

    [Fact]
    public async Task HlsTimeRange_RejectsAWindowOutsideThePlaylist()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        const string path = "/hls/clip-invalid/index.m3u8";
        server.HlsPlaylists[path] = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\ns0.ts\n#EXT-X-ENDLIST\n";
        server.HlsFiles["/hls/clip-invalid/s0.ts"] = FaultServer.HlsSegment(0);
        AddResult added = h.Engine.Add(new AddRequest(server.Url(path), FileName: "clip.ts", Kind: JobKind.Hls,
            MediaPlanJson: JsonSerializer.Serialize(new MediaTimeRange(2, 3))));
        Assert.True(added.Ok, added.Message);

        JobSnapshot rejected = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);
        Assert.Equal("clip-range", rejected.Reason);
        Assert.Equal(0, rejected.CommittedBytes);
        Assert.Equal(0, server.Hits("/hls/clip-invalid/s0.ts"));
    }

    [Fact]
    public async Task SegmentedMergeJob_MergesSeparateHlsFragmentedRenditions()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        void AddFmp4Rendition(string root, string init, params string[] fragments)
        {
            server.HlsPlaylists[$"{root}/index.m3u8"] = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI=\"init.mp4\"\n"
                + string.Concat(fragments.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";
            server.HlsFiles[$"{root}/init.mp4"] = DashTransferTests.Fixture(init);
            foreach (string name in fragments) server.HlsFiles[$"{root}/{name}"] = DashTransferTests.Fixture(name);
        }
        AddFmp4Rendition("/hls/merge/video", "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s");
        AddFmp4Rendition("/hls/merge/audio", "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s");

        string videoUrl = server.Url("/hls/merge/video/index.m3u8");
        string audioUrl = server.Url("/hls/merge/audio/index.m3u8");
        var plan = new MediaMergePlan
        {
            Version = 2, OutputContainer = "mp4", RequireAudio = true, RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "hls-video", videoUrl, 0, "mp4", "h264", Source: new MergeMediaSource("hls", videoUrl)),
                new MergeTrack("audio", "hls-audio", audioUrl, 0, "m4a", "aac", Source: new MergeMediaSource("hls", audioUrl))
            ]
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(videoUrl, FileName: "hls-merged.mp4", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        using var output = File.OpenRead(done.FinalPath!);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], MediaMerger.Demux(output, output.Length, default).Select(track => track.Kind));
        Assert.False(File.Exists(done.PartialPath + ".tracks"));
    }

    [Fact]
    public async Task SegmentedHlsMerge_UsesOneSharedTimeWindowForBothRenditions()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        void AddFmp4Rendition(string root, string init, params string[] fragments)
        {
            server.HlsPlaylists[$"{root}/index.m3u8"] = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI=\"init.mp4\"\n"
                + string.Concat(fragments.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";
            server.HlsFiles[$"{root}/init.mp4"] = DashTransferTests.Fixture(init);
            foreach (string name in fragments) server.HlsFiles[$"{root}/{name}"] = DashTransferTests.Fixture(name);
        }
        AddFmp4Rendition("/hls/clip-merge/video", "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s");
        AddFmp4Rendition("/hls/clip-merge/audio", "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s");
        string videoUrl = server.Url("/hls/clip-merge/video/index.m3u8");
        string audioUrl = server.Url("/hls/clip-merge/audio/index.m3u8");
        var plan = new MediaMergePlan
        {
            Version = 2, OutputContainer = "mp4", RequireAudio = true, RequireVideo = true,
            ClipRange = new MediaTimeRange(1.1, 1.8),
            Tracks =
            [
                new MergeTrack("video", "hls-video", videoUrl, 0, "mp4", "h264", Source: new MergeMediaSource("hls", videoUrl)),
                new MergeTrack("audio", "hls-audio", audioUrl, 0, "m4a", "aac", Source: new MergeMediaSource("hls", audioUrl))
            ]
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(videoUrl, FileName: "hls-clip.mp4", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention);

        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        Assert.Contains("Segment-aligned 00:00:01.000–00:00:02.000", done.MediaQuality);
        foreach (string root in new[] { "/hls/clip-merge/video", "/hls/clip-merge/audio" })
        {
            Assert.Equal(0, server.Hits(root + "/av-1.m4s"));
            Assert.Equal(1, server.Hits(root + "/av-2.m4s"));
            Assert.Equal(0, server.Hits(root + "/av-3.m4s"));
        }
    }

    [Fact]
    public async Task SegmentedMerge_DecryptsAes128RenditionsAndReusesTheKey()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/merge-encrypted";
        const string keyPath = root + "/key.bin";
        server.HlsFiles[keyPath] = FaultServer.HlsKey;

        byte[] Encrypt(byte[] bytes, byte[] iv)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = FaultServer.HlsKey;
            return aes.EncryptCbc(bytes, iv, System.Security.Cryptography.PaddingMode.PKCS7);
        }

        byte[] SequenceIv(int sequence)
        {
            var iv = new byte[16];
            for (int i = 0; i < 4; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
            return iv;
        }

        foreach (string rendition in new[] { "video", "audio" })
        {
            string path = $"{root}/{rendition}";
            server.HlsFiles[$"{path}/init.mp4"] = Encrypt(DashTransferTests.Fixture("av-init.mp4"), FaultServer.HlsIv);
            string[] fragments = ["av-1.m4s", "av-2.m4s", "av-3.m4s"];
            for (int i = 0; i < fragments.Length; i++)
                server.HlsFiles[$"{path}/{fragments[i]}"] = Encrypt(DashTransferTests.Fixture(fragments[i]), SequenceIv(i));

            server.HlsPlaylists[$"{path}/index.m3u8"] = string.Join('\n',
                "#EXTM3U", "#EXT-X-VERSION:7",
                $"#EXT-X-KEY:METHOD=AES-128,URI=\"../key.bin\",IV=0x{Convert.ToHexString(FaultServer.HlsIv)}",
                "#EXT-X-MAP:URI=\"init.mp4\"",
                "#EXT-X-KEY:METHOD=AES-128,URI=\"../key.bin\"",
                string.Join('\n', fragments.Select(fragment => $"#EXTINF:1,\n{fragment}")),
                "#EXT-X-ENDLIST", "");
        }

        string videoUrl = server.Url($"{root}/video/index.m3u8");
        string audioUrl = server.Url($"{root}/audio/index.m3u8");
        var plan = new MediaMergePlan
        {
            Version = 2, OutputContainer = "mp4", RequireAudio = true, RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "hls-video", videoUrl, 0, "mp4", "h264", Source: new MergeMediaSource("hls", videoUrl)),
                new MergeTrack("audio", "hls-audio", audioUrl, 0, "m4a", "aac", Source: new MergeMediaSource("hls", audioUrl))
            ]
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(videoUrl, FileName: "hls-encrypted-merged.mp4", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        using var output = File.OpenRead(done.FinalPath!);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], MediaMerger.Demux(output, output.Length, default).Select(track => track.Kind));
        Assert.Equal(1, server.Hits(keyPath));
    }

}
