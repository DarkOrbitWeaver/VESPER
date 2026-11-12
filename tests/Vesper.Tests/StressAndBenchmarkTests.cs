using System.Diagnostics;
using System.Text.Json;
using Vesper.Engine;
using Xunit.Abstractions;

namespace Vesper.Tests;

// The handle-count check measures the whole test process.
[CollectionDefinition("Process handle checks", DisableParallelization = true)]
public sealed class ProcessHandleChecks { }

[Collection("Process handle checks")]
public class StressTests
{
    // Open file handles: HandleCount on Windows, /proc/self/fd on Linux (where HandleCount is not supported).
    internal static int OpenHandles() =>
        Process.GetCurrentProcess().HandleCount is > 0 and int count ? count
        : Directory.Exists("/proc/self/fd") ? Directory.GetFileSystemEntries("/proc/self/fd").Length : 0;

    [Fact]
    public async Task Many_concurrent_downloads_finish_byte_exact_and_do_not_leak_handles()
    {
        await using var server = new FaultServer(512 * 1024); await server.StartAsync();
        using var h = new Harness(tweak: o => { o.MaxConcurrentJobs = 6; o.MaxJobsPerHost = 6; });
        await Task.Delay(300);
        int handlesBefore = OpenHandles();

        const int jobs = 40;
        Guid[] ids = Enumerable.Range(0, jobs).Select(i => h.Add(server.Url($"/file?n={i}"), $"stress-{i}.bin")).ToArray();
        foreach (Guid id in ids)
        {
            JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 120);
            Assert.Equal(Payloads.Sha256(server.V1), Payloads.Sha256(await File.ReadAllBytesAsync(done.FinalPath!)));
        }

        Assert.Equal(jobs, h.Files().Length);                    // every file published exactly once, no staging leftovers
        h.Engine.Dispose();                                      // include connection-pool and store cleanup in the leak check
        await Task.Delay(500);                                   // let pooled connections settle
        if (handlesBefore > 0)
            Assert.True(OpenHandles() - handlesBefore < 60, $"handles grew from {handlesBefore} to {OpenHandles()}");
    }

    [Fact]
    public async Task Pausing_and_resuming_repeatedly_never_corrupts_the_result()
    {
        await using var server = new FaultServer(3 * 1024 * 1024); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/slow"), "flapping.bin");
        for (int round = 0; round < 6; round++)
        {
            await h.WaitAsync(id, s => s.State == JobState.Downloading || s.State == JobState.Completed);
            if (h.Engine.Snapshot(id)!.State == JobState.Completed) break;
            h.Engine.Pause(id);
            await h.WaitAsync(id, s => s.State is JobState.Paused or JobState.Completed);
            h.Engine.Resume(id);
        }
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 90);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }
}

[Trait("Category", "Benchmark")]
public class BenchmarkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Prints measured loopback throughput and memory. It asserts only correctness, never speed: loopback numbers
    /// say nothing about real networks and must not be compared with FDM/IDM results taken elsewhere.
    /// Run: dotnet test --filter Category=Benchmark --logger "console;verbosity=detailed"
    /// </summary>
    [Fact]
    public async Task Measure_sequential_versus_segmented_throughput_on_loopback()
    {
        const int size = 96 * 1024 * 1024;
        await using var server = new FaultServer(size); await server.StartAsync();
        foreach (int connections in new[] { 1, 4 })
        {
            using var h = new Harness(tweak: o => o.MaxConnectionsPerJob = connections);
            long memoryBefore = Process.GetCurrentProcess().WorkingSet64;
            var watch = Stopwatch.StartNew();
            Guid id = h.Add(server.Url("/file"), $"bench-{connections}.bin");
            long peakMemory = memoryBefore;
            JobSnapshot done = await h.WaitAsync(id, s =>
            {
                peakMemory = Math.Max(peakMemory, Process.GetCurrentProcess().WorkingSet64);
                return s.State == JobState.Completed;
            }, 180);
            watch.Stop();

            Assert.Equal(size, new FileInfo(done.FinalPath!).Length);
            Assert.Equal(Payloads.Sha256(server.V1), Payloads.Sha256(await File.ReadAllBytesAsync(done.FinalPath!)));
            var sample = new
            {
                connections,
                sizeBytes = size,
                elapsedMs = watch.Elapsed.TotalMilliseconds,
                mibPerSecond = size / 1048576.0 / watch.Elapsed.TotalSeconds,
                workingSetGrowthMiB = (peakMemory - memoryBefore) / 1048576.0
            };
            string json = JsonSerializer.Serialize(sample);
            output.WriteLine(json);
        }
    }
}
