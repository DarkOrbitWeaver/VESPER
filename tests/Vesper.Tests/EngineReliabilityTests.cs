using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>Regression tests for the networking and integrity fixes: Happy Eyeballs, proxy refusal,
/// unknown-length verification, self-compressed files and recovery-manifest import.</summary>
public sealed class EngineReliabilityTests
{
    [Fact]
    public async Task Cancel_refuses_a_same_content_replacement_of_the_merge_tracks_sidecar()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = CancellationMergePlan();
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot snapshot = h.Engine.Snapshot(added.JobId)!;
        byte[] tracksBytes = [1, 3, 5, 7, 9, 11];
        await File.WriteAllBytesAsync(snapshot.PartialPath, tracksBytes);
        h.OwnFixturePartial(added.JobId);
        DownloadJob job = LiveJob(h, added.JobId);
        var cursor = new MergeTransfer.Cursor(1, plan.Fingerprint, "swapping",
            MuxLength: 6, MuxSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            TracksLength: tracksBytes.Length, TracksSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            MuxIdentity: new string('A', 64));
        h.Engine.UpdateJob(job, current => current.MediaState = JsonSerializer.Serialize(cursor));

        string tracks = MergeTransfer.TracksPath(job);
        File.Move(snapshot.PartialPath, tracks);
        string original = tracks + ".original";
        File.Move(tracks, original);
        await File.WriteAllBytesAsync(tracks, tracksBytes);

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.False(result.Cancelled);
        Assert.Equal(tracksBytes, await File.ReadAllBytesAsync(tracks));
        Assert.Equal(tracksBytes, await File.ReadAllBytesAsync(original));
        JobSnapshot parked = h.Engine.Snapshot(added.JobId)!;
        Assert.Equal(JobState.Cancelled, parked.State);
        Assert.Equal("cancel-cleanup", parked.Reason);
        Assert.True(parked.IsCleanupPending);
    }

    [Theory]
    [InlineData("swapping", false)]
    [InlineData("muxed", true)]
    public async Task Cancel_deletes_tracks_and_mux_partial_with_saved_cursor_identities(string phase, bool muxIdentityCommitted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = CancellationMergePlan();
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot snapshot = h.Engine.Snapshot(added.JobId)!;
        byte[] tracksBytes = [1, 3, 5, 7, 9, 11];
        byte[] muxBytes = [2, 4, 6, 8, 10, 12];
        await File.WriteAllBytesAsync(snapshot.PartialPath, tracksBytes);
        h.OwnFixturePartial(added.JobId);
        string tracksIdentity = LiveJob(h, added.JobId).PartialIdentity!;
        string tracks = MergeTransfer.TracksPath(LiveJob(h, added.JobId));
        File.Move(snapshot.PartialPath, tracks);
        await File.WriteAllBytesAsync(snapshot.PartialPath, muxBytes);
        h.OwnFixturePartial(added.JobId);

        DownloadJob job = LiveJob(h, added.JobId);
        string muxIdentity = job.PartialIdentity!;
        if (!muxIdentityCommitted)
        {
            h.Engine.UpdateJob(job, current => current.PartialIdentity = tracksIdentity);
            job = LiveJob(h, added.JobId);
        }
        var cursor = new MergeTransfer.Cursor(1, plan.Fingerprint, phase,
            MuxLength: muxBytes.Length, MuxSha256: Convert.ToHexString(SHA256.HashData(muxBytes)),
            TracksLength: tracksBytes.Length, TracksSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            MuxIdentity: muxIdentity, TracksIdentity: tracksIdentity);
        h.Engine.UpdateJob(job, current => current.MediaState = JsonSerializer.Serialize(cursor));

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.True(result.Cancelled, result.Message);
        Assert.Null(h.Engine.Snapshot(added.JobId));
        Assert.False(File.Exists(snapshot.PartialPath));
        Assert.False(File.Exists(tracks));
    }

    [Fact]
    public async Task Cancel_keeps_tracks_when_a_legacy_muxed_cursor_has_no_tracks_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = CancellationMergePlan();
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot snapshot = h.Engine.Snapshot(added.JobId)!;
        byte[] tracksBytes = [1, 3, 5, 7, 9, 11];
        byte[] muxBytes = [2, 4, 6, 8, 10, 12];
        await File.WriteAllBytesAsync(snapshot.PartialPath, tracksBytes);
        h.OwnFixturePartial(added.JobId);
        string tracks = MergeTransfer.TracksPath(LiveJob(h, added.JobId));
        File.Move(snapshot.PartialPath, tracks);
        await File.WriteAllBytesAsync(snapshot.PartialPath, muxBytes);
        h.OwnFixturePartial(added.JobId);

        DownloadJob job = LiveJob(h, added.JobId);
        var legacyCursor = new MergeTransfer.Cursor(1, plan.Fingerprint, "muxed",
            MuxLength: muxBytes.Length, MuxSha256: Convert.ToHexString(SHA256.HashData(muxBytes)),
            TracksLength: tracksBytes.Length, TracksSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            MuxIdentity: job.PartialIdentity);
        h.Engine.UpdateJob(job, current => current.MediaState = JsonSerializer.Serialize(legacyCursor));

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.False(result.Cancelled);
        Assert.Equal(tracksBytes, await File.ReadAllBytesAsync(tracks));
        Assert.Equal(muxBytes, await File.ReadAllBytesAsync(snapshot.PartialPath));
        JobSnapshot parked = h.Engine.Snapshot(added.JobId)!;
        Assert.Equal("cancel-cleanup", parked.Reason);
        Assert.True(parked.IsCleanupPending);
    }

    [Fact]
    public async Task Cancel_deletes_a_pre_swap_manifest_after_muxed_commit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = CancellationMergePlan();
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot snapshot = h.Engine.Snapshot(added.JobId)!;
        byte[] tracksBytes = [1, 3, 5, 7, 9, 11];
        byte[] muxBytes = [2, 4, 6, 8, 10, 12];
        await File.WriteAllBytesAsync(snapshot.PartialPath, tracksBytes);
        h.OwnFixturePartial(added.JobId);
        DownloadJob job = LiveJob(h, added.JobId);
        string tracksIdentity = job.PartialIdentity!;
        RecoveryManifest.Write(job);
        string manifest = RecoveryManifest.PathFor(snapshot.PartialPath);

        string tracks = MergeTransfer.TracksPath(job);
        File.Move(snapshot.PartialPath, tracks);
        await File.WriteAllBytesAsync(snapshot.PartialPath, muxBytes);
        h.OwnFixturePartial(added.JobId);
        job = LiveJob(h, added.JobId);
        var cursor = new MergeTransfer.Cursor(1, plan.Fingerprint, "muxed",
            MuxLength: muxBytes.Length, MuxSha256: Convert.ToHexString(SHA256.HashData(muxBytes)),
            TracksLength: tracksBytes.Length, TracksSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            MuxIdentity: job.PartialIdentity, TracksIdentity: tracksIdentity);
        var extents = new ExtentSet();
        extents.Add(new ByteRange(0, muxBytes.Length));
        h.Engine.CommitCheckpoint(job, extents, JsonSerializer.Serialize(cursor),
            totalBytes: muxBytes.Length, partialIdentity: job.PartialIdentity);

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.True(result.Cancelled, result.Message);
        Assert.Null(h.Engine.Snapshot(added.JobId));
        Assert.False(File.Exists(snapshot.PartialPath));
        Assert.False(File.Exists(tracks));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task Cancel_retry_accepts_the_saved_mux_identity_after_tracks_were_removed()
    {
        if (!OperatingSystem.IsWindows()) return;
        int failedPartialDelete = 0;
        using var h = new Harness(point =>
        {
            if (point == "cancel.beforePartialDelete" && Interlocked.Exchange(ref failedPartialDelete, 1) == 0)
                throw new IOException("Simulated partial delete refusal.");
        }, options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = CancellationMergePlan();
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merged.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot snapshot = h.Engine.Snapshot(added.JobId)!;
        byte[] tracksBytes = [1, 3, 5, 7, 9, 11];
        byte[] muxBytes = [2, 4, 6, 8, 10, 12];
        await File.WriteAllBytesAsync(snapshot.PartialPath, tracksBytes);
        h.OwnFixturePartial(added.JobId);
        DownloadJob job = LiveJob(h, added.JobId);
        string tracksIdentity = job.PartialIdentity!;
        string tracks = MergeTransfer.TracksPath(job);
        File.Move(snapshot.PartialPath, tracks);
        await File.WriteAllBytesAsync(snapshot.PartialPath, muxBytes);
        h.OwnFixturePartial(added.JobId);
        string muxIdentity = LiveJob(h, added.JobId).PartialIdentity!;

        var cursor = new MergeTransfer.Cursor(1, plan.Fingerprint, "swapping",
            MuxLength: muxBytes.Length, MuxSha256: Convert.ToHexString(SHA256.HashData(muxBytes)),
            TracksLength: tracksBytes.Length, TracksSha256: Convert.ToHexString(SHA256.HashData(tracksBytes)),
            MuxIdentity: muxIdentity, TracksIdentity: tracksIdentity);
        job = LiveJob(h, added.JobId);
        h.Engine.UpdateJob(job, current =>
        {
            current.PartialIdentity = tracksIdentity;
            current.MediaState = JsonSerializer.Serialize(cursor);
        });

        CancelDownloadResult refused = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.False(refused.Cancelled);
        Assert.False(File.Exists(tracks));
        Assert.Equal(muxBytes, await File.ReadAllBytesAsync(snapshot.PartialPath));
        Assert.True(h.Engine.Snapshot(added.JobId)!.IsCleanupPending);

        CancelDownloadResult retried = await h.Engine.CancelDownloadAsync(added.JobId);

        Assert.True(retried.Cancelled, retried.Message);
        Assert.Null(h.Engine.Snapshot(added.JobId));
        Assert.False(File.Exists(snapshot.PartialPath));
    }

    [Theory]
    [InlineData(".vmeta.tmp")]
    [InlineData(".tracks")]
    [InlineData(".merging")]
    [InlineData(".samples")]
    [InlineData(".convert.mp4")]
    [InlineData(".tagged.flac")]
    public async Task Cancel_keeps_partial_and_unproved_staging_files(string suffix)
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/unowned-stage", "stage.bin", startNow: false);
        JobSnapshot snapshot = h.Engine.Snapshot(id)!;
        byte[] bytes = [2, 4, 6, 8];
        await File.WriteAllBytesAsync(snapshot.PartialPath, bytes);
        h.OwnFixturePartial(id);
        string stage = snapshot.PartialPath + suffix;
        await File.WriteAllBytesAsync(stage, bytes);

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);

        Assert.False(result.Cancelled);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.PartialPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stage));
        JobSnapshot parked = h.Engine.Snapshot(id)!;
        Assert.Equal(JobState.Cancelled, parked.State);
        Assert.Equal("cancel-cleanup", parked.Reason);
        Assert.True(parked.IsCleanupPending);
    }

    [Fact]
    public async Task Cancel_keeps_an_unproved_conversion_rollback_backup()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/rollback-stage", "rollback.bin", startNow: false);
        JobSnapshot snapshot = h.Engine.Snapshot(id)!;
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(snapshot.PartialPath, bytes);
        h.OwnFixturePartial(id);
        string backup = Path.Combine(Path.GetDirectoryName(snapshot.PartialPath)!, $".vesper-rollback-{id:N}-{new string('B', 64)}");
        await File.WriteAllBytesAsync(backup, bytes);

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);

        Assert.False(result.Cancelled);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.PartialPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
        JobSnapshot parked = h.Engine.Snapshot(id)!;
        Assert.Equal(JobState.Cancelled, parked.State);
        Assert.Equal("cancel-cleanup", parked.Reason);
        Assert.True(parked.IsCleanupPending);
    }

    private static MediaMergePlan CancellationMergePlan() => new()
    {
        OutputContainer = "mp4",
        RequireVideo = true,
        Tracks =
        [
            new MergeTrack("video", "video-1", "https://media.example.test/video.mp4", 6, "mp4", "h264"),
            new MergeTrack("audio", "audio-1", "https://media.example.test/audio.m4a", 6, "m4a", "aac")
        ]
    };

    private static DownloadJob LiveJob(Harness h, Guid id) =>
        ((Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Engine)!)[id];

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
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1),
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
        Assert.Contains(server.RangeHeaders, r => r.Contains($"bytes={server.V1.Length}-{server.V1.Length}"));
    }

    [Fact]
    public async Task A_short_body_proven_by_the_range_probe_is_never_published()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/unknown-length-short"), FileName: "short.bin"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot result = await h.WaitAsync(added.JobId, job =>
            job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.NeedsAttention, result.State);
        Assert.Equal("truncated", result.Reason);
        Assert.Null(result.FinalPath);
        Assert.False(File.Exists(Path.Combine(h.Downloads, "short.bin")));
        Assert.Contains(server.RangeHeaders, r => r.Contains($"bytes={server.V1.Length / 2}-{server.V1.Length / 2}"));
    }

    [Fact]
    public async Task A_body_without_a_verifiable_length_is_kept_unpublished()
    {
        await using var server = new CloseDelimitedServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/close-delimited"), FileName: "chunked.bin"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot result = await h.WaitAsync(added.JobId, job =>
            job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.NeedsAttention, result.State);
        Assert.Equal("unverified-length", result.Reason);
        Assert.Null(result.FinalPath);
        Assert.False(File.Exists(Path.Combine(h.Downloads, "chunked.bin")));
        Assert.Equal(server.Body, File.ReadAllBytes(result.PartialPath));
    }

    [Theory]
    [InlineData("/html-attachment")]
    [InlineData("/mislabeled-html")]
    public async Task An_html_error_page_is_not_published_as_an_archive(string path)
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url(path), FileName: "export.zip"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot result = await h.WaitAsync(added.JobId, job =>
            job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.NeedsAttention, result.State);
        Assert.Equal("html", result.Reason);
        Assert.Null(result.FinalPath);
        Assert.False(File.Exists(Path.Combine(h.Downloads, "export.zip")));
    }

    [Fact]
    public async Task A_requested_html_document_remains_downloadable()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/html"), FileName: "page.html"));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Contains("Please sign in", await File.ReadAllTextAsync(done.FinalPath!));
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
    private static Vesper.MediaResolver.Models.MediaRepresentation Rep(string id, bool video, string container, string codec,
        Uri? url = null, Vesper.MediaResolver.Models.MediaRequestContext? context = null) => new()
    {
        Id = id, Url = url ?? new Uri($"https://cdn.example.com/{id}"), Protocol = Vesper.MediaResolver.Models.StreamProtocol.Http,
        Content = video ? Vesper.MediaResolver.Models.StreamContent.VideoOnly : Vesper.MediaResolver.Models.StreamContent.AudioOnly,
        Container = container, VideoCodec = video ? codec : null, AudioCodec = video ? null : codec, Context = context ?? Vesper.MediaResolver.Models.MediaRequestContext.Empty,
        FileSize = video ? 5_000_000 : 300_000, ContentIdentity = id + "-identity", Height = video ? 1080 : null
    };

    private static Vesper.MediaResolver.Models.MediaItem Item(params Vesper.MediaResolver.Models.MediaRepresentation[] reps) => new()
    {
        SourceUrl = new Uri("https://www.youtube.com/watch?v=fixture"), Provider = "youtube", MediaId = "fixture",
        Title = "Fixture", Representations = reps
    };

    [Fact]
    public void MergeBridgeCarriesMatchingCredentialsOnlyForSameOriginDirectTracks()
    {
        var context = new Vesper.MediaResolver.Models.MediaRequestContext
        {
            CookieHeader = "session=authorized",
            // Both direct tracks below are served by this exact CDN origin.
            CredentialOrigin = new Uri("https://cdn.example.com/"),
            CredentialHosts = ["cdn.example.com"]
        };
        var sameOrigin = Item(
            Rep("137", true, "mp4", "h264", new Uri("https://cdn.example.com/video"), context),
            Rep("140", false, "m4a", "aac", new Uri("https://cdn.example.com/audio"), context));
        var (choice, error) = VESPER.MediaTransferBridge.Prepare(sameOrigin,
            Vesper.MediaResolver.Formats.FormatPreferences.BestQuality);

        Assert.NotNull(choice);
        Assert.Equal(JobKind.Merge, choice!.Kind);
        Assert.Equal("session=authorized", choice.Secrets!["Cookie"]);

        var differentPathCookies = Item(
            Rep("137", true, "mp4", "h264", new Uri("https://cdn.example.com/video"), context),
            Rep("140", false, "m4a", "aac", new Uri("https://cdn.example.com/audio"), context with { CookieHeader = "audio=other-path" }));
        var (scoped, _) = VESPER.MediaTransferBridge.Prepare(differentPathCookies,
            Vesper.MediaResolver.Formats.FormatPreferences.BestQuality);
        Assert.NotEqual(JobKind.Merge, scoped?.Kind);

        var crossOrigin = Item(
            Rep("137", true, "mp4", "h264", new Uri("https://cdn.example.com/video"), context),
            Rep("140", false, "m4a", "aac", new Uri("https://audio.example.com/audio"), context));
        var (cross, _) = VESPER.MediaTransferBridge.Prepare(crossOrigin,
            Vesper.MediaResolver.Formats.FormatPreferences.BestQuality);
        Assert.NotEqual(JobKind.Merge, cross?.Kind);
    }

    [Fact]
    public void Can_refresh_only_media_jobs_that_know_their_page_and_formats()
    {
        Assert.True(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot
        { Kind = JobKind.Merge, OriginalPageUrl = "https://www.youtube.com/watch?v=a", MediaId = "a", MediaFormatId = "137+140" }));
        Assert.False(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Merge, OriginalPageUrl = "https://x/", MediaId = "a" }));
        Assert.True(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Hls, OriginalPageUrl = "https://x/", MediaId = "a", MediaFormatId = "1" }));
        Assert.True(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Dash, OriginalPageUrl = "https://x/", MediaId = "a", MediaFormatId = "1" }));
        Assert.False(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot { Kind = JobKind.Hls, OriginalPageUrl = "https://x/", MediaId = "a", MediaFormatId = "subtitle:en" }));
        Assert.True(VESPER.MediaSourceRescue.CanRefresh(new JobSnapshot
        { Kind = JobKind.Hls, IsLiveRecording = true, OriginalPageUrl = "https://x/", MediaId = "a", MediaFormatId = "1" }));
    }

    [Fact]
    public void An_automatic_merge_attempt_never_repeats_the_same_delivery_and_only_runs_for_stopped_media()
    {
        var expired = new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Merge, State = JobState.NeedsLink,
            MediaProvider = "youtube", Reason = "expired", OriginalPageUrl = "https://www.youtube.com/watch?v=b", MediaId = "b", MediaFormatId = "251" };
        Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(expired));
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(expired));                       // within the hour
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Merge, Reason = "auth",
            OriginalPageUrl = expired.OriginalPageUrl, MediaId = "b", MediaFormatId = "251" }));
        Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Http, Reason = "expired",
            OriginalPageUrl = expired.OriginalPageUrl, MediaId = "b", MediaFormatId = "251" }));

        VESPER.MediaSourceRescue.ForgetAutomaticAttempt(expired.Id);
        Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(expired));
        VESPER.MediaSourceRescue.ForgetAutomaticAttempt(expired.Id);
    }

    [Fact]
    public void Automatic_direct_refresh_requires_a_known_expired_media_identity()
    {
        var ready = new JobSnapshot { Id = Guid.NewGuid(), Kind = JobKind.Http, State = JobState.NeedsLink,
            Reason = "forbidden", MediaProvider = "youtube", MediaId = "fixture", MediaFormatId = "18",
            SourceUrl = "https://cdn.example.com/18?token=old", OriginalPageUrl = "https://www.youtube.com/watch?v=fixture" };
        try
        {
            Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(ready));
            Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(ready));
            Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot {
                Id = Guid.NewGuid(), Kind = JobKind.Http, State = JobState.NeedsLink, Reason = "expired",
                MediaProvider = "generic", MediaId = ready.MediaId, MediaFormatId = ready.MediaFormatId,
                OriginalPageUrl = ready.OriginalPageUrl }));
            Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(new JobSnapshot {
                Id = Guid.NewGuid(), Kind = JobKind.Http, State = JobState.Paused, Reason = "expired",
                MediaProvider = ready.MediaProvider, MediaId = ready.MediaId, MediaFormatId = ready.MediaFormatId,
                OriginalPageUrl = ready.OriginalPageUrl }));
        }
        finally { VESPER.MediaSourceRescue.ForgetAutomaticAttempt(ready.Id); }
    }

    [Fact]
    public void Automatic_recovery_allows_three_distinct_deliveries_but_does_not_loop()
    {
        Guid id = Guid.NewGuid();
        JobSnapshot Source(string suffix, JobKind kind = JobKind.Http) => new()
        {
            Id = id, Kind = kind, State = JobState.NeedsLink, Reason = "expired",
            MediaProvider = "youtube", MediaId = "fixture", MediaFormatId = "18",
            OriginalPageUrl = "https://www.youtube.com/watch?v=fixture", SourceUrl = "https://cdn.example.com/18?token=" + suffix
        };
        try
        {
            Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(Source("a")));
            Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(Source("a")));
            Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(Source("b")));
            Assert.True(VESPER.MediaSourceRescue.ShouldTryAutomatically(Source("c", JobKind.Dash)));
            Assert.False(VESPER.MediaSourceRescue.ShouldTryAutomatically(Source("d")));
        }
        finally { VESPER.MediaSourceRescue.ForgetAutomaticAttempt(id); }
    }

    [Fact]
    public async Task A_refreshed_merge_plan_continues_the_saved_tracks_when_the_formats_still_match()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
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
        RescueResult accepted = await InvokeRefreshMerge(h.Engine, job, fresh);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Contains("sig=new", h.Engine.Snapshot(added.JobId)!.SourceUrl);

        // A format that disappeared: refused, nothing changes.
        RescueResult missing = await InvokeRefreshMerge(h.Engine, h.Engine.Snapshot(added.JobId)!, Item(video));
        Assert.False(missing.Accepted);
        Assert.Contains("same formats", missing.Message);

        // Same ids but a different file size: the engine's identity check refuses it.
        RescueResult changed = await InvokeRefreshMerge(h.Engine, h.Engine.Snapshot(added.JobId)!, Item(video with { FileSize = 6_000_000 }, audio));
        Assert.False(changed.Accepted);
    }

    [Fact]
    public async Task Automatic_refresh_does_not_replace_a_queued_direct_merge_with_a_segmented_plan()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        var video = Rep("137", true, "mp4", "h264");
        var audio = Rep("140", false, "m4a", "aac");
        (VESPER.MediaDownloadChoice? choice, string? error) = VESPER.MediaTransferBridge.Prepare(Item(video, audio),
            Vesper.MediaResolver.Formats.FormatPreferences.BestQuality);
        Assert.NotNull(choice);
        AddResult added = h.Engine.Add(new AddRequest(choice!.Url, FileName: choice.FileName, StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "youtube", MediaId: "fixture", MediaFormatId: choice.FormatId,
            MediaPlanJson: choice.MediaPlanJson, OriginalPageUrl: "https://www.youtube.com/watch?v=fixture"));
        Assert.True(added.Ok, added.Message);

        var segmentedItem = Item(video with { Protocol = Vesper.MediaResolver.Models.StreamProtocol.Hls },
            audio with { Protocol = Vesper.MediaResolver.Models.StreamProtocol.Hls });
        RescueResult result = await InvokeRefreshMerge(h.Engine, h.Engine.Snapshot(added.JobId)!, segmentedItem, automatic: true);

        Assert.False(result.Accepted);
        Assert.Equal(JobState.Queued, h.Engine.Snapshot(added.JobId)!.State);
        Assert.Equal(choice.Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);
    }

    private static async Task<RescueResult> InvokeRefreshMerge(DownloadEngine engine, JobSnapshot job,
        Vesper.MediaResolver.Models.MediaItem item, bool automatic = false)
    {
        var method = typeof(VESPER.MediaSourceRescue).GetMethod("RefreshMergeAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var operation = (Task<RescueResult>)method.Invoke(null, [engine, job, item, CancellationToken.None, automatic])!;
        return await operation;
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

public sealed class BridgeSubtitleEmbeddingTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Explicit_subtitle_embedding_is_parsed_with_selected_languages()
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2,\"subtitles\":[\"en-US\"],\"embedSubtitles\":true}}";
        Assert.True(BridgeProtocol.TryParse(json, out BridgeRequest request, out string error), error);
        Assert.True(request.EmbedSubtitles);
        Assert.Equal(new[] { "en-US" }, request.SubtitleLanguages);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Invalid_embedding_preferences_are_refused(string value)
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2,\"subtitles\":[\"en\"],\"embedSubtitles\":{value}}}";
        Assert.False(BridgeProtocol.TryParse(json, out _, out string error));
        Assert.Equal("Invalid subtitle embedding preference.", error);
    }

    [Fact]
    public void Embedding_without_a_selected_language_is_refused()
    {
        string json = $"{{\"v\":1,\"id\":\"request-01\",\"type\":\"media-select\",\"mode\":\"download\",\"mediaSession\":\"{Session}\",\"formatChoice\":2,\"embedSubtitles\":true}}";
        Assert.False(BridgeProtocol.TryParse(json, out _, out string error));
        Assert.Equal("Choose at least one subtitle language before embedding.", error);
    }
}
