using Vesper.MediaResolver.Discovery;
using Xunit;

namespace Vesper.MediaResolver.Tests.DiscoveryTests;

public sealed class WebmHeaderFactsTests
{
    [Theory]
    [InlineData("video-only.webm", false)]
    [InlineData("muxed.webm", true)]
    public void RealWebmFixturesExposeTrackFacts(string name, bool audio)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var facts = Assert.IsType<DirectMediaFacts>(WebmHeaderFacts.Read(bytes, bytes.LongLength));
        Assert.True(facts.HasVideo);
        Assert.Equal(audio, facts.HasAudio);
        Assert.Equal("vp9", facts.VideoCodec);
        Assert.Equal(128, facts.Width);
        Assert.Equal(72, facts.Height);
        Assert.True(facts.CompleteFileCandidate);
        Assert.True(facts.DurationSeconds > 0);
        Assert.False(WebmHeaderFacts.Read(bytes, bytes.LongLength + 1)!.CompleteFileCandidate);
        Assert.Null(WebmHeaderFacts.Read(bytes.AsSpan(0, 20), bytes.LongLength));
    }
}
