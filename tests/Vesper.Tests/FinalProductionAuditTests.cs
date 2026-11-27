using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

public sealed class FinalProductionAuditTests
{
    [Theory]
    [InlineData(JobKind.Http)]
    [InlineData(JobKind.Hls)]
    [InlineData(JobKind.Dash)]
    [InlineData(JobKind.BrowserStream)]
    public async Task Finalization_never_pads_missing_committed_bytes_with_zeroes(JobKind kind)
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        string partial = Path.Combine(harness.Data, "short.vpart");
        string? identity;
        using (var file = PartialFile.Open(partial, FileMode.CreateNew))
        {
            await file.WriteAsync(new byte[64], 0, default);
            file.Flush();
            identity = file.CaptureIdentity();
        }
        var extents = new ExtentSet();
        extents.Add(new Vesper.Engine.ByteRange(0, 128));
        var job = new DownloadJob
        {
            Kind = kind, SourceUrl = "https://example.com/file.bin", DestinationFolder = harness.Data,
            PartialPath = partial, PartialIdentity = identity, FileName = "file.bin",
            TotalBytes = 128, Extents = extents
        };
        using var session = new JobSession(harness.Engine, job);

        RunOutcome result = await harness.Engine.FinalizeAsync(session, default);

        Assert.Equal(JobState.NeedsAttention, result.State);
        Assert.Equal("incomplete", result.Reason);
        Assert.Equal(64, session.OpenFile(create: false).Length);
        Assert.Null(job.FinalPath);
    }

    [Theory]
    [InlineData("W/\"revision\"", IdentityLevel.None)]
    [InlineData(null, IdentityLevel.LastModified)]
    [InlineData("\"revision\"", IdentityLevel.StrongEtag)]
    public async Task Weak_etag_does_not_authorize_date_based_resume(string? etag, IdentityLevel expected)
    {
        byte[] data = Payloads.Make(4096, 73);
        await using var server = await StartServerAsync(async context =>
        {
            context.Response.ContentLength = data.Length;
            context.Response.Headers.LastModified = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R");
            if (etag is not null) context.Response.Headers.ETag = etag;
            await context.Response.Body.WriteAsync(data);
        });
        using var harness = new Harness();
        Guid id = harness.Add(server.Urls.First() + "/file", "file.bin");
        JobSnapshot completed = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(expected, completed.Identity);
        Assert.Equal(data, await File.ReadAllBytesAsync(completed.FinalPath!));
    }

    [Theory]
    [InlineData(false, "file.bin")]
    [InlineData(false, "file.zip")]
    [InlineData(true, "file.bin")]
    [InlineData(true, "file.zip")]
    public async Task Encoded_response_is_decoded_fully_or_rejected(bool stacked, string name)
    {
        byte[] original = Payloads.Make(4096, 92);
        byte[] encoded = Gzip(original);
        if (stacked) encoded = Gzip(encoded);
        await using var server = await StartServerAsync(async context =>
        {
            context.Response.ContentLength = encoded.Length;
            context.Response.Headers.ContentEncoding = stacked ? "gzip, gzip" : "gzip";
            await context.Response.Body.WriteAsync(encoded);
        });
        using var harness = new Harness();
        Guid id = harness.Add(server.Urls.First() + "/file", name);
        JobSnapshot result = await harness.WaitAsync(id,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        if (stacked)
        {
            Assert.Equal(JobState.NeedsAttention, result.State);
            Assert.Equal("encoding", result.Reason);
            Assert.Null(result.FinalPath);
        }
        else
        {
            Assert.Equal(JobState.Completed, result.State);
            Assert.Equal(original, await File.ReadAllBytesAsync(result.FinalPath!));
        }
    }

    [Fact]
    public async Task Replacement_before_first_checkpoint_keeps_the_supplied_checksum()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = harness.Add("https://example.com/old", "file.bin", startNow: false);
        string expected = new('A', 64);
        RescueResult result = await harness.Engine.ReplaceLinkAsync(id, "https://example.com/new",
            new RescueInput(Sha256: expected));
        Assert.True(result.Accepted, result.Message);
        Assert.True(harness.Engine.Snapshot(id)!.HasExpectedHash);
        using JobStore store = JobStore.Open(Path.Combine(harness.Data, "vesper.db"));
        Assert.Equal(expected.ToLowerInvariant(), Assert.Single(store.LoadAll()).ExpectedSha256);
    }

    [Fact]
    public async Task Replacement_before_first_checkpoint_keeps_the_supplied_piece_map()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = harness.Add("https://example.com/old", "file.bin", startNow: false);
        var map = new PieceMap(16 * 1024, 16 * 1024, [new string('a', 64)]);
        RescueResult result = await harness.Engine.ReplaceLinkAsync(id, "https://example.com/new",
            new RescueInput(PieceMapJson: map.ToJson()));
        Assert.True(result.Accepted, result.Message);
        Assert.True(harness.Engine.Snapshot(id)!.HasPieceMap);
        Assert.Equal(map.TotalLength, harness.Engine.Snapshot(id)!.TotalBytes);
    }

    [Fact]
    public async Task R0_replacement_keeps_a_new_trusted_checksum()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness(tweak: options => options.MaxConnectionsPerJob = 1);
        Guid id = harness.Add(server.Url("/slow"), "file.bin");
        await harness.WaitAsync(id, job => job.ProgressBytes > 64 * 1024);
        harness.Engine.Pause(id);
        await harness.WaitForStateAsync(id, JobState.Paused);
        harness.Engine.Options.MaxConcurrentJobs = 0;
        RescueResult result = await harness.Engine.ReplaceLinkAsync(id, server.Url("/slow"),
            new RescueInput(Sha256: Payloads.Sha256(server.V1)));
        Assert.True(result.Accepted, result.Message);
        Assert.Equal(RescueMode.R0, result.Mode);
        Assert.True(harness.Engine.Snapshot(id)!.HasExpectedHash);
    }

    [Fact]
    public async Task Unfinished_tagging_job_cannot_acquire_a_source_checksum_later()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        AddResult added = harness.Engine.Add(new AddRequest("https://example.com/audio.mp3",
            FileName: "audio.mp3", MediaProvider: "fixture", MediaMetadataJson: "{\"Title\":\"Track\"}"));
        Assert.True(added.Ok, added.Message);
        Assert.False(harness.Engine.SetExpectedSha256(added.JobId, new string('a', 64)));
        RescueResult result = await harness.Engine.ReplaceLinkAsync(added.JobId, "https://example.com/new.mp3",
            new RescueInput(Sha256: new string('a', 64)));
        Assert.False(result.Accepted);
        Assert.False(harness.Engine.Snapshot(added.JobId)!.HasExpectedHash);
    }

    [Fact]
    public async Task Gzip_file_is_not_unpacked_under_its_archive_name()
    {
        byte[] archive = Gzip(Payloads.Make(4096, 38));
        await using var server = await StartServerAsync(async context =>
        {
            context.Response.ContentLength = archive.Length;
            context.Response.Headers.ContentEncoding = "gzip";
            await context.Response.Body.WriteAsync(archive);
        });
        using var harness = new Harness();
        Guid id = harness.Add(server.Urls.First() + "/archive", "archive.tgz");
        JobSnapshot result = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(archive, await File.ReadAllBytesAsync(result.FinalPath!));
    }

    [Fact]
    public async Task Hls_probe_resolves_variants_against_the_final_playlist_url()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        server.Redirects["/entry.m3u8"] = server.Url("/hls/relocated/master.m3u8");
        server.HlsPlaylists["/hls/relocated/master.m3u8"] =
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1280x720\nvideo.m3u8\n";
        using var harness = new Harness();
        MediaProbeResult result = await harness.Engine.ProbeMediaAsync(server.Url("/entry.m3u8"));
        Assert.Equal("hls-master", result.Kind);
        Assert.Equal(server.Url("/hls/relocated/video.m3u8"), Assert.Single(result.Variants).Url);
    }

    [Fact]
    public async Task Discovery_honors_retry_after_longer_than_one_hour()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset? retryAt = null;
        var options = new ResolverOptions { OriginThrottled = (_, until) => retryAt = until };
        using var http = new SecureHttpClient(options, new ThrottledHandler());
        await Assert.ThrowsAsync<MediaResolverException>(() =>
            http.GetStringAsync(new Uri("https://example.com/media"), null, CancellationToken.None));
        Assert.NotNull(retryAt);
        Assert.True(retryAt >= before.AddHours(2), $"Server requested two hours; recorded {retryAt:O}.");
    }

    [Fact]
    public async Task Rotated_query_requires_independent_evidence()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = harness.Add(server.Url("/expires-after-truncate"), "file.bin");
        JobSnapshot waiting = await harness.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(waiting.CommittedBytes > 0);
        byte[] saved = await File.ReadAllBytesAsync(waiting.PartialPath);
        string fresh = server.Url("/expires-after-truncate?token=fresh");
        RescueResult rejected = await harness.Engine.ReplaceLinkAsync(id, fresh);
        Assert.False(rejected.Accepted);
        Assert.Equal(saved, await File.ReadAllBytesAsync(waiting.PartialPath));
        RescueResult verified = await harness.Engine.ReplaceLinkAsync(id, fresh,
            new RescueInput(Sha256: Payloads.Sha256(server.V1)));
        Assert.True(verified.Accepted, verified.Message);
        Assert.Equal(RescueMode.R1, verified.Mode);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Refresh_cannot_claim_an_existing_downloads_source()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = harness.Add("https://example.com/first", "first.bin", startNow: false);
        Guid second = harness.Add("https://example.com/second", "second.bin", startNow: false);
        RescueResult result = await harness.Engine.ReplaceLinkAsync(first, "https://example.com/second");
        Assert.False(result.Accepted);
        Assert.Equal("https://example.com/first", harness.Engine.Snapshot(first)!.SourceUrl);
        Assert.Equal("https://example.com/second", harness.Engine.Snapshot(second)!.SourceUrl);
    }

    private static async Task<WebApplication> StartServerAsync(RequestDelegate handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(input);
        return output.ToArray();
    }

    private sealed class ThrottledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        }
    }
}
