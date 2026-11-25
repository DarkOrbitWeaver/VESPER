using System.Buffers.Binary;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

/// <summary>HLS end to end: playlist parsing, optional AES-128, segment commits and resume at a segment boundary.</summary>
public class HlsTests
{
    private static Guid AddHls(Harness h, FaultServer s, string path) =>
        h.Engine.Add(new AddRequest(s.Url(path), FileName: "video", Kind: JobKind.Hls)).JobId;

    private static byte[] Id3TimestampTag(ulong timestamp, byte version = 3)
    {
        byte[] owner = "com.apple.streaming.transportStreamTimestamp"u8.ToArray();
        int bodyLength = owner.Length + 1 + 8;
        byte[] tag = new byte[10 + 10 + bodyLength];
        "ID3"u8.CopyTo(tag);
        tag[3] = version;
        int tagBodyLength = tag.Length - 10;
        tag[6] = (byte)((tagBodyLength >> 21) & 0x7f);
        tag[7] = (byte)((tagBodyLength >> 14) & 0x7f);
        tag[8] = (byte)((tagBodyLength >> 7) & 0x7f);
        tag[9] = (byte)(tagBodyLength & 0x7f);
        "PRIV"u8.CopyTo(tag.AsSpan(10));
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(14), (uint)bodyLength);
        owner.CopyTo(tag.AsSpan(20));
        BinaryPrimitives.WriteUInt64BigEndian(tag.AsSpan(21 + owner.Length), timestamp);
        return tag;
    }

    private static byte[] AdtsFrame(ReadOnlySpan<byte> sample, DemuxedTrack track)
    {
        byte[] config = track.CodecPrivate!;
        int objectType = config[0] >> 3;
        int frequencyIndex = ((config[0] & 7) << 1) | (config[1] >> 7);
        int channels = (config[1] >> 3) & 15;
        int frameLength = sample.Length + 7;
        var frame = new byte[frameLength];
        frame[0] = 0xff;
        frame[1] = 0xf1;
        frame[2] = (byte)(((objectType - 1) << 6) | (frequencyIndex << 2) | (channels >> 2));
        frame[3] = (byte)(((channels & 3) << 6) | ((frameLength >> 11) & 3));
        frame[4] = (byte)(frameLength >> 3);
        frame[5] = (byte)(((frameLength & 7) << 5) | 0x1f);
        frame[6] = 0xfc;
        sample.CopyTo(frame.AsSpan(7));
        return frame;
    }

    [Theory]
    [InlineData("/hls/plain/index.m3u8")]
    [InlineData("/hls/plain/playlist")]
    public async Task A_plain_VOD_playlist_becomes_one_transport_stream_file(string path)
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        if (path.EndsWith("/playlist", StringComparison.Ordinal))
            server.HlsPlaylists[path] = "#EXTM3U\n" + string.Concat(Enumerable.Range(0, FaultServer.SegmentCount)
                .Select(index => $"#EXTINF:4,\ns{index}.ts\n")) + "#EXT-X-ENDLIST\n";
        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, path), JobState.Completed);
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
    public async Task WebVtt_rendition_merges_segments_and_maps_timestamps_to_one_sidecar()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\ns0.vtt\n#EXTINF:5,\ns1.vtt\n#EXT-X-ENDLIST\n";
        server.HlsPlaylists["/hls/subtitles/index.m3u8"] = playlist;
        server.HlsFiles["/hls/subtitles/s0.vtt"] = Encoding.UTF8.GetBytes(
            "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\nopening\n00:00:01.000 --> 00:00:03.000\nOpening\n\nshared\n00:00:02.000 --> 00:00:04.000 align:start\nShared\n");
        server.HlsFiles["/hls/subtitles/s1.vtt"] = Encoding.UTF8.GetBytes(
            "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:1350000\n\nnext\n00:00:01.000 --> 00:00:02.000\nLater\n");
        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/hls/subtitles/index.m3u8"), FileName: "captions.en.vtt",
            Kind: JobKind.Hls, MediaPlanJson: new HlsWebVttPlan().ToJson()));

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        string saved = await File.ReadAllTextAsync(done.FinalPath!);
        Assert.StartsWith("WEBVTT\n", saved);
        Assert.Contains("00:00:01.000 --> 00:00:03.000", saved);
        Assert.Contains("00:00:02.000 --> 00:00:04.000 align:start", saved);
        Assert.Contains("00:00:06.000 --> 00:00:07.000", saved);
        Assert.EndsWith(".vtt", done.FinalPath);
    }

    [Fact]
    public void HlsWebVtt_subtitle_choices_use_a_valid_srt_output_plan()
    {
        var subtitle = new SubtitleTrack(new Uri("https://cdn.example/captions/index.m3u8"), "en", "English", "hls-webvtt");
        var video = new MediaDownloadChoice("https://cdn.example/video.mkv", "video.mkv", JobKind.Merge, null, null,
            SubtitleTracks: [subtitle]);

        MediaDownloadChoice choice = Assert.Single(MediaTransferBridge.ExpandSubtitleChoices([video]).Skip(1));
        Assert.Equal("video.en.srt", choice.FileName);
        Assert.Equal(JobKind.Hls, choice.Kind);
        Assert.True(HlsWebVttPlan.TryParse(choice.MediaPlanJson, out HlsWebVttPlan plan));
        Assert.Equal("srt", plan.OutputFormat);
        Assert.False(HlsWebVttPlan.TryParse(new HlsWebVttPlan { OutputFormat = "ass" }.ToJson(), out _));
        Assert.True(HlsWebVttPlan.TryParse("{\"Version\":1,\"Type\":\"hls-webvtt\"}", out HlsWebVttPlan legacyPlan));
        Assert.Equal("vtt", legacyPlan.OutputFormat);

        SubtitleTrack directSubtitle = subtitle with
        {
            Url = new Uri("https://cdn.example/captions/en.vtt"),
            Format = "vtt"
        };
        MediaDownloadChoice direct = Assert.Single(MediaTransferBridge.ExpandSubtitleChoices(
            [video with { SubtitleTracks = [directSubtitle] }]).Skip(1));
        Assert.Equal("video.en.vtt", direct.FileName);
        Assert.Equal(JobKind.Http, direct.Kind);
        Assert.Null(direct.MediaPlanJson);
    }

    [Fact]
    public async Task WebVtt_rendition_converts_to_srt_with_shifted_timing_and_multiline_text()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/subtitles-srt/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\ns0.vtt\n#EXT-X-ENDLIST\n";
        server.HlsFiles["/hls/subtitles-srt/s0.vtt"] = Encoding.UTF8.GetBytes(
            "WEBVTT\n\nopening\n00:00:02.125 --> 00:00:03.500 align:start\nFirst line\nSecond line &amp; text\n");
        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/hls/subtitles-srt/index.m3u8"), FileName: "captions.en.srt",
            Kind: JobKind.Hls, MediaPlanJson: new HlsWebVttPlan { OutputFormat = "srt" }.ToJson()));

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        string saved = (await File.ReadAllTextAsync(done.FinalPath!)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("1\n00:00:02,125 --> 00:00:03,500\nFirst line\nSecond line &amp; text\n\n", saved);
        Assert.EndsWith(".srt", done.FinalPath);
    }

    [Fact]
    public async Task WebVtt_rendition_rejects_live_playlists_without_publishing_partial_output()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/subtitles-live/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\ns0.vtt\n";
        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/hls/subtitles-live/index.m3u8"), FileName: "captions.en.vtt",
            Kind: JobKind.Hls, MediaPlanJson: new HlsWebVttPlan().ToJson()));

        JobSnapshot stopped = await h.WaitAsync(added.JobId, state => state.State == JobState.NeedsAttention);
        Assert.Equal("subtitle-live", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task WebVtt_rendition_rejects_malformed_timestamp_maps()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/subtitles-invalid/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\ns0.vtt\n#EXT-X-ENDLIST\n";
        server.HlsFiles["/hls/subtitles-invalid/s0.vtt"] = Encoding.UTF8.GetBytes(
            "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:invalid,MPEGTS:900000\n\n00:00:01.000 --> 00:00:02.000\nText\n");
        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/hls/subtitles-invalid/index.m3u8"), FileName: "captions.en.vtt",
            Kind: JobKind.Hls, MediaPlanJson: new HlsWebVttPlan().ToJson()));

        JobSnapshot stopped = await h.WaitAsync(added.JobId, state => state.State == JobState.NeedsAttention);
        Assert.Equal("subtitle-invalid", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task WebVtt_completed_output_recovers_without_fetching_the_playlist_again()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/subtitles-recovery/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXTINF:5,\ns0.vtt\n#EXT-X-ENDLIST\n";
        server.HlsFiles["/hls/subtitles-recovery/s0.vtt"] = Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nSaved\n");
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(point =>
        {
            if (point == "finalize.afterFlush")
            {
                crashed.TrySetResult();
                throw new FailpointCrash(point);
            }
        });
        var added = h.Engine.Add(new AddRequest(server.Url("/hls/subtitles-recovery/index.m3u8"), FileName: "captions.en.vtt",
            Kind: JobKind.Hls, MediaPlanJson: new HlsWebVttPlan().ToJson()));
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Contains("Saved", await File.ReadAllTextAsync(done.FinalPath!));
        Assert.Equal(1, server.Hits("/hls/subtitles-recovery/index.m3u8"));
        Assert.Equal(1, server.Hits("/hls/subtitles-recovery/s0.vtt"));
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
        server.HlsPlaylists["/hls/unsupported/index.m3u8"] =
            "#EXTM3U\n#EXT-X-I-FRAMES-ONLY\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns0.ts\n#EXT-X-ENDLIST\n";
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, "/hls/unsupported/index.m3u8"),
            state => state.State == JobState.NeedsAttention);
        Assert.Equal("unsupported", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
    }

    [Fact]
    public async Task Compatible_ts_epochs_are_rebased_and_accept_aes_key_rotation()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-keys";
        byte[] keyA = FaultServer.HlsKey;
        byte[] keyB = Payloads.Make(16, 902);
        byte[] ivA = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        byte[] ivB = Convert.FromHexString("101112131415161718191A1B1C1D1E1F");
        byte[] first = FaultServer.HlsSegment(0), second = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/key-a.bin"] = keyA;
        server.HlsFiles[$"{root}/key-b.bin"] = keyB;
        server.HlsFiles[$"{root}/s0.ts"] = Encrypt(first, keyA, ivA);
        server.HlsFiles[$"{root}/s1.ts"] = Encrypt(second, keyB, ivB);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXT-X-MEDIA-SEQUENCE:20", "#EXT-X-DISCONTINUITY-SEQUENCE:5",
            $"#EXT-X-KEY:METHOD=AES-128,URI=\"key-a.bin\",IV=0x{Convert.ToHexString(ivA)}",
            "#EXTINF:4.0,", "s0.ts", "#EXT-X-DISCONTINUITY",
            $"#EXT-X-KEY:METHOD=AES-128,URI=\"key-b.bin\",IV=0x{Convert.ToHexString(ivB)}",
            "#EXTINF:4.0,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot done = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.Completed);
        byte[] output = await File.ReadAllBytesAsync(done.FinalPath!);
        Assert.EndsWith(".ts", done.FinalPath);
        Assert.Equal(first.Length + second.Length, output.Length);
        Assert.NotEqual(first.Concat(second).ToArray(), output);
        Assert.Equal(1, server.Hits($"{root}/key-a.bin"));
        Assert.Equal(1, server.Hits($"{root}/key-b.bin"));

        using var input = new MemoryStream(output);
        using var payloads = new MemoryStream();
        List<DemuxedTrack> tracks = MpegTsDemuxer.Read(input, input.Length, payloads, CancellationToken.None);
        Assert.Contains(tracks, track => track.Kind == TrackKind.Video);
        Assert.Contains(tracks, track => track.Kind == TrackKind.Audio);
    }

    [Fact]
    public async Task Hls_epoch_with_a_changed_track_map_is_refused_before_its_bytes_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-track-change";
        byte[] first = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = ChangeVideoStreamType(FaultServer.HlsSegment(0), 0x24);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Hls_epoch_with_a_changed_ignored_stream_pid_is_refused_before_its_bytes_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-data-stream-change";
        byte[] first = AddIgnoredStream(FaultServer.HlsSegment(0), 0x1e0, 0x06);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = AddIgnoredStream(FaultServer.HlsSegment(0), 0x1e1, 0x06);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Theory]
    [InlineData(0x1e0, true)]
    [InlineData(0, false)]
    [InlineData(0x1fff, false)]
    public async Task Hls_pmt_rejects_duplicate_or_reserved_ignored_stream_pids_before_commit(int pid, bool duplicate)
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/malformed-ignored-pid";
        byte[] segment = AddIgnoredStream(FaultServer.HlsSegment(0), pid, 0x06);
        if (duplicate) segment = AddIgnoredStream(segment, pid, 0x06);
        server.HlsFiles[$"{root}/s0.ts"] = segment;
        server.HlsPlaylists[$"{root}/index.m3u8"] =
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns0.ts\n#EXT-X-ENDLIST\n";
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Hls_epoch_with_a_changed_program_descriptor_is_refused_before_its_bytes_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-program-descriptor-change";
        byte[] first = AddProgramDescriptor(FaultServer.HlsSegment(0), 0x11);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = AddProgramDescriptor(FaultServer.HlsSegment(0), 0x22);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Hls_epoch_with_a_changed_transport_stream_identity_is_refused_before_its_bytes_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-transport-identity-change";
        byte[] first = ChangeTransportStreamId(FaultServer.HlsSegment(0), 11);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = ChangeTransportStreamId(FaultServer.HlsSegment(0), 12);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Hls_epoch_with_same_pmt_but_changed_h264_profile_is_refused_before_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-codec-config-change";
        byte[] first = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = ChangeH264ProfileIdc(FaultServer.HlsSegment(0));
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Hls_epoch_with_a_malformed_timestamp_is_refused_before_its_bytes_commit()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-invalid-time";
        byte[] first = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = CorruptFirstPresentationTimestamp(FaultServer.HlsSegment(0));
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Hls_epoch_with_a_short_declared_pes_header_is_refused_before_commit(int pesOrdinal)
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-short-pes-header";
        byte[] first = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = ShortenPresentationHeader(FaultServer.HlsSegment(0), pesOrdinal);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("invalid-container", stopped.Reason);
        Assert.Equal(first.Length, stopped.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public async Task Fragmented_mp4_discontinuity_is_refused_before_fetching_media()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/fmp4-discontinuity";
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXTINF:1,", "av-1.m4s", "#EXT-X-DISCONTINUITY", "#EXTINF:1,", "av-2.m4s", "#EXT-X-ENDLIST", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitForStateAsync(AddHls(h, server, $"{root}/index.m3u8"), JobState.NeedsAttention);

        Assert.Equal("discontinuity", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Equal(0, server.Hits($"{root}/init.mp4"));
        Assert.Equal(0, server.Hits($"{root}/av-1.m4s"));
    }

    [Fact]
    public async Task Hls_epoch_cursor_restores_rebased_timing_without_redownloading_the_committed_prefix()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-restart";
        byte[] first = FaultServer.HlsSegment(0), second = FaultServer.HlsSegment(0), third = FaultServer.HlsSegment(1);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = second;
        server.HlsFiles[$"{root}/s2.ts"] = third;
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXTINF:4,", "s2.ts", "#EXT-X-ENDLIST", "");
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int commits = 0;
        using var h = new Harness(point =>
        {
            if (point == "checkpoint.afterCommit" && Interlocked.Increment(ref commits) == 2)
            {
                crashed.TrySetResult();
                throw new FailpointCrash(point);
            }
        });
        Guid id = AddHls(h, server, $"{root}/index.m3u8");
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(first.Length + second.Length + third.Length, done.CommittedBytes);
        Assert.Equal(1, server.Hits($"{root}/s0.ts"));
        Assert.Equal(1, server.Hits($"{root}/s1.ts"));
        using var input = File.OpenRead(done.FinalPath!);
        using var payloads = new MemoryStream();
        Assert.Equal(2, MpegTsDemuxer.Read(input, input.Length, payloads, CancellationToken.None).Count);
    }

    [Fact]
    public async Task RotatedVodRefreshReplaysAndVerifiesRewrittenTransportEpochs()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string oldRoot = "/hls/old-epochs", newRoot = "/hls/new-epochs";
        byte[] first = FaultServer.HlsSegment(0), second = FaultServer.HlsSegment(0), third = FaultServer.HlsSegment(1);
        string playlist = string.Join('\n', "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXTINF:4,", "s0.ts",
            "#EXT-X-DISCONTINUITY", "#EXTINF:4,", "s1.ts", "#EXTINF:4,", "final.ts", "#EXT-X-ENDLIST", "");
        foreach (string root in new[] { oldRoot, newRoot })
        {
            server.HlsFiles[root + "/s0.ts"] = first;
            server.HlsFiles[root + "/s1.ts"] = second;
            server.HlsPlaylists[root + "/index.m3u8"] = playlist;
        }
        server.HlsFiles[newRoot + "/final.ts"] = third;
        string oldUrl = server.Url(oldRoot + "/index.m3u8"), newUrl = server.Url(newRoot + "/index.m3u8");
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "epoch.ts", Kind: JobKind.Hls,
            MediaProvider: "fixture", MediaId: "epoch", MediaFormatId: "video", MediaQuality: "720p with audio"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        Assert.Equal(first.Length + second.Length, stopped.CommittedBytes);
        byte[] prefix = await File.ReadAllBytesAsync(stopped.PartialPath);
        Assert.NotEqual(first.Concat(second).ToArray(), prefix); // epoch 2 has rewritten timestamps/counters
        var source = new ResolvedMediaSource("fixture", "epoch", "video", newUrl);
        server.HlsFiles[newRoot + "/s1.ts"] = third;
        RescueResult refused = await h.Engine.RefreshMediaSourceAsync(added.JobId, source, default, oldUrl);
        Assert.False(refused.Accepted);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(stopped.PartialPath));
        server.HlsFiles[newRoot + "/s1.ts"] = second;
        h.Engine.Options.MaxConcurrentJobs = 0;
        h.Engine.SettingsChanged();
        RescueResult accepted = await h.Engine.RefreshMediaSourceAsync(added.JobId, source, default, oldUrl);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Equal(stopped.CommittedBytes, accepted.RetainedBytes);
        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(stopped.PartialPath));
        int firstHits = server.Hits(newRoot + "/s0.ts"), secondHits = server.Hits(newRoot + "/s1.ts");
        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(firstHits, server.Hits(newRoot + "/s0.ts"));
        Assert.Equal(secondHits, server.Hits(newRoot + "/s1.ts"));
        using var baseline = new Harness();
        JobSnapshot full = await baseline.WaitForStateAsync(AddHls(baseline, server, newRoot + "/index.m3u8"), JobState.Completed);
        Assert.Equal(await File.ReadAllBytesAsync(full.FinalPath!), await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Hls_resume_rejects_a_changed_discontinuity_sequence_before_appending()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string root = "/hls/epoch-sequence-change";
        byte[] first = FaultServer.HlsSegment(0);
        server.HlsFiles[$"{root}/s0.ts"] = first;
        server.HlsFiles[$"{root}/s1.ts"] = FaultServer.HlsSegment(1);
        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXT-X-DISCONTINUITY-SEQUENCE:0",
            "#EXTINF:4,", "s0.ts", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int commits = 0;
        using var h = new Harness(point =>
        {
            if (point == "checkpoint.afterCommit" && Interlocked.Increment(ref commits) == 1)
            {
                crashed.TrySetResult();
                throw new FailpointCrash(point);
            }
        });
        Guid id = AddHls(h, server, $"{root}/index.m3u8");
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int priorSecondHits = server.Hits($"{root}/s1.ts");

        server.HlsPlaylists[$"{root}/index.m3u8"] = string.Join('\n',
            "#EXTM3U", "#EXT-X-TARGETDURATION:4", "#EXT-X-DISCONTINUITY-SEQUENCE:1",
            "#EXTINF:4,", "s0.ts", "#EXTINF:4,", "s1.ts", "#EXT-X-ENDLIST", "");
        h.Restart();
        JobSnapshot refused = await h.WaitForStateAsync(id, JobState.NeedsAttention);

        Assert.Equal("source-changed", refused.Reason);
        Assert.Equal(first.Length, refused.CommittedBytes);
        Assert.Equal(first, await File.ReadAllBytesAsync(refused.PartialPath));
        Assert.Equal(priorSecondHits, server.Hits($"{root}/s1.ts"));
    }

    [Fact]
    public void Epoch_rewrite_honors_cancellation_before_returning_a_segment()
    {
        byte[] original = FaultServer.HlsSegment(0);
        byte[] saved = [.. original];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var timeline = new HlsTransportTimeline();

        Assert.Throws<OperationCanceledException>(() => timeline.Rewrite(original, 90_000, startEpoch: true,
            ct: cancellation.Token));
        Assert.Equal(saved, original);
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
    public async Task Master_playlist_without_a_usable_address_says_so()
    {
        // The only quality has an address that cannot be resolved. This used to end as "Unexpected error: Invalid URI".
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/no-usable-address/index.m3u8";
        server.HlsPlaylists[path] = string.Join('\n', "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000", "https://[broken/variant.m3u8", "");
        using var h = new Harness();

        JobSnapshot stopped = await h.WaitAsync(AddHls(h, server, path), state => state.State == JobState.NeedsAttention);
        MediaProbeResult probe = await h.Engine.ProbeMediaAsync(server.Url(path));

        Assert.Equal("unsupported", stopped.Reason);
        Assert.Equal("The playlist lists no quality with a usable address.", stopped.Message);
        Assert.Equal("unsupported", probe.Kind);
        Assert.Equal("The playlist lists no quality with a usable address.", probe.Message);
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
    public async Task LiveHls_A_checkpoint_keeps_a_stop_requested_after_its_batch_was_taken()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        const string playlist = "/hls/live-stop-checkpoint/index.m3u8";
        byte[] first = FaultServer.HlsSegment(0);
        byte[] second = FaultServer.HlsSegment(1);
        server.HlsPlaylists[playlist] = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\none.ts\n#EXTINF:1,\ntwo.ts\n";
        server.HlsFiles["/hls/live-stop-checkpoint/one.ts"] = first;
        server.HlsFiles["/hls/live-stop-checkpoint/two.ts"] = second;

        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness? active = null;
        int flushes = 0, commits = 0;
        bool stopAccepted = false;
        using var h = active = new Harness(point =>
        {
            if (point == "checkpoint.afterFlush" && Interlocked.Increment(ref flushes) == 2)
            {
                DownloadEngine engine = active!.Engine;
                stopAccepted = engine.StopAndKeepLive(Assert.Single(engine.Snapshots()).Id);
            }
            if (point == "checkpoint.afterCommit" && Interlocked.Increment(ref commits) == 2)
            {
                crashed.TrySetResult();
                throw new FailpointCrash(point);
            }
        });
        Guid id = AddHls(h, server, playlist);
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stopAccepted);
        using (var store = JobStore.Open(Path.Combine(h.Data, "vesper.db")))
        {
            DownloadJob saved = Assert.Single(store.LoadAll());
            using JsonDocument cursor = JsonDocument.Parse(saved.MediaState!);
            Assert.True(cursor.RootElement.GetProperty("StopRequested").GetBoolean());
            Assert.Equal(first.LongLength + second.LongLength, saved.Extents.TotalBytes);
        }

        h.Restart();
        JobSnapshot completed = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(first.Concat(second).ToArray(), await File.ReadAllBytesAsync(completed.FinalPath!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_fmp4_initializations_release_slots_before_fetching_fragments(bool live)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchingSecondParts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondParts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int initRequests = 0, secondPartRequests = 0, includeSecond = 0;
        byte[] init = DashTransferTests.Fixture("av-init.mp4");
        byte[] first = DashTransferTests.Fixture("av-1.m4s");
        byte[] second = DashTransferTests.Fixture("av-2.m4s");
        const string playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\nfirst.m4s\n";

        server.MapGet("/{id}/index.m3u8", () =>
        {
            string text = live ? playlist : playlist + "#EXT-X-ENDLIST\n";
            if (live && Volatile.Read(ref includeSecond) != 0)
                text += "#EXTINF:1,\nsecond.m4s\n";
            return Results.Text(text, "application/vnd.apple.mpegurl");
        });
        server.MapGet("/{id}/init.mp4", async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref initRequests) == 3) initialized.TrySetResult();
            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(8), context.RequestAborted);
            return Results.File(init, "video/mp4");
        });
        server.MapGet("/{id}/first.m4s", () => Results.File(first, "video/mp4"));
        server.MapGet("/{id}/second.m4s", async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref secondPartRequests) == 3) fetchingSecondParts.TrySetResult();
            await releaseSecondParts.Task.WaitAsync(context.RequestAborted);
            return Results.File(second, "video/mp4");
        });
        await server.StartAsync();

        using var h = new Harness(tweak: options => options.MaxJobsPerHost = 3);
        Guid[] ids = Enumerable.Range(0, 3).Select(i => h.Engine.Add(new AddRequest(
            $"{server.Urls.Single()}/{i}/index.m3u8", FileName: $"recording-{i}.mp4", Kind: JobKind.Hls)).JobId).ToArray();
        byte[] expected = [.. init, .. first];
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(8));

        if (!live)
        {
            JobSnapshot[] results = await Task.WhenAll(ids.Select(id => h.WaitForStateAsync(id, JobState.Completed, seconds: 8)));
            foreach (JobSnapshot completed in results)
                Assert.Equal(expected, await File.ReadAllBytesAsync(completed.FinalPath!));
            return;
        }

        await Task.WhenAll(ids.Select(id => h.WaitAsync(id, s => s.CommittedBytes == expected.Length, seconds: 8)));
        Volatile.Write(ref includeSecond, 1);
        try
        {
            await fetchingSecondParts.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True((await h.Engine.CancelDownloadAsync(ids[0]).WaitAsync(TimeSpan.FromSeconds(8))).Cancelled);
            Assert.True(h.Engine.StopAndKeepLive(ids[1]));
            JobSnapshot stopped = await h.WaitForStateAsync(ids[1], JobState.Completed, seconds: 8);
            Assert.Equal(expected, await File.ReadAllBytesAsync(stopped.FinalPath!));
        }
        finally { releaseSecondParts.TrySetResult(); }

        long fullLength = expected.LongLength + second.Length;
        await h.WaitAsync(ids[2], s => s.CommittedBytes == fullLength, seconds: 8);
        Assert.True(h.Engine.StopAndKeepLive(ids[2]));
        JobSnapshot final = await h.WaitForStateAsync(ids[2], JobState.Completed, seconds: 8);
        Assert.Equal(expected.Concat(second).ToArray(), await File.ReadAllBytesAsync(final.FinalPath!));
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
    public async Task HlsResume_discards_uncommitted_bytes_before_fetching_the_next_segment()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        const string path = "/hls/resume-tail/index.m3u8";
        server.HlsPlaylists[path] = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns0.ts\n#EXTINF:4,\nmissing.ts\n#EXT-X-ENDLIST\n";
        server.HlsFiles["/hls/resume-tail/s0.ts"] = FaultServer.HlsSegment(0);
        using var h = new Harness();

        Guid id = AddHls(h, server, path);
        JobSnapshot waiting = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(waiting.CommittedBytes > 0);

        h.Restart();
        await AppendUncommittedTailAsync(waiting.PartialPath);
        h.Engine.Resume(id);
        await h.WaitAsync(id, job => job.State == JobState.NeedsLink
            && server.Hits("/hls/resume-tail/missing.ts") >= 2);

        Assert.Equal(waiting.CommittedBytes, new FileInfo(waiting.PartialPath).Length);
    }

    [Fact]
    public async Task AutomaticVodRefreshRebasesOnlyAnIdenticalCommittedPrefix()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        string[] names = ["av-1.m4s", "av-2.m4s", "av-3.m4s"];
        byte[] init = DashTransferTests.Fixture("av-init.mp4");
        byte[][] fragments = names.Select(DashTransferTests.Fixture).ToArray();
        string playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n"
            + string.Concat(names.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";
        foreach (string root in new[] { "/hls/old-vod", "/hls/new-vod" })
        {
            server.HlsPlaylists[root + "/index.m3u8"] = playlist;
            server.HlsFiles[root + "/init.mp4"] = init;
            server.HlsFiles[root + "/" + names[0]] = fragments[0];
        }
        string oldUrl = server.Url("/hls/old-vod/index.m3u8");
        string newUrl = server.Url("/hls/new-vod/index.m3u8");
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "vod-refresh.mp4", Kind: JobKind.Hls,
            MediaProvider: "fixture", MediaId: "vod", MediaFormatId: "video", OriginalPageUrl: server.Url("/page")));
        Assert.True(added.Ok, added.Message);
        JobSnapshot waiting = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        Assert.Equal(init.Length + fragments[0].Length, waiting.CommittedBytes);
        var replacement = new ResolvedMediaSource("fixture", "vod", "video", newUrl);
        RescueResult stale = await h.Engine.RefreshMediaSourceAsync(added.JobId, replacement, default, oldUrl + "?stale");
        Assert.False(stale.Accepted);
        byte[] altered = fragments[0].ToArray();
        altered[^1] ^= 1;
        server.HlsFiles["/hls/new-vod/" + names[0]] = altered;
        RescueResult mismatch = await h.Engine.RefreshMediaSourceAsync(added.JobId, replacement, default, oldUrl);
        Assert.False(mismatch.Accepted);
        byte[] expectedPrefix = [.. init, .. fragments[0]];
        Assert.Equal(expectedPrefix, await File.ReadAllBytesAsync(waiting.PartialPath));
        for (int i = 0; i < names.Length; i++) server.HlsFiles["/hls/new-vod/" + names[i]] = fragments[i];
        RescueResult accepted = await h.Engine.RefreshMediaSourceAsync(added.JobId, replacement, default, oldUrl);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Equal(waiting.CommittedBytes, accepted.RetainedBytes);
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        byte[] expected = [.. init, .. fragments[0], .. fragments[1], .. fragments[2]];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    private static async Task AppendUncommittedTailAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] tail = [0xA5, 0x5A, 0xC3, 0x3C];
        await file.WriteAsync(tail);
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

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SegmentedMergeJob_MergesId3TimestampedPackedAacHlsAudio(byte version)
    {
        await using var server = new FaultServer(); await server.StartAsync();
        string[] fragmentNames = ["av-1.m4s", "av-2.m4s", "av-3.m4s"];
        byte[] initialization = DashTransferTests.Fixture("av-init.mp4");
        byte[][] fragments = fragmentNames.Select(DashTransferTests.Fixture).ToArray();
        byte[] sourceBytes = [.. initialization, .. fragments.SelectMany(fragment => fragment)];
        var sourceMp4 = FragmentedMp4.Open(initialization, captureSamples: true);
        long sourceOffset = initialization.Length;
        long[] fragmentStarts = new long[fragments.Length];
        for (int i = 0; i < fragments.Length; i++)
        {
            fragmentStarts[i] = sourceOffset;
            sourceMp4.ValidateFragment(fragments[i], sourceOffset);
            sourceOffset += fragments[i].Length;
        }
        sourceMp4.Finish(requireAudio: true, requireVideo: true);
        DemuxedTrack sourceAudio = sourceMp4.GetTracks().Single(track => track.Kind == TrackKind.Audio);

        byte[][] packedSegments = new byte[fragments.Length][];
        for (int i = 0; i < fragments.Length; i++)
        {
            long fragmentEnd = fragmentStarts[i] + fragments[i].Length;
            TrackSample[] samples = [.. sourceAudio.Samples.Where(sample => sample.Offset >= fragmentStarts[i]
                && sample.Offset < fragmentEnd)];
            Assert.NotEmpty(samples);
            ulong timestamp = checked((ulong)((Int128)samples[0].Dts * 90_000 / sourceAudio.Timescale));
            using var segment = new MemoryStream();
            segment.Write(Id3TimestampTag(timestamp, version));
            foreach (TrackSample sample in samples)
                segment.Write(AdtsFrame(sourceBytes.AsSpan(checked((int)sample.Offset), sample.Size), sourceAudio));
            packedSegments[i] = segment.ToArray();
        }

        const string videoRoot = "/hls/packed-audio/video";
        const string audioRoot = "/hls/packed-audio/audio";
        server.HlsPlaylists[videoRoot + "/index.m3u8"] = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n"
            + string.Concat(fragmentNames.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";
        server.HlsFiles[videoRoot + "/init.mp4"] = initialization;
        for (int i = 0; i < fragments.Length; i++)
            server.HlsFiles[videoRoot + "/" + fragmentNames[i]] = fragments[i];
        server.HlsPlaylists[audioRoot + "/index.m3u8"] = "#EXTM3U\n#EXT-X-TARGETDURATION:1\n"
            + string.Concat(packedSegments.Select((_, i) => $"#EXTINF:1,\naudio-{i + 1}.aac\n")) + "#EXT-X-ENDLIST\n";
        for (int i = 0; i < packedSegments.Length; i++)
            server.HlsFiles[audioRoot + $"/audio-{i + 1}.aac"] = packedSegments[i];

        string videoUrl = server.Url(videoRoot + "/index.m3u8");
        string audioUrl = server.Url(audioRoot + "/index.m3u8");
        var plan = new MediaMergePlan
        {
            Version = 2,
            OutputContainer = "mkv",
            RequireAudio = true,
            RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "hls-video", videoUrl, 0, "mp4", "h264", Source: new MergeMediaSource("hls", videoUrl)),
                new MergeTrack("audio", "hls-audio", audioUrl, 0, "ts", "aac", Source: new MergeMediaSource("hls", audioUrl))
            ]
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(videoUrl, FileName: "packed-audio.mkv", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        using var output = File.OpenRead(done.FinalPath!);
        List<DemuxedTrack> outputTracks = MediaMerger.Demux(output, output.Length, default);
        DemuxedTrack outputAudio = Assert.Single(outputTracks, track => track.Kind == TrackKind.Audio);
        Assert.Equal(sourceAudio.Samples.Count, outputAudio.Samples.Count);
        Assert.Equal(sourceAudio.SampleRate, outputAudio.SampleRate);
        Assert.Equal(sourceAudio.Channels, outputAudio.Channels);
        // ADTS carries the AAC-LC profile, sample rate and channels.
        Assert.Equal(sourceAudio.CodecPrivate![..2], outputAudio.CodecPrivate);
        for (int i = 0; i < sourceAudio.Samples.Count; i++)
        {
            TrackSample expected = sourceAudio.Samples[i];
            TrackSample actual = outputAudio.Samples[i];
            Assert.Equal(expected.Size, actual.Size);
            var expectedBytes = sourceBytes.AsSpan(checked((int)expected.Offset), expected.Size).ToArray();
            var actualBytes = new byte[actual.Size];
            output.Position = actual.Offset;
            output.ReadExactly(actualBytes);
            Assert.Equal(expectedBytes, actualBytes);
        }
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

    private static byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, iv, System.Security.Cryptography.PaddingMode.PKCS7);
    }

    private static byte[] CorruptFirstPresentationTimestamp(byte[] transport)
    {
        for (int packetOffset = 0; packetOffset < transport.Length; packetOffset += 188)
        {
            Span<byte> packet = transport.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset + 14 > packet.Length
                || packet[payloadOffset] != 0 || packet[payloadOffset + 1] != 0 || packet[payloadOffset + 2] != 1)
                continue;
            int flags = (packet[payloadOffset + 7] >> 6) & 3;
            if (flags is not (2 or 3)) continue;
            packet[payloadOffset + 11] &= 0xfe;
            return transport;
        }
        throw new InvalidOperationException("The transport fixture has no presentation timestamp.");
    }

    private static byte[] ShortenPresentationHeader(byte[] transport, int pesOrdinal)
    {
        int seen = 0;
        for (int packetOffset = 0; packetOffset < transport.Length; packetOffset += 188)
        {
            Span<byte> packet = transport.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset + 14 > packet.Length
                || packet[payloadOffset] != 0 || packet[payloadOffset + 1] != 0 || packet[payloadOffset + 2] != 1)
                continue;
            int flags = (packet[payloadOffset + 7] >> 6) & 3;
            if (flags is not (2 or 3) || ++seen != pesOrdinal) continue;
            packet[payloadOffset + 8] = (byte)(flags == 3 ? 5 : 0);
            return transport;
        }
        throw new InvalidOperationException("The transport fixture has too few timestamped PES packets.");
    }

    private static byte[] ChangeVideoStreamType(byte[] transport, byte streamType)
    {
        byte[] result = [.. transport];
        int? pmtPid = null;
        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != 0
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 12 > packet.Length || packet[sectionStart] != 0) continue;
            int sectionLength = 3 + (((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2]);
            int sectionEnd = sectionStart + sectionLength;
            if (sectionEnd > packet.Length) continue;
            for (int entry = sectionStart + 8; entry + 4 <= sectionEnd - 4; entry += 4)
            {
                if (packet[entry] == 0 && packet[entry + 1] == 0) continue;
                pmtPid = ((packet[entry + 2] & 0x1f) << 8) | packet[entry + 3];
                break;
            }
            if (pmtPid is not null) break;
        }
        if (pmtPid is null) throw new InvalidOperationException("The transport fixture has no program map PID.");

        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != pmtPid.Value
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 16 > packet.Length || packet[sectionStart] != 2) continue;
            int sectionLength = 3 + (((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2]);
            int sectionEnd = sectionStart + sectionLength;
            if (sectionEnd > packet.Length) continue;
            int programInfoLength = ((packet[sectionStart + 10] & 0x0f) << 8) | packet[sectionStart + 11];
            for (int entry = sectionStart + 12 + programInfoLength; entry + 5 <= sectionEnd - 4;)
            {
                int descriptorsLength = ((packet[entry + 3] & 0x0f) << 8) | packet[entry + 4];
                if (packet[entry] == 0x1b)
                {
                    packet[entry] = streamType;
                    uint crc = Mpeg2Crc(packet.Slice(sectionStart, sectionLength - 4));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(sectionEnd - 4, 4), crc);
                    return result;
                }
                entry += 5 + descriptorsLength;
            }
        }
        throw new InvalidOperationException("The transport fixture has no H.264 program entry.");
    }

    private static byte[] AddIgnoredStream(byte[] transport, int pid, byte streamType)
    {
        byte[] result = [.. transport];
        int? pmtPid = null;
        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != 0
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 12 > packet.Length || packet[sectionStart] != 0) continue;
            int sectionLength = 3 + (((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2]);
            int sectionEnd = sectionStart + sectionLength;
            if (sectionEnd > packet.Length) continue;
            for (int entry = sectionStart + 8; entry + 4 <= sectionEnd - 4; entry += 4)
            {
                if (packet[entry] == 0 && packet[entry + 1] == 0) continue;
                pmtPid = ((packet[entry + 2] & 0x1f) << 8) | packet[entry + 3];
                break;
            }
            if (pmtPid is not null) break;
        }
        if (pmtPid is null) throw new InvalidOperationException("The transport fixture has no program map PID.");

        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != pmtPid.Value
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 16 > packet.Length || packet[sectionStart] != 2) continue;
            int sectionLengthField = ((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2];
            int sectionLength = 3 + sectionLengthField;
            int sectionEnd = sectionStart + sectionLength;
            int newSectionEnd = sectionEnd + 5;
            if (newSectionEnd > packet.Length) continue;

            packet.Slice(sectionEnd - 4, 4).CopyTo(packet.Slice(sectionEnd + 1, 4));
            packet[sectionEnd - 4] = streamType;
            packet[sectionEnd - 3] = (byte)(0xe0 | (pid >> 8));
            packet[sectionEnd - 2] = (byte)pid;
            packet[sectionEnd - 1] = 0xf0;
            packet[sectionEnd] = 0;

            int newSectionLengthField = sectionLengthField + 5;
            packet[sectionStart + 1] = (byte)((packet[sectionStart + 1] & 0xf0) | (newSectionLengthField >> 8));
            packet[sectionStart + 2] = (byte)newSectionLengthField;
            uint crc = Mpeg2Crc(packet.Slice(sectionStart, newSectionEnd - sectionStart - 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(newSectionEnd - 4, 4), crc);
            return result;
        }
        throw new InvalidOperationException("The transport fixture has no room for another program entry.");
    }

    private static byte[] AddProgramDescriptor(byte[] transport, byte value)
    {
        byte[] result = [.. transport];
        int? pmtPid = null;
        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != 0
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 12 > packet.Length || packet[sectionStart] != 0) continue;
            int sectionLength = 3 + (((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2]);
            int sectionEnd = sectionStart + sectionLength;
            if (sectionEnd > packet.Length) continue;
            for (int entry = sectionStart + 8; entry + 4 <= sectionEnd - 4; entry += 4)
            {
                if (packet[entry] == 0 && packet[entry + 1] == 0) continue;
                pmtPid = ((packet[entry + 2] & 0x1f) << 8) | packet[entry + 3];
                break;
            }
            if (pmtPid is not null) break;
        }
        if (pmtPid is null) throw new InvalidOperationException("The transport fixture has no program map PID.");

        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != pmtPid.Value
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 16 > packet.Length || packet[sectionStart] != 2) continue;
            int sectionLengthField = ((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2];
            int sectionLength = 3 + sectionLengthField;
            int sectionEnd = sectionStart + sectionLength;
            int programInfoLength = ((packet[sectionStart + 10] & 0x0f) << 8) | packet[sectionStart + 11];
            int insertAt = sectionStart + 12 + programInfoLength;
            int newSectionEnd = sectionEnd + 3;
            if (newSectionEnd > packet.Length || programInfoLength > 0x0ffc) continue;

            packet.Slice(insertAt, sectionEnd - insertAt).CopyTo(packet.Slice(insertAt + 3, sectionEnd - insertAt));
            packet[insertAt] = 0x80;
            packet[insertAt + 1] = 1;
            packet[insertAt + 2] = value;
            int newProgramInfoLength = programInfoLength + 3;
            packet[sectionStart + 10] = (byte)((packet[sectionStart + 10] & 0xf0) | (newProgramInfoLength >> 8));
            packet[sectionStart + 11] = (byte)newProgramInfoLength;
            int newSectionLengthField = sectionLengthField + 3;
            packet[sectionStart + 1] = (byte)((packet[sectionStart + 1] & 0xf0) | (newSectionLengthField >> 8));
            packet[sectionStart + 2] = (byte)newSectionLengthField;
            uint crc = Mpeg2Crc(packet.Slice(sectionStart, newSectionEnd - sectionStart - 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(newSectionEnd - 4, 4), crc);
            return result;
        }
        throw new InvalidOperationException("The transport fixture has no room for another program descriptor.");
    }

    private static byte[] ChangeTransportStreamId(byte[] transport, ushort id)
    {
        byte[] result = [.. transport];
        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != 0
                || (packet[1] & 0x40) == 0) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0 || payloadOffset >= packet.Length) continue;
            int sectionStart = payloadOffset + 1 + packet[payloadOffset];
            if (sectionStart + 12 > packet.Length || packet[sectionStart] != 0) continue;
            int sectionLength = 3 + (((packet[sectionStart + 1] & 0x0f) << 8) | packet[sectionStart + 2]);
            int sectionEnd = sectionStart + sectionLength;
            if (sectionEnd > packet.Length) continue;
            packet[sectionStart + 3] = (byte)(id >> 8);
            packet[sectionStart + 4] = (byte)id;
            uint crc = Mpeg2Crc(packet.Slice(sectionStart, sectionLength - 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(sectionEnd - 4, 4), crc);
            return result;
        }
        throw new InvalidOperationException("The transport fixture has no program association table.");
    }

    private static byte[] ChangeH264ProfileIdc(byte[] transport)
    {
        byte[] result = [.. transport];
        for (int packetOffset = 0; packetOffset < result.Length; packetOffset += 188)
        {
            Span<byte> packet = result.AsSpan(packetOffset, 188);
            if (packet[0] != 0x47 || ((packet[1] & 0x1f) << 8 | packet[2]) != 0x100) continue;
            int payloadOffset = PayloadOffset(packet);
            if (payloadOffset < 0) continue;
            if ((packet[1] & 0x40) != 0 && payloadOffset + 9 <= packet.Length
                && packet[payloadOffset] == 0 && packet[payloadOffset + 1] == 0 && packet[payloadOffset + 2] == 1)
                payloadOffset += 9 + packet[payloadOffset + 8];
            for (int i = payloadOffset; i + 4 < packet.Length; i++)
            {
                int nal = packet[i] == 0 && packet[i + 1] == 0 && packet[i + 2] == 1 ? i + 3
                    : packet[i] == 0 && packet[i + 1] == 0 && packet[i + 2] == 0 && packet[i + 3] == 1 ? i + 4 : -1;
                if (nal < 0 || nal + 1 >= packet.Length || (packet[nal] & 0x1f) != 7) continue;
                packet[nal + 1] = packet[nal + 1] switch
                {
                    66 => 77,
                    77 => 66,
                    100 => 110,
                    110 => 100,
                    122 => 244,
                    244 => 122,
                    44 => 83,
                    83 => 44,
                    86 => 118,
                    118 => 86,
                    128 => 138,
                    138 => 128,
                    139 => 134,
                    134 => 139,
                    135 => 100,
                    _ => throw new InvalidOperationException("The H.264 fixture uses a profile without a compatible test alternate.")
                };
                return result;
            }
        }
        throw new InvalidOperationException("The transport fixture has no H.264 sequence parameter set.");
    }

    [Fact]
    public async Task BrokenHighestQualityVariantAddressDoesNotSilentlyDownloadALowerQuality()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        server.HlsPlaylists["/hls/broken-best/index.m3u8"] =
            "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080\nhttps://[broken/high.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=640000,RESOLUTION=640x360\n/hls/plain/index.m3u8\n";
        using var h = new Harness();
        Guid id = AddHls(h, server, "/hls/broken-best/index.m3u8");

        JobSnapshot stopped = await h.WaitAsync(id, s => s.State == JobState.NeedsAttention);
        Assert.Equal("unsupported", stopped.Reason);
        Assert.Contains("invalid playlist address", stopped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Null(stopped.FinalPath);
    }

    private static int PayloadOffset(Span<byte> packet)
    {
        int control = (packet[3] >> 4) & 3;
        if (control == 0 || (control & 1) == 0) return -1;
        int offset = 4;
        if ((control & 2) != 0) offset += 1 + packet[4];
        return offset < 188 ? offset : -1;
    }

    private static uint Mpeg2Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
                value = (value << 1) ^ ((value & 0x80000000) != 0 ? 0x04c11db7u : 0u);
        }
        return value;
    }

}
