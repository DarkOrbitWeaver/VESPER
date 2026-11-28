using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Vesper.Engine;
using Xunit.Abstractions;

namespace Vesper.Tests;

// Run explicitly with Category=CompetitiveBenchmark. Large payloads are generated
// in bounded blocks; the server and client share a process, so CPU/RAM are combined.
[Collection("Process handle checks")]
[Trait("Category", "CompetitiveBenchmark")]
public sealed class CompetitiveWorkloadTests(ITestOutputHelper output)
{
    private const int MiB = 1024 * 1024;
    private const int Block = 64 * 1024;
    private static readonly byte[] Pattern = Payloads.Make(Block, 83);

    public sealed class CompetitiveTheoryAttribute : TheoryAttribute
    {
        public CompetitiveTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_COMPETITIVE_BENCHMARK") != "1")
                Skip = "Set VESPER_COMPETITIVE_BENCHMARK=1 to opt in.";
        }
    }
    [CompetitiveTheory]
    [InlineData("loopback", 1024, 0, 0, 8, 0)]
    [InlineData("latency", 100, 100, 0, 8, 0)]
    [InlineData("shared-cap", 100, 0, 16, 8, 0)]
    [InlineData("two-streams", 100, 40, 0, 2, 0)]
    [InlineData("per-stream-cap", 100, 20, 0, 8, 8)]
    public async Task Measure_generated_range_workload(string workload, int sizeMiB, int latencyMs,
        int bandwidthMiB, int serverSlots, int perStreamMiB)
    {
        // Expensive workloads are explicitly opt-in.
        long size = (long)sizeMiB * MiB;
        string expectedHash = ExpectedHash(size);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listener => listener.Protocols = HttpProtocols.Http1));
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        using var slots = new SemaphoreSlim(serverSlots);
        var bandwidth = new RateLimiter();
        bandwidth.SetRate((long)bandwidthMiB * MiB);
        int requests = 0, active = 0, peak = 0;
        long served = 0;
        object gate = new();
        app.MapGet("/file.bin", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            await slots.WaitAsync(context.RequestAborted);
            lock (gate) peak = Math.Max(peak, ++active);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Block);
            try
            {
                var streamBandwidth = new RateLimiter();
                streamBandwidth.SetRate((long)perStreamMiB * MiB);
                if (latencyMs > 0) await Task.Delay(latencyMs, context.RequestAborted);
                long start = 0, end = size - 1;
                string range = context.Request.Headers.Range.ToString();
                const string tag = "\"generated-v1\"";
                string ifRange = context.Request.Headers.IfRange.ToString();
                if (range.Length > 0 && (ifRange.Length == 0 || ifRange == tag))
                {
                    var pieces = range.StartsWith("bytes=", StringComparison.Ordinal)
                        ? range[6..].Split('-') : [];
                    if (pieces.Length != 2 || !long.TryParse(pieces[0], out start)
                        || start < 0 || start >= size
                        || (pieces[1].Length > 0 && (!long.TryParse(pieces[1], out end) || end < start)))
                    {
                        context.Response.StatusCode = 416;
                        context.Response.Headers.ContentRange = $"bytes */{size}";
                        return;
                    }
                    end = Math.Min(end, size - 1);
                    context.Response.StatusCode = 206;
                    context.Response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
                }
                context.Response.Headers.ETag = tag;
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = end - start + 1;
                for (long position = start; position <= end;)
                {
                    long blockIndex = position / Block;
                    int offset = (int)(position % Block);
                    int count = (int)Math.Min(Block - offset, end - position + 1);
                    FillBlock(buffer, blockIndex);
                    await bandwidth.AcquireAsync(count, context.RequestAborted);
                    await streamBandwidth.AcquireAsync(count, context.RequestAborted);
                    await context.Response.Body.WriteAsync(buffer.AsMemory(offset, count), context.RequestAborted);
                    Interlocked.Add(ref served, count);
                    position += count;
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                lock (gate) active--;
                slots.Release();
            }
        });
        await app.StartAsync();
        var samples = new List<(int Connections, double Ms, double FirstMs, int Requests, long Served,
            double CpuMs, double PeakDeltaMiB, int PeakStreams)>();
        foreach (int connections in new[] { 1, 8, 8, 1, 1, 8 })
        {
            // Use production engine defaults, not Harness's aggressive durability cadence.
            string root = Path.Combine(Path.GetTempPath(), "vesper-competitive-" + Guid.NewGuid().ToString("N"));
            try
            {
                long firstStamp = 0;
                using var engine = DownloadEngine.Start(new EngineOptions
                {
                    DataDirectory = Path.Combine(root, "data"),
                    DefaultFolder = Path.Combine(root, "files"),
                    AllowPrivateNetworks = true, MaxConnectionsPerJob = connections,
                    FirstBodyRead = (_, stamp) => Interlocked.CompareExchange(ref firstStamp, stamp, 0)
                });
                var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.JobChanged += job =>
                {
                    if (job.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink)
                        done.TrySetResult(job);
                };
                // Wait for preceding aborted responses to release their server slots.
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    lock (gate) { if (active == 0) { peak = 0; break; } }
                    await Task.Delay(20, settle.Token);
                }
                int beforeRequests = Volatile.Read(ref requests);
                long beforeServed = Interlocked.Read(ref served);
                using var process = Process.GetCurrentProcess();
                long initialMemory = process.WorkingSet64, peakMemory = initialMemory;
                TimeSpan cpuBefore = process.TotalProcessorTime;
                long begin = Stopwatch.GetTimestamp();
                var added = engine.Add(new AddRequest(app.Urls.Single() + "/file.bin",
                    FileName: "fixture.bin"));
                Assert.True(added.Ok, added.Message);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!done.Task.IsCompleted)
                {
                    await Task.WhenAny(done.Task, Task.Delay(25, timeout.Token));
                    timeout.Token.ThrowIfCancellationRequested();
                    process.Refresh();
                    peakMemory = Math.Max(peakMemory, process.WorkingSet64);
                }
                JobSnapshot result = await done.Task;
                double elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                process.Refresh();
                peakMemory = Math.Max(peakMemory, process.WorkingSet64);
                double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                Assert.True(result.State == JobState.Completed, $"{result.State}: {result.Reason} {result.Message}");
                Assert.Equal(size, new FileInfo(result.FinalPath!).Length);
                Assert.NotEqual(0, Volatile.Read(ref firstStamp));
                double firstMs = Stopwatch.GetElapsedTime(begin, firstStamp).TotalMilliseconds;
                Assert.InRange(firstMs, 0, elapsed + 1);
                // Count discarded bytes and requests as well as useful bytes.
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    lock (gate) { if (active == 0) break; }
                    await Task.Delay(20, drain.Token);
                }
                int peakStreams; lock (gate) peakStreams = peak;
                int requestCount = Volatile.Read(ref requests) - beforeRequests;
                long sent = Interlocked.Read(ref served) - beforeServed;
                Assert.InRange(peakStreams, 1, serverSlots);
                Assert.True(sent >= size);
                await using var file = File.OpenRead(result.FinalPath!);
                Assert.Equal(expectedHash, Convert.ToHexString(await SHA256.HashDataAsync(file)));
                samples.Add((connections, elapsed, firstMs, requestCount, sent, cpuMs,
                    (peakMemory - initialMemory) / (double)MiB, peakStreams));
                output.WriteLine(JsonSerializer.Serialize(new { workload, connections, elapsed, firstMs,
                    requestCount, sent, cpuMs, peakDeltaMiB = (peakMemory - initialMemory) / (double)MiB, peakStreams }));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
        foreach (var group in samples.GroupBy(s => s.Connections))
        {
            var ordered = group.OrderBy(s => s.Ms).ToArray();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                workload, sizeMiB, latencyMs, bandwidthMiB, perStreamMiB, serverSlots, connections = group.Key,
                samples = ordered.Length, medianMs = ordered[ordered.Length / 2].Ms, tailMs = ordered[^1].Ms,
                medianFirstBodyMs = Median(group.Select(s => s.FirstMs)),
                medianRequests = Median(group.Select(s => (double)s.Requests)),
                medianServedBytes = Median(group.Select(s => (double)s.Served)),
                medianCombinedCpuMs = Median(group.Select(s => s.CpuMs)),
                maxCombinedWorkingSetDeltaMiB = group.Max(s => s.PeakDeltaMiB),
                peakStreams = group.Max(s => s.PeakStreams), sha256 = expectedHash,
                processorCount = Environment.ProcessorCount, runtime = Environment.Version.ToString()
            }));
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static void FillBlock(byte[] buffer, long index)
    {
        Pattern.CopyTo(buffer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buffer, index);
    }

    private static string ExpectedHash(long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Block];
        for (long offset = 0; offset < size; offset += Block)
        {
            FillBlock(buffer, offset / Block);
            hash.AppendData(buffer.AsSpan(0, (int)Math.Min(Block, size - offset)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
