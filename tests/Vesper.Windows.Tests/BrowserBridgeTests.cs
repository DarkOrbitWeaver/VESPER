using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using StreamContent = Vesper.MediaResolver.Models.StreamContent;

namespace Vesper.Windows.Tests;

public sealed class BrowserBridgeTests
{
    private sealed class PickerEngineFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vesper-picker-" + Guid.NewGuid().ToString("N"));
        internal DownloadEngine Engine { get; }

        internal PickerEngineFixture()
        {
            Engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(_root, "data"), DefaultFolder = Path.Combine(_root, "files")
            });
        }

        public void Dispose()
        {
            Engine.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.com/demos", true, true)]
    [InlineData("https://example.com/demos", false, false)]
    [InlineData("https://www.instagram.com/reel/123/", true, false)]
    [InlineData("https://www.tiktok.com/@test/video/123", true, false)]
    public void VideoWithoutAudioIsAllowedOnlyForADeclaredStandaloneMutedLoop(string page, bool mutedLoop, bool allowSilent)
    {
        var item = new MediaItem
        {
            SourceUrl = new Uri(page), Provider = "generic", Title = "Video",
            Representations = [new MediaRepresentation
            {
                Id = "direct-0", Url = new Uri("https://cdn.example.com/demo.mp4"), Protocol = StreamProtocol.Http,
                Content = StreamContent.VideoAndAudio, AudioUnverified = true, Container = "mp4", MutedLoop = mutedLoop
            }]
        };

        var (choice, error) = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);

        Assert.Null(error);
        Assert.NotNull(choice);
        Assert.Equal(allowSilent ? DirectMediaPlan.AudioIfPresent : null, choice.MediaPlanJson);
        Assert.Contains(allowSilent ? "may be silent" : "audio not confirmed", choice.DisplayDetail);
    }

    [Fact]
    public void Butterfly_default_compatible_preset_exposes_time_range_for_fragmented_mp4_hls()
    {
        var hls = new MediaRepresentation
        {
            Id = "hls-1080", Url = new Uri("https://cdn.example.test/1080.m3u8"),
            Protocol = StreamProtocol.Hls, Content = StreamContent.VideoAndAudio,
            Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = 1080,
            Segments = new SegmentPlan
            {
                InitializationUrl = new Uri("https://cdn.example.test/init.mp4"),
                Segments = [new MediaSegment(new Uri("https://cdn.example.test/part.m4s"), 60, StartSeconds: 0)],
                TotalDurationSeconds = 60,
                IsFragmentedMp4 = true
            }
        };
        var http = new MediaRepresentation
        {
            Id = "http-720", Url = new Uri("https://cdn.example.test/720.mp4"),
            Content = StreamContent.VideoAndAudio, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac",
            Height = 720
        };
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch/one"), Provider = "test", Title = "Video",
            Duration = TimeSpan.FromSeconds(60), Representations = [http, hls]
        };
        using var fixture = new PickerEngineFixture();
        var bridge = new BrowserBridgeHandler(null!, fixture.Engine, () => new VesperPreferences());
        var createOptions = typeof(BrowserBridgeHandler).GetMethod("CreateBrowserOptions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var response = (BridgeResponse)createOptions.Invoke(bridge,
            ["test-options", item, item.SourceUrl.AbsoluteUri, CancellationToken.None, new[] {
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("https://cdn.example.test/part.m4s")))
            }])!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, BridgeProtocol.Json));
        JsonElement best = json.RootElement.GetProperty("choices")[0];

        Assert.True(response.Ok, response.Message);
        Assert.Equal(1, json.RootElement.GetProperty("playbackMatches").GetInt32());
        Assert.Equal("Best quality", best.GetProperty("label").GetString());
        Assert.Contains("1080p", best.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(best.GetProperty("canClip").GetBoolean());
        Assert.False(best.GetProperty("clipUsesWholeFile").GetBoolean());
    }

    [Fact]
    public void Butterfly_default_compatible_mp4_preset_discloses_full_source_trim()
    {
        var direct = new MediaRepresentation
        {
            Id = "137", Url = new Uri("https://cdn.example.test/video.mp4"),
            Content = StreamContent.VideoAndAudio, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac",
            RawCodecs = "avc1.640028, mp4a.40.2", Height = 1080, FileSize = 2_097_152,
            ContentIdentity = "youtube:video:137:modified:2097152"
        };
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch/one"), Provider = "youtube", MediaId = "video",
            Title = "Video", Duration = TimeSpan.FromSeconds(60), Representations = [direct]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => new VesperPreferences());
        var createOptions = typeof(BrowserBridgeHandler).GetMethod("CreateBrowserOptions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var response = (BridgeResponse)createOptions.Invoke(bridge,
            ["test-options", item, item.SourceUrl.AbsoluteUri, CancellationToken.None, null])!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, BridgeProtocol.Json));
        JsonElement best = json.RootElement.GetProperty("choices")[0];

        Assert.True(response.Ok, response.Message);
        Assert.True(best.GetProperty("canClip").GetBoolean());
        Assert.True(best.GetProperty("clipUsesWholeFile").GetBoolean());
    }

    [Fact]
    public void Browser_collection_shortcut_requires_a_resolver_supplied_http_collection()
    {
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch?v=one&list=PL1"),
            Provider = "test",
            Title = "One"
        };

        Assert.False(BrowserBridgeHandler.HasRelatedCollection(item));
        Assert.False(BrowserBridgeHandler.HasRelatedCollection(item with
        {
            Related = new CollectionHint(CollectionKind.Playlist, "PL1", new Uri("javascript:alert(1)"), false, "Playlist")
        }));
        Assert.True(BrowserBridgeHandler.HasRelatedCollection(item with
        {
            Related = new CollectionHint(CollectionKind.Playlist, "PL1", new Uri("https://video.example.test/playlist?list=PL1"), false, "Playlist")
        }));
    }

    [Fact]
    public void App_mutex_and_show_event_are_scoped_to_the_current_user()
    {
        string currentPipe = BridgePipe.NameForCurrentUser();
        string identityHash = currentPipe["vesper-".Length..];

        Assert.Equal(@"Local\VESPER.SingleInstance-" + identityHash, Program.InstanceMutexName);
        Assert.Equal(@"Local\VESPER.Show-" + identityHash, Program.ShowEventName);
    }

    [Theory]
    [InlineData(false, 16 * 1024, false)]
    [InlineData(true, 5 * 1024 * 1024, false)]
    [InlineData(true, 200 * 1024 * 1024, false)]
    [InlineData(true, 16 * 1024, true)]
    public async Task NativeHelperCompletesAStagedDownloadThroughTheRealDesktopBridge(bool multiplexed, int length, bool mismatch)
    {
        string? isolatedHelper = Environment.GetEnvironmentVariable("VESPER_QA_ISOLATED_HOST");
        bool isolated = !string.IsNullOrWhiteSpace(isolatedHelper);
        string? helper = isolated ? isolatedHelper : Environment.GetEnvironmentVariable("VESPER_QA_NATIVE_HOST");
        if (string.IsNullOrWhiteSpace(helper)) return;
        Assert.True(File.Exists(helper));
        if (isolated) Assert.Equal("vesper-qa-host.exe", Path.GetFileName(helper));
        string pipe = isolated ? "vesper-qa-" + Guid.NewGuid().ToString("N") : BridgePipe.NameForCurrentUser();
        string? qaPipe = isolated ? pipe : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;
        using (var occupancy = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            try
            {
                await occupancy.ConnectAsync(150, ct);
                throw new InvalidOperationException("A VESPER bridge is already listening. Use a separate Windows account.");
            }
            catch (TimeoutException) { }
        }

        string root = Path.Combine(Path.GetTempPath(), "vesper-native-qa-" + Guid.NewGuid().ToString("N"));
        string helperRoot = Path.Combine(root, "helper");
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(helperRoot);
        Directory.CreateDirectory(downloads);
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(helper))!))
                File.Copy(file, Path.Combine(helperRoot, Path.GetFileName(file)));
            Assert.False(File.Exists(Path.Combine(helperRoot, "VESPER.exe")), "The isolated helper must not be able to start a desktop.");
            string executable = Path.Combine(helperRoot, Path.GetFileName(helper));
            byte[] payload = new byte[Math.Min(length, 64 * 1024)];
            new Random(4821).NextBytes(payload);
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            Task response = ServeNativeFixtureAsync(server, payload, length, mismatch, ct);
            string url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/fixture";
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"), DefaultFolder = downloads, AllowPrivateNetworks = true
            });
            var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
            using var desktop = new BridgeServer(pipe, bridge.HandleAsync);
            desktop.Start();

            using var session = multiplexed ? new NativeTestSession(executable, qaPipe) : null;
            if (session is not null)
            {
                BridgeResponse opened = await session.SendAsync(new { v = 1, id = "native-session-0001", type = "session-open" }, ct);
                Assert.True(opened.Ok, opened.Message);
                Assert.True(Assert.IsType<JsonElement>(opened.Data).GetProperty("mux").GetBoolean());
                // Multiple frames before any read: responses are correlated by id, not order.
                await session.WriteAsync(new { v = 1, id = "native-invalid-0001", type = "unknown" }, ct);
                await session.WriteAsync(new { v = 1, id = "native-invalid-0002", type = "unknown" }, ct);
                var invalid = new[] { await session.ReadAsync(ct), await session.ReadAsync(ct) };
                Assert.All(invalid, reply => Assert.False(reply.Ok));
                Assert.Equal(new[] { "native-invalid-0001", "native-invalid-0002" }, invalid.Select(reply => reply.Id).Order().ToArray());
            }
            Task<BridgeResponse> Send(object request) => session is null
                ? SendThroughHelperAsync(executable, request, ct, qaPipe) : session.SendAsync(request, ct);
            const string transferId = "native-fixture-0001";
            BridgeResponse submit = await Send(new
            {
                v = 1, id = transferId, type = multiplexed ? "browser-capture" : "browser-submit", mode = "download",
                items = new[] { new { url, fileName = "native-fixture.bin", kind = "http" } },
                browserProof = new { length, etag = "\"native-fixture\"", contentType = "application/octet-stream" }
            });
            Assert.True(submit.Ok, submit.Message);
            BridgeResult result = Assert.Single(submit.Results!);
            if (mismatch)
            {
                Assert.False(result.TransferReady ?? true);
                Assert.Empty(Directory.GetFiles(downloads, "*.bin"));
                Assert.All(engine.Snapshots(), entry => Assert.Equal(0, entry.CommittedBytes));
                await Send(new { v = 1, id = "native-abandon-0001", type = "browser-transfer", transferId, operation = "abandon" });
                await response;
                if (session is not null) await session.CloseAsync(ct);
                return;
            }
            Assert.True(result.TransferReady, result.TransferReason);
            JobSnapshot staged = Assert.IsType<JobSnapshot>(engine.Snapshot(Guid.Parse(result.JobId!)));
            Assert.Equal(0, staged.CommittedBytes);
            Assert.Null(staged.FinalPath);
            BridgeResponse status = await Send(
                new { v = 1, id = "native-status-0001", type = "browser-transfer", transferId, operation = "status" });
            Assert.True(status.Ok, status.Message);
            Assert.Equal("staged", Assert.IsType<JsonElement>(status.Data).GetProperty("state").GetString());

            BridgeResponse accepted = await Send(
                new { v = 1, id = "native-accept-0001", type = "browser-transfer", transferId, operation = "accept" });
            Assert.True(accepted.Ok, accepted.Message);
            BridgeResponse watched = await Send(
                new { v = 1, id = "native-watch-0001", type = "browser-transfer", transferId, operation = "watch" });
            Assert.True(watched.Ok, watched.Message);
            Assert.Equal("complete", Assert.IsType<JsonElement>(watched.Data).GetProperty("state").GetString());
            string final = Assert.Single(Directory.GetFiles(downloads, "*.bin"));
            Assert.Equal(length, new FileInfo(final).Length);
            using var expected = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int offset = 0; offset < length; offset += payload.Length)
                expected.AppendData(payload.AsSpan(0, Math.Min(payload.Length, length - offset)));
            await using (var finished = File.OpenRead(final))
                Assert.Equal(expected.GetHashAndReset(), await System.Security.Cryptography.SHA256.HashDataAsync(finished, ct));
            Assert.Single(engine.Snapshots());
            await response;
            if (session is not null) await session.CloseAsync(ct);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NativeHelperMultiplexHandshakeAndInvalidRequestsNeedNoDesktop()
    {
        string? helper = Environment.GetEnvironmentVariable("VESPER_QA_NATIVE_HOST");
        if (string.IsNullOrWhiteSpace(helper)) return;
        Assert.True(File.Exists(helper));
        // The helper answers the handshake locally and rejects invalid messages
        // before any pipe lookup. Never send a valid desktop command in this test.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var session = new NativeTestSession(helper);
        var opened = await session.SendAsync(new { v = 1, id = "native-session-0002", type = "session-open" }, deadline.Token);
        Assert.True(opened.Ok, opened.Message);
        Assert.True(Assert.IsType<JsonElement>(opened.Data).GetProperty("mux").GetBoolean());
        for (int i = 0; i < 12; i++)
            await session.WriteAsync(new { v = 1, id = $"native-invalid-{i:0000}", type = "unknown" }, deadline.Token);
        var ids = new HashSet<string>();
        for (int i = 0; i < 12; i++)
        {
            var reply = await session.ReadAsync(deadline.Token);
            Assert.False(reply.Ok);
            Assert.Equal("invalid", reply.Code);
            Assert.True(ids.Add(reply.Id));
        }
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"native-invalid-{i:0000}").ToArray(), ids.Order().ToArray());
        await session.CloseAsync(deadline.Token);
    }

    [Fact]
    public async Task IsolatedNativeHelperPipelinesBrowserBodyAndDiscardsDisconnectedCopy()
    {
        string? helper = Environment.GetEnvironmentVariable("VESPER_QA_ISOLATED_HOST");
        if (string.IsNullOrWhiteSpace(helper)) return;
        Assert.Equal("vesper-qa-host.exe", Path.GetFileName(helper));
        Assert.True(File.Exists(helper));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(helper)!, "VESPER.exe")));
        string pipe = "vesper-qa-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "vesper-native-body-qa-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data"), downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;
        try
        {
            using var engine = DownloadEngine.Start(new EngineOptions { DataDirectory = data, DefaultFolder = downloads });
            var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
            using var desktop = new BridgeServer(pipe, bridge.HandleAsync);
            desktop.Start();
            const int length = 5 * 1024 * 1024, chunkSize = 512 * 1024;
            object Begin(string id) => new { v = 1, id = "body-qa-begin", type = "browser-body", operation = "begin",
                transferId = id, session = true, origin = "https://example.test/", fileName = "browser-qa.bin", length };
            byte[] chunk = new byte[chunkSize];
            new Random(731).NextBytes(chunk);
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chunk));
            string encoded = Convert.ToBase64String(chunk);
            object Append(string id, int offset) => new { v = 1, id = $"body-qa-{offset}", type = "browser-body",
                operation = "append", transferId = id, session = true, offset, chunk = encoded, digest };
            const string abandoned = "body-qa-abandoned";
            using (var copy = new NativeTestSession(helper, pipe))
            {
                Assert.True((await copy.SendAsync(Begin(abandoned), ct)).Ok);
                Assert.True((await copy.SendAsync(Append(abandoned, 0), ct)).Ok);
                await copy.CloseAsync(ct);
            }
            for (int i = 0; i < 100 && Directory.GetFiles(data, "browser-body-*.tmp").Length != 0; i++)
                await Task.Delay(20, ct);
            Assert.Empty(Directory.GetFiles(data, "browser-body-*.tmp"));
            Assert.Empty(engine.Snapshots());
            const string transferId = "body-qa-complete";
            using var session = new NativeTestSession(helper, pipe);
            Assert.True((await session.SendAsync(Begin(transferId), ct)).Ok);
            for (int first = 0; first < length; first += 4 * chunkSize)
            {
                int end = Math.Min(first + 4 * chunkSize, length);
                for (int offset = first; offset < end; offset += chunkSize)
                    await session.WriteAsync(Append(transferId, offset), ct);
                for (int offset = first; offset < end; offset += chunkSize)
                {
                    var reply = await session.ReadAsync(ct);
                    Assert.True(reply.Ok, reply.Message);
                    Assert.Equal($"body-qa-{offset}", reply.Id);
                    Assert.Equal(offset + chunkSize, Assert.IsType<JsonElement>(reply.Data).GetProperty("offset").GetInt64());
                }
            }
            Assert.Empty(engine.Snapshots());
            var finished = await session.SendAsync(new { v = 1, id = "body-qa-finish", type = "browser-body",
                operation = "finish", transferId, session = true }, ct);
            Assert.True(finished.Ok, finished.Message);
            Assert.True(Assert.IsType<JsonElement>(finished.Data).GetProperty("accepted").GetBoolean());
            await session.CloseAsync(ct);
            var jobId = Assert.IsType<JsonElement>(finished.Data).GetProperty("jobId").GetGuid();
            while (engine.Snapshot(jobId)?.State != JobState.Completed) await Task.Delay(20, ct);
            string final = Assert.Single(Directory.GetFiles(downloads, "*.bin"));
            Assert.Equal(length, new FileInfo(final).Length);
            using var expected = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int offset = 0; offset < length; offset += chunkSize) expected.AppendData(chunk);
            await using (var file = File.OpenRead(final))
                Assert.Equal(expected.GetHashAndReset(), await System.Security.Cryptography.SHA256.HashDataAsync(file, ct));
            Assert.Single(engine.Snapshots());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IsolatedNativeHelperSubmitsARealMediaSessionExactlyOnce()
    {
        string? helper = Environment.GetEnvironmentVariable("VESPER_QA_ISOLATED_HOST");
        if (string.IsNullOrWhiteSpace(helper) || Environment.GetEnvironmentVariable("VESPER_QA_LIVE_MEDIA") != "1") return;
        Assert.Equal("vesper-qa-host.exe", Path.GetFileName(helper));
        Assert.True(File.Exists(helper));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(helper)!, "VESPER.exe")));
        int trials = int.TryParse(Environment.GetEnvironmentVariable("VESPER_QA_HANDOFF_TRIALS"), out int requested)
            ? Math.Clamp(requested, 1, 5) : 1;
        for (int trial = 1; trial <= trials; trial++)
            await MeasureVerifiedMediaHandoffAsync(helper, trial);
    }

    private static async Task MeasureVerifiedMediaHandoffAsync(string helper, int trial)
    {
        string pipe = "vesper-qa-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "vesper-native-media-qa-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;
        const string url = "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.webm";
        try
        {
            // Independent bounded reference; never substitute a successful discovery for correct bytes.
            using var http = new HttpClient { MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            byte[] payload = await http.GetByteArrayAsync(url, ct);
            Assert.InRange(payload.Length, 1, 4 * 1024 * 1024);
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"), DefaultFolder = downloads
            });
            var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
            using var desktop = new BridgeServer(pipe, bridge.HandleAsync);
            desktop.Start();
            try
            {
                using var session = new NativeTestSession(helper, pipe);
                var clock = Stopwatch.StartNew();
                Assert.True((await session.SendAsync(new { v = 1, id = "native-session-0003", type = "session-open" }, ct)).Ok);
                double handshakeMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                BridgeResponse options = await session.SendAsync(new { v = 1, id = "native-options-0001", type = "media-options", url }, ct);
                double discoveryMs = clock.Elapsed.TotalMilliseconds;
                Assert.True(options.Ok, options.Message);
                JsonElement data = Assert.IsType<JsonElement>(options.Data);
                string token = data.GetProperty("mediaSession").GetString()!;
                int index = data.GetProperty("choices").EnumerateArray()
                    .Concat(data.GetProperty("originalChoices").EnumerateArray())
                    .Concat(data.GetProperty("more").EnumerateArray())
                    .First(option => option.GetProperty("container").GetString() == "WEBM").GetProperty("index").GetInt32();

                var invalid = await session.SendAsync(new { v = 1, id = "native-media-invalid", type = "media-select",
                    mediaSession = Guid.NewGuid().ToString("N"), formatChoice = index, mode = "download" }, ct);
                Assert.False(invalid.Ok);
                Assert.Equal("expired-selection", invalid.Code);
                Assert.Empty(engine.Snapshots());

                var request = new { v = 1, id = "native-media-select", type = "media-select", mediaSession = token,
                    formatChoice = index, mode = "download" };
                clock.Restart();
                BridgeResponse accepted = await session.SendAsync(request, ct);
                double acceptanceMs = clock.Elapsed.TotalMilliseconds;
                Assert.True(accepted.Ok, accepted.Message);
                Guid job = Guid.Parse(Assert.Single(accepted.Results!).JobId!);
                JobSnapshot? snapshot;
                while ((snapshot = engine.Snapshot(job))?.State is not (JobState.Completed or JobState.NeedsAttention or JobState.Failed or JobState.NeedsLink))
                    await Task.Delay(50, ct);
                Assert.True(snapshot.State == JobState.Completed, snapshot.Message);
                Assert.EndsWith(".webm", snapshot.FinalPath!);
                Assert.Equal(payload, await File.ReadAllBytesAsync(snapshot.FinalPath!, ct));
                double verifiedCompletionMs = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                BridgeResponse replay = await session.SendAsync(request, ct);
                double replayMs = clock.Elapsed.TotalMilliseconds;
                Assert.True(replay.Ok, replay.Message);
                Assert.Equal(job, Guid.Parse(Assert.Single(replay.Results!).JobId!));
                Assert.Single(engine.Snapshots());
                Assert.Single(Directory.GetFiles(downloads, "*.webm"));
                await session.CloseAsync(ct);
                Console.WriteLine("VESPER_HANDOFF " + JsonSerializer.Serialize(new
                {
                    trial, handshakeMs, discoveryMs, acceptanceMs, verifiedCompletionMs, replayMs,
                    bytes = payload.Length, verified = true, track = "isolated-native-media-select"
                }));
            }
            finally { bridge.CloseResolver(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task IsolatedChromePickerUsesRealNativeHelperAndEngine()
    {
        string? script = Environment.GetEnvironmentVariable("VESPER_QA_CHROME_RELAY_SCRIPT");
        string? helper = Environment.GetEnvironmentVariable("VESPER_QA_ISOLATED_HOST");
        if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(helper)) return;
        Assert.Equal("vesper-qa-host.exe", Path.GetFileName(helper));
        Assert.True(File.Exists(helper));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(helper)!, "VESPER.exe")));
        Assert.True(File.Exists(script));
        string pipe = "vesper-qa-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "vesper-chrome-media-qa-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        CancellationToken ct = deadline.Token;
        bool generatedReference = Environment.GetEnvironmentVariable("VESPER_QA_GENERATED_REFERENCE") == "1";
        string? generatedPath = generatedReference ? Path.Combine(root, "generated-source.zip") : null;
        bool socialReference = Environment.GetEnvironmentVariable("VESPER_QA_SOCIAL_REFERENCE") == "1";
        string url = socialReference ? "https://www.tiktok.com/@nasa/video/7665075736742530317"
            : "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.webm";
        try
        {
            if (generatedPath is not null)
            {
                byte[] payload = new byte[5_300_000];
                new Random(42).NextBytes(payload);
                using var stream = File.Create(generatedPath);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
                using var entry = archive.CreateEntry("fixture.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
                entry.Write(payload);
            }
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"), DefaultFolder = downloads,
                // Only the explicitly selected isolated local HTTP fixture may use loopback.
                AllowPrivateNetworks = generatedReference && Environment.GetEnvironmentVariable("VESPER_QA_EARLY_DIRECT") == "1"
            });
            var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
            using var desktop = new BridgeServer(pipe, bridge.HandleAsync);
            desktop.Start();
            try
            {
                var start = new ProcessStartInfo("node")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(script);
                start.Environment["VESPER_QA_PIPE"] = pipe;
                start.Environment["VESPER_QA_HOST"] = helper;
                start.Environment["VESPER_QA_RESULT"] = Path.Combine(root, "result.json");
                start.Environment["VESPER_QA_REFERENCE_URL"] = url;
                if (generatedPath is not null) start.Environment["VESPER_QA_GENERATED_FILE"] = generatedPath;
                using Process process = Process.Start(start)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct); }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                Assert.True(process.ExitCode == 0, (await stdout) + "\n" + (await stderr));
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(start.Environment["VESPER_QA_RESULT"]!, ct));
                Assert.True(result.RootElement.GetProperty("submitted").GetBoolean(), result.RootElement.ToString());
                var job = Guid.Parse(result.RootElement.GetProperty("jobId").GetString()!);
                JobSnapshot? snapshot;
                while ((snapshot = engine.Snapshot(job))?.State is not (JobState.Completed or JobState.NeedsAttention or JobState.Failed or JobState.NeedsLink))
                    await Task.Delay(50, ct);
                Assert.True(snapshot.State == JobState.Completed, snapshot.Message);
                if (generatedPath is not null)
                {
                    byte[] expected = await File.ReadAllBytesAsync(generatedPath, ct);
                    Assert.Equal(expected, await File.ReadAllBytesAsync(snapshot.FinalPath!, ct));
                    Assert.Equal("generated-native.zip", Path.GetFileName(snapshot.FinalPath));
                    Console.WriteLine($"Generated ZIP: {expected.Length} exact bytes published by VESPER.");
                }
                else if (socialReference)
                {
                    // This is a specific public NASA clip, previously acquired and
                    // independently decoded by the standalone QA campaign.
                    // A different representation is NOT counted as byte identity.
                    const string previouslyDecodedSha = "A88A8631DE898AAD0CB8AEC4FC2D004BCFE91C227268EAE3D1A885A75049A3E8";
                    await using var file = File.OpenRead(snapshot.FinalPath!);
                    string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, ct));
                    Console.WriteLine($"Social reference SHA256: {actual}; bytes: {file.Length}");
                    Assert.Equal(previouslyDecodedSha, actual);
                }
                else
                {
                    using var http = new HttpClient { MaxResponseContentBufferSize = 4 * 1024 * 1024 };
                    byte[] expected = await http.GetByteArrayAsync(url, ct);
                    Assert.InRange(expected.Length, 1, 4 * 1024 * 1024);
                    Assert.Equal(expected, await File.ReadAllBytesAsync(snapshot.FinalPath!, ct));
                }
                if (Environment.GetEnvironmentVariable("VESPER_QA_ZAI_ACQUISITION") == "1")
                {
                    JsonElement[] blogJobs = result.RootElement.GetProperty("blogJobs").EnumerateArray().ToArray();
                    int expectedBlogJobs = int.TryParse(Environment.GetEnvironmentVariable("VESPER_QA_ZAI_INDEX"), out _) ? 1 : 4;
                    Assert.Equal(expectedBlogJobs, blogJobs.Length);
                    foreach (JsonElement entry in blogJobs)
                    {
                        int index = entry.GetProperty("index").GetInt32();
                        Guid mediaJob = entry.GetProperty("jobId").GetGuid();
                        JobSnapshot? acquired;
                        while ((acquired = engine.Snapshot(mediaJob))?.State is not
                            (JobState.Completed or JobState.NeedsAttention or JobState.Failed or JobState.NeedsLink))
                            await Task.Delay(100, ct);
                        Console.WriteLine($"Blog player {index}: {acquired.State}; {acquired.TotalBytes} bytes; {acquired.Kind}; {acquired.Reason} {acquired.Message}");
                        Assert.True(acquired.State == JobState.Completed, $"Blog {index}: {acquired.Message}");
                        Assert.NotNull(acquired.FinalPath);
                        Assert.True(File.Exists(acquired.FinalPath));
                        var inspect = new ProcessStartInfo("ffprobe")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        };
                        foreach (string arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height:format=duration,size", "-of", "json", acquired.FinalPath! })
                            inspect.ArgumentList.Add(arg);
                        using Process ffprobe = Process.Start(inspect)!;
                        Task<string> probeOut = ffprobe.StandardOutput.ReadToEndAsync(ct);
                        Task<string> probeErr = ffprobe.StandardError.ReadToEndAsync(ct);
                        try { await ffprobe.WaitForExitAsync(ct); }
                        finally { if (!ffprobe.HasExited) ffprobe.Kill(entireProcessTree: true); }
                        Assert.True(ffprobe.ExitCode == 0, await probeErr);
                        using var facts = JsonDocument.Parse(await probeOut);
                        var streams = facts.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                        Assert.Contains(streams, track => track.GetProperty("codec_type").GetString() == "video");
                        Assert.Equal(index != 2, streams.Any(track => track.GetProperty("codec_type").GetString() == "audio"));
                        double duration = double.Parse(facts.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        Assert.InRange(duration, entry.GetProperty("duration").GetDouble() - 1, entry.GetProperty("duration").GetDouble() + 1);
                        Console.WriteLine($"Blog player {index}: independent FFprobe verification {facts.RootElement.GetRawText()}");
                    }
                    Assert.Equal(expectedBlogJobs + 1, engine.Snapshots().Count);
                    Assert.Equal(expectedBlogJobs + 1, Directory.GetFiles(downloads).Length);
                }
                else
                {
                    Assert.Single(engine.Snapshots());
                    Assert.Single(Directory.GetFiles(downloads));
                }
                Console.WriteLine("Real Chrome, QA relay, actual native helper, desktop handler and engine: exact bytes verified.");
            }
            finally { bridge.CloseResolver(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Process StartNativeTestProcess(string path, string? qaPipe)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (qaPipe is not null) start.Environment["VESPER_QA_PIPE"] = qaPipe;
        return Process.Start(start)!;
    }

    private sealed class NativeTestSession(string path, string? qaPipe = null) : IDisposable
    {
        private readonly Process _process = StartNativeTestProcess(path, qaPipe);

        public Task WriteAsync(object request, CancellationToken ct) =>
            BridgeProtocol.WriteFrameAsync(_process.StandardInput.BaseStream, JsonSerializer.Serialize(request), ct);

        public async Task<BridgeResponse> ReadAsync(CancellationToken ct)
        {
            string? json = await BridgeProtocol.ReadFrameAsync(_process.StandardOutput.BaseStream, ct);
            Assert.NotNull(json);
            return JsonSerializer.Deserialize<BridgeResponse>(json, BridgeProtocol.Json)!;
        }

        public async Task<BridgeResponse> SendAsync(object request, CancellationToken ct)
        {
            await WriteAsync(request, ct);
            return await ReadAsync(ct);
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(ct);
            Assert.Equal(0, _process.ExitCode);
        }

        public void Dispose()
        {
            if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(2000); }
            _process.Dispose();
        }
    }

    private static async Task<BridgeResponse> SendThroughHelperAsync(string path, object request, CancellationToken ct, string? qaPipe = null)
    {
        using Process process = StartNativeTestProcess(path, qaPipe);
        try
        {
            await BridgeProtocol.WriteFrameAsync(process.StandardInput.BaseStream, JsonSerializer.Serialize(request), ct);
            process.StandardInput.Close();
            string? json = await BridgeProtocol.ReadFrameAsync(process.StandardOutput.BaseStream, ct);
            await process.WaitForExitAsync(ct);
            Assert.Equal(0, process.ExitCode);
            Assert.NotNull(json);
            return JsonSerializer.Deserialize<BridgeResponse>(json, BridgeProtocol.Json)!;
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    private static async Task ServeNativeFixtureAsync(TcpListener server, byte[] payload, int length, bool mismatch, CancellationToken ct)
    {
        using TcpClient client = await server.AcceptTcpClientAsync(ct);
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
        string tag = mismatch ? "different-fixture" : "native-fixture";
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n"
            + $"Content-Type: application/octet-stream\r\nETag: \"{tag}\"\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(headers, ct);
            for (int offset = 0; offset < length; offset += payload.Length)
                await stream.WriteAsync(payload.AsMemory(0, Math.Min(payload.Length, length - offset)), ct);
        }
        catch (IOException) when (mismatch) { /* The engine correctly refused the response. */ }
    }

    [Fact]
    public async Task LegacyChromeActivityCannotAddBrowserOwnedDownloads()
    {
        var bridge = new BrowserBridgeHandler(null!, null!, () => null!);
        var request = new BridgeRequest("chrome-activity-test", "browser-activity", "ask", null, []);
        BridgeResponse reply = await bridge.HandleAsync(request, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Equal("chrome-owned", reply.Code);
    }

    [Fact]
    public async Task ManagedChromeConnectStillHonorsNeverCaptureRules()
    {
        var preferences = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "blocked.example", NeverCapture = true }]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        var file = new BrowserActiveFile("c-demo-profile-7-abc", "leaf.zip", 0, 256,
            DateTimeOffset.FromUnixTimeMilliseconds(1760000000000).UtcDateTime, false, false, ["blocked.example"], true);
        var request = new BridgeRequest("managed-blocked-1", "browser-managed", "ask", null, [],
            Operation: "connect", Activity: new BrowserActivity("demo-profile", [file]));

        BridgeResponse reply = await bridge.HandleAsync(request, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Equal("site-capture-disabled", reply.Code);
    }

    [Fact]
    public async Task Browser_auto_capture_refuses_every_known_host_before_engine_submission()
    {
        var preferences = new VesperPreferences
        {
            SiteRules =
            [
                new SiteRule { Host = "blocked-origin.example", NeverCapture = true },
                new SiteRule { Host = "blocked-final.example", NeverCapture = true },
                new SiteRule { Host = "blocked-page.example", NeverCapture = true }
            ]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        var cases = new[]
        {
            (Original: "https://blocked-origin.example/start?token=private", Final: "https://cdn.example/file.zip?token=private", Referrer: "https://safe.example/page"),
            (Original: "https://origin.example/start?token=private", Final: "https://blocked-final.example/file.zip?token=private", Referrer: "https://safe.example/page"),
            (Original: "https://origin.example/start?token=private", Final: "https://cdn.example/file.zip?token=private", Referrer: "https://blocked-page.example/watch")
        };

        for (int i = 0; i < cases.Length; i++)
        {
            var testCase = cases[i];
            string json = JsonSerializer.Serialize(new
            {
                v = BridgeProtocol.Version,
                id = $"browser-host-check-{i}",
                type = "browser-submit",
                mode = "download",
                items = new[]
                {
                    new
                    {
                        url = testCase.Final,
                        originalUrl = testCase.Original,
                        referer = testCase.Referrer,
                        fileName = "video.zip",
                        kind = "http"
                    }
                },
                browserProof = new { length = 1000, etag = "\"fixture\"", contentType = "application/zip" }
            });

            Assert.True(BridgeProtocol.TryParse(json, out BridgeRequest request, out string error), error);
            Assert.Equal(testCase.Original, Assert.Single(request.Items).OriginalUrl);
            BridgeResponse response = await bridge.HandleAsync(request, CancellationToken.None);

            Assert.False(response.Ok);
            Assert.Equal("site-capture-disabled", response.Code);
            Assert.DoesNotContain("private", response.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NativeMediaQueuePersistsDirectMp3Metadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-browser-mp3-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data");
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(downloads);
        const string metadata = "{\"Title\":\"Song One\",\"Artist\":\"Fixture Band\",\"Album\":\"Fixture Album\"}";
        try
        {
            var options = new EngineOptions { DataDirectory = data, DefaultFolder = downloads };
            using (var engine = DownloadEngine.Start(options))
            {
                var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
                var choice = new MediaDownloadChoice("https://cdn.example.test/song.mp3", "song.mp3", JobKind.Http,
                    null, null, Provider: "bandcamp", MediaId: "track-1", MediaMetadataJson: metadata);

                BridgeResult result = bridge.QueueMedia(choice, queue: true, requestId: "media-choice-1");

                Assert.Equal("queued", result.Status);
                Assert.NotNull(result.JobId);
            }

            using var store = JobStore.Open(Path.Combine(data, "vesper.db"));
            DownloadJob saved = Assert.Single(store.LoadAll());
            MediaMergeMetadata? savedTags = JsonSerializer.Deserialize<MediaMergeMetadata>(saved.MediaMetadataJson!);
            Assert.NotNull(savedTags);
            Assert.Equal("Song One", savedTags!.Title);
            Assert.Equal("Fixture Band", savedTags.Artist);
            Assert.Equal("Fixture Album", savedTags.Album);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BatchReviewShowsThePageTitleTypeAndDurationWithoutExposingTheSignedUrl()
    {
        const string json = """
            {"v":1,"id":"smart-review-1","type":"batch-review","mode":"ask","items":[
              {"url":"https://media.example/clip.mp4?signature=private-value",
               "reviewLabel":"Launch demo","reviewType":"MP4 video","reviewDurationSeconds":95.5},
              {"url":"https://media.example/another.mp4","reviewType":"MP4 video","reviewDurationSeconds":1e100}
            ]}
            """;
        Assert.True(BridgeProtocol.TryParse(json, out var request, out var error), error);
        Assert.Null(request.Items[0].FileName);
        Assert.Null(request.Items[1].ReviewDurationSeconds);
        OnSta(() =>
        {
            using var form = new BatchReviewForm(new VesperPreferences(), request.Items);
            var files = Assert.IsType<VesperCheckedList>(Assert.Single(form.Controls.Find("filesToReview", true)));
            Assert.Contains("MP4 video · 1:35  |  Launch demo  |  media.example", files.Items[0].ToString());
            Assert.DoesNotContain("private-value", files.Items[0].ToString());
            Assert.Contains("Duration unknown", files.Items[1].ToString());
        });
    }

    [Fact]
    public void BatchReviewShowsTheFullPageHashAndRequiresAnExplicitTrustChoice()
    {
        OnSta(() =>
        {
            string hash = new('a', 64);
            var item = new BridgeItem("https://cdn.example.test/archive.zip", "archive.zip", "http",
                "https://publisher.example/downloads", null, hash, null, null);
            using var form = new BatchReviewForm(new VesperPreferences(), [item]);
            form.Size = form.MinimumSize;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(520, 360);
            _ = form.Handle;
            Application.DoEvents();

            var files = Assert.IsType<VesperCheckedList>(Assert.Single(form.Controls.Find("filesToReview", true)));
            var trust = Assert.IsType<VesperCheckBox>(Assert.Single(form.Controls.Find("trustPageChecksums", true)));
            var details = Assert.IsType<TextBox>(Assert.Single(form.Controls.Find("pageChecksumDetails", true)));
            var footer = Assert.IsType<VesperPanel>(Assert.Single(form.Controls.Find("reviewFooter", true)));
            var download = Assert.IsType<VesperButton>(Assert.Single(form.Controls.Find("downloadReviewedFiles", true)));
            Assert.Empty(form.TrustedChecksumIndices);
            Assert.Equal(BorderStyle.None, files.BorderStyle);
            Assert.Equal(ContentAlignment.MiddleCenter, download.TextAlign);
            Assert.True(footer.ClientRectangle.Contains(download.Bounds), "The Download button is clipped by the footer.");
            Assert.Contains("page SHA-256 available", files.Items[0]!.ToString() ?? "");

            string chosenFolder = Path.Combine(Path.GetTempPath(), "vesper-review-chosen-folder");
            TextBox folder = Assert.IsType<TextBox>(Assert.Single(form.Controls.Find("destinationFolder", true)));
            Assert.IsType<VesperInputFrame>(folder.Parent);
            folder.Text = chosenFolder;
            Assert.Equal(Path.GetFullPath(chosenFolder), form.Destination);

            files.SetItemChecked(0, true);
            files.SelectedIndex = 0;
            Application.DoEvents();
            Assert.Equal(0, Assert.Single(form.SelectedIndices));
            Assert.True(trust.Enabled);
            Assert.Contains(hash, details.Text);
            Assert.Contains("https://publisher.example", details.Text);
            Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(details.RectangleToScreen(details.ClientRectangle)),
                "The checksum detail is clipped at the minimum review size.");
            Assert.Empty(form.TrustedChecksumIndices);

            trust.Checked = true;
            Assert.Equal(0, Assert.Single(form.TrustedChecksumIndices));
            trust.Checked = false;
            Assert.Empty(form.TrustedChecksumIndices);

            files.SetItemChecked(0, false);
            Application.DoEvents();
            Assert.Empty(form.SelectedIndices);
            Assert.False(download.Enabled);
        });
    }

    [Fact]
    public void BridgeForwardsOnlyChecksumsWhoseReviewIndexWasConfirmed()
    {
        var item = new BridgeItem("https://cdn.example.test/archive.zip", "archive.zip", "http",
            "https://publisher.example/downloads", null, new string('b', 64), null, null);

        Assert.True(BrowserBridgeHandler.RequiresNativeReview(new BridgeRequest("request-12345678", "submit", "download", null, [item])));
        Assert.False(BrowserBridgeHandler.RequiresNativeReview(new BridgeRequest("request-12345678", "submit", "download", null, [item with { Sha256 = null }])));
        Assert.Null(BrowserBridgeHandler.ConfirmedChecksum(item, 0, new HashSet<int>()));
        Assert.Null(BrowserBridgeHandler.ConfirmedChecksum(item, 1, new HashSet<int> { 0 }));
        Assert.Equal(item.Sha256, BrowserBridgeHandler.ConfirmedChecksum(item, 0, new HashSet<int> { 0 }));
    }

    [Theory]
    [InlineData(MediaErrorCode.NoDownloadableMedia, "no-media")]
    [InlineData(MediaErrorCode.VideoUnavailable, "unavailable")]
    [InlineData(MediaErrorCode.SourceExpired, "source-expired")]
    [InlineData(MediaErrorCode.SourceChanged, "source-changed")]
    [InlineData(MediaErrorCode.LoginRequired, "sign-in")]
    [InlineData(MediaErrorCode.AuthorizationRequired, "site-refused")]
    [InlineData(MediaErrorCode.DrmProtected, "protected")]
    [InlineData(MediaErrorCode.UnsupportedManifest, "unsupported")]
    [InlineData(MediaErrorCode.LiveStreamNotSupported, "unsupported")]
    [InlineData(MediaErrorCode.BrowserContextRequired, "browser-context")]
    [InlineData(MediaErrorCode.NetworkFailure, "network")]
    [InlineData(MediaErrorCode.InternalError, "resolver-error")]
    [InlineData(MediaErrorCode.ResponseTooLarge, "unsupported")]
    public void ResolverFailuresKeepTheirUserFacingReason(MediaErrorCode code, string expected)
    {
        Assert.Equal(expected, BrowserBridgeHandler.MapResolverError(code));
    }

    [Fact]
    public void BrowserPresetsStayConciseAndDeduplicateIdenticalRecipes()
    {
        var standalone = new MediaRepresentation
        {
            Id = "solo-1080", Url = new Uri("https://cdn.example.test/solo.webm"),
            Content = StreamContent.VideoOnly, StandaloneVideo = true, Container = "webm",
            VideoCodec = "vp9", Height = 1080, FileSize = 5_557_452
        };
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch/solo"), Provider = "test", Title = "Solo",
            Duration = TimeSpan.FromSeconds(29), Representations = [standalone]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => new VesperPreferences());
        var createOptions = typeof(BrowserBridgeHandler).GetMethod("CreateBrowserOptions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var soloResponse = (BridgeResponse)createOptions.Invoke(bridge,
            ["solo", item, item.SourceUrl.AbsoluteUri, CancellationToken.None, null])!;
        using var soloJson = JsonDocument.Parse(JsonSerializer.Serialize(soloResponse.Data, BridgeProtocol.Json));
        var soloChoices = soloJson.RootElement.GetProperty("choices");
        Assert.Equal(1, soloChoices.GetArrayLength());
        Assert.Equal("1080p · no audio", soloChoices[0].GetProperty("detail").GetString());

        var h264 = new MediaRepresentation
        {
            Id = "137", Url = new Uri("https://cdn.example.test/v1080.mp4"),
            Content = StreamContent.VideoOnly, Container = "mp4", VideoCodec = "h264",
            Height = 1080, FileSize = 1_500_000_000
        };
        var aac = new MediaRepresentation
        {
            Id = "140", Url = new Uri("https://cdn.example.test/a.m4a"),
            Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac",
            BitrateKbps = 129, FileSize = 114_500_000
        };
        var richItem = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch/rich"), Provider = "youtube", Title = "Rich",
            Duration = TimeSpan.FromMinutes(117), Representations = [h264, aac]
        };
        var richResponse = (BridgeResponse)createOptions.Invoke(bridge,
            ["rich", richItem, richItem.SourceUrl.AbsoluteUri, CancellationToken.None, null])!;
        using var richJson = JsonDocument.Parse(JsonSerializer.Serialize(richResponse.Data, BridgeProtocol.Json));
        var richChoices = richJson.RootElement.GetProperty("choices");
        Assert.Equal("1080p", richChoices[0].GetProperty("detail").GetString());
        Assert.Equal("129 kbps", richChoices[1].GetProperty("detail").GetString());
        var moreFirst = richJson.RootElement.GetProperty("more")[0].GetProperty("detail").GetString();
        Assert.Contains("file layout not confirmed", moreFirst, StringComparison.OrdinalIgnoreCase);
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The browser review UI test did not finish.");
        error?.Throw();
    }

}
