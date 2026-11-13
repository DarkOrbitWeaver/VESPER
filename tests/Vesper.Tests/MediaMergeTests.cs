using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Xunit;
using Xunit.Abstractions;

namespace Vesper.Tests;

/// <summary>
/// Native remux coverage using the checked-in CC0 synthetic fixtures (real H.264/AAC/VP9/Opus bitstreams) plus
/// mutated copies for adversarial input. Every merge is re-read and compared sample-by-sample with its sources.
/// </summary>
[Collection("Process handle checks")]
public sealed class MediaMergeTests
{
    private const int BenchmarkDurationSeconds = 600;
    private readonly ITestOutputHelper _output;

    public MediaMergeTests(ITestOutputHelper output) => _output = output;

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
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    private static DemuxedTrack Repeat(DemuxedTrack source, int count)
    {
        var repeated = new DemuxedTrack
        {
            Kind = source.Kind,
            Codec = source.Codec,
            SourceContainer = source.SourceContainer,
            Timescale = source.Timescale,
            Mp4SampleEntry = source.Mp4SampleEntry,
            CodecPrivate = source.CodecPrivate,
            MatroskaCodecId = source.MatroskaCodecId,
            MatroskaSettings = source.MatroskaSettings,
            Width = source.Width,
            Height = source.Height,
            Channels = source.Channels,
            SampleRate = source.SampleRate,
            BitDepth = source.BitDepth,
            CodecDelayNs = source.CodecDelayNs,
            SeekPreRollNs = source.SeekPreRollNs,
            DefaultDurationNs = source.DefaultDurationNs,
            Language = source.Language,
            EditMediaTime = source.EditMediaTime,
            Samples = new List<TrackSample>(checked(source.Samples.Count * count))
        };
        long duration = source.DurationTicks;
        for (int repeat = 0; repeat < count; repeat++)
        {
            long offset = checked(duration * repeat);
            foreach (TrackSample sample in source.Samples)
            {
                TrackSample copy = sample;
                copy.Dts = checked(copy.Dts + offset);
                repeated.Samples.Add(copy);
            }
        }
        return repeated;
    }

    private static int RepeatsFor(DemuxedTrack track) =>
        Math.Max(1, (int)Math.Ceiling(BenchmarkDurationSeconds / track.DurationSeconds));

