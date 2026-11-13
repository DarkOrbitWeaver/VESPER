using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Xunit;

namespace Vesper.Tests;

/// <summary>
/// Native remux coverage using the checked-in CC0 synthetic fixtures (real H.264/AAC/VP9/Opus bitstreams) plus
/// mutated copies for adversarial input. Every merge is re-read and compared sample-by-sample with its sources.
/// </summary>
public sealed class MediaMergeTests
{
    [Fact]
    public void MediaTimeRange_RequiresFullContinuousCoverage()
    {
        var range = new MediaTimeRange(0.5, 2.5);

        Assert.False(range.TrySelect([1, 1], out _, out _, out _, out _));
        Assert.False(new MediaTimeRange(1.5, 2.5).TrySelect([1, 1], new double?[] { 0, 2 }, out _, out _, out _, out _));
        Assert.True(new MediaTimeRange(1.1, 1.8).TrySelect([1, 1, 1],
            out int first, out int count, out double actualStart, out double actualEnd));
        Assert.Equal(1, first);
        Assert.Equal(1, count);
        Assert.Equal(1, actualStart);
        Assert.Equal(2, actualEnd);
    }

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
    public void MatroskaMerge_RetainsTitleTagsAndChapters()
    {
        var videoStream = new MemoryStream(Fixture("video-only.mp4"));
        var audioStream = new MemoryStream(Fixture("muxed.webm"));
        DemuxedTrack video = Track(videoStream, TrackKind.Video), audio = Track(audioStream, TrackKind.Audio);
        double duration = Math.Min(video.DurationSeconds, audio.DurationSeconds);
        Assert.True(duration > 0.1, $"fixture duration was {duration:0.###} seconds");
        TimeSpan mediaDuration = TimeSpan.FromSeconds(duration);
        TimeSpan end = TimeSpan.FromSeconds(duration * 0.8);
        TimeSpan middle = TimeSpan.FromTicks(end.Ticks / 2);
        var metadata = new MediaMergeMetadata
        {
            Title = "A short film",
            Uploader = "VESPER test channel",
            Description = "A native chapter metadata check.",
            Duration = mediaDuration,
            Chapters =
            [
                new MediaMergeChapter("Opening", TimeSpan.Zero, middle),
                new MediaMergeChapter("Ending", middle, end)
            ]
        };
        var output = new MemoryStream();

        MergeResult result = MediaMerger.Merge([(video, videoStream), (audio, audioStream)], "mkv", output, default, metadata);
        MediaMerger.Validate(output, output.Length, [video, audio], default, metadata);
        MediaMergeMetadata stored = WebmDemuxer.ReadMetadata(output, output.Length);

        Assert.Equal("mkv", result.Container);
        Assert.Equal(metadata.Title, stored.Title);
        Assert.Equal(metadata.Uploader, stored.Uploader);
        Assert.Equal(metadata.Description, stored.Description);
        Assert.Equal(metadata.Chapters, stored.Chapters);
    }

    [Fact]
    public void Mp4Merge_Retains3GppMetadataAndChapterMarkers()
    {
        var videoStream = new MemoryStream(Fixture("video-only.mp4"));
        var audioStream = new MemoryStream(AacFmp4());
        DemuxedTrack video = Track(videoStream, TrackKind.Video), audio = Track(audioStream, TrackKind.Audio);
        double duration = Math.Min(video.DurationSeconds, audio.DurationSeconds);
        var metadata = new MediaMergeMetadata
        {
            Title = "MP4 chapter sample",
            Uploader = "VESPER uploader",
            Description = "Stored as 3GPP metadata.",
            Duration = TimeSpan.FromSeconds(duration),
            Chapters =
            [
                new MediaMergeChapter("Opening", TimeSpan.Zero),
                new MediaMergeChapter("Next", TimeSpan.FromSeconds(duration / 2))
            ]
        };
        var output = new MemoryStream();

        MergeResult result = MediaMerger.Merge([(video, videoStream), (audio, audioStream)], "mp4", output, default, metadata);
        MediaMerger.Validate(output, output.Length, [video, audio], default, result.Metadata);
        MediaMergeMetadata stored = Mp4Demuxer.ReadMetadata(output, output.Length);

        Assert.Equal("MP4 chapter sample", stored.Title);
        Assert.Equal("VESPER uploader", stored.Uploader);
        Assert.Equal("Stored as 3GPP metadata.", stored.Description);
        Assert.Equal(result.Metadata!.Chapters, stored.Chapters);
        Assert.Equal(2, MediaMerger.Demux(output, output.Length, default).Count);
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
        Assert.True(original.CanContinueWith(original with
        {
            Metadata = new MediaMergeMetadata { Title = "Updated title" }
        }, out _));
        Assert.False(original.CanContinueWith(Plan("https://cdn.example.com", videoSize: 101), out _));
        Assert.False(Plan("https://cdn.example.com", identity: null).CanContinueWith(Plan("https://cdn.example.com", identity: null), out _));
        Assert.True(MediaMergePlan.TryParse(original.ToJson(), allowPrivate: false, out _, out _));
        Assert.False(MediaMergePlan.TryParse(Plan("http://127.0.0.1:9").ToJson(), allowPrivate: false, out _, out _));
        Assert.False(MediaMergePlan.TryParse((original with { OutputContainer = "m4a" }).ToJson(), false, out _, out _));
        Assert.False(MediaMergePlan.TryParse((original with
        {
            Metadata = new MediaMergeMetadata
            {
                Chapters = [new MediaMergeChapter("Bad chapter", TimeSpan.FromSeconds(-1))]
            }
        }).ToJson(), false, out _, out _));
        Assert.False(MediaMergePlan.TryParse((original with { Tracks = [.. original.Tracks, .. original.Tracks, .. original.Tracks] }).ToJson(), false, out _, out _));
    }

