using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaRefreshTests
{
    [Theory]
    [InlineData("different", "video", "muxed-720")]
    [InlineData("fixture", "different", "muxed-720")]
    [InlineData("fixture", "video", "different")]
    public async Task IdentityMismatch_IsRejectedBeforeAnyReplacementRequest(string provider, string mediaId, string format)
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/expires-after-truncate"), MediaProvider: "fixture", MediaId: "video", MediaFormatId: "muxed-720"));
        var stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        int requests = server.Hits("/expires-after-truncate");
        var result = await h.Engine.RefreshMediaSourceAsync(added.JobId, new(provider, mediaId, format, server.Url("/expires-after-truncate?token=fresh")));
        Assert.False(result.Accepted); Assert.Equal(requests, server.Hits("/expires-after-truncate"));
        Assert.Equal(stopped.CommittedBytes, h.Engine.Snapshot(added.JobId)!.CommittedBytes);
    }

    [Fact]
    public async Task MatchingMediaIdentity_StillRequiresAndUsesRescueByteIdentity()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/expires-after-truncate"), MediaProvider: "fixture", MediaId: "video", MediaFormatId: "muxed-720"));
        await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        var result = await h.Engine.RefreshMediaSourceAsync(added.JobId,
            new("fixture", "video", "muxed-720", server.Url("/expires-after-truncate?token=fresh"), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.True(result.Accepted, result.Message); Assert.Equal(RescueMode.R0, result.Mode);
        var done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!)); Assert.Equal("muxed-720", done.MediaFormatId);
    }

    [Fact]
    public async Task LegacyMediaWithoutAFormatIdentity_IsNotGuessed()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/file"), StartNow: false));
        h.Engine.Pause(added.JobId);
        var result = await h.Engine.RefreshMediaSourceAsync(added.JobId, new("fixture", "video", "720", server.Url("/file?new=1")));
        Assert.False(result.Accepted); Assert.Contains("older job", result.Message);
    }
}
