using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace Vesper.Windows.Tests;

public sealed class BrowserBridgeTests
{
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

    [Fact]
    public async Task NativeHelperCompletesAStagedDownloadThroughTheRealDesktopBridge()
    {
        string? helper = Environment.GetEnvironmentVariable("VESPER_QA_NATIVE_HOST");
        if (string.IsNullOrWhiteSpace(helper)) return;
        Assert.True(File.Exists(helper));
        string pipe = BridgePipe.NameForCurrentUser();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
            byte[] payload = Enumerable.Repeat((byte)0x5a, 16 * 1024).ToArray();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            Task response = ServeNativeFixtureAsync(server, payload, ct);
            string url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/fixture";
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"), DefaultFolder = downloads, AllowPrivateNetworks = true
            });
            var bridge = new BrowserBridgeHandler(null!, engine, () => new VesperPreferences());
            using var desktop = new BridgeServer(pipe, bridge.HandleAsync);
            desktop.Start();

            const string transferId = "native-fixture-0001";
            BridgeResponse submit = await SendThroughHelperAsync(executable, new
            {
                v = 1, id = transferId, type = "browser-submit", mode = "download",
                items = new[] { new { url, fileName = "native-fixture.bin", kind = "http" } },
                browserProof = new { length = payload.Length, etag = "\"native-fixture\"", contentType = "application/octet-stream" }
            }, ct);
            Assert.True(submit.Ok, submit.Message);
            BridgeResult result = Assert.Single(submit.Results!);
            Assert.True(result.TransferReady, result.TransferReason);
            JobSnapshot staged = Assert.IsType<JobSnapshot>(engine.Snapshot(Guid.Parse(result.JobId!)));
            Assert.Equal(0, staged.CommittedBytes);
            Assert.Null(staged.FinalPath);

            BridgeResponse accepted = await SendThroughHelperAsync(executable,
                new { v = 1, id = "native-accept-0001", type = "browser-transfer", transferId, operation = "accept" }, ct);
            Assert.True(accepted.Ok, accepted.Message);
            BridgeResponse watched = await SendThroughHelperAsync(executable,
                new { v = 1, id = "native-watch-0001", type = "browser-transfer", transferId, operation = "watch" }, ct);
            Assert.True(watched.Ok, watched.Message);
            Assert.Equal("complete", Assert.IsType<JsonElement>(watched.Data).GetProperty("state").GetString());
            Assert.Equal(payload, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(downloads, "*.bin")), ct));
            Assert.Single(engine.Snapshots());
            await response;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<BridgeResponse> SendThroughHelperAsync(string path, object request, CancellationToken ct)
    {
        using Process process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        })!;
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

    private static async Task ServeNativeFixtureAsync(TcpListener server, byte[] payload, CancellationToken ct)
    {
        using TcpClient client = await server.AcceptTcpClientAsync(ct);
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n"
            + "Content-Type: application/octet-stream\r\nETag: \"native-fixture\"\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(payload, ct);
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
    public void BatchReviewShowsTheFullPageHashAndRequiresAnExplicitTrustChoice()
    {
        OnSta(() =>
        {
            string hash = new('a', 64);
            var item = new BridgeItem("https://cdn.example.test/archive.zip", "archive.zip", "http",
                "https://publisher.example/downloads", null, hash, null, null);
            using var form = new BatchReviewForm(new VesperPreferences(), [item]);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(520, 360);
            _ = form.Handle;
            Application.DoEvents();

            var files = Assert.IsType<CheckedListBox>(Assert.Single(form.Controls.Find("filesToReview", true)));
            var trust = Assert.IsType<CheckBox>(Assert.Single(form.Controls.Find("trustPageChecksums", true)));
            var details = Assert.IsType<TextBox>(Assert.Single(form.Controls.Find("pageChecksumDetails", true)));
            Assert.Empty(form.TrustedChecksumIndices);
            Assert.Contains("page SHA-256 available", files.Items[0]!.ToString() ?? "");

            files.SetItemChecked(0, true);
            files.SelectedIndex = 0;
            Application.DoEvents();
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
    [InlineData(MediaErrorCode.LoginRequired, "sign-in")]
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
