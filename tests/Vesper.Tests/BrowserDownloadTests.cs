using Vesper.Engine;

namespace Vesper.Tests;

public sealed class BrowserDownloadTests
{
    private const string TransferId = "chrome-transfer-test";

    [Fact]
    public void ActivityCarriesOnlyBoundedDisplayFactsAndRejectsForeignIds()
    {
        const string sample = """
            {"v":1,"id":"sample-command","type":"browser-activity","operation":"watch","profile":"demo-profile",
             "files":[{"id":"c-demo-profile-7-abc","name":"leaf.zip","received":128,"length":256,"start":1760000000000,
                       "paused":true,"canResume":true}]}
            """;
        Assert.True(BridgeProtocol.TryParse(sample, out var request, out _));
        var file = Assert.Single(request.Activity!.Files);
        Assert.Equal("leaf.zip", file.Name);
        Assert.Equal(128, file.Received);
        Assert.True(file.CanResume);
        Assert.False(BridgeProtocol.TryParse(sample.Replace("c-demo-profile-7", "c-other-profile-7"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(sample.Replace("leaf.zip", "../leaf.zip"), out _, out _));
    }

    [Fact]
    public async Task ADiscoveryCooldownDefersAnotherRequestOnTheSameRoute()
    {
        var options = new EngineOptions { DataDirectory = Path.GetTempPath() };
        var origin = new Uri("https://media.example/movie");
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        using var resolver = new Vesper.MediaResolver.Core.SecureHttpClient(new()
        {
            OriginRetryAt = options.Cooldown.RetryAt,
            OriginThrottled = options.Cooldown.Remember
        }, new ThrottledResponse(until));
        var limited = await Assert.ThrowsAsync<Vesper.MediaResolver.Models.MediaResolverException>(() =>
            resolver.GetStringAsync(origin, null, default));
        Assert.Equal(Vesper.MediaResolver.Models.MediaErrorCode.RateLimited, limited.Error.Code);
        Assert.Equal(until, limited.Error.RetryAt);
        using var transfer = new HttpTransport(options);
        using var response = await transfer.SendAsync(new Uri(origin, "/other"), new RequestSpec(), default);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(until, response.Headers.RetryAfter!.Date);
    }

    private sealed class ThrottledResponse(DateTimeOffset until) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reply = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            reply.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(until);
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Matching_response_waits_for_Chrome_before_writing_and_publishes_the_file_once()
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = Stage(harness, server, new BrowserTransferProof(server.V1.Length, "\"p1\"", "application/octet-stream"));

        JobSnapshot staged = await harness.WaitAsync(id, job => job.Reason == "browser-ready");
        Assert.Equal(0, staged.ProgressBytes);
        Assert.Null(staged.FinalPath);
        Assert.True(staged.IsUnacceptedBrowserCapture);
        Assert.True(harness.Engine.AcceptBrowserTransfer(TransferId));
        Assert.False(harness.Engine.BrowserTransferSnapshot(TransferId)!.IsUnacceptedBrowserCapture);
        Assert.True(harness.Engine.AcceptBrowserTransfer(TransferId));

        JobSnapshot complete = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(complete.FinalPath!));
        Assert.Equal(complete.FinalPath, Assert.Single(harness.Files()));
        Assert.False(complete.IsUnacceptedBrowserCapture);
    }

    [Fact]
    public async Task Abandon_reports_stopping_until_the_browser_transfer_session_exits()
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = Stage(harness, server, new BrowserTransferProof(server.V1.Length, "\"p1\"", null));
        await harness.WaitAsync(id, job => job.Reason == "browser-ready");

        Assert.True(harness.Engine.BrowserTransferIsRunning(TransferId));
        harness.Engine.AbandonBrowserTransfer(TransferId);
        JobSnapshot stopped = await harness.WaitAsync(id, job => job.State == JobState.Cancelled
            && !harness.Engine.BrowserTransferIsRunning(TransferId));

        Assert.Equal("browser-fallback", stopped.Reason);
        Assert.True(stopped.IsUnacceptedBrowserCapture);
        Assert.False(harness.Engine.BrowserTransferIsRunning(TransferId));
    }

    [Theory]
    [InlineData(1, "\"p1\"", "application/octet-stream")]
    [InlineData(0, "\"older\"", "application/octet-stream")]
    [InlineData(0, "\"p1\"", "application/zip")]
    public async Task A_changed_size_etag_or_type_never_takes_over(int sizeDifference, string etag, string contentType)
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = Stage(harness, server, new BrowserTransferProof(server.V1.Length + sizeDifference, etag, contentType));

        JobSnapshot stopped = await harness.WaitForStateAsync(id, JobState.Cancelled);
        Assert.Equal("browser-fallback", stopped.Reason);
        Assert.Equal(0, stopped.CommittedBytes);
        Assert.Null(stopped.FinalPath);
        Assert.False(harness.Engine.AcceptBrowserTransfer(TransferId));
    }

    [Fact]
    public async Task Restart_does_not_publish_a_transfer_Chrome_never_confirmed()
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = Stage(harness, server, new BrowserTransferProof(server.V1.Length, "\"p1\"", null));
        await harness.WaitAsync(id, job => job.Reason == "browser-ready");

        harness.Restart();

        JobSnapshot recovered = await harness.WaitForStateAsync(id, JobState.Cancelled);
        Assert.Equal(0, recovered.CommittedBytes);
        Assert.Null(recovered.FinalPath);
        Assert.False(harness.Engine.AcceptBrowserTransfer(TransferId));
    }

    private static Guid Stage(Harness harness, FaultServer server, BrowserTransferProof proof)
    {
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/file"), RequestId: TransferId + ":0", BrowserProof: proof));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }
}
