using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

public sealed class MediaRefreshTests
{
    private static byte[] MediaFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

    private static byte[] FragmentedAudioFixture() =>
        [.. MediaFixture("audio-init.mp4"), .. MediaFixture("audio-1.m4s")];

    [Fact]
    public async Task No_media_refresh_path_can_clear_a_parked_restart_cleanup_marker()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        const string oldUrl = "http://127.0.0.1:65530/expired.m4a?generation=1";
        const string freshUrl = "http://127.0.0.1:65530/expired.m4a?generation=2";
        byte[] media = MediaFixture("audio-init.mp4");
        MediaMergePlan oldPlan = AudioPlan(oldUrl, media);
        MediaMergePlan freshPlan = oldPlan with
        {
            Tracks = [oldPlan.Tracks[0] with { Url = freshUrl }]
        };
        AddResult added = harness.Engine.Add(new AddRequest(oldUrl, FileName: "audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "audio",
            MediaFormatId: "audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot before = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(added.JobId));
        await File.WriteAllBytesAsync(before.PartialPath, media);
        string database = Path.Combine(harness.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER block_final_restart BEFORE UPDATE ON jobs WHEN NEW.state = 0 BEGIN SELECT RAISE(ABORT, 'final queue commit refused'); END;";
        command.ExecuteNonQuery();
        try
        {
            Assert.False(harness.Engine.RestartFromScratch(added.JobId));
            Assert.False(File.Exists(before.PartialPath));
        }
        finally
        {
            command.CommandText = "DROP TRIGGER block_final_restart";
            command.ExecuteNonQuery();
        }

        harness.Restart();
        JobSnapshot parked = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(added.JobId));
        Assert.Equal(JobState.NeedsAttention, parked.State);
        Assert.Equal("restart-cleanup", parked.Reason);
        Assert.False(File.Exists(parked.PartialPath));

        RescueResult direct = harness.Engine.RefreshMergePlan(added.JobId, freshPlan.ToJson());
        RescueResult asynchronous = await harness.Engine.RefreshMergePlanAsync(added.JobId, freshPlan.ToJson());
        RescueResult credentialed = await harness.Engine.RefreshMergePlanWithCredentialsAsync(added.JobId,
            freshPlan.ToJson(), null, new Dictionary<string, string> { ["Cookie"] = "session=must-not-be-installed" });
        RescueResult source = await harness.Engine.RefreshMediaSourceAsync(added.JobId,
            new ResolvedMediaSource("fixture", "audio", "audio-format", freshUrl,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        RescueResult replacement = await harness.Engine.ReplaceLinkAsync(added.JobId, freshUrl);

        foreach (RescueResult result in new[] { direct, asynchronous, credentialed, source, replacement })
        {
            Assert.False(result.Accepted, result.Message);
        }
        Assert.Contains("cleanup", direct.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup", asynchronous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup", credentialed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup", source.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restart from scratch", replacement.Message, StringComparison.OrdinalIgnoreCase);

        JobSnapshot after = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(added.JobId));
        Assert.Equal("restart-cleanup", after.Reason);
        Assert.Equal(oldUrl, after.SourceUrl);
        using var savedStore = JobStore.Open(database);
        Assert.Equal(oldPlan.ToJson(), Assert.Single(savedStore.LoadAll()).MediaPlanJson);
        Assert.False(File.Exists(after.PartialPath));
    }

    private static MediaMergePlan AudioPlan(string url, byte[] audio) => new()
    {
        Provider = "fixture", MediaId = "audio", OutputContainer = "m4a",
        Tracks = [new MergeTrack("audio", "audio-format", url, audio.Length, "m4a", "aac", "audio-bytes-v1")]
    };

    private static MediaMergePlan VideoAudioPlan(string videoUrl, byte[] video, string audioUrl, byte[] audio) => new()
    {
        Provider = "fixture", MediaId = "video-audio", OutputContainer = "mp4",
        Tracks =
        [
            new MergeTrack("video", "video-format", videoUrl, video.Length, "mp4", "h264", "video-bytes-v1"),
            new MergeTrack("audio", "audio-format", audioUrl, audio.Length, "m4a", "aac", "audio-bytes-v1")
        ]
    };

    private static MediaMergePlan StaticHlsPlan(string token)
    {
        MergeTrack Track(string role, string container, string codec)
        {
            string url = $"https://cdn.example.com/{role}.m3u8?token={token}";
            return new MergeTrack(role, role + "-format", url, 0, container, codec,
                Source: new MergeMediaSource("hls", PlaylistUrl: url));
        }

        return new MediaMergePlan
        {
            Version = 2, Provider = "youtube", MediaId = "fixture-video", OutputContainer = "mp4",
            RequireAudio = true, RequireVideo = true,
            Tracks = [Track("video", "mp4", "h264"), Track("audio", "m4a", "aac")]
        };
    }

    private static MediaMergePlan StaticFmp4HlsPlan(FaultServer server, string token, byte[] initialization,
        byte[][] fragments, double segmentDuration = 1)
    {
        MergeTrack Track(string role, string container, string codec)
        {
            string root = $"/hls/segmented-refresh-{token}/{role}";
            string playlistPath = root + "/index.m3u8";
            string durationText = segmentDuration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
                + $"#EXT-X-MAP:URI=\"init.mp4?sig={token}\"\n"
                + string.Concat(Enumerable.Range(1, fragments.Length)
                    .Select(index => $"#EXTINF:{durationText},\npart-{index}.m4s?sig={token}\n"))
                + "#EXT-X-ENDLIST\n";
            server.HlsPlaylists[playlistPath] = playlist;
            server.HlsFiles[root + "/init.mp4"] = initialization;
            for (int i = 0; i < fragments.Length; i++) server.HlsFiles[root + $"/part-{i + 1}.m4s"] = fragments[i];
            string url = server.Url(playlistPath) + "?sig=" + token;
            return new MergeTrack(role, role + "-format", url, 0, container, codec,
                Source: new MergeMediaSource("hls", PlaylistUrl: url));
        }

        return new MediaMergePlan
        {
            Version = 2, Provider = "fixture", MediaId = "segmented-media", OutputContainer = "mp4",
            RequireAudio = true, RequireVideo = true,
            Tracks = [Track("video", "mp4", "h264"), Track("audio", "m4a", "aac")]
        };
    }

    private static string HlsShape(FaultServer server, MergeTrack track)
    {
        Uri playlistUrl = new(track.Source!.PlaylistUrl!);
        HlsMedia media = HlsParser.ParseMedia(server.HlsPlaylists[playlistUrl.AbsolutePath], playlistUrl);
        return SegmentedMergeTransfer.HlsShapeFingerprint(media);
    }

    private static MediaMergePlan StaticEncryptedHlsPlan(FaultServer server, string token, byte[] initialization,
        byte[][] encryptedFragments)
    {
        MergeTrack Track(string role, string container, string codec)
        {
            string root = $"/hls/encrypted-refresh-{token}/{role}";
            string playlistPath = root + "/index.m3u8";
            string playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n"
                + $"#EXT-X-MAP:URI=\"init.mp4?sig={token}\"\n"
                + $"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin?sig={token}\",IV=0x{Convert.ToHexString(FaultServer.HlsIv)}\n"
                + string.Concat(Enumerable.Range(1, encryptedFragments.Length)
                    .Select(index => $"#EXTINF:1,\npart-{index}.m4s?sig={token}\n"))
                + "#EXT-X-ENDLIST\n";
            server.HlsPlaylists[playlistPath] = playlist;
            server.HlsFiles[root + "/init.mp4"] = initialization;
            server.HlsFiles[root + "/key.bin"] = FaultServer.HlsKey;
            for (int i = 0; i < encryptedFragments.Length; i++)
                server.HlsFiles[root + $"/part-{i + 1}.m4s"] = encryptedFragments[i];
            string url = server.Url(playlistPath) + "?sig=" + token;
            return new MergeTrack(role, role + "-format", url, 0, container, codec,
                Source: new MergeMediaSource("hls", PlaylistUrl: url));
        }

        return new MediaMergePlan
        {
            Version = 2, Provider = "fixture", MediaId = "encrypted-media", OutputContainer = "mp4",
            RequireAudio = true, RequireVideo = true,
            Tracks = [Track("video", "mp4", "h264"), Track("audio", "m4a", "aac")]
        };
    }

    private static MediaMergePlan StaticDashPlan(FaultServer server, string token, byte[] initialization, byte[][] fragments)
    {
        MergeTrack Track(string role, string container, string codec)
        {
            string root = $"/dash/segmented-refresh-{token}/{role}";
            string Url(string name) => server.Url(root + "/" + name + "?sig=" + token);
            server.Extra[root + "/init.mp4"] = initialization;
            var dash = new DashDownloadPlan
            {
                Provider = "fixture", MediaId = "segmented-media", RepresentationId = role + "-format",
                Initialization = new DashFragment(Url("init.mp4")),
                Fragments = Enumerable.Range(0, fragments.Length).Select(index =>
                {
                    string name = $"part-{index + 1}.m4s";
                    server.Extra[root + "/" + name] = fragments[index];
                    return new DashFragment(Url(name), DurationSeconds: 1, StartSeconds: index);
                }).ToArray(),
                RequireAudio = role == "audio", RequireVideo = role == "video"
            };
            string mediaUrl = server.Url($"/manifest-{role}.mpd?sig={token}");
            return new MergeTrack(role, dash.RepresentationId, mediaUrl, 0, container, codec,
                Source: new MergeMediaSource("dash", Dash: dash));
        }

        return new MediaMergePlan
        {
            Version = 2, Provider = "fixture", MediaId = "segmented-media", OutputContainer = "mp4",
            RequireAudio = true, RequireVideo = true,
            Tracks = [Track("video", "mp4", "h264"), Track("audio", "m4a", "aac")]
        };
    }

    private static void SeedSegmentedInitialization(Harness harness, Guid id, MediaMergePlan plan, byte[] initialization) =>
        SeedSegmentedParts(harness, id, plan, 1, initialization);

    private static void SeedSegmentedParts(Harness harness, Guid id, MediaMergePlan plan, int completedVideoParts,
        params byte[][] committedParts) =>
        SeedSegmentedParts(harness, id, plan, completedVideoParts, null, committedParts);

    private static void SeedSegmentedParts(Harness harness, Guid id, MediaMergePlan plan, int completedVideoParts,
        string?[]? shapeFingerprints, params byte[][] committedParts)
    {
        Assert.Equal(completedVideoParts, committedParts.Length);
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(harness.Engine)!;
        DownloadJob job = jobs[id];
        byte[] prefix = [.. committedParts.SelectMany(part => part)];
        File.WriteAllBytes(job.PartialPath, prefix);
        var extents = new ExtentSet();
        extents.Add(new Vesper.Engine.ByteRange(0, prefix.LongLength));
        int version = shapeFingerprints is null ? 1 : 2;
        var cursor = new SegmentedMergeTransfer.Cursor(version, plan.Fingerprint, "download", Track: 0, Next: completedVideoParts,
            Offset: prefix.LongLength, PrefixHash: Payloads.Sha256(prefix),
            CompletedParts: [completedVideoParts, 0], PartLengths: committedParts.Select(part => (long)part.Length).ToArray(),
            SourceFingerprints: [new string('A', 64), new string('B', 64)], SourceShapeFingerprints: shapeFingerprints);
        harness.Engine.UpdateJob(job, current => { current.Extents = extents; current.MediaState = JsonSerializer.Serialize(cursor); });
    }

    private static byte[] EncryptHlsSegment(byte[] data)
    {
        using Aes aes = Aes.Create();
        aes.Key = FaultServer.HlsKey;
        return aes.EncryptCbc(data, FaultServer.HlsIv, PaddingMode.PKCS7);
    }

    private static void AssertSecretNotOnDisk(Harness harness, string secret)
    {
        foreach (string path in Directory.GetFiles(harness.Data, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
    }

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
    public async Task CredentialedMergeDownloadsWithScopedCookiesWithoutPersistingThem()
    {
        await using var server = new FaultServer();
        byte[] audio = MediaFixture("quiet-track-aac.m4a");
        const string cookie = "session=merge-secret";
        server.AuthenticatedFiles["/private/audio"] = (cookie, audio);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = AudioPlan(server.Url("/private/audio"), audio);

        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "private-audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "audio", MediaFormatId: "audio-format",
            MediaPlanJson: plan.ToJson(), Secrets: new Dictionary<string, string> { ["Cookie"] = cookie }));
        Assert.True(added.Ok, added.Message);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.NotEmpty(server.RequestsFor("/private/audio"));
        Assert.All(server.RequestsFor("/private/audio"), request => Assert.Equal(cookie, request.Cookie));
        h.Engine.Dispose();
        AssertSecretNotOnDisk(h, cookie);
    }

    [Fact]
    public async Task FailedCredentialedMergeRefreshKeepsTheOldPlanAndCredentials()
    {
        await using var server = new FaultServer();
        byte[] video = MediaFixture("video-only.mp4");
        byte[] audio = MediaFixture("quiet-track-aac.m4a");
        const string oldCookie = "session=old-merge-secret";
        const string freshCookie = "session=fresh-merge-secret";
        server.AuthenticatedFiles["/old/video"] = (oldCookie, video);
        server.AuthenticatedFiles["/old/audio"] = (oldCookie, audio);
        server.AuthenticatedFiles["/fresh/video"] = (freshCookie, video);
        server.AuthenticatedFiles["/fresh/audio"] = ("session=other-path", audio);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = VideoAudioPlan(server.Url("/old/video"), video, server.Url("/old/audio"), audio);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "private-video.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "video-audio", MediaFormatId: "video-format+audio-format",
            MediaPlanJson: oldPlan.ToJson(), Secrets: new Dictionary<string, string> { ["Cookie"] = oldCookie }));
        Assert.True(added.Ok, added.Message);

        MediaMergePlan freshPlan = VideoAudioPlan(server.Url("/fresh/video"), video, server.Url("/fresh/audio"), audio);
        RescueResult result = await h.Engine.RefreshMergePlanWithCredentialsAsync(added.JobId, freshPlan.ToJson(),
            DateTimeOffset.UtcNow.AddMinutes(5), new Dictionary<string, string> { ["Cookie"] = freshCookie });

        Assert.False(result.Accepted);
        Assert.Contains("requires different sign-in access", result.Message);
        Assert.Equal(oldPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);
        Assert.Equal(freshCookie, Assert.Single(server.RequestsFor("/fresh/video")).Cookie);
        Assert.Equal(freshCookie, Assert.Single(server.RequestsFor("/fresh/audio")).Cookie);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.All(server.RequestsFor("/old/video"), request => Assert.Equal(oldCookie, request.Cookie));
        Assert.All(server.RequestsFor("/old/audio"), request => Assert.Equal(oldCookie, request.Cookie));
        Assert.Single(server.RequestsFor("/fresh/video"));
        Assert.Single(server.RequestsFor("/fresh/audio"));
        h.Engine.Dispose();
        AssertSecretNotOnDisk(h, oldCookie);
        AssertSecretNotOnDisk(h, freshCookie);
    }