    [Fact]
    [Trait("Category", "MediaBenchmark")]
    public async Task Measure_large_local_remux_paths()
    {
        // Repeated checked-in samples create a long remux workload without network or transcoding.
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string outputFolder = Path.Combine(tempRoot, "vesper-media-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputFolder);

        try
        {
            var disk = new DriveInfo(Path.GetPathRoot(outputFolder)!);
            _output.WriteLine($"environment: os={RuntimeInformation.OSDescription}; runtime={Environment.Version}; processors={Environment.ProcessorCount}; available-memory={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MiB; disk-free={disk.AvailableFreeSpace / (1024 * 1024)} MiB");

            string fixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
            string videoPath = Path.Combine(fixtureFolder, "video-only.mp4");
            string webmPath = Path.Combine(fixtureFolder, "muxed.webm");
            string audioPath = Path.Combine(outputFolder, "audio.mp4");
            File.WriteAllBytes(audioPath, AacFmp4());

            using var mp4VideoSource = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            DemuxedTrack mp4Video = Track(mp4VideoSource, TrackKind.Video);
            using var mp4AudioSource = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            DemuxedTrack mp4Audio = Track(mp4AudioSource, TrackKind.Audio);
            var mp4Inputs = new List<(DemuxedTrack Track, Stream Source)>
            {
                (Repeat(mp4Video, RepeatsFor(mp4Video)), mp4VideoSource),
                (Repeat(mp4Audio, RepeatsFor(mp4Audio)), mp4AudioSource)
            };

            using var webmSource = new FileStream(webmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            List<DemuxedTrack> webmTracks = MediaMerger.Demux(webmSource, webmSource.Length, default);
            DemuxedTrack webmVideo = webmTracks.Single(track => track.Kind == TrackKind.Video);
            DemuxedTrack webmAudio = webmTracks.Single(track => track.Kind == TrackKind.Audio);
            var webmInputs = new List<(DemuxedTrack Track, Stream Source)>
            {
                (Repeat(webmVideo, RepeatsFor(webmVideo)), webmSource),
                (Repeat(webmAudio, RepeatsFor(webmAudio)), webmSource)
            };

            using var mkvAudioSource = new FileStream(webmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            DemuxedTrack mkvAudio = Track(mkvAudioSource, TrackKind.Audio);
            var mkvInputs = new List<(DemuxedTrack Track, Stream Source)>
            {
                (Repeat(mp4Video, RepeatsFor(mp4Video)), mp4VideoSource),
                (Repeat(mkvAudio, RepeatsFor(mkvAudio)), mkvAudioSource)
            };

            await MeasureRemux("mp4", mp4Inputs, outputFolder);
            await MeasureRemux("webm", webmInputs, outputFolder);
            await MeasureRemux("mkv", mkvInputs, outputFolder);
        }
        finally
        {
            string fullOutputFolder = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (!fullOutputFolder.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullOutputFolder).StartsWith("vesper-media-benchmark-", StringComparison.Ordinal))
                throw new IOException($"Refusing to remove an unexpected benchmark directory: {fullOutputFolder}");
            Directory.Delete(fullOutputFolder, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "SegmentedMergeBenchmark")]
    public async Task Measure_local_hls_and_dash_rendition_merges()
    {
        foreach (string protocol in new[] { "hls", "dash" })
        {
            var runs = new List<SegmentedMergeSample>();
            for (int run = 0; run < 4; run++)
            {
                SegmentedMergeSample sample = await RunSegmentedMerge(protocol);
                _output.WriteLine($"segmented: protocol={protocol}; run={run}; elapsed-ms={sample.ElapsedMs:0.###}; media-bytes={sample.MediaBytes}; entity-bytes={sample.EntityBytes}; output-bytes={sample.OutputBytes}; managed-allocated-MiB={sample.AllocatedMiB:0.###}; validation-ms={sample.ValidationMs:0.###}; tracks={sample.TrackCount}; samples={sample.SampleCount}; requests={sample.Requests}");
                if (run > 0) runs.Add(sample);
            }

            _output.WriteLine($"segmented-median: protocol={protocol}; samples=3 after one warm-up; elapsed-ms={Median(runs.Select(run => run.ElapsedMs).ToArray()):0.###}; media-bytes={Median(runs.Select(run => (double)run.MediaBytes).ToArray()):0}; entity-bytes={Median(runs.Select(run => (double)run.EntityBytes).ToArray()):0}; output-bytes={Median(runs.Select(run => (double)run.OutputBytes).ToArray()):0}; managed-allocated-MiB={Median(runs.Select(run => run.AllocatedMiB).ToArray()):0.###}; validation-ms={Median(runs.Select(run => run.ValidationMs).ToArray()):0.###}; requests={Median(runs.Select(run => (double)run.Requests).ToArray()):0}");
        }
    }

    private sealed record SegmentedMergeSample(double ElapsedMs, long MediaBytes, long EntityBytes, long OutputBytes,
        double AllocatedMiB, double ValidationMs, int TrackCount, int SampleCount, int Requests);

    private async Task<SegmentedMergeSample> RunSegmentedMerge(string protocol)
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        MediaMergePlan plan = CreateSegmentedPlan(server, protocol);
        using var harness = new Harness();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        AddResult added = harness.Engine.Add(new AddRequest(plan.Tracks[0].Url,
            FileName: $"{protocol}-benchmark.mp4", Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed or JobState.NeedsLink);
        watch.Stop();
        string requestedPaths = string.Join(", ", server.Requests.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key}={entry.Value.Count}"));
        Assert.True(done.State == JobState.Completed,
            $"{done.State} {done.Reason}: {done.Message}; requests: {requestedPaths}");

        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long mediaBytes = ServedMediaBytes(server);
        long entityBytes = ServedEntityBytes(server);
        int requests = server.Requests.Values.Sum(queue => queue.Count);
        long outputBytes = new FileInfo(done.FinalPath!).Length;
        var validationWatch = Stopwatch.StartNew();
        (int trackCount, int sampleCount) = ValidateSegmentedOutput(done.FinalPath!);
        validationWatch.Stop();

        Assert.Equal(protocol == "hls" ? 10 : 8, requests);
        Assert.True(mediaBytes > 0);
        Assert.True(entityBytes >= mediaBytes);
        return new SegmentedMergeSample(watch.Elapsed.TotalMilliseconds, mediaBytes, entityBytes, outputBytes,
            allocatedBytes / (1024.0 * 1024.0), validationWatch.Elapsed.TotalMilliseconds, trackCount, sampleCount, requests);
    }

    private static MediaMergePlan CreateSegmentedPlan(FaultServer server, string protocol)
    {
        const string initName = "av-init.mp4";
        string[] fragmentNames = ["av-1.m4s", "av-2.m4s", "av-3.m4s"];
        byte[] initialization = Fixture(initName);
        var fragments = fragmentNames.Select(Fixture).ToArray();
        var tracks = new List<MergeTrack>(2);

        foreach (string role in new[] { "video", "audio" })
        {
            string root = protocol == "hls" ? $"/hls/segmented-benchmark/{role}" : $"/dash/segmented-benchmark/{role}";
            string url;
            MergeMediaSource source;
            if (protocol == "hls")
            {
                string playlistPath = root + "/index.m3u8";
                string playlist = $"#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MAP:URI=\"{initName}\"\n"
                    + string.Concat(fragmentNames.Select(name => $"#EXTINF:1,\n{name}\n")) + "#EXT-X-ENDLIST\n";
                server.HlsPlaylists[playlistPath] = playlist;
                server.HlsFiles[root + "/" + initName] = initialization;
                for (int i = 0; i < fragmentNames.Length; i++)
                    server.HlsFiles[root + "/" + fragmentNames[i]] = fragments[i];
                url = server.Url(playlistPath);
                source = new MergeMediaSource("hls", PlaylistUrl: url);
            }
            else
            {
                string initPath = root + "/" + initName;
                server.Extra[initPath] = initialization;
                var dashPlan = new DashDownloadPlan
                {
                    RepresentationId = role + "-fixture",
                    Initialization = new DashFragment(server.Url(initPath)),
                    Fragments = fragmentNames.Select((name, index) =>
                        new DashFragment(server.Url(root + "/" + name), DurationSeconds: 1, StartSeconds: index)).ToArray(),
                    RequireAudio = role == "audio",
                    RequireVideo = role == "video"
                };
                for (int i = 0; i < fragmentNames.Length; i++)
                    server.Extra[root + "/" + fragmentNames[i]] = fragments[i];
                url = server.Url("/manifest.mpd");
                source = new MergeMediaSource("dash", Dash: dashPlan);
            }

            tracks.Add(new MergeTrack(role, role + "-fixture", url, 0,
                role == "video" ? "mp4" : "m4a", role == "video" ? "h264" : "aac", Source: source));
        }

        return new MediaMergePlan
        {
            Version = 2,
            OutputContainer = "mp4",
            RequireAudio = true,
            RequireVideo = true,
            Tracks = tracks
        };
    }

    private static long ServedMediaBytes(FaultServer server)
    {
        long total = 0;
        foreach (var entry in server.HlsFiles) total = checked(total + (long)entry.Value.Length * server.Hits(entry.Key));
        foreach (var entry in server.Extra) total = checked(total + (long)entry.Value.Length * server.Hits(entry.Key));
        return total;
    }

    private static long ServedEntityBytes(FaultServer server)
    {
        // The fixture server sends complete, uncompressed bodies, so path hit counts give exact entity bytes.
        long total = ServedMediaBytes(server);
        foreach (var entry in server.HlsPlaylists)
            total = checked(total + (long)Encoding.UTF8.GetByteCount(entry.Value) * server.Hits(entry.Key));
        return total;
    }

    private static (int TrackCount, int SampleCount) ValidateSegmentedOutput(string path)
    {
        byte[] sourceBytes = [.. Fixture("av-init.mp4"), .. Fixture("av-1.m4s"), .. Fixture("av-2.m4s"), .. Fixture("av-3.m4s")];
        using var source = new MemoryStream(sourceBytes);
        List<DemuxedTrack> expected = MediaMerger.Demux(source, source.Length, default);
        using var output = File.OpenRead(path);
        MediaMerger.Validate(output, output.Length, expected, default);
        List<DemuxedTrack> actual = MediaMerger.Demux(output, output.Length, default);
        foreach (TrackKind kind in new[] { TrackKind.Video, TrackKind.Audio })
            AssertSamePayloads(expected.Single(track => track.Kind == kind), source,
                actual.Single(track => track.Kind == kind), output);
        return (actual.Count, actual.Sum(track => track.Samples.Count));
    }

    private async Task MeasureRemux(string container, IReadOnlyList<(DemuxedTrack Track, Stream Source)> inputs,
        string outputFolder)
    {
        var mergeTimes = new List<double>();
        var validationTimes = new List<double>();
        var mergeAllocations = new List<double>();
        var validationAllocations = new List<double>();
        var peakGrowth = new List<double>();
        long outputBytes = 0;
        double outputDuration = 0;

        for (int run = 0; run < 4; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var mergeWatch = Stopwatch.StartNew();
            string outputPath = Path.Combine(outputFolder, $"{container}-{run}.media");
            long mergeAllocatedBytes = 0;
            long peak = await MeasurePeakWorkingSet(() =>
            {
                using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                    FileOptions.SequentialScan);
                MergeResult result = MediaMerger.Merge(inputs, container, output, default);
                output.Flush(flushToDisk: true);
                outputBytes = result.Bytes;
                outputDuration = result.DurationSeconds;
                mergeWatch.Stop();
                mergeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                long validationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
                var validationWatch = Stopwatch.StartNew();
                MediaMerger.Validate(output, output.Length, inputs.Select(input => input.Track).ToArray(), default);
                validationWatch.Stop();
                validationAllocations.Add((GC.GetTotalAllocatedBytes(precise: true) - validationAllocatedBytes) / (1024.0 * 1024.0));
                validationTimes.Add(validationWatch.Elapsed.TotalMilliseconds);
            });
            mergeTimes.Add(mergeWatch.Elapsed.TotalMilliseconds);
            mergeAllocations.Add(mergeAllocatedBytes / (1024.0 * 1024.0));
            peakGrowth.Add(peak / (1024.0 * 1024.0));
            File.Delete(outputPath);
        }

        double medianMerge = Median(mergeTimes.Skip(1).ToArray());
        double medianValidation = Median(validationTimes.Skip(1).ToArray());
        double medianMergeAllocated = Median(mergeAllocations.Skip(1).ToArray());
        double medianValidationAllocated = Median(validationAllocations.Skip(1).ToArray());
        double peakWorkingSetGrowthMiB = peakGrowth.Max();
        double mibPerSecond = outputBytes / (1024.0 * 1024.0) / (medianMerge / 1000.0);
        _output.WriteLine($"remux: container={container}; duration={outputDuration:0.###}s; samples={inputs.Sum(input => input.Track.Samples.Count)}; bytes={outputBytes}; merge-p50-ms={medianMerge:0.###}; validate-p50-ms={medianValidation:0.###}; merge-MiB-s={mibPerSecond:0.###}; merge-allocated-MiB={medianMergeAllocated:0.###}; validate-allocated-MiB={medianValidationAllocated:0.###}; peak-process-working-set-growth-MiB={peakWorkingSetGrowthMiB:0.###}");
    }

    private static async Task<long> MeasurePeakWorkingSet(Action operation)
    {
        using var done = new ManualResetEventSlim();
        using Process initial = Process.GetCurrentProcess();
        initial.Refresh();
        long baseline = initial.WorkingSet64;
        long peak = baseline;
        Task sampler = Task.Run(() =>
        {
            using Process process = Process.GetCurrentProcess();
            while (!done.Wait(10))
            {
                process.Refresh();
                long current = process.WorkingSet64;
                long observed;
                do
                {
                    observed = Interlocked.Read(ref peak);
                    if (current <= observed) break;
                }
                while (Interlocked.CompareExchange(ref peak, current, observed) != observed);
            }
            process.Refresh();
            long final = process.WorkingSet64;
            if (final > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, final);
        });

        try
        {
            operation();
        }
        finally
        {
            done.Set();
            await sampler;
        }
        return Math.Max(0, peak - baseline);
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

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
            Artist = "Fixture Artist",
            Album = "Fixture Album",
            AlbumArtist = "Fixture Album Artist",
            TrackNumber = 2,
            DiscNumber = 1,
            ReleaseDate = "2024-03-12",
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
        Assert.Equal(metadata.Artist, stored.Artist);
        Assert.Equal(metadata.Album, stored.Album);
        Assert.Equal(metadata.AlbumArtist, stored.AlbumArtist);
        Assert.Equal(metadata.TrackNumber, stored.TrackNumber);
        Assert.Equal(metadata.DiscNumber, stored.DiscNumber);
        Assert.Equal(metadata.ReleaseDate, stored.ReleaseDate);
        Assert.Equal(metadata.Chapters, stored.Chapters);
    }

    [Fact]
    public void ThumbnailValidation_RequiresBoundedWellFormedImage()
    {
        byte[] png = TinyPng();
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(png));

        Assert.Equal("image/png", thumbnail.MediaType);
        Assert.Equal("cover.png", thumbnail.FileName);
        Assert.Equal(Payloads.Sha256(png), thumbnail.Sha256);
        Assert.Null(MediaThumbnail.TryCreate(png[..^1]));
        Assert.Null(MediaThumbnail.TryCreate(Payloads.Make(MediaThumbnail.MaxBytes + 1, 12)));
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
    public void M4aMerge_EmbedsMusicTagsAndValidatedCoverWithoutChangingAudioSamples()
    {
        var audioStream = new MemoryStream(AacFmp4());
        DemuxedTrack audio = Track(audioStream, TrackKind.Audio);
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One",
            Artist = "Fixture Band",
            Album = "Fixture Album",
            AlbumArtist = "Fixture Band",
            TrackNumber = 2,
            DiscNumber = 1,
            ReleaseDate = "2024-03-12"
        };
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));
        var output = new MemoryStream();

