using Vesper.Engine;

namespace Vesper.Tests;

public sealed class RawAudioTimelineTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

    [Theory]
    [InlineData("quiet-track.mp3", "mp3", 44100, 1)]
    [InlineData("quiet-track.ogg", "vorbis", 44100, 1)]
    [InlineData("quiet-track.opus", "opus", 48000, 1)]
    public void CompleteRawAudioHasAnIndependentlyParsedTimeline(string name, string codec, int rate, int channels)
    {
        using var source = new MemoryStream(Fixture(name));
        var facts = RawAudioTimeline.Read(source, name, default);
        Assert.Equal(codec, facts.Codec);
        Assert.Equal(rate, facts.SampleRate);
        Assert.Equal(channels, facts.Channels);
        Assert.InRange(facts.DurationSeconds, .35, .6);
    }

    [Theory]
    [InlineData("quiet-track.mp3")]
    [InlineData("quiet-track.ogg")]
    [InlineData("quiet-track.opus")]
    public void TruncatedSourceCannotProduceACompleteTimeline(string name)
    {
        using var source = new MemoryStream(Fixture(name)[..^1]);
        Exception? ex = Record.Exception(() => RawAudioTimeline.Read(source, name, default));
        Assert.True(ex is IOException or MediaContainerException, ex?.ToString() ?? "Truncated audio was accepted.");
    }

    [Theory]
    [InlineData("quiet-track.ogg")]
    [InlineData("quiet-track.opus")]
    public void OggCorruptPageChecksumIsRejected(string name)
    {
        byte[] bytes = Fixture(name);
        bytes[^1] ^= 1;
        using var source = new MemoryStream(bytes);
        Exception? ex = Record.Exception(() => RawAudioTimeline.Read(source, name, default));
        Assert.True(ex is IOException or MediaContainerException, ex?.ToString() ?? "Corrupted audio was accepted.");
    }

    [WindowsFact]
    public async Task DirectMp3ProducesValidatedPcmWave()
    {
        byte[] source = Fixture("quiet-track.mp3");
        await using var server = new FaultServer();
        server.Extra["/raw.mp3"] = source;
        await server.StartAsync();
        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/raw.mp3"), FileName: "raw.mp3",
            Output: MediaOutput.Wav, MediaProvider: "fixture", MediaId: "raw", MediaFormatId: "raw-mp3"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot result = await h.WaitAsync(added.JobId, j => j.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(result.State == JobState.Completed, result.Message);
        Assert.EndsWith(".wav", result.FinalPath);
        using var wave = File.OpenRead(result.FinalPath!);
        var info = WaveAudio.Read(wave);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.Bits);
        Assert.InRange(info.Duration, .25, .6);
    }
}
