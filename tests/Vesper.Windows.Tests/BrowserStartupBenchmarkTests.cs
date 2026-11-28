using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using VESPER;
using Vesper.Engine;

namespace Vesper.Windows.Tests;

public sealed class BrowserStartupBenchmarkTests
{
    // Explicit opt-in: Chrome for Testing, test-only helper and a disposable
    // browser profile. No installed registration or production user data.
    public sealed class StartupFactAttribute : FactAttribute
    {
        public StartupFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_QA_STARTUP") != "1")
                Skip = "Set VESPER_QA_STARTUP=1 with CHROME_PATH, VESPER_QA_ISOLATED_HOST and VESPER_QA_STARTUP_SCRIPT.";
        }
    }

    [StartupFact]
    public async Task IsolatedChromeMeasuresClickThroughVerifiedNativePublication()
    {
        string helper = Environment.GetEnvironmentVariable("VESPER_QA_ISOLATED_HOST")!;
        string script = Environment.GetEnvironmentVariable("VESPER_QA_STARTUP_SCRIPT")!;
        Assert.True(File.Exists(helper));
        Assert.Equal("vesper-qa-host.exe", Path.GetFileName(helper));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(helper)!, "VESPER.exe")));
        Assert.True(File.Exists(script));
        string pipe = "vesper-qa-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "vesper-startup-qa-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        try
        {
            var firstReads = new ConcurrentDictionary<Guid, long>();
            var published = new ConcurrentDictionary<Guid, long>();
            long epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long origin = Stopwatch.GetTimestamp();
            long EpochMs(long stamp) => epoch + (long)Stopwatch.GetElapsedTime(origin, stamp).TotalMilliseconds;
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"), DefaultFolder = downloads, AllowPrivateNetworks = true,
                FirstBodyRead = (id, stamp) => firstReads.TryAdd(id, EpochMs(stamp))
            });
            engine.JobChanged += job =>
            {
                if (job.State == JobState.Completed && !job.FileMissing)
                    published.TryAdd(job.Id, EpochMs(Stopwatch.GetTimestamp()));
            };
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
                start.Environment["VESPER_QA_DOWNLOADS"] = downloads;
                using var process = Process.Start(start)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct); }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                Assert.True(process.ExitCode == 0, (await stdout) + "\n" + (await stderr));
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(start.Environment["VESPER_QA_RESULT"]!, ct));
                var rows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                bool publicMedia = Environment.GetEnvironmentVariable("VESPER_QA_PUBLIC_MEDIA") == "1";
                Assert.Equal(publicMedia ? 0 : 12, rows.Length);
                byte[] expected = new byte[256 * 1024];
                for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i * 31 + (i >> 8));
                var ids = new HashSet<Guid>();
                foreach (var row in rows)
                {
                    Guid id = row.GetProperty("jobId").GetGuid();
                    Assert.True(ids.Add(id));
                    long click = row.GetProperty("clickEpochMs").GetInt64();
                    JobSnapshot? snapshot = engine.Snapshot(id);
                    Assert.NotNull(snapshot);
                    Assert.Equal(JobState.Completed, snapshot.State);
                    Assert.Equal(expected, await File.ReadAllBytesAsync(snapshot.FinalPath!, ct));
                    Assert.True(firstReads.TryGetValue(id, out long first));
                    Assert.True(published.TryGetValue(id, out long publication));
                    Assert.InRange(first - click, 0, 15000);
                    Assert.InRange(publication - first, 0, 15000);
                    Console.WriteLine("VESPER_STARTUP " + JsonSerializer.Serialize(new
                    {
                        mode = row.GetProperty("mode").GetString(), delayMs = row.GetProperty("delayMs").GetInt32(),
                        trial = row.GetProperty("trial").GetInt32(), browser = row.GetProperty("timing"),
                        firstBodyMs = first - click, publicationMs = publication - click,
                        handshakeMs = row.GetProperty("handshakeMs"), requests = row.GetProperty("requests"),
                        bytes = expected.Length, verified = true
                    }));
                }
                var mediaRows = result.RootElement.GetProperty("mediaRows").EnumerateArray().ToArray();
                Assert.Equal(publicMedia ? 2 : 0, mediaRows.Length);
                if (publicMedia)
                {
                    string[] addresses = ["https://mdn.github.io/shared-assets/videos/flower.mp4",
                        "https://mdn.github.io/shared-assets/videos/flower.webm"];
                    using var client = new HttpClient(new SocketsHttpHandler
                    {
                        UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
                        ConnectTimeout = TimeSpan.FromSeconds(8)
                    }) { Timeout = TimeSpan.FromSeconds(15) };
                    for (int i = 0; i < mediaRows.Length; i++)
                    {
                        var row = mediaRows[i];
                        Assert.Equal(addresses[i], row.GetProperty("address").GetString());
                        Assert.Equal(addresses[i], row.GetProperty("playback").GetProperty("src").GetString());
                        Guid id = row.GetProperty("jobId").GetGuid();
                        Assert.True(ids.Add(id));
                        JobSnapshot? snapshot;
                        while (true)
                        {
                            snapshot = engine.Snapshot(id);
                            Assert.NotNull(snapshot);
                            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink) break;
                            await Task.Delay(25, ct);
                        }
                        Assert.True(snapshot.State == JobState.Completed, $"{snapshot.State}: {snapshot.Reason} {snapshot.Message}");
                        using var reference = await client.GetAsync(addresses[i], HttpCompletionOption.ResponseHeadersRead, ct);
                        reference.EnsureSuccessStatusCode();
                        const int limit = 4 * 1024 * 1024;
                        Assert.InRange(reference.Content.Headers.ContentLength ?? 0, 1, limit);
                        using var bytes = new MemoryStream();
                        await using (var stream = await reference.Content.ReadAsStreamAsync(ct))
                        {
                            byte[] block = new byte[65536];
                            int n;
                            while ((n = await stream.ReadAsync(block, ct)) > 0)
                            {
                                Assert.True(bytes.Length + n <= limit);
                                bytes.Write(block, 0, n);
                            }
                        }
                        Assert.Equal(bytes.ToArray(), await File.ReadAllBytesAsync(snapshot.FinalPath!, ct));
                        Console.WriteLine("PUBLIC_BROWSER_MEDIA " + JsonSerializer.Serialize(new
                        {
                            address = addresses[i], bytes = bytes.Length,
                            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())),
                            playback = row.GetProperty("playback"), verified = true
                        }));
                    }
                }
                Assert.Equal(rows.Length + mediaRows.Length, engine.Snapshots().Count);
                Assert.Equal(rows.Length + mediaRows.Length, Directory.GetFiles(downloads).Length);
                Assert.Equal(0, result.RootElement.GetProperty("chromeDownloads").GetInt32());
            }
            finally { bridge.CloseResolver(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