    [Fact]
    public async Task CredentialedMergeRefreshProbesAndDownloadsWithFreshCredentials()
    {
        await using var server = new FaultServer();
        byte[] audio = MediaFixture("quiet-track-aac.m4a");
        const string cookie = "session=refreshed-merge-secret";
        server.AuthenticatedFiles["/fresh/audio"] = (cookie, audio);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = AudioPlan(server.Url("/old/audio"), audio);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "refreshed-audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "audio", MediaFormatId: "audio-format",
            MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);

        MediaMergePlan freshPlan = AudioPlan(server.Url("/fresh/audio"), audio);
        RescueResult refreshed = await h.Engine.RefreshMergePlanWithCredentialsAsync(added.JobId, freshPlan.ToJson(),
            DateTimeOffset.UtcNow.AddMinutes(5), new Dictionary<string, string> { ["Cookie"] = cookie });
        Assert.True(refreshed.Accepted, refreshed.Message);
        Assert.Equal(freshPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.True(server.RequestsFor("/fresh/audio").Count >= 2); // one probe and at least one transfer range
        Assert.All(server.RequestsFor("/fresh/audio"), request => Assert.Equal(cookie, request.Cookie));
        h.Engine.Dispose();
        AssertSecretNotOnDisk(h, cookie);
    }

