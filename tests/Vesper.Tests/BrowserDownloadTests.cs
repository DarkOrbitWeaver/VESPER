using Microsoft.Data.Sqlite;
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
    public void ManagedActivityRequiresObservedHostsAndMarksTheOriginalTransferReadOnly()
    {
        const string sample = """
            {"v":1,"id":"managed-connect-1","type":"browser-managed","operation":"connect","profile":"demo-profile",
             "files":[{"id":"c-demo-profile-7-abc","name":"leaf.zip","received":128,"length":256,
                       "start":1760000000000,"hosts":["WWW.EXAMPLE.COM","www.example.com"]}]}
            """;

        Assert.True(BridgeProtocol.TryParse(sample, out BridgeRequest request, out string error), error);
        BrowserActiveFile file = Assert.Single(request.Activity!.Files);
        Assert.True(file.Managed);
        Assert.False(file.CanResume);
        Assert.Equal(new[] { "www.example.com" }, file.Hosts);
        Assert.False(BridgeProtocol.TryParse(sample.Replace("\"hosts\":[\"WWW.EXAMPLE.COM\",\"www.example.com\"]", "\"hosts\":[]"),
            out _, out _));
        Assert.False(BridgeProtocol.TryParse(sample.Replace("www.example.com", "example.com/path"), out _, out _));
    }

    [Fact]
    public void ManagedCompletionRequiresAProfileBoundReceiptAndObservedHost()
    {
        const string sample = """
            {"v":1,"id":"managed-complete-1","type":"browser-managed","operation":"complete",
             "profile":"demo-profile","downloadId":"c-demo-profile-7-abc","path":"C:\\Users\\person\\Downloads\\VESPER\\leaf.zip",
             "length":256,"hosts":["example.com"]}
            """;

        Assert.True(BridgeProtocol.TryParse(sample, out BridgeRequest request, out string error), error);
        Assert.Equal("c-demo-profile-7-abc", request.CompletedFile!.DownloadId);
        Assert.Equal(new[] { "example.com" }, request.CompletedFile.Hosts);
        Assert.False(BridgeProtocol.TryParse(sample.Replace("c-demo-profile-7-abc", "c-someone-7-abc"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(sample.Replace("\"hosts\":[\"example.com\"]", "\"hosts\":[]"), out _, out _));
    }

    [Fact]
    public void RegisteringManagedChromeFileAddsAReadOnlyReferenceAndIsIdempotent()
    {
        using var harness = new Harness();
        string destination = Path.Combine(harness.Root, "chrome-custom-folder");
        Directory.CreateDirectory(destination);
        string path = Path.Combine(destination, "saved.zip");
        byte[] bytes = [1, 2, 3, 4, 5];
        File.WriteAllBytes(path, bytes);
        var file = new BrowserCompletedFile("demo-profile", "c-demo-profile-7-abc", path, bytes.Length, ["example.com"]);

        AddResult first = harness.Engine.RegisterBrowserDownload(file);
        AddResult duplicate = harness.Engine.RegisterBrowserDownload(file);

        Assert.True(first.Ok, first.Message);
        Assert.Equal(AddStatus.AlreadySubmitted, duplicate.Status);
        JobSnapshot saved = harness.Engine.Snapshot(first.JobId)!;
        Assert.Equal(JobKind.BrowserFile, saved.Kind);
        Assert.Equal(JobState.Completed, saved.State);
        Assert.Equal(path, saved.FinalPath);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(File.Exists(path));
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

    [Fact]
    public async Task A_pending_browser_publication_keeps_the_accepted_handoff()
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = Stage(harness, server, new BrowserTransferProof(server.V1.Length, "\"p1\"", null));
        await harness.WaitAsync(id, job => job.Reason == "browser-ready");

        string database = Path.Combine(harness.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER refuse_completion BEFORE UPDATE ON jobs WHEN NEW.state = 10
            BEGIN SELECT RAISE(ABORT, 'completion write refused'); END;
            """;
        command.ExecuteNonQuery();

        Assert.True(harness.Engine.AcceptBrowserTransfer(TransferId));
        JobSnapshot pending = await harness.WaitForStateAsync(id, JobState.NeedsAttention);
        Assert.Equal("publication-pending", pending.Reason);
        string file = Assert.Single(harness.Files(), path => !path.EndsWith(".vmeta", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(file));

        Assert.True(harness.Engine.AcceptBrowserTransfer(TransferId));
        harness.Engine.AbandonBrowserTransfer(TransferId);
        JobSnapshot handoff = Assert.IsType<JobSnapshot>(harness.Engine.BrowserTransferSnapshot(TransferId));
        Assert.Equal(JobState.Finalizing, handoff.State);
        Assert.False(handoff.IsUnacceptedBrowserCapture);
        using (var store = JobStore.Open(database))
        {
            DownloadJob saved = Assert.Single(store.LoadAll());
            Assert.Equal(JobState.NeedsAttention, saved.State);
            Assert.Equal(file, saved.PendingFinalPath);
            Assert.False(saved.BrowserProof!.AwaitingConfirmation);
        }

        command.CommandText = "DROP TRIGGER refuse_completion;";
        command.ExecuteNonQuery();
        harness.Engine.Resume(id);

        JobSnapshot completed = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.True(harness.Engine.AcceptBrowserTransfer(TransferId));
        Assert.Equal(file, completed.FinalPath);
        Assert.Equal(file, Assert.Single(harness.Files()));
        using var completedStore = JobStore.Open(database);
        Assert.Equal(Payloads.Sha256(server.V1), Assert.Single(completedStore.LoadAll()).PublishedSha256,
            ignoreCase: true);
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

    private const string ServerModified = "Tue, 02 Jan 2024 03:04:05 GMT";

    private static BrowserTransferProof DatedProof(long length, string modified = ServerModified) =>
        new(length, null, "application/octet-stream", LastModified: modified, Basis: "last-modified");

    [Fact]
    public async Task A_last_modified_proof_takes_over_a_file_from_a_server_without_etags()
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/lmonly"), RequestId: TransferId + ":0",
            BrowserProof: DatedProof(server.V1.Length)));
        Assert.True(added.Ok, added.Message);
        await harness.WaitAsync(added.JobId, job => job.Reason == "browser-ready");

        Assert.True(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(Payloads.Sha256(server.V1), Payloads.Sha256(File.ReadAllBytes(done.FinalPath!)));
    }

    [Theory]
    [InlineData("/file", ServerModified, 0)]                                // the server now sends an ETag
    [InlineData("/noetag", ServerModified, 0)]                              // the server sends no date at all
    [InlineData("/lmonly", "Mon, 01 Jan 2024 03:04:05 GMT", 0)]             // another modification time
    [InlineData("/lmonly", ServerModified, 1)]                              // another length
    public async Task A_last_modified_proof_returns_the_file_to_Chrome_when_the_server_answers_differently(
        string path, string modified, int extraLength)
    {
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        AddResult added = harness.Engine.Add(new AddRequest(server.Url(path), RequestId: TransferId + ":0",
            BrowserProof: DatedProof(server.V1.Length + extraLength, modified)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot refused = await harness.WaitForStateAsync(added.JobId, JobState.Cancelled);
        Assert.Equal("browser-fallback", refused.Reason);
        Assert.Equal(0, refused.CommittedBytes);
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
    }

    private static AddRequest FreshLink(FaultServer server, string path, string etag = "\"p1\"", long shorter = 0) =>
        new(server.ReopenExpired(path, "/fresh-entry"), RequestId: TransferId + ":0",
            BrowserProof: new BrowserTransferProof(server.V1.Length - shorter, etag, "application/octet-stream"));

    private static async Task<(Guid Id, long Saved)> WaitingForLinkAsync(Harness harness, FaultServer server, string path,
        string name = "expiring.bin")
    {
        Guid id = harness.Add(server.Url(path), name);
        JobSnapshot waiting = await harness.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(waiting.CommittedBytes > 0);
        return (id, waiting.CommittedBytes);
    }

    [Fact]
    public async Task A_fresh_browser_link_continues_the_waiting_download_from_its_saved_bytes()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        (Guid id, long saved) = await WaitingForLinkAsync(harness, server, "/expires-after-truncate");

        Assert.Equal(id, await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));
        // Offering changes nothing: Chrome has not been parked yet.
        Assert.Equal(JobState.NeedsLink, harness.Engine.Snapshot(id)!.State);
        Assert.Null(harness.Engine.BrowserTransferSnapshot(TransferId));

        Assert.True(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(id, harness.Engine.BrowserTransferSnapshot(TransferId)!.Id);
        Assert.Equal(Payloads.Sha256(server.V1), Payloads.Sha256(File.ReadAllBytes(done.FinalPath!)));
        Assert.Single(harness.Engine.Snapshots());
        Assert.Contains(server.RangeHeaders, header => header == $"/expires-after-truncate bytes={saved}-");
        Assert.True(await harness.Engine.AcceptBrowserTransferAsync(TransferId), "a repeated accept after a worker restart stays true");
    }

    [Theory]
    [InlineData("/expires-after-truncate", "\"p2\"", 0)]   // Chrome received another version
    [InlineData("/expires-after-truncate", "\"p1\"", 1)]   // Chrome received another size
    [InlineData("/expires-without-etag", "\"p1\"", 0)]     // the server no longer backs the validator Chrome reported
    public async Task A_browser_link_that_is_not_provably_the_same_file_is_not_adopted(string path, string etag, long shorter)
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        (Guid id, long saved) = await WaitingForLinkAsync(harness, server, path);

        Assert.Null(await harness.Engine.OfferFreshLinkAsync(FreshLink(server, path, etag, shorter)));

        JobSnapshot untouched = harness.Engine.Snapshot(id)!;
        Assert.Equal(JobState.NeedsLink, untouched.State);
        Assert.Equal(saved, untouched.CommittedBytes);
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
    }

    [Fact]
    public async Task Two_waiting_downloads_that_both_fit_are_too_ambiguous_to_adopt_a_link()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        // Same host, same size and the same strong ETag: either could be what Chrome is downloading again.
        (Guid first, long firstSaved) = await WaitingForLinkAsync(harness, server, "/expires-after-truncate");
        (Guid second, long secondSaved) = await WaitingForLinkAsync(harness, server, "/expires-without-etag", "expiring-two.bin");

        Assert.Null(await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));

        foreach ((Guid id, long saved) in new[] { (first, firstSaved), (second, secondSaved) })
        {
            JobSnapshot untouched = harness.Engine.Snapshot(id)!;
            Assert.Equal(JobState.NeedsLink, untouched.State);
            Assert.Equal(saved, untouched.CommittedBytes);
        }
    }

    [Fact]
    public async Task Abandoning_a_fresh_link_never_cancels_the_waiting_download_or_its_saved_bytes()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        (Guid id, long saved) = await WaitingForLinkAsync(harness, server, "/expires-after-truncate");

        // Chrome kept its download before it was parked: the offer simply lapses.
        Assert.Equal(id, await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));
        harness.Engine.AbandonBrowserTransfer(TransferId);
        Assert.Equal(JobState.NeedsLink, harness.Engine.Snapshot(id)!.State);
        Assert.Equal(saved, harness.Engine.Snapshot(id)!.CommittedBytes);
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));

        // Chrome took the file back after the link was adopted: the download pauses with everything it has.
        Assert.Equal(id, await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));
        Assert.True(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        harness.Engine.AbandonBrowserTransfer(TransferId);

        JobSnapshot after = await harness.WaitAsync(id, job => job.State is JobState.Paused or JobState.Completed);
        Assert.True(after.CommittedBytes >= saved);
        if (after.State == JobState.Paused) Assert.Null(harness.Engine.BrowserTransferSnapshot(TransferId));
    }

    [Fact]
    public async Task AbandonWhileAdoptionIsCommittingCannotStartTheSavedJob()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var beforeCommit = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var harness = new Harness(failpoint: name =>
        {
            if (name != "rescue.beforeCommit") return;
            beforeCommit.Set();
            if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Adoption test was not released.");
        });
        (Guid id, long saved) = await WaitingForLinkAsync(harness, server, "/expires-after-truncate");
        Assert.Equal(id, await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));
        var accept = Task.Run(() => harness.Engine.AcceptBrowserTransferAsync(TransferId));
        try
        {
            Assert.True(beforeCommit.Wait(TimeSpan.FromSeconds(10)));
            harness.Engine.AbandonBrowserTransfer(TransferId);
        }
        finally { proceed.Set(); }
        Assert.False(await accept);
        Assert.Equal(JobState.NeedsLink, harness.Engine.Snapshot(id)!.State);
        Assert.Equal(saved, harness.Engine.Snapshot(id)!.CommittedBytes);
        Assert.Null(harness.Engine.BrowserTransferSnapshot(TransferId));
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
    }

    [Fact]
    public async Task AdoptedReleaseSurvivesRestartAndRejectsLateAccept()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        (Guid id, long saved) = await WaitingForLinkAsync(harness, server, "/expires-after-truncate");
        harness.Engine.Options.MaxConcurrentJobs = 0;
        Assert.Equal(id, await harness.Engine.OfferFreshLinkAsync(FreshLink(server, "/expires-after-truncate")));
        Assert.True(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        harness.Engine.AbandonBrowserTransfer(TransferId);
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        Assert.Null(harness.Engine.BrowserTransferSnapshot(TransferId));
        harness.Restart();
        Assert.Equal(JobState.Paused, harness.Engine.Snapshot(id)!.State);
        Assert.Equal(saved, harness.Engine.Snapshot(id)!.CommittedBytes);
        Assert.False(await harness.Engine.AcceptBrowserTransferAsync(TransferId));
        Assert.Null(harness.Engine.BrowserTransferSnapshot(TransferId));
    }

    private static Guid Stage(Harness harness, FaultServer server, BrowserTransferProof proof)
    {
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/file"), RequestId: TransferId + ":0", BrowserProof: proof));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }

    [Fact]
    public async Task AcceptedMediaSubmissionReceiptSurvivesEngineRestart()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        const string receipt = "fixture-media-request:0";
        var added = h.Engine.Add(new AddRequest(server.Url("/file"),StartNow:false,RequestId:receipt));
        Assert.True(added.Ok,added.Message);
        Assert.Equal(added.JobId,h.Engine.FindAcceptedSubmission(receipt));
        Assert.Null(h.Engine.FindAcceptedSubmission("not-accepted"));
        h.Restart();
        Assert.Equal(added.JobId,h.Engine.FindAcceptedSubmission(receipt));
        Assert.Single(h.Engine.Snapshots());
    }
}
