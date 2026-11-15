using System.Net;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>Regression tests for the networking and integrity fixes: Happy Eyeballs, proxy refusal,
/// unknown-length verification, self-compressed files and recovery-manifest import.</summary>
public sealed class EngineReliabilityTests
{
    // ---- VES-010: a dead first address must not eat the whole connect budget -------------------------------

    [Fact]
    public async Task A_dead_first_address_falls_through_to_a_working_one()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        // 100::/64 is discard-only: nothing answers there. Loopback is the working address.
        var options = new EngineOptions
        {
            DataDirectory = Path.GetTempPath(),
            AllowPrivateNetworks = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            HeaderTimeout = TimeSpan.FromSeconds(5),
            ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Parse("100::1"), IPAddress.Loopback })
        };
        using var transport = new HttpTransport(options);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await transport.SendAsync(new Uri(server.Url("/file")), new RequestSpec(), default);
        watch.Stop();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
            $"Took {watch.Elapsed}; a black-holed first address should not consume the connect budget.");
    }

    [Fact]
    public async Task Only_blocked_addresses_are_refused_before_connecting()
    {
        var options = new EngineOptions
        {
            DataDirectory = Path.GetTempPath(),
            AllowPrivateNetworks = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Parse("64:ff9b::7f00:1") })   // NAT64 -> 127.0.0.1
        };
        using var transport = new HttpTransport(options);
        // HttpClient wraps whatever the connect callback throws.
        var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            transport.SendAsync(new Uri("http://example.test/file"), new RequestSpec(), default));
        Assert.IsType<BlockedAddressException>(ex.InnerException);
    }

    // ---- VES-011: an invalid custom proxy is a settings error, never a silent fallback --------------------

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://proxy.example.test:21")]          // parses, but is not an HTTP proxy
    [InlineData("")]
    public async Task An_invalid_custom_proxy_is_rejected_before_anything_is_sent(string address)
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        var options = new EngineOptions
        {
            DataDirectory = Path.GetTempPath(),
            Proxy = ProxyMode.Custom,
            ProxyAddress = address,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        using var transport = new HttpTransport(options);
        await Assert.ThrowsAsync<ProxyConfigurationException>(() =>
            transport.SendAsync(new Uri(server.Url("/file")), new RequestSpec(), default));
        Assert.Equal(0, server.Hits("/file"));            // nothing was sent anywhere
    }

    // ---- VES-013: a body with no stated length is confirmed before it is published ------------------------

    [Fact]
    public async Task A_body_with_no_stated_length_is_confirmed_and_published_complete()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/unknown-length"), FileName: "file.bin"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("", done.Reason);                     // the probe confirmed the length
        Assert.Equal(server.V1, File.ReadAllBytes(done.FinalPath!));
        Assert.True(server.RangeHeaders.Any(r => r.Contains("bytes=")), "the confirmation probe was sent");
    }

    // ---- VES-014: a file that is already compressed is stored as-is --------------------------------------

    [Fact]
    public async Task A_gzipped_file_served_with_content_encoding_is_saved_undecoded()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/gzip"), FileName: "archive.tar.gz"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        byte[] saved = File.ReadAllBytes(done.FinalPath!);
        Assert.Equal(0x1F, saved[0]);                       // gzip magic: the file itself, not the decoded tar
        Assert.Equal(0x8B, saved[1]);
        Assert.Equal("archive.tar.gz", done.FileName);
    }

    // ---- VES-017: a planted recovery manifest cannot redirect the download --------------------------------

    [Fact]
    public void A_manifest_naming_another_folder_is_refused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-manifest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string partial = Path.Combine(folder, "file.bin.vpart");
            File.WriteAllBytes(partial, [1, 2, 3]);
            string manifest = partial + ".vmeta";
            var body = new
            {
                V = 1, JobId = Guid.NewGuid(), Kind = 0, Url = "https://example.test/file",
                Destination = Path.Combine(Path.GetTempPath(), "somewhere-else"), FileName = "evil.exe",
                Total = 3L, Extents = "0-3", ETag = (string?)null, Identity = 0, Generation = 1L,
                LastModified = (string?)null, MediaThumbnailUrl = (string?)null, MediaMetadataJson = (string?)null
            };
            string bodyJson = System.Text.Json.JsonSerializer.Serialize(body);
            string check = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(bodyJson)));
            File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new { Body = bodyJson, Check = check }));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Contains("different folder", problem);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

/// <summary>VES-009: an expired merge job is re-planned for exactly the formats it started with.</summary>
public sealed class MediaSourceRescueTests
{
    private static Vesper.MediaResolver.Models.MediaRepresentation Rep(string id, bool video, string container, string codec) => new()
    {
        Id = id, Url = new Uri($"https://cdn.example.com/{id}"), Protocol = Vesper.MediaResolver.Models.StreamProtocol.Http,
        Content = video ? Vesper.MediaResolver.Models.StreamContent.VideoOnly : Vesper.MediaResolver.Models.StreamContent.AudioOnly,
        Container = container, VideoCodec = video ? codec : null, AudioCodec = video ? null : codec,
        FileSize = video ? 5_000_000 : 300_000, ContentIdentity = id + "-identity", Height = video ? 1080 : null
    };

