using System.Text;
using Vesper.Engine;
using Xunit;

namespace Vesper.Tests;

/// <summary>
/// Native remux coverage using the checked-in CC0 synthetic fixtures (real H.264/AAC/VP9/Opus bitstreams) plus
/// mutated copies for adversarial input. Every merge is re-read and compared sample-by-sample with its sources.
/// </summary>
public sealed class MediaMergeTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));
    private static byte[] AacFmp4() => [.. Fixture("audio-init.mp4"), .. Fixture("audio-1.m4s")];

    private static DemuxedTrack Track(Stream s, TrackKind kind) => MediaMerger.Demux(s, s.Length, default).First(t => t.Kind == kind);

    private static (MemoryStream Output, string Container) Merge(byte[] video, byte[] audio, string? requested = null)
    {
        var vs = new MemoryStream(video);
        var aus = new MemoryStream(audio);
        DemuxedTrack v = Track(vs, TrackKind.Video), a = Track(aus, TrackKind.Audio);
        string container = MediaMerger.ChooseContainer([v, a], requested) ?? throw new InvalidOperationException("no legal container");
        var output = new MemoryStream();
        MediaMerger.Merge([(v, vs), (a, aus)], container, output, default);
        MediaMerger.Validate(output, output.Length, [v, a], default);
        var merged = MediaMerger.Demux(output, output.Length, default);
        AssertSamePayloads(v, vs, merged[0], output);
        AssertSamePayloads(a, aus, merged[1], output);
        return (output, container);
    }

    private static void AssertSamePayloads(DemuxedTrack source, Stream sourceStream, DemuxedTrack merged, Stream mergedStream)
    {
        Assert.Equal(source.Samples.Count, merged.Samples.Count);
        for (int i = 0; i < source.Samples.Count; i++)
        {
            var a = new byte[source.Samples[i].Size];
            var b = new byte[merged.Samples[i].Size];
            MuxIo.ReadExact(sourceStream, source.Samples[i].Offset, a);
            MuxIo.ReadExact(mergedStream, merged.Samples[i].Offset, b);
            Assert.True(a.AsSpan().SequenceEqual(b), $"{source.Kind} sample {i} differs after remux");
            Assert.Equal(source.Samples[i].Key, merged.Samples[i].Key);
        }
    }

    [Fact]
    public void RealH264AndAac_RemuxToFastStartMp4_WithEverySampleIntact()
    {
        var (output, container) = Merge(Fixture("video-only.mp4"), AacFmp4());
        Assert.Equal("mp4", container);
        byte[] bytes = output.ToArray();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
        int moov = bytes.AsSpan().IndexOf("moov"u8), mdat = bytes.AsSpan().IndexOf("mdat"u8);
        Assert.True(moov > 0 && moov < mdat, "moov must precede mdat (progressive playback)");
        Assert.Equal(-1, bytes.AsSpan().IndexOf("moof"u8));                 // flat output, not concatenated fragments
    }

    [Fact]
    public void RealVp9AndOpus_RemuxToWebm()
    {
        var (output, container) = Merge(Fixture("video-only.webm"), Fixture("muxed.webm"));
        Assert.Equal("webm", container);
        Assert.True(output.ToArray().AsSpan().IndexOf("webm"u8) is > 0 and < 64);
    }

    [Fact]
    public void H264WithOpus_GoesToMatroska_NeverMislabelledAsMp4OrWebm()
    {
        var vs = new MemoryStream(Fixture("video-only.mp4"));
        var aus = new MemoryStream(Fixture("muxed.webm"));
        DemuxedTrack v = Track(vs, TrackKind.Video), a = Track(aus, TrackKind.Audio);
        Assert.Null(MediaMerger.ChooseContainer([v, a], "mp4"));
        Assert.Null(MediaMerger.ChooseContainer([v, a], "webm"));
        var (_, container) = Merge(Fixture("video-only.mp4"), Fixture("muxed.webm"));
        Assert.Equal("mkv", container);
    }

    [Fact]
    public void Vp9WithAac_RequestedAsMp4_IsRefused()
    {
        var vs = new MemoryStream(Fixture("video-only.webm"));
        var aus = new MemoryStream(AacFmp4());
        Assert.Null(MediaMerger.ChooseContainer([Track(vs, TrackKind.Video), Track(aus, TrackKind.Audio)], "mp4"));
        Assert.Equal("mkv", MediaMerger.ChooseContainer([Track(vs, TrackKind.Video), Track(aus, TrackKind.Audio)], null));
    }

    [Fact]
    public void AudioOnlyFragmentedM4a_IsNormalisedToFlatM4a()
    {
        var aus = new MemoryStream(AacFmp4());
        var a = Track(aus, TrackKind.Audio);
        Assert.Equal("m4a", MediaMerger.ChooseContainer([a], "m4a"));
        var output = new MemoryStream();
        MediaMerger.Merge([(a, aus)], "m4a", output, default);
        MediaMerger.Validate(output, output.Length, [a], default);
    }

    [Fact]
    public void TruncatedOrCorruptInputs_AreRejected()
    {
        byte[] aac = AacFmp4();
        Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(aac[..^200]); MediaMerger.Demux(s, s.Length, default); });
        byte[] badSize = (byte[])aac.Clone();
        badSize[0] = 0x7F;                                                   // first box claims ~2 GB
        Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(badSize); MediaMerger.Demux(s, s.Length, default); });
        var random = new byte[4096];
        new Random(7).NextBytes(random);
        Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(random); MediaMerger.Demux(s, s.Length, default); });
        byte[] webm = Fixture("video-only.webm");
        Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(webm[..(webm.Length / 2)]); MediaMerger.Demux(s, s.Length, default); });
    }

    [Fact]
    public void EncryptedSampleEntries_AreRejectedAsDrm()
    {
        byte[] video = Fixture("video-only.mp4");
        byte[] encrypted = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(video).Replace("avc1", "encv"));
        var ex = Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(encrypted); MediaMerger.Demux(s, s.Length, default); });
        Assert.Contains("DRM", ex.Message);
        byte[] webm = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Fixture("video-only.webm")).Replace("V_VP9", "V_XX9"));
        Assert.Throws<MediaContainerException>(() => { var s = new MemoryStream(webm); MediaMerger.Demux(s, s.Length, default); });
    }

    private static MediaMergePlan Plan(string baseUrl, long videoSize = 100, long audioSize = 50, string? identity = "id") => new()
    {
        Provider = "test", MediaId = "m1", OutputContainer = "mp4", RequireVideo = true,
        Tracks =
        [
            new MergeTrack("video", "v1", baseUrl + "/v?sig=1", videoSize, "mp4", "h264", identity is null ? null : identity + "-v"),
            new MergeTrack("audio", "a1", baseUrl + "/a?sig=1", audioSize, "m4a", "aac", identity is null ? null : identity + "-a")
        ]
    };

    [Fact]
    public void MergePlan_ContinuationRequiresByteIdentity_NotUrls()
    {
        var original = Plan("https://cdn.example.com");
        var refreshed = original with { Tracks = original.Tracks.Select(t => t with { Url = t.Url.Replace("sig=1", "sig=2") }).ToArray() };
        Assert.True(original.CanContinueWith(refreshed, out _));
        Assert.False(original.CanContinueWith(Plan("https://cdn.example.com", videoSize: 101), out _));
        Assert.False(Plan("https://cdn.example.com", identity: null).CanContinueWith(Plan("https://cdn.example.com", identity: null), out _));
        Assert.True(MediaMergePlan.TryParse(original.ToJson(), allowPrivate: false, out _, out _));
        Assert.False(MediaMergePlan.TryParse(Plan("http://127.0.0.1:9").ToJson(), allowPrivate: false, out _, out _));
        Assert.False(MediaMergePlan.TryParse((original with { OutputContainer = "m4a" }).ToJson(), false, out _, out _));
        Assert.False(MediaMergePlan.TryParse((original with { Tracks = [.. original.Tracks, .. original.Tracks, .. original.Tracks] }).ToJson(), false, out _, out _));
    }

    [Fact]
    public async Task MergeJob_DownloadsBothTracks_MergesAndPublishesOneValidatedFile()
    {
        await using var server = new FaultServer();
        server.Extra["/m/video.mp4"] = Fixture("video-only.mp4");
        server.Extra["/m/audio.m4a"] = AacFmp4();
        await server.StartAsync();
        using var h = new Harness();
        var plan = new MediaMergePlan
        {
            Provider = "test", MediaId = "m1", OutputContainer = "mp4", RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), server.Extra["/m/video.mp4"].Length, "mp4", "h264", "v-id"),
                new MergeTrack("audio", "a1", server.Url("/m/audio.m4a"), server.Extra["/m/audio.m4a"].Length, "m4a", "aac", "a-id")
            ]
        };
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson(), MediaProvider: "test", MediaId: "m1"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await h.WaitAsync(added.JobId, s => s.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        using (var fs = File.OpenRead(done.FinalPath!))
        {
            var tracks = MediaMerger.Demux(fs, fs.Length, default);
            Assert.Equal([TrackKind.Video, TrackKind.Audio], tracks.Select(t => t.Kind));
        }
        Assert.False(File.Exists(done.PartialPath + ".tracks"));
        Assert.Contains(server.RangeHeaders, r => r.StartsWith("/m/audio.m4a ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeJob_WhoseAudioStreamHasNoAudio_IsNeverPublished()
    {
        await using var server = new FaultServer();
        server.Extra["/m/video.mp4"] = Fixture("video-only.mp4");
        await server.StartAsync();
        using var h = new Harness();
        long size = server.Extra["/m/video.mp4"].Length;
        var plan = new MediaMergePlan
        {
            Provider = "test", MediaId = "m2", OutputContainer = "mp4", RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), size, "mp4", "h264"),
                new MergeTrack("audio", "a1", server.Url("/m/video.mp4") + "?as-audio", size, "m4a", "aac")
            ]
        };
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "silent.mp4", Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await h.WaitAsync(added.JobId, s => s.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.Equal(JobState.NeedsAttention, done.State);
        Assert.Equal("missing-audio", done.Reason);
        Assert.Null(done.FinalPath);
        Assert.True(File.Exists(done.PartialPath), "downloaded track data must be kept");
    }
}