    [Fact]
    public void MediaTransferBridge_PreparesSeparateHlsRenditionsAsOneNativeMerge()
    {
        var video = new MediaRepresentation
        {
            Id = "hls-video-720", Url = new Uri("https://cdn.example.com/video.m3u8"), Protocol = StreamProtocol.Hls,
            Content = StreamContent.VideoOnly, Container = "mp4", VideoCodec = "h264", Height = 720
        };
        var audio = new MediaRepresentation
        {
            Id = "hls-audio-main", Url = new Uri("https://cdn.example.com/audio.m3u8"), Protocol = StreamProtocol.Hls,
            Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac", IsDefaultAudio = true,
            AudioTrackId = "main", AudioLanguage = "en"
        };
        var commentary = audio with { Id = "hls-audio-commentary", AudioTrackId = "commentary", IsDefaultAudio = false };
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.com/watch/one"), Provider = "hls", MediaId = "one", Title = "One",
            Description = "A test description.", Uploader = "Test channel", Duration = TimeSpan.FromSeconds(6),
            Chapters = [new Chapter("Start", TimeSpan.Zero, TimeSpan.FromSeconds(3)),
                new Chapter("Finish", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6))],
            Representations = [video, audio, commentary],
            AudioTracks = [new AudioTrack("main", "en", "Main", true), new AudioTrack("commentary", "en", "Commentary")]
        };

        var prepared = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);

        Assert.Null(prepared.Error);
        Assert.NotNull(prepared.Choice);
        Assert.Equal(JobKind.Merge, prepared.Choice!.Kind);
        Assert.Equal(".mp4", Path.GetExtension(prepared.Choice.FileName));
        Assert.True(MediaMergePlan.TryParse(prepared.Choice.MediaPlanJson, allowPrivate: false, out MediaMergePlan plan, out string error), error);
        Assert.Equal(2, plan.Version);
        Assert.Collection(plan.Tracks,
            track => Assert.Equal("hls", track.Source?.Protocol),
            track => Assert.Equal("hls", track.Source?.Protocol));
        Assert.Equal("One", plan.Metadata?.Title);
        Assert.Equal("Test channel", plan.Metadata?.Uploader);
        Assert.Equal("A test description.", plan.Metadata?.Description);
        Assert.Equal(2, plan.Metadata?.Chapters.Count);

        var selectedTrack = MediaTransferBridge.Prepare(item,
            FormatPreferences.BestQuality with { AudioTrackId = "commentary" });
        Assert.Null(selectedTrack.Error);
        Assert.True(MediaMergePlan.TryParse(selectedTrack.Choice?.MediaPlanJson, false, out MediaMergePlan selectedPlan, out error), error);
        Assert.Equal("hls-audio-commentary", selectedPlan.Tracks.Single(track => track.Role == "audio").FormatId);

        var clipped = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality,
            clipStartSeconds: 1.1, clipEndSeconds: 2.2);
        Assert.Null(clipped.Error);
        Assert.True(MediaMergePlan.TryParse(clipped.Choice?.MediaPlanJson, false, out MediaMergePlan clippedPlan, out error), error);
        Assert.Equal(new MediaTimeRange(1.1, 2.2), clippedPlan.ClipRange);
        Assert.Empty(clippedPlan.Metadata!.Chapters);
        Assert.True(MediaTransferBridge.SupportsSegmentClip(clipped.Choice!));
    }

    [Fact]
    public void MediaTransferBridge_QueuesDirectSubtitlesAsSeparateHttpFiles()
    {
        var stream = new MediaRepresentation
        {
            Id = "muxed", Url = new Uri("https://cdn.example.com/video.mp4"),
            Content = StreamContent.VideoAndAudio, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac"
        };
        var subtitle = new SubtitleTrack(new Uri("https://cdn.example.com/captions/en.vtt"), "en", "English", "vtt");
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.com/watch/one"), Provider = "test", MediaId = "one",
            Title = "One", Representations = [stream], Subtitles = [subtitle]
        };
        FormatPreferences preferences = FormatPreferences.BestQuality with { SubtitleLanguages = ["en"] };

        var prepared = MediaTransferBridge.Prepare(item, preferences);
        var exact = MediaTransferBridge.Prepare(item, preferences, stream);
        Assert.Null(prepared.Error);
        Assert.Null(exact.Error);
        MediaDownloadChoice[] jobs = [.. MediaTransferBridge.ExpandSubtitleChoices([prepared.Choice!])];

        Assert.Single(exact.Choice!.SubtitleTracks!);
        Assert.Equal(2, jobs.Length);
        Assert.Equal(JobKind.Http, jobs[1].Kind);
        Assert.Equal(subtitle.Url.AbsoluteUri, jobs[1].Url);
        Assert.Equal("One.en.vtt", jobs[1].FileName);
        Assert.Equal("Subtitle · en", jobs[1].Quality);
        Assert.Null(jobs[1].Provider);
        Assert.Null(jobs[1].MediaPlanJson);
        Assert.Null(jobs[1].SubtitleTracks);

        var segmented = MediaTransferBridge.Prepare(item with
        {
            Subtitles = [subtitle with { Format = "hls-webvtt" }]
        }, preferences);
        Assert.Null(segmented.Error);
        MediaDownloadChoice segmentedJob = Assert.Single(MediaTransferBridge.ExpandSubtitleChoices([segmented.Choice!]).Skip(1));
        Assert.Equal(JobKind.Hls, segmentedJob.Kind);
        Assert.Equal("One.en.vtt", segmentedJob.FileName);
        Assert.True(HlsWebVttPlan.TryParse(segmentedJob.MediaPlanJson, out _));

        var unsupported = MediaTransferBridge.Prepare(item with
        {
            Subtitles = [subtitle with { Format = "imsc1" }]
        }, preferences);
        Assert.Null(unsupported.Choice);
        Assert.Contains("transport or format", unsupported.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaTransferBridge_PreparesSeparateDashRenditionsAsOneNativeMerge()
    {
        var video = new MediaRepresentation
        {
            Id = "dash-video-720", Url = new Uri("https://cdn.example.com/manifest.mpd"), Protocol = StreamProtocol.Dash,
            Content = StreamContent.VideoOnly, Container = "mp4", VideoCodec = "h264", Height = 720,
            Segments = DashSegments("video")
        };
        var audio = new MediaRepresentation
        {
            Id = "dash-audio-main", Url = new Uri("https://cdn.example.com/manifest.mpd"), Protocol = StreamProtocol.Dash,
            Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac", Segments = DashSegments("audio")
        };
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.com/watch/one"), Provider = "dash", MediaId = "one", Title = "One",
            Duration = TimeSpan.FromSeconds(6), Representations = [video, audio]
        };

        var prepared = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);

        Assert.Null(prepared.Error);
        Assert.Equal(JobKind.Merge, prepared.Choice?.Kind);
        Assert.True(MediaMergePlan.TryParse(prepared.Choice?.MediaPlanJson, allowPrivate: false, out MediaMergePlan plan, out string error), error);
        Assert.Collection(plan.Tracks,
            track => { Assert.Equal("dash", track.Source?.Protocol); Assert.True(track.Source?.Dash?.RequireVideo); },
            track => { Assert.Equal("dash", track.Source?.Protocol); Assert.True(track.Source?.Dash?.RequireAudio); });

        var clipped = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality,
            clipStartSeconds: 1.1, clipEndSeconds: 2.2);
        Assert.Null(clipped.Error);
        Assert.True(MediaMergePlan.TryParse(clipped.Choice?.MediaPlanJson, false, out MediaMergePlan clippedPlan, out error), error);
        Assert.Equal(new MediaTimeRange(1.1, 2.2), clippedPlan.ClipRange);
        Assert.All(clippedPlan.Tracks, track => Assert.All(track.Source!.Dash!.Fragments,
            fragment => Assert.Equal(1d, fragment.DurationSeconds!.Value)));
        Assert.Equal(new double?[] { 0, 1, 2 }, clippedPlan.Tracks[0].Source!.Dash!.Fragments.Select(fragment => fragment.StartSeconds));
        Assert.True(MediaTransferBridge.SupportsSegmentClip(clipped.Choice!));
    }

    [Fact]
    public void HlsTransportStreams_OnlyOfferTheH264AacMatroskaPath()
    {
        var video = new MediaRepresentation
        {
            Id = "v", Url = new Uri("https://cdn.example.com/video.m3u8"), Protocol = StreamProtocol.Hls,
            Content = StreamContent.VideoOnly, Container = "ts", VideoCodec = "h264"
        };
        var audio = new MediaRepresentation
        {
            Id = "a", Url = new Uri("https://cdn.example.com/audio.m3u8"), Protocol = StreamProtocol.Hls,
            Content = StreamContent.AudioOnly, Container = "ts", AudioCodec = "aac"
        };
        Assert.Equal("mkv", MergeCapability.MergeContainer(video, audio));
        Assert.Null(MergeCapability.MergeContainer(video, audio with { AudioCodec = "opus" }));
        Assert.Null(MergeCapability.MergeContainer(video with { Protocol = StreamProtocol.Http, FileSize = 100 }, audio));
    }

    private static SegmentPlan DashSegments(string id) => new()
    {
        InitializationUrl = new Uri($"https://cdn.example.com/{id}-init.mp4"), IsFragmentedMp4 = true,
        Segments = Enumerable.Range(1, 3).Select(i => new MediaSegment(
            new Uri($"https://cdn.example.com/{id}-{i}.m4s"), 1, StartSeconds: i - 1)).ToArray()
    };

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
            Metadata = new MediaMergeMetadata
            {
                Title = "Merged sample", Uploader = "VESPER", Description = "Saved through the merge job.",
                Chapters = [new MediaMergeChapter("Start", TimeSpan.Zero)]
            },
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
            MediaMergeMetadata metadata = Mp4Demuxer.ReadMetadata(fs, fs.Length);
            Assert.Equal("Merged sample", metadata.Title);
            Assert.Equal("VESPER", metadata.Uploader);
            Assert.Equal("Saved through the merge job.", metadata.Description);
            Assert.Equal("Start", Assert.Single(metadata.Chapters).Title);
        }
        Assert.False(File.Exists(done.PartialPath + ".tracks"));
        Assert.Contains(server.RangeHeaders, r => r.StartsWith("/m/audio.m4a ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeJob_EmbedsMetadataAndChaptersInMatroska()
    {
        await using var server = new FaultServer();
        server.Extra["/m/video.mp4"] = Fixture("video-only.mp4");
        server.Extra["/m/audio.webm"] = Fixture("muxed.webm");
        await server.StartAsync();
        using var h = new Harness();
        var plan = new MediaMergePlan
        {
            Provider = "test", MediaId = "chaptered", OutputContainer = "mkv", RequireVideo = true,
            Metadata = new MediaMergeMetadata
            {
                Title = "Chaptered sample", Uploader = "VESPER", Description = "Saved through a resumable merge job.",
                Chapters = [new MediaMergeChapter("Beginning", TimeSpan.Zero)]
            },
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), server.Extra["/m/video.mp4"].Length, "mp4", "h264", "v-id"),
                new MergeTrack("audio", "a1", server.Url("/m/audio.webm"), server.Extra["/m/audio.webm"].Length, "webm", "opus", "a-id")
            ]
        };
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "chaptered.mkv", Kind: JobKind.Merge,
            MediaPlanJson: plan.ToJson(), MediaProvider: "test", MediaId: "chaptered"));
        Assert.True(added.Ok, added.Message);
        AddResult duplicate = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "chaptered.mkv", Kind: JobKind.Merge,
            MediaPlanJson: (plan with { Metadata = plan.Metadata with { Title = "Updated title" } }).ToJson(),
            MediaProvider: "test", MediaId: "chaptered"));
        Assert.Equal(AddStatus.Duplicate, duplicate.Status);

        JobSnapshot done = await h.WaitAsync(added.JobId, snapshot => snapshot.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.Equal(JobState.Completed, done.State);
        using var output = File.OpenRead(done.FinalPath!);
        MediaMergeMetadata stored = WebmDemuxer.ReadMetadata(output, output.Length);
        Assert.Equal("Chaptered sample", stored.Title);
        Assert.Equal("VESPER", stored.Uploader);
        Assert.Equal("Saved through a resumable merge job.", stored.Description);
        Assert.Equal([new MediaMergeChapter("Beginning", TimeSpan.Zero)], stored.Chapters);
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
