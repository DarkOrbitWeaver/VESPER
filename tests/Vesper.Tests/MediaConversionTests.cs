using System.Text;
using Vesper.Engine;
using Xunit;

namespace Vesper.Tests;

public sealed class MediaConversionTests
{
    private static byte[] Audio()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
        return [.. File.ReadAllBytes(Path.Combine(folder, "audio-init.mp4")), .. File.ReadAllBytes(Path.Combine(folder, "audio-1.m4s"))];
    }

    [WindowsFact]
    public async Task WindowsMediaFoundation_PreservesFragmentedAacInAnM4aOutput()
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
        Assert.Equal(audio, File.ReadAllBytes(Assert.Single(h.Files())));
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
        Assert.Equal(moved ? "audio.m4a" : "audio (1).m4a", done.FileName);
        Assert.Equal("", done.Reason);
        Assert.Equal(audio, File.ReadAllBytes(done.FinalPath!));
        Assert.Equal(audio.LongLength, done.TotalBytes);
        Assert.Equal("audio/mp4", done.ContentType);
        Assert.Equal("unrelated", File.ReadAllText(occupied));
        Assert.Equal(2, h.Files().Length);
        Assert.Equal(moved ? 1 : 2, conversions);
    }

    private static Guid AddAudio(Harness h, FaultServer server)
    {
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }
}
