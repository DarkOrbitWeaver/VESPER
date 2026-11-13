using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private const int SmallTransferIterations = 20;
    private const int TransferIterations = 5;
    private const int QueueIterations = 3;

    private readonly record struct DiskIo(ulong ReadBytes, ulong WrittenBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WrittenBytes;
        public ulong OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    /// <summary>
    /// Reports repeated loopback transfer throughput, process allocations, peak working set and disk I/O.
    /// These synthetic local numbers are not real-network performance and must not be compared with FDM/IDM.
    /// Run: dotnet test --filter Category=Benchmark --logger "console;verbosity=detailed"
    /// </summary>
    [Fact]
    public async Task Measure_small_direct_download_latency()
    {
        const int size = 16 * 1024;
        await using var server = new FaultServer(size);
        await server.StartAsync();
        string expectedHash = Payloads.Sha256(server.V1);
        using var h = new Harness();
        var elapsed = new List<double>(SmallTransferIterations);
        var allocated = new List<double>(SmallTransferIterations);

        for (int iteration = 0; iteration < SmallTransferIterations; iteration++)
        {
            string url = server.Url($"/file?small={iteration}");
            var completed = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnJobChanged(JobSnapshot snapshot)
            {
                if (snapshot.SourceUrl == url && snapshot.State == JobState.Completed)
                    completed.TrySetResult(snapshot);
            }

            h.Engine.JobChanged += OnJobChanged;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            try
            {
                AddResult added = h.Engine.Add(new AddRequest(url, FileName: $"small-{iteration}.bin"));
                Assert.True(added.Ok, added.Message);
                JobSnapshot done = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                watch.Stop();
                Assert.Equal(added.JobId, done.Id);
                Assert.Equal(size, new FileInfo(done.FinalPath!).Length);
                elapsed.Add(watch.Elapsed.TotalMilliseconds);
                allocated.Add((GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1048576.0);
                Assert.Equal(expectedHash, await HashFileAsync(done.FinalPath!));
            }
            finally
            {
                h.Engine.JobChanged -= OnJobChanged;
            }
        }

        double[] elapsedSorted = elapsed.Order().ToArray();
        double[] allocatedSorted = allocated.Order().ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            sizeBytes = size,
            samples = elapsed.Count,
            medianMs = Median(elapsedSorted),
            p95Ms = Percentile(elapsedSorted, 0.95),
            medianAllocatedMiB = Median(allocatedSorted)
        }));
    }

    [Fact]
    public async Task Measure_sequential_versus_segmented_throughput_on_loopback()
    {
        const int size = 96 * 1024 * 1024;
        await using var server = new FaultServer(size); await server.StartAsync();
        string expectedHash = Payloads.Sha256(server.V1);
        var samples = new List<TransferSample>(TransferIterations * 2);

        for (int iteration = 0; iteration < TransferIterations; iteration++)
        {
            int[] connectionCounts = iteration % 2 == 0 ? [1, 4] : [4, 1];
            foreach (int connections in connectionCounts)
            {
                using var h = new Harness(tweak: o => o.MaxConnectionsPerJob = connections);
                using Process process = Process.GetCurrentProcess();
                long workingSetBefore = process.WorkingSet64;
                long peakWorkingSet = workingSetBefore;
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                TimeSpan cpuBefore = process.TotalProcessorTime;
                DiskIo? ioBefore = GetDiskIo(process);

                var watch = Stopwatch.StartNew();
                Guid id = h.Add(server.Url($"/file?sample={iteration}"), $"bench-{iteration}-{connections}.bin");
                JobSnapshot done = await h.WaitAsync(id, snapshot =>
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    return snapshot.State == JobState.Completed;
                }, 180);
                watch.Stop();

                process.Refresh();
                long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
                DiskIo? ioAfter = GetDiskIo(process);
                long writtenBytes = ioBefore is { } startIo && ioAfter is { } endIo
                    ? checked((long)(endIo.WrittenBytes - startIo.WrittenBytes))
                    : -1;
                long readBytes = ioBefore is { } readStart && ioAfter is { } readEnd
                    ? checked((long)(readEnd.ReadBytes - readStart.ReadBytes))
                    : -1;

                Assert.Equal(size, new FileInfo(done.FinalPath!).Length);
                Assert.Equal(expectedHash, await HashFileAsync(done.FinalPath!));

                var sample = new TransferSample(iteration + 1, connections, size,
                    watch.Elapsed.TotalMilliseconds,
                    size / 1048576.0 / watch.Elapsed.TotalSeconds,
                    allocatedBytes / 1048576.0,
                    cpu.TotalMilliseconds,
                    (peakWorkingSet - workingSetBefore) / 1048576.0,
                    writtenBytes,
                    readBytes);
                samples.Add(sample);
                output.WriteLine(JsonSerializer.Serialize(sample));
            }
        }

        output.WriteLine(JsonSerializer.Serialize(new
        {
            environment = new
            {
                os = Environment.OSVersion.VersionString,
                processorCount = Environment.ProcessorCount,
                availableMemoryMiB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0,
                runtime = RuntimeInformation.FrameworkDescription
            },
            summaries = new[] { Summarize(samples, 1), Summarize(samples, 4) }
        }));
    }

    [Fact]
    public void Measure_adding_two_thousand_queued_items()
    {
        const int count = 2_000;
        SchedulePolicy closedWindow = ClosedWindow();
        var samples = new List<QueueSample>(QueueIterations);

        for (int iteration = 1; iteration <= QueueIterations; iteration++)
        {
            using var h = new Harness(tweak: o => o.Schedule = closedWindow);
            using Process process = Process.GetCurrentProcess();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long workingSetBefore = process.WorkingSet64;
            DiskIo? ioBefore = GetDiskIo(process);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                AddResult result = h.Engine.Add(new AddRequest(
                    $"http://127.0.0.1:65530/queued?run={iteration}&id={i}", FileName: $"queued-{i}.bin", StartNow: false));
                Assert.Equal(AddStatus.Added, result.Status);
            }
            watch.Stop();
            process.Refresh();
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            long workingSetAfter = process.WorkingSet64;
            DiskIo? ioAfter = GetDiskIo(process);

            IReadOnlyList<JobSnapshot> jobs = h.Engine.Snapshots();
            Assert.Equal(count, jobs.Count);
            Assert.All(jobs, job => Assert.Equal(JobState.Queued, job.State));

            long writtenBytes = ioBefore is { } startIo && ioAfter is { } endIo
                ? checked((long)(endIo.WrittenBytes - startIo.WrittenBytes))
                : -1;
            var sample = new QueueSample(iteration, count, watch.Elapsed.TotalMilliseconds,
                count / watch.Elapsed.TotalSeconds,
                allocatedBytes / 1048576.0,
                (workingSetAfter - workingSetBefore) / 1048576.0,
                writtenBytes);
            samples.Add(sample);
            output.WriteLine(JsonSerializer.Serialize(sample));
        }

        double[] queueDiskWrites = samples.Select(sample => (double)sample.ProcessWrittenBytes)
            .Where(value => value >= 0).Order().ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            environment = new
            {
                os = Environment.OSVersion.VersionString,
                processorCount = Environment.ProcessorCount,
                runtime = RuntimeInformation.FrameworkDescription
            },
            summary = new
            {
                items = count,
                samples = samples.Count,
                medianElapsedMs = Median(samples.Select(sample => sample.ElapsedMs).Order().ToArray()),
                medianItemsPerSecond = Median(samples.Select(sample => sample.ItemsPerSecond).Order().ToArray()),
                medianAllocatedMiB = Median(samples.Select(sample => sample.AllocatedMiB).Order().ToArray()),
                medianDiskWriteBytes = queueDiskWrites.Length == 0 ? (double?)null : Median(queueDiskWrites)
            }
        }));
    }

    [Fact]
    public async Task Duplicate_index_tracks_source_refresh_removal_requeue_and_reload()
    {
        SchedulePolicy closedWindow = ClosedWindow();
        using var h = new Harness(tweak: options => options.Schedule = closedWindow);
        string oldUrl = "http://127.0.0.1:65530/old?token=one";
        string newUrl = "http://127.0.0.1:65530/new?token=two";
        AddResult first = h.Engine.Add(new AddRequest(oldUrl, FileName: "first.bin", StartNow: false,
            MediaProvider: "fixture", MediaId: "media", MediaFormatId: "format"));
        Assert.Equal(AddStatus.Added, first.Status);

        h.Engine.Pause(first.JobId);
        h.Engine.Resume(first.JobId);
        AddResult repeatedOld = h.Engine.Add(new AddRequest(oldUrl + "#part", FileName: "duplicate-old.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, repeatedOld.Status);
        Assert.Equal(first.JobId, repeatedOld.JobId);

        RescueResult refresh = await h.Engine.RefreshMediaSourceAsync(first.JobId,
            new("fixture", "media", "format", newUrl, DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.True(refresh.Accepted, refresh.Message);

        AddResult oldCopy = h.Engine.Add(new AddRequest(oldUrl, FileName: "old-copy.bin", StartNow: false));
        Assert.Equal(AddStatus.Added, oldCopy.Status);
        AddResult newDuplicate = h.Engine.Add(new AddRequest(newUrl, FileName: "duplicate-new.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, newDuplicate.Status);
        Assert.Equal(first.JobId, newDuplicate.JobId);

        h.Engine.Dispose();
        using DownloadEngine reopened = DownloadEngine.Start(new EngineOptions
        {
            DataDirectory = h.Data,
            DefaultFolder = h.Downloads,
            AllowPrivateNetworks = true,
            Schedule = closedWindow
        });

        AddResult loadedNew = reopened.Add(new AddRequest(newUrl, FileName: "loaded-new.bin", StartNow: false));
        AddResult loadedOld = reopened.Add(new AddRequest(oldUrl, FileName: "loaded-old.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, loadedNew.Status);
        Assert.Equal(first.JobId, loadedNew.JobId);
        Assert.Equal(AddStatus.Duplicate, loadedOld.Status);
        Assert.Equal(oldCopy.JobId, loadedOld.JobId);

        Assert.True(reopened.RemoveFromList(first.JobId).Removed);
        AddResult afterRemove = reopened.Add(new AddRequest(newUrl, FileName: "after-remove.bin", StartNow: false));
        Assert.Equal(AddStatus.Added, afterRemove.Status);
    }

    [Fact]
    public async Task Completed_jobs_are_removed_from_the_duplicate_index()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file?completed-index"), "completed-index.bin");

        await h.WaitAsync(id, job => job.State == JobState.Completed);

        FieldInfo? field = typeof(DownloadEngine).GetField("_duplicateCandidates",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) return;
        var candidates = Assert.IsAssignableFrom<IDictionary>(field.GetValue(h.Engine));
        Assert.Empty(candidates);
    }

    [Fact]
    public void Duplicate_index_rekeys_merge_plan_refresh()
    {
        SchedulePolicy closedWindow = ClosedWindow();
        using var h = new Harness(tweak: options => options.Schedule = closedWindow);
        const string oldUrl = "http://127.0.0.1:65530/audio-old";
        const string newUrl = "http://127.0.0.1:65530/audio-new";
        var track = new MergeTrack("audio", "audio-format", oldUrl, 1024, "m4a", "aac", "trusted-identity");
        var oldPlan = new MediaMergePlan
        {
            Provider = "fixture", MediaId = "media", OutputContainer = "m4a", Tracks = [track], RequireVideo = false
        };
        MediaMergePlan newPlan = oldPlan with { Tracks = [track with { Url = newUrl }] };
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", Output: MediaOutput.M4a, MediaPlanJson: oldPlan.ToJson()));
        Assert.Equal(AddStatus.Added, added.Status);

        RescueResult refresh = h.Engine.RefreshMergePlan(added.JobId, newPlan.ToJson());
        Assert.True(refresh.Accepted, refresh.Message);

        AddResult oldCopy = h.Engine.Add(new AddRequest(oldUrl, FileName: "old-audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", Output: MediaOutput.M4a, MediaPlanJson: oldPlan.ToJson()));
        AddResult newDuplicate = h.Engine.Add(new AddRequest(newUrl, FileName: "duplicate-audio.m4a", StartNow: false,
            Kind: JobKind.Merge, MediaProvider: "fixture", Output: MediaOutput.M4a, MediaPlanJson: newPlan.ToJson()));
        Assert.Equal(AddStatus.Added, oldCopy.Status);
        Assert.Equal(AddStatus.Duplicate, newDuplicate.Status);
        Assert.Equal(added.JobId, newDuplicate.JobId);
    }

    [Fact]
    public async Task Cancelled_duplicate_candidate_is_pruned_and_can_be_restarted()
    {
        await using var server = new FaultServer(256 * 1024);
        await server.StartAsync();
        using var h = new Harness();

        var proof = new BrowserTransferProof(1, null, "application/octet-stream");
        AddResult first = h.Engine.Add(new AddRequest(server.Url("/file"), FileName: "first.bin",
            RequestId: "queue-index:1", BrowserProof: proof));
        Assert.Equal(AddStatus.Added, first.Status);
        await h.WaitForStateAsync(first.JobId, JobState.Cancelled);

        h.Engine.Options.Schedule = ClosedWindow();
        h.Engine.SettingsChanged();
        AddResult second = h.Engine.Add(new AddRequest(server.Url("/file"), FileName: "second.bin", StartNow: false));
        Assert.Equal(AddStatus.Added, second.Status);
        Assert.True(h.Engine.RemoveFromList(second.JobId).Removed);

        Assert.True(h.Engine.RestartFromScratch(first.JobId));
        AddResult duplicate = h.Engine.Add(new AddRequest(server.Url("/file"), FileName: "duplicate.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.JobId, duplicate.JobId);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(file);
        return Convert.ToHexString(hash);
    }

    private static DiskIo? GetDiskIo(Process process)
    {
        if (!OperatingSystem.IsWindows() || !GetProcessIoCounters(process.Handle, out IoCounters counters))
            return null;
        return new DiskIo(counters.ReadBytes, counters.WrittenBytes);
    }

    private static object Summarize(List<TransferSample> samples, int connections)
    {
        double[] throughput = samples.Where(sample => sample.Connections == connections)
            .Select(sample => sample.MiBPerSecond).Order().ToArray();
        double[] allocations = samples.Where(sample => sample.Connections == connections)
            .Select(sample => sample.AllocatedMiB).Order().ToArray();
        double[] diskWrites = samples.Where(sample => sample.Connections == connections)
            .Select(sample => (double)sample.ProcessWrittenBytes).Where(value => value >= 0).Order().ToArray();
        return new
        {
            connections,
            samples = throughput.Length,
            throughputMedianMiBPerSecond = Median(throughput),
            throughputP95MiBPerSecond = Percentile(throughput, 0.95),
            allocationMedianMiB = Median(allocations),
            diskWriteMedianBytes = diskWrites.Length == 0 ? (double?)null : Median(diskWrites)
        };
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];

    private static SchedulePolicy ClosedWindow()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        return new SchedulePolicy(true, now.AddHours(1), now.AddHours(2));
    }

    private sealed record TransferSample(
        int Iteration,
        int Connections,
        int SizeBytes,
        double ElapsedMs,
        double MiBPerSecond,
        double AllocatedMiB,
        double ProcessCpuMs,
        double WorkingSetGrowthMiB,
        long ProcessWrittenBytes,
        long ProcessReadBytes);

    private sealed record QueueSample(
        int Iteration,
        int Items,
        double ElapsedMs,
        double ItemsPerSecond,
        double AllocatedMiB,
        double WorkingSetGrowthMiB,
        long ProcessWrittenBytes);
}