    private static Vesper.MediaResolver.Models.MediaItem Item(params Vesper.MediaResolver.Models.MediaRepresentation[] reps) => new()
    {
        SourceUrl = new Uri("https://www.youtube.com/watch?v=fixture"), Provider = "youtube", MediaId = "fixture",
        Title = "Fixture", Representations = reps
    };

    [Fact]
    public void Can_refresh_only_media_jobs_that_know_their_page_and_formats()
    {
        Assert.True(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot
        { Kind = JobKind.Merge, OriginalPageUrl = "https://www.youtube.com/watch?v=a", MediaId = "a", MediaFormatId = "137+140" }));
        Assert.False(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Merge, OriginalPageUrl = "https://x/", MediaId = "a" }));
        Assert.False(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Hls, OriginalPageUrl = "https://x/", MediaId = "a", MediaFormatId = "1" }));
    }

    [Fact]
    public void An_automatic_attempt_happens_once_per_hour_and_only_for_expired_merge_jobs()
    {
        var expired = new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Merge, Reason = "expired",
            OriginalPageUrl = "https://www.youtube.com/watch?v=b", MediaId = "b", MediaFormatId = "251" };
        Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(expired));
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(expired));                       // within the hour
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Merge, Reason = "auth",
            OriginalPageUrl = expired.OriginalPageUrl, MediaId = "b", MediaFormatId = "251" }));
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Http, Reason = "expired",
            OriginalPageUrl = expired.OriginalPageUrl, MediaId = "b", MediaFormatId = "251" }));
    }

    [Fact]
    public async Task A_refreshed_merge_plan_continues_the_saved_tracks_when_the_formats_still_match()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        using var h = new Harness(tweak: o => o.Schedule = new SchedulePolicy(true, now.AddHours(1), now.AddHours(2)));   // nothing starts
        var video = Rep("137", true, "mp4", "h264");
        var audio = Rep("140", false, "m4a", "aac");
        (VESPER.MediaDownloadChoice? first, string? error) = VESPER.MediaTransferBridge.Prepare(Item(video, audio),
            Vesper.MediaResolver.Formats.FormatPreferences.BestQuality);
        Assert.NotNull(first);
        Assert.Equal(JobKind.Merge, first!.Kind);
        AddResult added = h.Engine.Add(new AddRequest(first.Url, FileName: first.FileName, StartNow: false, Kind: JobKind.Merge,
            MediaProvider: "youtube", MediaId: "fixture", MediaFormatId: first.FormatId, MediaPlanJson: first.MediaPlanJson,
            OriginalPageUrl: "https://www.youtube.com/watch?v=fixture"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot job = h.Engine.Snapshot(added.JobId)!;
        Assert.Equal("137+140", job.MediaFormatId);

        // Same formats, new signed links: accepted, the plan now points at the new URLs.
        var fresh = Item(video with { Url = new Uri("https://cdn.example.com/137?sig=new") }, audio with { Url = new Uri("https://cdn.example.com/140?sig=new") });
        RescueResult accepted = InvokeRefreshMerge(h.Engine, job, fresh);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Contains("sig=new", h.Engine.Snapshot(added.JobId)!.SourceUrl);

        // A format that disappeared: refused, nothing changes.
        RescueResult missing = InvokeRefreshMerge(h.Engine, h.Engine.Snapshot(added.JobId)!, Item(video));
        Assert.False(missing.Accepted);
        Assert.Contains("same formats", missing.Message);

        // Same ids but a different file size: the engine's identity check refuses it.
        RescueResult changed = InvokeRefreshMerge(h.Engine, h.Engine.Snapshot(added.JobId)!, Item(video with { FileSize = 6_000_000 }, audio));
        Assert.False(changed.Accepted);
        await Task.CompletedTask;
    }

    private static RescueResult InvokeRefreshMerge(DownloadEngine engine, JobSnapshot job, Vesper.MediaResolver.Models.MediaItem item)
    {
        var method = typeof(VESPER.MediaSourceRescue).GetMethod("RefreshMerge",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (RescueResult)method.Invoke(null, [engine, job, item])!;
    }
}

/// <summary>VES-023: the bridge accepts an optional time range on a media selection and rejects a bad one.</summary>
public sealed class BridgeClipRangeTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_valid_range_is_parsed()
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2,\"range\":{{\"start\":72,\"end\":102.5}}}}";
        Assert.True(BridgeProtocol.TryParse(json, out BridgeRequest request, out string error), error);
        Assert.NotNull(request.Clip);
        Assert.Equal(72, request.Clip!.StartSeconds);
        Assert.Equal(102.5, request.Clip.EndSeconds);
    }

    [Theory]
    [InlineData("{\"start\":10,\"end\":5}")]
    [InlineData("{\"start\":-1,\"end\":5}")]
    [InlineData("{\"start\":0,\"end\":31536001}")]
    [InlineData("{\"start\":\"0\",\"end\":5}")]
    [InlineData("[0,5]")]
    public void A_bad_range_is_refused(string range)
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2,\"range\":{range}}}";
        Assert.False(BridgeProtocol.TryParse(json, out _, out string error));
        Assert.Equal("Invalid time range.", error);
    }

    [Fact]
    public void No_range_means_the_whole_file()
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2}}";
        Assert.True(BridgeProtocol.TryParse(json, out BridgeRequest request, out _));
        Assert.Null(request.Clip);
    }
}
