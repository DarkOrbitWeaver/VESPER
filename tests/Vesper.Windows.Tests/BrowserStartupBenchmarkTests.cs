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

    // Test-only independent ID3v2 parser. Accept the ordinary v2.4 header
    // emitted by VESPER, and require MPEG frame sync after the tag. Any
    // unsupported/malformed header is a verification failure.
    private static int MpegAudioOffset(byte[] bytes)
    {
        int offset = 0;
        if (bytes.AsSpan().StartsWith("ID3"u8))
        {
            Assert.True(bytes.Length >= 10 && bytes[3] == 4 && bytes[4] == 0 && bytes[5] == 0);
            for (int i = 6; i < 10; i++)
            {
                Assert.True((bytes[i] & 128) == 0);
                offset = (offset << 7) | bytes[i];
            }
            Assert.InRange(offset, 1, 1000000);
            offset += 10;
        }
        Assert.True(bytes.Length >= offset + 4 && bytes[offset] == 255
            && (bytes[offset + 1] & 0xe0) == 0xe0 && (bytes[offset + 1] & 6) == 2);
        return offset;
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
        string? campaignPath = Environment.GetEnvironmentVariable("VESPER_QA_PUBLIC_CASES");
        bool publicCampaign = Environment.GetEnvironmentVariable("VESPER_QA_PUBLIC_MEDIA") == "1" && !string.IsNullOrEmpty(campaignPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(publicCampaign ? 10 : 3));
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
                bool retainedMedia = Environment.GetEnvironmentVariable("VESPER_QA_RETAINED_MEDIA") == "1";
                bool publicArchive = Environment.GetEnvironmentVariable("VESPER_QA_PUBLIC_ARCHIVE") == "1";
                Assert.True(new[] { publicMedia, retainedMedia, publicArchive }.Count(v => v) <= 1);
                Assert.Equal(publicMedia || retainedMedia || publicArchive ? 0 : 12, rows.Length);
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
                var campaignOutcomes = new List<object>();
                int publicVerified = 0;
                if (publicMedia)
                {
                    string[] addresses = publicCampaign
                        ? JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(campaignPath!, ct))!
                        : ["https://mdn.github.io/shared-assets/videos/flower.mp4", "https://mdn.github.io/shared-assets/videos/flower.webm"];
                    Assert.InRange(addresses.Length, 1, 24);
                    Assert.Equal(addresses.Length, addresses.Distinct(StringComparer.Ordinal).Count());
                    Assert.Equal(addresses.Length, mediaRows.Length);
                    foreach (string address in addresses)
                    {
                        Uri uri = new(address);
                        Assert.True(uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0
                            && uri.Fragment.Length == 0 && uri.IsDefaultPort);
                    }
                    using var client = new HttpClient(new SocketsHttpHandler
                    {
                        UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
                        ConnectTimeout = TimeSpan.FromSeconds(8)
                    }) { Timeout = TimeSpan.FromSeconds(15) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("VESPER-QA/1.0 (public media integrity verification)");
                    for (int i = 0; i < mediaRows.Length; i++)
                    {
                        var row = mediaRows[i];
                        Assert.Equal(addresses[i], row.GetProperty("address").GetString());
                        if (publicCampaign && row.GetProperty("status").GetString() != "submitted")
                        {
                            campaignOutcomes.Add(new { address = addresses[i], status = "browser-miss", evidence = row.Clone() });
                            continue;
                        }
                        try
                        {
                        Assert.Equal(addresses[i], row.GetProperty("playback").GetProperty("src").GetString());
                        Guid id = row.GetProperty("jobId").GetGuid();
                        Assert.True(ids.Add(id));
                        JobSnapshot? snapshot;
                        long publicationDeadline = Environment.TickCount64 + 30000;
                        while (true)
                        {
                            snapshot = engine.Snapshot(id);
                            Assert.NotNull(snapshot);
                            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink) break;
                            Assert.True(Environment.TickCount64 < publicationDeadline, "Native publication deadline");
                            await Task.Delay(25, ct);
                        }
                        Assert.True(snapshot.State == JobState.Completed, $"{snapshot.State}: {snapshot.Reason} {snapshot.Message}");
                        using var reference = await client.GetAsync(addresses[i], HttpCompletionOption.ResponseHeadersRead, ct);
                        reference.EnsureSuccessStatusCode();
                        int limit = (publicCampaign ? 16 : 4) * 1024 * 1024;
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
                        byte[] sourceBytes = bytes.ToArray();
                        byte[] publishedBytes = await File.ReadAllBytesAsync(snapshot.FinalPath!, ct);
                        string verification = "byte-exact";
                        if (!sourceBytes.AsSpan().SequenceEqual(publishedBytes))
                        {
                            // Original MP3 output intentionally embeds title/artwork.
                            // Check the entire unchanged encoded stream independently
                            // instead of mistaking an added ID3 tag for corruption.
                            Assert.Equal(".mp3", Path.GetExtension(snapshot.FinalPath!).ToLowerInvariant());
                            Assert.True(sourceBytes.AsSpan(MpegAudioOffset(sourceBytes))
                                .SequenceEqual(publishedBytes.AsSpan(MpegAudioOffset(publishedBytes))),
                                "The original MP3 payload was changed");
                            verification = "encoded-audio-exact";
                        }
                        var verified = new
                        {
                            address = addresses[i], bytes = bytes.Length,
                            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)),
                            publishedBytes = publishedBytes.Length,
                            publishedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(publishedBytes)),
                            verification, route = row.GetProperty("route").GetString(),
                            playback = row.GetProperty("playback"), verified = true
                        };
                        campaignOutcomes.Add(verified);
                        publicVerified++;
                        Console.WriteLine("PUBLIC_BROWSER_MEDIA " + JsonSerializer.Serialize(verified));
                        }
                        catch (Exception error) when (publicCampaign && error is not OperationCanceledException)
                        {
                            campaignOutcomes.Add(new { address = addresses[i], status = "publication-miss", reason = error.GetType().Name,
                                detail = error.Message.Length <= 256 ? error.Message : error.Message[..256] });
                        }
                    }
                }
                else Assert.Empty(mediaRows);
                if (publicCampaign)
                {
                    var report = new { attempted = mediaRows.Length, verified = publicVerified,
                        chromeDownloads = result.RootElement.GetProperty("chromeDownloads").GetInt32(),
                        nativeJobs = engine.Snapshots().Count, outcomes = campaignOutcomes };
                    string? path = Environment.GetEnvironmentVariable("VESPER_QA_CAMPAIGN_REPORT");
                    if (!string.IsNullOrEmpty(path))
                    {
                        await using var output = File.Open(path, FileMode.CreateNew, FileAccess.Write);
                        await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true }, ct);
                    }
                    Console.WriteLine("PUBLIC_CAMPAIGN " + JsonSerializer.Serialize(report));
                    Assert.Equal(mediaRows.Length, publicVerified);
                }
                var retainedRows = result.RootElement.GetProperty("retainedRows").EnumerateArray().ToArray();
                Assert.Equal(retainedMedia ? 1 : 0, retainedRows.Length);
                foreach (var row in retainedRows)
                {
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
                    string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script)!, "..", ".."));
                    using var input = File.OpenRead(Path.Combine(repo, "tests", "Vesper.Tests", "Fixtures", "Media", "instinct-mse.mp4"));
                    using var output = File.OpenRead(snapshot.FinalPath!);
                    var original = Mp4Demuxer.Read(input, input.Length, ct).OrderBy(t => t.Kind).ToArray();
                    var actual = Mp4Demuxer.Read(output, output.Length, ct).OrderBy(t => t.Kind).ToArray();
                    Assert.Equal(original.Length, actual.Length);
                    Assert.Contains(actual, t => t.Kind == TrackKind.Video);
                    Assert.Contains(actual, t => t.Kind == TrackKind.Audio);
                    int samples = 0;
                    for (int i = 0; i < original.Length; i++)
                    {
                        var a = original[i]; var b = actual[i];
                        Assert.Equal(a.Kind, b.Kind);
                        Assert.Equal(a.Codec, b.Codec);
                        Assert.Equal(a.Samples.Count, b.Samples.Count);
                        for (int n = 0; n < a.Samples.Count; n++)
                        {
                            var x = a.Samples[n]; var y = b.Samples[n];
                            Assert.Equal(x.Size, y.Size);
                            byte[] left = new byte[x.Size], right = new byte[y.Size];
                            input.Position = x.Offset; input.ReadExactly(left);
                            output.Position = y.Offset; output.ReadExactly(right);
                            Assert.Equal(left, right);
                            samples++;
                        }
                    }
                    Console.WriteLine("RETAINED_BROWSER_MEDIA " + JsonSerializer.Serialize(new { samples,
                        inputBytes = input.Length, outputBytes = output.Length, tracks = actual.Length, verified = true }));
                }
                var archiveRows = result.RootElement.GetProperty("archiveRows").EnumerateArray().ToArray();
                Assert.Equal(publicArchive ? 2 : 0, archiveRows.Length);
                if (publicArchive)
                {
                    string[] expectedAddresses = [
                        "https://github.com/octocat/Hello-World/archive/refs/heads/master.zip",
                        "https://codeload.github.com/octocat/Hello-World/zip/7fd1a60b01f91b314f59955a4e4d4e80d8edf11d"];
                    using var referenceClient = new HttpClient(new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                        ConnectTimeout = TimeSpan.FromSeconds(8)
                    }) { Timeout = TimeSpan.FromSeconds(20) };
                    referenceClient.DefaultRequestHeaders.UserAgent.ParseAdd("VESPER-QA/1.0");
                    for (int i = 0; i < archiveRows.Length; i++)
                    {
                        var row = archiveRows[i];
                        Assert.Equal(expectedAddresses[i], row.GetProperty("address").GetString());
                        Guid id = row.GetProperty("jobId").GetGuid();
                        Assert.True(ids.Add(id));
                        var saved = engine.Snapshot(id);
                        Assert.NotNull(saved);
                        Assert.Equal(JobState.Completed, saved.State);
                        string target = i == 0 ? "https://codeload.github.com/octocat/Hello-World/zip/refs/heads/master" : expectedAddresses[i];
                        using var response = await referenceClient.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
                        using var bytes = new MemoryStream();
                        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
                        {
                            byte[] block = new byte[16384];
                            int n;
                            while ((n = await stream.ReadAsync(block, ct)) > 0)
                            {
                                Assert.True(bytes.Length + n <= 2 * 1024 * 1024);
                                bytes.Write(block, 0, n);
                            }
                        }
                        byte[] expectedArchive = bytes.ToArray();
                        Assert.Equal(expectedArchive, await File.ReadAllBytesAsync(saved.FinalPath!, ct));
                        bytes.Position = 0;
                        using var archive = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read);
                        Assert.Contains(archive.Entries, e => e.Name == "README");
                        Console.WriteLine("PUBLIC_ARCHIVE " + JsonSerializer.Serialize(new
                        {
                            route = i == 0 ? "github-redirect" : "direct-codeload",
                            address = expectedAddresses[i], bytes = expectedArchive.Length,
                            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedArchive)), verified = true
                        }));
                    }
                }
                Assert.Equal(rows.Length + mediaRows.Length + retainedRows.Length + archiveRows.Length, engine.Snapshots().Count);
                Assert.Equal(rows.Length + mediaRows.Length + retainedRows.Length + archiveRows.Length, Directory.GetFiles(downloads).Length);
                Assert.Equal(0, result.RootElement.GetProperty("chromeDownloads").GetInt32());
            }
            finally { bridge.CloseResolver(); }
        }
        finally
        {
            // Chromium's child processes can release their profile handles a
            // little after the parent exits. Never mask an acquisition error.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); break; }
                catch (IOException) when (attempt < 11) { await Task.Delay(250); }
                catch (UnauthorizedAccessException) when (attempt < 11) { await Task.Delay(250); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.WriteLine("QA scratch cleanup deferred: " + ex.GetType().Name); }
            }
        }
    }
}
