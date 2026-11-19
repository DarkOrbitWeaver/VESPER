using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Vesper.Engine;
using Xunit.Abstractions;
using Xunit;

namespace Vesper.Tests;

public sealed class MediaConversionTests
{
    private readonly ITestOutputHelper _output;

    public MediaConversionTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(MediaOutput.Mp3, 1, 32000, true)]
    [InlineData(MediaOutput.Mp3, 2, 44100, true)]
    [InlineData(MediaOutput.Mp3, 3, 32000, false)]
    [InlineData(MediaOutput.M4a, 2, 32000, false)]
    [InlineData(MediaOutput.M4a, 2, 48000, true)]
    public void Audio_conversion_accepts_only_supported_channel_and_sample_rate_pairs(
        MediaOutput output, int channels, int sampleRate, bool expected) =>
        Assert.Equal(expected, DownloadEngine.SupportsConversionAudioFormat(output, channels, sampleRate));

    private static byte[] Audio()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
        return [.. File.ReadAllBytes(Path.Combine(folder, "audio-init.mp4")), .. File.ReadAllBytes(Path.Combine(folder, "audio-1.m4s"))];
    }

    private static byte[] MuxedMp4()
    {
        using var videoSource = new MemoryStream(Fixture("video-only.mp4"));
        using var audioSource = new MemoryStream(Audio());
        DemuxedTrack video = Assert.Single(MediaMerger.Demux(videoSource, videoSource.Length, default));
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(audioSource, audioSource.Length, default));
        using var merged = new MemoryStream();
        MediaMerger.Merge([(video, videoSource), (audio, audioSource)], "mp4", merged, default);
        return merged.ToArray();
    }

    private static void AssertSameSamples(Stream source, DemuxedTrack expected, Stream output, DemuxedTrack actual)
    {
        Assert.Equal(expected.Codec, actual.Codec);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        for (int i = 0; i < expected.Samples.Count; i++)
        {
            byte[] expectedSample = new byte[expected.Samples[i].Size];
            byte[] actualSample = new byte[actual.Samples[i].Size];
            source.Position = expected.Samples[i].Offset;
            source.ReadExactly(expectedSample);
            output.Position = actual.Samples[i].Offset;
            output.ReadExactly(actualSample);
            Assert.Equal(expectedSample, actualSample);
        }
    }

    [Fact]
    public async Task CompatibleFragmentedAacIsRemuxedToM4aWithoutReencoding()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = AddAudio(harness, server);

        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("", done.Reason);
        Assert.Equal("audio.m4a", done.FileName);
        Assert.Equal("audio/mp4", done.ContentType);
        Assert.Equal("Audio AAC", done.MediaQuality);
        Assert.NotNull(done.FinalPath);
        Assert.True(server.Hits("/audio.mp4") > 0);

        byte[] outputBytes = await File.ReadAllBytesAsync(done.FinalPath!);
        Assert.True(outputBytes.Length > 8);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(outputBytes, 4, 4));
        Assert.Equal(outputBytes.LongLength, done.TotalBytes);

        using var source = new MemoryStream(sourceBytes);
        DemuxedTrack sourceAudio = Assert.Single(MediaMerger.Demux(source, source.Length, default));
        using var output = new MemoryStream(outputBytes);
        DemuxedTrack outputAudio = Assert.Single(MediaMerger.Demux(output, output.Length, default));

        Assert.Equal(TrackKind.Audio, outputAudio.Kind);
        Assert.Equal("aac", outputAudio.Codec);
        Assert.Equal("mp4", outputAudio.SourceContainer);
        Assert.Equal(sourceAudio.Channels, outputAudio.Channels);
        Assert.Equal(sourceAudio.SampleRate, outputAudio.SampleRate);
        Assert.NotEmpty(outputAudio.Samples);
        Assert.InRange(Math.Abs(outputAudio.DurationSeconds - sourceAudio.DurationSeconds), 0, 0.05);
        AssertSameSamples(source, sourceAudio, output, outputAudio);
    }

    [Fact]
    public async Task CompatibleH264AacMp4IsRemuxedWithoutReencoding()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = server.Extra["/muxed.mp4"] = MuxedMp4();
        await server.StartAsync();
        using var harness = new Harness();
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/muxed.mp4"), FileName: "muxed.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Mp4));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal("muxed.mp4", done.FileName);
        using var source = new MemoryStream(sourceBytes);
        using var output = File.OpenRead(done.FinalPath!);
        List<DemuxedTrack> sourceTracks = MediaMerger.Demux(source, source.Length, default);
        List<DemuxedTrack> outputTracks = MediaMerger.Demux(output, output.Length, default);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], outputTracks.Select(track => track.Kind));
        AssertSameSamples(source, sourceTracks[0], output, outputTracks[0]);
        AssertSameSamples(source, sourceTracks[1], output, outputTracks[1]);
    }

    [Fact]
    public async Task IndependentLosslessRemuxesDoNotWaitForTheEncoder()
    {
        await using var server = new FaultServer();
        byte[] source = Audio();
        server.Extra["/first.mp4"] = source;
        server.Extra["/second.mp4"] = source;
        await server.StartAsync();
        using var harness = new Harness();
        var gate = Assert.IsType<SemaphoreSlim>(typeof(DownloadEngine)
            .GetField("_conversionSlot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Engine));
        Assert.True(gate.Wait(0));
        try
        {
            var first = harness.Engine.Add(new AddRequest(server.Url("/first.mp4"),
                FileName: "first.mp4", MediaProvider: "fixture", Output: MediaOutput.M4a));
            var second = harness.Engine.Add(new AddRequest(server.Url("/second.mp4"),
                FileName: "second.mp4", MediaProvider: "fixture", Output: MediaOutput.M4a));
            Assert.True(first.Ok, first.Message);
            Assert.True(second.Ok, second.Message);

            JobSnapshot[] completed = await Task.WhenAll(
                harness.WaitForStateAsync(first.JobId, JobState.Completed, seconds: 5),
                harness.WaitForStateAsync(second.JobId, JobState.Completed, seconds: 5));
            Assert.Equal(0, gate.CurrentCount);
            Assert.Equal(2, completed.Select(j => j.FinalPath).Distinct().Count());
            foreach (JobSnapshot job in completed)
            {
                Assert.EndsWith(".m4a", job.FileName);
                using var file = File.OpenRead(job.FinalPath!);
                DemuxedTrack audio = Assert.Single(MediaMerger.Demux(file, file.Length, default));
                Assert.Equal("aac", audio.Codec);
                Assert.NotEmpty(audio.Samples);
            }
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task InjectedEncoderStillWaitsForTheExclusiveSlot()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var entered = new ManualResetEventSlim();
        using var harness = new Harness(convertMedia: (source, extension, stage, output, ct) =>
        {
            entered.Set();
            File.Copy(source, stage);
        });
        var gate = Assert.IsType<SemaphoreSlim>(typeof(DownloadEngine)
            .GetField("_conversionSlot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Engine));
        Assert.True(gate.Wait(0));
        try
        {
            Guid id = AddAudio(harness, server);
            await harness.WaitAsync(id, job => job.State == JobState.Converting);
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(125)),
                "An injected encoder must not run while another encoder owns the slot.");
        }
        finally { gate.Release(); }
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
    }

    [WindowsFact]
    public async Task WindowsMediaFoundationDecodesAacToWavWhenPcmIsRequested()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = AddAudio(harness, server, MediaOutput.Wav);

        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("audio.wav", done.FileName);
        using var source = new MemoryStream(sourceBytes);
        DemuxedTrack sourceAudio = Assert.Single(MediaMerger.Demux(source, source.Length, default));
        using var wave = File.OpenRead(done.FinalPath!);
        var info = WaveAudio.Read(wave);
        Assert.Equal(sourceAudio.Channels, info.Channels);
        Assert.Equal(sourceAudio.SampleRate, info.SampleRate);
        Assert.InRange(Math.Abs(info.Duration - sourceAudio.DurationSeconds), 0, 0.05);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task MediaFoundationEncodesMp3WhenTheNativeEncoderIsAvailable()
    {
        CodecCapabilities capabilities = CodecCapabilities.Current;
        if (!capabilities.Known || !capabilities.Mp3Encoder)
        {
            _output.WriteLine("Skipped the real MP3 encode because Windows reported no native MP3 encoder.");
            return;
        }

        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness(convertMedia: (source, extension, target, output, ct) =>
            WindowsMediaConverter.Convert(source, extension, target, output, ct));
        Guid id = AddAudio(harness, server, MediaOutput.Mp3);

        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("audio.mp3", done.FileName);
        Assert.Equal("audio/mpeg", done.ContentType);
        Assert.Equal("MP3 audio", done.MediaQuality);
        using var outputFile = File.OpenRead(done.FinalPath!);
        Assert.True(Mp3MetadataWriter.IsPlayableMpegLayer3(outputFile));
    }

    [Fact]
    public async Task WaveAudioWritesAndReadsRf64SizeTable()
    {
        using var decoded = new MemoryStream();
        using (var writer = new BinaryWriter(decoded, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVEfmt "u8); writer.Write(16u);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(8u);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            writer.Seek(4, SeekOrigin.Begin); writer.Write((uint)(decoded.Length - 8));
        }

        using var rf64 = new MemoryStream();
        await WaveAudio.WriteAsync(decoded, rf64, null, null, CancellationToken.None, forceRf64: true);

        Assert.Equal("RF64", Encoding.ASCII.GetString(rf64.GetBuffer(), 0, 4));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(rf64.GetBuffer(), 4));
        Assert.Equal(8UL, BitConverter.ToUInt64(rf64.GetBuffer(), 28));
        Assert.Equal(4UL, BitConverter.ToUInt64(rf64.GetBuffer(), 36));
        WaveAudio.Info result = WaveAudio.Read(rf64);
        Assert.Equal(4, result.SampleCount);
        Assert.Equal(8, result.DataBytes);
    }

    [WindowsFact]
    [Trait("Category", "ConversionBenchmark")]
    [SupportedOSPlatform("windows")]
    public async Task MeasureSmallAacM4aRemuxAgainstMediaFoundationTranscoding()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new FaultServer();
        byte[] source = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();

        async Task<double> Run(bool useMediaFoundation)
        {
            Action<string, string, string, MediaOutput, CancellationToken>? converter = useMediaFoundation
                ? (path, extension, target, output, ct) => WindowsMediaConverter.Convert(path, extension, target, output, ct)
                : null;
            using var harness = new Harness(convertMedia: converter);
            var timer = Stopwatch.StartNew();
            Guid id = AddAudio(harness, server);
            JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
            timer.Stop();
            Assert.Equal("audio.m4a", done.FileName);
            return timer.Elapsed.TotalMilliseconds;
        }

        var remuxTimes = new List<double>();
        var transcodeTimes = new List<double>();
        for (int run = 0; run < 10; run++)
        {
            bool useMediaFoundation = run % 2 != 0;
            double elapsed = await Run(useMediaFoundation);
            if (run < 2) continue;
            (useMediaFoundation ? transcodeTimes : remuxTimes).Add(elapsed);
        }

        static double Median(List<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return sorted[sorted.Length / 2];
        }
        _output.WriteLine($"AAC M4A conversion: source-bytes={source.Length}; runs={remuxTimes.Count}; remux-p50-ms={Median(remuxTimes):0.###}; MediaFoundation-transcode-p50-ms={Median(transcodeTimes):0.###}");
    }

    [Fact]
    public async Task Invalid_converted_output_publishes_the_original_unchanged()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var h = new Harness(convertMedia: (_, _, target, _, _) => File.WriteAllBytes(target, [1, 2, 3]));
        Guid id = AddAudio(h, server);

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal("saved-original", done.Reason);
        Assert.Equal("audio.mp4", done.FileName);
        string[] files = h.Files();
        Assert.True(files.Length == 1, $"Unexpected files: {string.Join(", ", files)}\n{string.Join("\n", h.Engine.ReadLog())}");
        Assert.Equal(audio, File.ReadAllBytes(files[0]));
    }

    [Fact]
    public async Task Invalid_mp3_conversion_publishes_the_original_with_its_source_name()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness(convertMedia: (_, _, target, _, _) => File.WriteAllBytes(target, [1, 2, 3]));

        Guid id = AddAudio(harness, server, MediaOutput.Mp3);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("saved-original", done.Reason);
        Assert.Equal("audio.mp4", done.FileName);
        Assert.Contains("original was saved unchanged", done.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(audio, File.ReadAllBytes(Assert.Single(harness.Files())));
        Assert.DoesNotContain(harness.Files(), path => Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mp3_conversion_ignores_video_in_a_muxed_mp4_source()
    {
        await using var server = new FaultServer();
        server.Extra["/muxed.mp4"] = MuxedMp4();
        await server.StartAsync();
        using var harness = new Harness(convertMedia: (_, extension, target, output, _) =>
        {
            Assert.Equal(".mp4", extension);
            Assert.Equal(MediaOutput.Mp3, output);
            File.WriteAllBytes(target, Fixture("quiet-track.mp3"));
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/muxed.mp4"), FileName: "muxed.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Mp3));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("muxed.mp3", done.FileName);
        Assert.Equal("", done.Reason);
        using var output = File.OpenRead(done.FinalPath!);
        Assert.True(Mp3MetadataWriter.IsPlayableMpegLayer3(output));
    }

    [Fact]
    public async Task Generic_wav_failure_keeps_the_approved_saved_original_fallback()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness(convertMedia: (_, _, target, _, _) => File.WriteAllBytes(target, [1, 2, 3]));

        Guid id = AddAudio(harness, server, MediaOutput.Wav);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("saved-original", done.Reason);
        Assert.Equal("audio.mp4", done.FileName);
        Assert.Equal(audio, File.ReadAllBytes(Assert.Single(harness.Files())));
    }

    [Fact]
    public async Task Failed_wav_clip_does_not_publish_the_unclipped_original()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness(convertMedia: (_, _, target, _, _) => File.WriteAllBytes(target, [1, 2, 3]));
        MediaTimeRange range = new(.1, .3);
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Wav, MediaPlanJson: System.Text.Json.JsonSerializer.Serialize(range)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot failed = await harness.WaitForStateAsync(added.JobId, JobState.NeedsAttention);

        Assert.Null(failed.FinalPath);
        Assert.Contains("WAV output", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(audio, await File.ReadAllBytesAsync(failed.PartialPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(failed.PartialPath + ".convert.wav"));
        Assert.False(File.Exists(Path.Combine(harness.Downloads, "audio.wav")));
    }

    [Fact]
    public async Task Mp3_conversion_publishes_a_tagged_playable_mp3_and_recovers_its_stage()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();

        using var crashed = new ManualResetEventSlim();
        int conversions = 0;
        using var harness = new Harness(failpoint: point =>
        {
            if (point != "finalize.afterMediaPrepare") return;
            crashed.Set();
            throw new FailpointCrash(point);
        }, convertMedia: (_, extension, target, output, _) =>
        {
            Assert.Equal(".mp4", extension);
            Assert.Equal(MediaOutput.Mp3, output);
            Interlocked.Increment(ref conversions);
            File.WriteAllBytes(target, Fixture("quiet-track.mp3"));
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Mp3,
            MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(SongTags)));
        Assert.True(added.Ok, added.Message);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The MP3 conversion did not reach its prepare failpoint.");

        harness.Restart();
        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.mp3", done.FileName);
        Assert.Equal("audio/mpeg", done.ContentType);
        Assert.Equal("MP3 audio", done.MediaQuality);
        Assert.Equal("", done.Reason);
        Assert.Equal(1, conversions);
        Assert.Single(harness.Files());
        byte[] outputBytes = File.ReadAllBytes(done.FinalPath!);
        Assert.True(Mp3MetadataWriter.HasId3v2Tag(new MemoryStream(outputBytes)));
        Assert.Contains("Song One", Encoding.UTF8.GetString(outputBytes));
        using var output = new MemoryStream(outputBytes);
        Assert.True(Mp3MetadataWriter.IsPlayableMpegLayer3(output));
        Assert.Empty(Directory.GetFiles(harness.Downloads, "*.convert*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task A_crash_while_publishing_compatible_output_recovers_one_valid_file(string point)
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var crashed = new ManualResetEventSlim();
        int conversions = 0;
        using var h = new Harness(failpoint: step =>
        {
            if (step != point) return;
            crashed.Set();
            throw new FailpointCrash(step);
        }, convertMedia: (source, _, target, _, _) =>
        {
            Interlocked.Increment(ref conversions);
            File.Copy(source, target);
        });
        Guid id = AddAudio(h, server);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The conversion did not reach the publication failpoint.");
        JobSnapshot before = h.Engine.Snapshot(id)!;
        Assert.True(File.Exists(before.PartialPath));
        bool moved = point == "finalize.afterMove";
        string occupied = Path.Combine(h.Downloads, moved ? "kept.m4a" : "audio.m4a");
        File.WriteAllText(occupied, "unrelated");

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.True(done.FileName == (moved ? "audio.m4a" : "audio (1).m4a"),
            $"Unexpected published name: {done.FileName}\n{string.Join(Environment.NewLine, h.Engine.ReadLog())}");
        Assert.Equal("", done.Reason);
        Assert.Equal(audio, File.ReadAllBytes(done.FinalPath!));
        Assert.Equal(audio.LongLength, done.TotalBytes);
        Assert.Equal("audio/mp4", done.ContentType);
        Assert.Equal("unrelated", File.ReadAllText(occupied));
        Assert.Equal(2, h.Files().Length);
        Assert.Equal(1, conversions);
    }

    [Fact]
    public async Task Recovery_does_not_accept_a_same_content_replacement_for_a_prepared_conversion()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var crashed = new ManualResetEventSlim();
        using var harness = new Harness(failpoint: point =>
        {
            if (point != "finalize.afterMove") return;
            crashed.Set();
            throw new FailpointCrash(point);
        }, convertMedia: (source, _, target, _, _) => File.Copy(source, target));

        Guid id = AddAudio(harness, server);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The conversion did not reach the move failpoint.");
        string published = Path.Combine(harness.Downloads, "audio.m4a");
        Assert.Equal(audio, File.ReadAllBytes(published));
        File.Delete(published);
        File.WriteAllBytes(published, audio);

        harness.Restart();
        JobSnapshot attention = await harness.WaitForStateAsync(id, JobState.NeedsAttention);

        Assert.Equal("finalize-lost", attention.Reason);
        Assert.Null(attention.FinalPath);
        Assert.Equal(audio, File.ReadAllBytes(published));
    }

    private static Guid AddAudio(Harness h, FaultServer server, MediaOutput output = MediaOutput.M4a)
    {
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: output));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }

    // ---- converted output keeps title, artist and cover (VES-006) ----------------------------------------------

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    private static readonly MediaMergeMetadata SongTags = new()
    {
        Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
        TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
    };

    // Stands in for Media Foundation: quiet-track-aac.m4a is the real AAC encode of quiet-track.webm.
    private static void WriteConvertedAac(string source, string extension, string target, MediaOutput output, CancellationToken ct) =>
        File.WriteAllBytes(target, Fixture("quiet-track-aac.m4a"));

    private static Guid AddOpusSong(Harness h, FaultServer server, string? cover = "/m/cover.png")
    {
        byte[] opus = Fixture("quiet-track.webm");
        var plan = new MediaMergePlan
        {
            Provider = "test", MediaId = "webm-song", OutputContainer = "webm", Metadata = SongTags,
            Tracks = [new MergeTrack("audio", "opus-track", server.Url("/m/audio.webm"), opus.Length, "webm", "opus", "audio-id")]
        };
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/m/audio.webm"), FileName: "song.webm",
            Kind: JobKind.Merge, MediaProvider: "test", MediaId: "webm-song", MediaPlanJson: plan.ToJson(),
            MediaThumbnailUrl: cover is null ? null : server.Url(cover), Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }

    private static void AssertSongTags(MediaMergeMetadata stored)
    {
        Assert.Equal(SongTags.Title, stored.Title);
        Assert.Equal(SongTags.Artist, stored.Artist);
        Assert.Equal(SongTags.Album, stored.Album);
        Assert.Equal(SongTags.AlbumArtist, stored.AlbumArtist);
        Assert.Equal(SongTags.TrackNumber, stored.TrackNumber);
        Assert.Equal(SongTags.DiscNumber, stored.DiscNumber);
        Assert.Equal(SongTags.ReleaseDate, stored.ReleaseDate);
    }

    // The converted bytes are copied, never re-encoded: every AAC frame must equal the converter's output.
    private static void AssertSameAacFrames(Stream published)
    {
        using var converted = new MemoryStream(Fixture("quiet-track-aac.m4a"));
        DemuxedTrack expected = Assert.Single(Mp4Demuxer.Read(converted, converted.Length, default));
        DemuxedTrack actual = Assert.Single(Mp4Demuxer.Read(published, published.Length, default));
        Assert.Equal("aac", actual.Codec);
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        for (int i = 0; i < expected.Samples.Count; i++)
        {
            var want = new byte[expected.Samples[i].Size];
            var got = new byte[actual.Samples[i].Size];
            converted.Position = expected.Samples[i].Offset;
            converted.ReadExactly(want);
            published.Position = actual.Samples[i].Offset;
            published.ReadExactly(got);
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public async Task Opus_converted_to_m4a_keeps_title_artist_and_cover()
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server);

        JobSnapshot done = await h.WaitAsync(id, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal("", done.Reason);
        Assert.Equal("song.m4a", done.FileName);
        Assert.Equal("audio/mp4", done.ContentType);
        using FileStream output = File.OpenRead(done.FinalPath!);
        Assert.Equal(output.Length, done.TotalBytes);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(Mp4Demuxer.ReadThumbnail(output, output.Length));
        Assert.Equal(Payloads.Sha256(TinyPng()), cover.Sha256);
        AssertSameAacFrames(output);
        Assert.Single(h.Files());                                       // no .convert or .convert-tagged leftovers
        Assert.Equal(1, server.Hits("/m/cover.png"));                    // the cover the merge stored is reused, not fetched twice

        // Lets a developer hand the exact published bytes to ffprobe, mutagen or Windows Media Player.
        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_CONVERTED_M4A");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(qaPath))!);
            File.Copy(done.FinalPath!, Path.GetFullPath(qaPath), overwrite: true);
        }
    }

    [Fact]
    public async Task A_directly_downloaded_file_converted_to_m4a_is_tagged_and_renamed()
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/m/audio.webm"), FileName: "song.webm",
            MediaProvider: "test", Output: MediaOutput.M4a, MediaThumbnailUrl: server.Url("/m/cover.png"),
            MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(SongTags)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        // Before the fix this name stayed "song.webm" as soon as a converted job carried tags.
        Assert.Equal("song.m4a", done.FileName);
        Assert.Equal("", done.Reason);
        using FileStream output = File.OpenRead(done.FinalPath!);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        Assert.NotNull(Mp4Demuxer.ReadThumbnail(output, output.Length));
        AssertSameAacFrames(output);
        Assert.Single(h.Files());
    }

    [Theory]
    [InlineData(null)]                 // no artwork was found for this media
    [InlineData("/m/broken.png")]      // the server sent something that is not an image
    [InlineData("/m/missing.png")]     // the artwork link is dead
    public async Task Missing_or_broken_artwork_never_blocks_the_converted_file(string? cover)
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/broken.png"] = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server, cover);

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("song.m4a", done.FileName);
        using FileStream output = File.OpenRead(done.FinalPath!);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        Assert.Null(Mp4Demuxer.ReadThumbnail(output, output.Length));
        AssertSameAacFrames(output);
        if (cover is null) Assert.Equal("", done.Reason);
        else
        {
            Assert.Equal("media-metadata", done.Reason);
            Assert.Contains("artwork was unavailable", done.Message);
        }
    }

    [Fact]
    public async Task A_converted_file_without_any_tags_is_published_exactly_as_converted()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.webm"] = Fixture("quiet-track.webm");
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/audio.webm"), FileName: "audio.webm",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.m4a", done.FileName);
        Assert.Equal(Fixture("quiet-track-aac.m4a"), File.ReadAllBytes(done.FinalPath!));   // nothing invented, nothing rewritten
    }

    [Theory]
    [InlineData("finalize.afterMediaPrepare")]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task A_crash_around_publishing_a_tagged_conversion_recovers_one_tagged_file(string point)
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var crashed = new ManualResetEventSlim();
        using var h = new Harness(failpoint: step =>
        {
            if (step != point) return;
            crashed.Set();
            throw new FailpointCrash(step);
        }, convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The conversion did not reach the failpoint.");

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("song.m4a", done.FileName);
        using (FileStream output = File.OpenRead(done.FinalPath!))
        {
            Assert.Equal(output.Length, done.TotalBytes);
            AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
            Assert.NotNull(Mp4Demuxer.ReadThumbnail(output, output.Length));
            AssertSameAacFrames(output);
        }
        Assert.Single(h.Files());
        Assert.Empty(Directory.GetFiles(h.Downloads, "*.convert*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Tags_are_refused_for_an_original_file_that_cannot_hold_them()
    {
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest("https://example.com/audio.webm", FileName: "audio.webm",
            MediaProvider: "fixture", MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(SongTags), StartNow: false));
        Assert.Equal(AddStatus.Invalid, added.Status);
    }
}