        MergeResult result = MediaMerger.Merge([(audio, audioStream)], "m4a", output, default, metadata, thumbnail);
        MediaMerger.Validate(output, output.Length, [audio], default, result.Metadata, result.ThumbnailSha256);
        MediaMergeMetadata stored = Mp4Demuxer.ReadMetadata(output, output.Length);
        MediaThumbnail storedCover = Assert.IsType<MediaThumbnail>(Mp4Demuxer.ReadThumbnail(output, output.Length));
        DemuxedTrack muxedAudio = Assert.Single(MediaMerger.Demux(output, output.Length, default));

        Assert.Equal(metadata.Title, stored.Title);
        Assert.Equal(metadata.Artist, stored.Artist);
        Assert.Equal(metadata.Album, stored.Album);
        Assert.Equal(metadata.AlbumArtist, stored.AlbumArtist);
        Assert.Equal(metadata.TrackNumber, stored.TrackNumber);
        Assert.Equal(metadata.DiscNumber, stored.DiscNumber);
        Assert.Equal(metadata.ReleaseDate, stored.ReleaseDate);
        Assert.Equal(thumbnail.Sha256, storedCover.Sha256);
        AssertSamePayloads(audio, audioStream, muxedAudio, output);
        Assert.True(output.ToArray().AsSpan().IndexOf(new byte[] { 0xA9, (byte)'n', (byte)'a', (byte)'m' }) >= 0);

        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var qaFile = File.Create(fullPath);
            output.Position = 0;
            output.CopyTo(qaFile);
        }
    }

    [Fact]
    public void WebmAudioMergeEmbedsMusicTagsAndCoverWithoutChangingAudioSamples()
    {
        var source = new MemoryStream(Fixture("muxed.webm"));
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(source, source.Length, default), track => track.Kind == TrackKind.Audio);
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));
        var output = new MemoryStream();

        MergeResult result = MediaMerger.Merge([(audio, source)], "webm", output, default, metadata, thumbnail);
        MediaMerger.Validate(output, output.Length, [audio], default, result.Metadata, result.ThumbnailSha256);
        MediaMergeMetadata stored = WebmDemuxer.ReadMetadata(output, output.Length);
        MediaThumbnail storedCover = Assert.IsType<MediaThumbnail>(WebmDemuxer.ReadThumbnail(output, output.Length));
        DemuxedTrack muxedAudio = Assert.Single(MediaMerger.Demux(output, output.Length, default));

        Assert.Equal("webm", result.Container);
        Assert.Equal(metadata.Title, stored.Title);
        Assert.Equal(metadata.Artist, stored.Artist);
        Assert.Equal(metadata.Album, stored.Album);
        Assert.Equal(metadata.AlbumArtist, stored.AlbumArtist);
        Assert.Equal(metadata.TrackNumber, stored.TrackNumber);
        Assert.Equal(metadata.DiscNumber, stored.DiscNumber);
        Assert.Equal(metadata.ReleaseDate, stored.ReleaseDate);
        Assert.Equal(thumbnail.Sha256, storedCover.Sha256);
        AssertSamePayloads(audio, source, muxedAudio, output);

        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_WEBM_MUSIC");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var qaFile = File.Create(fullPath);
            output.Position = 0;
            output.CopyTo(qaFile);
        }
    }

    [Fact]
    public void M4aMerge_InvalidOrMissingCoverDoesNotPreventAudioOutput()
    {
        var audioStream = new MemoryStream(AacFmp4());
        DemuxedTrack audio = Track(audioStream, TrackKind.Audio);
        var output = new MemoryStream();

        Assert.Null(MediaThumbnail.TryCreate([1, 2, 3, 4]));
        var metadata = new MediaMergeMetadata { Title = "Song One" };
        MediaMerger.Merge([(audio, audioStream)], "m4a", output, default, metadata, thumbnail: null);
        MediaMerger.Validate(output, output.Length, [audio], default, metadata);
        Assert.Null(Mp4Demuxer.ReadThumbnail(output, output.Length));
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
        Assert.False(MediaMergePlan.TryParse((original with
        {
            Metadata = new MediaMergeMetadata { TrackNumber = 0, ReleaseDate = "next spring" }
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
            Artist = "Known artist", Album = "Known album", AlbumArtist = "Album artist",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12",
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
        Assert.Equal("Known artist", plan.Metadata?.Artist);
        Assert.Equal("Known album", plan.Metadata?.Album);
        Assert.Equal("Album artist", plan.Metadata?.AlbumArtist);
        Assert.Equal(2, plan.Metadata?.TrackNumber);
        Assert.Equal(1, plan.Metadata?.DiscNumber);
        Assert.Equal("2024-03-12", plan.Metadata?.ReleaseDate);
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
        var french = new SubtitleTrack(new Uri("https://cdn.example.com/captions/fr.srt"), "fr", "French", "srt");
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.com/watch/one"), Provider = "test", MediaId = "one",
            Title = "One", Representations = [stream], Subtitles = [subtitle, french]
        };
        var defaults = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);
        Assert.Null(defaults.Error);
        Assert.Empty(defaults.Choice!.SubtitleTracks ?? []);
        FormatPreferences preferences = FormatPreferences.BestQuality with { SubtitleLanguages = ["en", "fr"] };

        var prepared = MediaTransferBridge.Prepare(item, preferences);
        var exact = MediaTransferBridge.Prepare(item, preferences, stream);
        Assert.Null(prepared.Error);
        Assert.Null(exact.Error);
        MediaDownloadChoice[] jobs = [.. MediaTransferBridge.ExpandSubtitleChoices([prepared.Choice!])];

        Assert.Equal(2, exact.Choice!.SubtitleTracks!.Count);
        Assert.Equal(3, jobs.Length);
        Assert.Equal(JobKind.Http, jobs[1].Kind);
        Assert.Equal(subtitle.Url.AbsoluteUri, jobs[1].Url);
        Assert.Equal("One.en.vtt", jobs[1].FileName);
        Assert.Equal("Subtitle · en", jobs[1].Quality);
        Assert.Null(jobs[1].Provider);
        Assert.Null(jobs[1].MediaPlanJson);
        Assert.Null(jobs[1].SubtitleTracks);
        Assert.Equal(JobKind.Http, jobs[2].Kind);
        Assert.Equal(french.Url.AbsoluteUri, jobs[2].Url);
        Assert.Equal("One.fr.srt", jobs[2].FileName);
        Assert.Null(jobs[2].SubtitleTracks);

        var segmented = MediaTransferBridge.Prepare(item with
        {
            Subtitles = [subtitle with { Format = "hls-webvtt" }]
        }, preferences);
        Assert.Null(segmented.Error);
        MediaDownloadChoice segmentedJob = Assert.Single(MediaTransferBridge.ExpandSubtitleChoices([segmented.Choice!]).Skip(1));
        Assert.Equal(JobKind.Hls, segmentedJob.Kind);
        Assert.Equal("One.en.srt", segmentedJob.FileName);
        Assert.True(HlsWebVttPlan.TryParse(segmentedJob.MediaPlanJson, out HlsWebVttPlan subtitlePlan));
        Assert.Equal("srt", subtitlePlan.OutputFormat);

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
        server.Extra["/m/cover.png"] = TinyPng();
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
            MediaPlanJson: plan.ToJson(), MediaProvider: "test", MediaId: "chaptered",
            MediaThumbnailUrl: server.Url("/m/cover.png")));
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
        Assert.Equal(Payloads.Sha256(TinyPng()), Assert.IsType<MediaThumbnail>(WebmDemuxer.ReadThumbnail(output, output.Length)).Sha256);
        FaultServer.ObservedHeaders thumbnailRequest = Assert.Single(server.RequestsFor("/m/cover.png"));
        Assert.Null(thumbnailRequest.Cookie);
        Assert.Null(thumbnailRequest.Authorization);
        Assert.Null(thumbnailRequest.Referer);

        MediaMergePlan noCoverPlan = plan with { MediaId = "chaptered-no-cover" };
        AddResult noCoverAdded = h.Engine.Add(new AddRequest(noCoverPlan.Tracks[0].Url, FileName: "chaptered-no-cover.mkv",
            Kind: JobKind.Merge, MediaPlanJson: noCoverPlan.ToJson(), MediaThumbnailUrl: server.Url("/notfound")));
        Assert.True(noCoverAdded.Ok, noCoverAdded.Message);
        JobSnapshot noCoverDone = await h.WaitAsync(noCoverAdded.JobId,
            snapshot => snapshot.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.Equal(JobState.Completed, noCoverDone.State);
        using var noCoverOutput = File.OpenRead(noCoverDone.FinalPath!);
        Assert.Null(WebmDemuxer.ReadThumbnail(noCoverOutput, noCoverOutput.Length));
    }

    [Fact]
    public async Task MergeJob_EmbedsMusicTagsAndCoverInM4a_AndKeepsGoingWhenCoverIsInvalid()
    {
        await using var server = new FaultServer();
        byte[] audio = AacFmp4();
        server.Extra["/m/song.m4a"] = audio;
        server.Extra["/m/song-no-cover.m4a"] = audio;
        server.Extra["/m/cover.png"] = TinyPng();
        server.Extra["/m/bad-cover.png"] = Payloads.Make(128, 12);
        await server.StartAsync();
        using var h = new Harness();

        async Task<(JobSnapshot Job, MediaMergePlan Plan)> AddAndWait(string id, string audioPath, string coverPath, string name)
        {
            var plan = new MediaMergePlan
            {
                Provider = "bandcamp", MediaId = id, OutputContainer = "m4a",
                Metadata = new MediaMergeMetadata
                {
                    Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album",
                    AlbumArtist = "Fixture Band", TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
                },
                Tracks = [new MergeTrack("audio", "mp3-128", server.Url(audioPath), audio.Length, "m4a", "aac", id)]
            };
            AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: name, Kind: JobKind.Merge,
                MediaProvider: "bandcamp", MediaId: id, MediaPlanJson: plan.ToJson(),
                MediaThumbnailUrl: server.Url(coverPath)));
            Assert.True(added.Ok, added.Message);
            JobSnapshot done = await h.WaitAsync(added.JobId,
                snapshot => snapshot.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
            Assert.Equal(JobState.Completed, done.State);
            return (done, plan);
        }

        var (withCover, expectedPlan) = await AddAndWait("song-cover", "/m/song.m4a", "/m/cover.png", "song-cover.m4a");
        using (var output = File.OpenRead(withCover.FinalPath!))
        {
            MediaMergeMetadata stored = Mp4Demuxer.ReadMetadata(output, output.Length);
            Assert.Equal(expectedPlan.Metadata, stored);
            Assert.Equal(Payloads.Sha256(TinyPng()), Assert.IsType<MediaThumbnail>(Mp4Demuxer.ReadThumbnail(output, output.Length)).Sha256);
            DemuxedTrack muxed = Assert.Single(MediaMerger.Demux(output, output.Length, default));
            using var source = new MemoryStream(audio);
            AssertSamePayloads(Track(source, TrackKind.Audio), source, muxed, output);
        }

        var (withoutCover, _) = await AddAndWait("song-no-cover", "/m/song-no-cover.m4a", "/m/bad-cover.png", "song-no-cover.m4a");
        using var noCoverOutput = File.OpenRead(withoutCover.FinalPath!);
        Assert.Null(Mp4Demuxer.ReadThumbnail(noCoverOutput, noCoverOutput.Length));
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