    [WindowsFact]
    public async Task MergeRefreshKeepsTheSavedWavTrimRange()
    {
        await using var server = new FaultServer();
        byte[] audio = FragmentedAudioFixture();
        server.Extra["/old/audio"] = audio;
        server.Extra["/fresh/audio"] = audio;
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = AudioPlan(server.Url("/old/audio"), audio) with
        { OutputRange = new MediaTimeRange(.25, .75) };
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "trim.wav", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "audio", MediaFormatId: "audio-format",
            MediaPlanJson: oldPlan.ToJson(), Output: MediaOutput.Wav));
        Assert.True(added.Ok, added.Message);

        MediaMergePlan freshPlan = AudioPlan(server.Url("/fresh/audio"), audio);
        RescueResult refreshed = h.Engine.RefreshMergePlan(added.JobId, freshPlan.ToJson());
        Assert.True(refreshed.Accepted, refreshed.Message);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        using var wave = File.OpenRead(done.FinalPath!);
        var result = WaveAudio.Read(wave);
        Assert.InRange(Math.Abs(result.Duration - .5), 0, .02);
    }

    [Fact]
    public void SegmentedRefreshExplainsWhyChangedPlaylistsCannotReuseSavedParts()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = StaticHlsPlan("old-signature");
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "fixture.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);

        MediaMergePlan refreshedPlan = StaticHlsPlan("new-signature");
        RescueResult refreshed = h.Engine.RefreshMergePlan(added.JobId, refreshedPlan.ToJson());

        Assert.False(refreshed.Accepted);
        Assert.Contains("HLS/DASH", refreshed.Message);
        Assert.Contains("Saved segments were kept", refreshed.Message);
        Assert.Equal(oldPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);
    }

    [Fact]
    public async Task SegmentedRefreshRechecksCommittedPartsBeforeRebasingTheCursor()
    {
        await using var server = new FaultServer();
        byte[] initialization = MediaFixture("av-init.mp4");
        byte[][] fragments = [MediaFixture("av-1.m4s"), MediaFixture("av-2.m4s"), MediaFixture("av-3.m4s")];
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = StaticFmp4HlsPlan(server, "old", initialization, fragments);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "segmented-refresh.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);
        SeedSegmentedInitialization(h, added.JobId, oldPlan, initialization);

        MediaMergePlan refreshedPlan = StaticFmp4HlsPlan(server, "new", initialization, fragments);
        RescueResult refreshed = await h.Engine.RefreshMergePlanAsync(added.JobId, refreshedPlan.ToJson());
        Assert.True(refreshed.Accepted, refreshed.Message);
        Assert.Equal(refreshedPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.Equal(1, server.Hits("/hls/segmented-refresh-new/video/init.mp4"));
    }

    [Fact]
    public async Task SegmentedHlsClipRefreshRequiresTheSameSavedTimeline()
    {
        await using var server = new FaultServer();
        byte[] initialization = MediaFixture("av-init.mp4");
        byte[][] fragments = [MediaFixture("av-1.m4s"), MediaFixture("av-2.m4s"), MediaFixture("av-3.m4s")];
        await server.StartAsync();
        MediaMergePlan oldPlan = StaticFmp4HlsPlan(server, "clip-old", initialization, fragments) with
        { ClipRange = new MediaTimeRange(.25, .75) };
        string[] savedShapes = oldPlan.Tracks.Select(track => HlsShape(server, track)).ToArray();

        using var matching = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        AddResult matchingAdded = matching.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "clip-match.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(matchingAdded.Ok, matchingAdded.Message);
        SeedSegmentedParts(matching, matchingAdded.JobId, oldPlan, 1, savedShapes, initialization);
        MediaMergePlan sameTimeline = StaticFmp4HlsPlan(server, "clip-fresh", initialization, fragments);
        RescueResult accepted = await matching.Engine.RefreshMergePlanAsync(matchingAdded.JobId, sameTimeline.ToJson());
        Assert.True(accepted.Accepted, accepted.Message);

        using var changed = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        AddResult changedAdded = changed.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "clip-changed.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(changedAdded.Ok, changedAdded.Message);
        SeedSegmentedParts(changed, changedAdded.JobId, oldPlan, 1, savedShapes, initialization);
        MediaMergePlan changedTimeline = StaticFmp4HlsPlan(server, "clip-changed", initialization, fragments, segmentDuration: .5);

        RescueResult refused = await changed.Engine.RefreshMergePlanAsync(changedAdded.JobId, changedTimeline.ToJson());
        Assert.False(refused.Accepted);
        Assert.Contains("clipped HLS playlist timeline changed", refused.Message);
        Assert.Equal(oldPlan.Tracks[0].Url, changed.Engine.Snapshot(changedAdded.JobId)!.SourceUrl);
        Assert.Equal(initialization, await File.ReadAllBytesAsync(changed.Engine.Snapshot(changedAdded.JobId)!.PartialPath));
    }

    [Fact]
    public async Task SegmentedRefreshRefusesWhenACommittedPartDiffers()
    {
        await using var server = new FaultServer();
        byte[] initialization = MediaFixture("av-init.mp4");
        byte[] changedInitialization = [.. initialization];
        changedInitialization[^1] ^= 0x5A;
        byte[][] fragments = [MediaFixture("av-1.m4s"), MediaFixture("av-2.m4s"), MediaFixture("av-3.m4s")];
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = StaticFmp4HlsPlan(server, "old", initialization, fragments);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "segmented-mismatch.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);
        SeedSegmentedInitialization(h, added.JobId, oldPlan, initialization);

        MediaMergePlan changedPlan = StaticFmp4HlsPlan(server, "changed", changedInitialization, fragments);
        RescueResult refreshed = await h.Engine.RefreshMergePlanAsync(added.JobId, changedPlan.ToJson());

        Assert.False(refreshed.Accepted);
        Assert.Contains("differs from the saved bytes", refreshed.Message);
        Assert.Equal(oldPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);
        Assert.Equal(initialization, await File.ReadAllBytesAsync(h.Engine.Snapshot(added.JobId)!.PartialPath));
    }

    [Fact]
    public async Task SegmentedRefreshDecryptsEncryptedHlsPartsBeforeReusingThem()
    {
        await using var server = new FaultServer();
        byte[] initialization = MediaFixture("av-init.mp4");
        byte[] firstPlainPart = MediaFixture("av-1.m4s");
        byte[][] encryptedParts = [
            EncryptHlsSegment(firstPlainPart),
            EncryptHlsSegment(MediaFixture("av-2.m4s")),
            EncryptHlsSegment(MediaFixture("av-3.m4s"))
        ];
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = StaticEncryptedHlsPlan(server, "old", initialization, encryptedParts);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "encrypted-refresh.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);
        SeedSegmentedParts(h, added.JobId, oldPlan, 2, initialization, firstPlainPart);

        MediaMergePlan refreshedPlan = StaticEncryptedHlsPlan(server, "new", initialization, encryptedParts);
        RescueResult refreshed = await h.Engine.RefreshMergePlanAsync(added.JobId, refreshedPlan.ToJson());
        Assert.True(refreshed.Accepted, refreshed.Message);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.Equal(1, server.Hits("/hls/encrypted-refresh-new/video/part-1.m4s"));
    }

    [Fact]
    public async Task SegmentedDashRefreshRechecksCommittedPartsBeforeRebasingTheCursor()
    {
        await using var server = new FaultServer();
        byte[] initialization = MediaFixture("av-init.mp4");
        byte[][] fragments = [MediaFixture("av-1.m4s"), MediaFixture("av-2.m4s"), MediaFixture("av-3.m4s")];
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan oldPlan = StaticDashPlan(server, "old", initialization, fragments);
        AddResult added = h.Engine.Add(new AddRequest(oldPlan.Tracks[0].Url, FileName: "dash-refresh.mp4", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: oldPlan.Provider, MediaId: oldPlan.MediaId,
            MediaFormatId: "video-format+audio-format", MediaPlanJson: oldPlan.ToJson()));
        Assert.True(added.Ok, added.Message);
        SeedSegmentedInitialization(h, added.JobId, oldPlan, initialization);

        MediaMergePlan refreshedPlan = StaticDashPlan(server, "new", initialization, fragments);
        RescueResult refreshed = await h.Engine.RefreshMergePlanAsync(added.JobId, refreshedPlan.ToJson());
        Assert.True(refreshed.Accepted, refreshed.Message);
        Assert.Equal(refreshedPlan.Tracks[0].Url, h.Engine.Snapshot(added.JobId)!.SourceUrl);

        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.True(new FileInfo(done.FinalPath!).Length > 0);
        Assert.Equal(1, server.Hits("/dash/segmented-refresh-new/video/init.mp4"));
    }

    [Fact]
    public async Task CredentialedMergeSecretsDoNotSurviveAnEngineRestart()
    {
        await using var server = new FaultServer();
        byte[] audio = MediaFixture("quiet-track-aac.m4a");
        const string cookie = "session=restart-merge-secret";
        server.AuthenticatedFiles["/private/audio"] = (cookie, audio);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan plan = AudioPlan(server.Url("/private/audio"), audio);
        AddResult added = h.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "restart-audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", MediaId: "audio", MediaFormatId: "audio-format",
            MediaPlanJson: plan.ToJson(), Secrets: new Dictionary<string, string> { ["Cookie"] = cookie }));
        Assert.True(added.Ok, added.Message);

        h.Restart();
        JobSnapshot needsLink = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        Assert.Contains("refused", needsLink.Message);
        Assert.NotEmpty(server.RequestsFor("/private/audio"));
        Assert.All(server.RequestsFor("/private/audio"), request => Assert.Null(request.Cookie));
        h.Engine.Dispose();
        AssertSecretNotOnDisk(h, cookie);
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
