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
                Assert.Equal(12, rows.Length);
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
                Assert.Equal(rows.Length, engine.Snapshots().Count);
                Assert.Equal(rows.Length, Directory.GetFiles(downloads).Length);
                Assert.Equal(0, result.RootElement.GetProperty("chromeDownloads").GetInt32());
            }
            finally { bridge.CloseResolver(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
