using System.Text.Json;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

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
    public async Task RefreshedMediaCookiesAreScopedToTheNewOriginAndNeverSerialized()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/expires-after-truncate"), MediaProvider: "fixture",
            MediaId: "video", MediaFormatId: "720", ExpectedSha256: Payloads.Sha256(server.V1)));
        await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);

        const string cookie = "session=refresh-secret";
        var replacement = new ResolvedMediaSource("fixture", "video", "720", server.Url("/authenticated-refresh"),
            DateTimeOffset.UtcNow.AddMinutes(5), new Dictionary<string, string> { ["Cookie"] = cookie });
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://example.test/video"), Provider = "fixture", MediaId = "video",
            Representations = [new MediaRepresentation
            {
                Id = "720", Url = new Uri(server.Url("/authenticated-refresh")),
                Context = new MediaRequestContext
                {
                    CookieHeader = cookie, CredentialOrigin = new Uri(server.BaseUrl), CredentialHosts = ["127.0.0.1"]
                }
            }]
        };
        RescueResult result = await VESPER.MediaSourceRescue.RefreshDirectAsync(h.Engine, h.Engine.Snapshot(added.JobId)!, item, CancellationToken.None);
        Assert.True(result.Accepted, result.Message);
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.NotEmpty(server.RequestsFor("/authenticated-refresh"));
        Assert.All(server.RequestsFor("/authenticated-refresh"), request => Assert.Equal(cookie, request.Cookie));
        Assert.DoesNotContain(cookie, JsonSerializer.Serialize(replacement), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshDoesNotForwardCookiesToAHostOutsideTheResolverContext()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/expires-after-truncate"), MediaProvider: "fixture",
            MediaId: "video", MediaFormatId: "720", ExpectedSha256: Payloads.Sha256(server.V1)));
        JobSnapshot job = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://example.test/video"), Provider = "fixture", MediaId = "video",
            Representations = [new MediaRepresentation
            {
                Id = "720", Url = new Uri(server.Url("/authenticated-refresh")),
                Context = new MediaRequestContext
                {
                    CookieHeader = "session=not-authorized", CredentialOrigin = new Uri("https://example.test/"),
                    CredentialHosts = ["example.test"]
                }
            }]
        };

        RescueResult result = await VESPER.MediaSourceRescue.RefreshDirectAsync(h.Engine, job, item, CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Contains("not authorized", result.Message);
        Assert.Equal(0, server.Hits("/authenticated-refresh"));
    }

    [Fact]
    public async Task RefreshedMediaReportsWhenTheServerRequiresSignIn()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/expires-after-truncate"), MediaProvider: "fixture",
            MediaId: "video", MediaFormatId: "720", ExpectedSha256: Payloads.Sha256(server.V1)));
        await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);

        RescueResult result = await h.Engine.RefreshMediaSourceAsync(added.JobId,
            new("fixture", "video", "720", server.Url("/authenticated-refresh"), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(result.Accepted);
        Assert.Contains("requires sign-in", result.Message);
        Assert.Equal(server.Url("/expires-after-truncate"), h.Engine.Snapshot(added.JobId)!.SourceUrl);
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

    [Fact]
    public async Task LiveHlsRefreshRequiresSameIdentityAndAnAvailableNextSequence()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        const string oldPath = "/hls/live-refresh/old/index.m3u8";
        const string newPath = "/hls/live-refresh/new/index.m3u8";
        server.HlsFiles["/hls/live-refresh/old/s100.ts"] = FaultServer.HlsSegment(0);
        server.HlsFiles["/hls/live-refresh/new/s100.ts"] = FaultServer.HlsSegment(0);
        server.HlsFiles["/hls/live-refresh/new/s101.ts"] = FaultServer.HlsSegment(1);
        server.HlsFiles["/hls/live-refresh/new/s102.ts"] = FaultServer.HlsSegment(2);
        server.HlsPlaylists[oldPath] = LivePlaylist(100, "s100.ts");
        server.HlsPlaylists[newPath] = LivePlaylist(102, "s102.ts");

        AddResult added = h.Engine.Add(new AddRequest(server.Url(oldPath), FileName: "live-refresh.ts", Kind: JobKind.Hls,
            MediaProvider: "fixture", MediaId: "live-one", MediaFormatId: "transport-stream"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot captured = await h.WaitAsync(added.JobId, job => job.IsLiveRecording && job.CommittedBytes > 0);
        Assert.True(server.HlsPlaylists.TryRemove(oldPath, out _));
        JobSnapshot expired = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);

        RescueResult mismatch = await h.Engine.RefreshMediaSourceAsync(added.JobId,
            new("fixture", "another-live", "transport-stream", server.Url(newPath), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(mismatch.Accepted);
        Assert.Equal(0, server.Hits(newPath));
        Assert.Equal(expired.CommittedBytes, h.Engine.Snapshot(added.JobId)!.CommittedBytes);

        RescueResult gap = await h.Engine.RefreshMediaSourceAsync(added.JobId,
            new("fixture", "live-one", "transport-stream", server.Url(newPath), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(gap.Accepted);
        Assert.Contains("dropped segment", gap.Message);
        Assert.Equal(server.Url(oldPath), h.Engine.Snapshot(added.JobId)!.SourceUrl);

        server.HlsPlaylists[newPath] = LivePlaylist(100, "s100.ts", "s101.ts");
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://example.test/live"), Provider = "fixture", MediaId = "live-one",
            Representations = [new MediaRepresentation
            { Id = "transport-stream", Url = new Uri(server.Url(newPath)), Protocol = Vesper.MediaResolver.Models.StreamProtocol.Hls }]
        };
        RescueResult refreshed = await VESPER.MediaSourceRescue.RefreshLiveHlsAsync(
            h.Engine, h.Engine.Snapshot(added.JobId)!, item, CancellationToken.None);
        Assert.True(refreshed.Accepted, refreshed.Message);
        Assert.Equal(RescueMode.None, refreshed.Mode);

        await h.WaitAsync(added.JobId, job => job.IsLiveRecording && job.CommittedBytes > captured.CommittedBytes);
        Assert.True(h.Engine.StopAndKeepLive(added.JobId));
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        byte[] expected = [.. FaultServer.HlsSegment(0), .. FaultServer.HlsSegment(1)];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(0, server.Hits("/hls/live-refresh/new/s100.ts"));
        Assert.Equal(1, server.Hits("/hls/live-refresh/new/s101.ts"));
        Assert.True(expired.IsLiveRecording);
    }

    private string LivePlaylist(long sequence, params string[] segments)
    {
        var lines = new List<string> { "#EXTM3U", "#EXT-X-TARGETDURATION:1", $"#EXT-X-MEDIA-SEQUENCE:{sequence}" };
        foreach (string segment in segments) { lines.Add("#EXTINF:1,"); lines.Add(segment); }
        return string.Join('\n', lines) + "\n";
    }
}
