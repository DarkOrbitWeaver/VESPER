using System.Reflection;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaSourceRefreshCancellationTests
{
    [Fact]
    public async Task CancelAtLiveRefreshCommitDoesNotInstallTheReplacement()
    {
        await using var server = new FaultServer();
        await server.StartAsync();

        using var cancellation = new CancellationTokenSource();
        var beforeCommit = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        object? engineLock = null;
        using var lockAcquired = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        Thread? lockOwner = null;
        using var harness = new Harness(failpoint: point =>
        {
            if (point != "refresh.live.beforeCommit") return;

            lockOwner = new Thread(() =>
            {
                lock (engineLock!)
                {
                    lockAcquired.Set();
                    releaseLock.Wait();
                }
            }) { IsBackground = true };
            lockOwner.Start();
            if (!lockAcquired.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test could not hold the engine lock at refresh commit.");
            beforeCommit.TrySetResult(Thread.CurrentThread);
        });

        const string oldPath = "/hls/refresh-cancel/old/index.m3u8";
        const string newPath = "/hls/refresh-cancel/new/index.m3u8";
        server.HlsPlaylists[oldPath] = LivePlaylist(100, "s100.ts");
        server.HlsFiles["/hls/refresh-cancel/old/s100.ts"] = FaultServer.HlsSegment(0);
        server.HlsPlaylists[newPath] = LivePlaylist(101, "s101.ts");
        server.HlsFiles["/hls/refresh-cancel/new/s101.ts"] = FaultServer.HlsSegment(1);

        AddResult added = harness.Engine.Add(new AddRequest(server.Url(oldPath), FileName: "refresh-cancel.ts",
            Kind: JobKind.Hls, MediaProvider: "fixture", MediaId: "live-one",
            MediaFormatId: "transport-stream", MediaExpiresAt: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(added.Ok, added.Message);

        await harness.WaitAsync(added.JobId, job => job.IsLiveRecording && job.CommittedBytes > 0);
        harness.Engine.Pause(added.JobId);
        JobSnapshot paused = await harness.WaitForStateAsync(added.JobId, JobState.Paused);

        harness.Engine.Options.MaxConcurrentJobs = 0;
        harness.Engine.SettingsChanged();

        DownloadJob before = GetJob(harness.Engine, added.JobId);
        string oldSourceUrl = before.SourceUrl;
        string oldMediaState = before.MediaState!;
        DateTimeOffset? oldExpiry = before.MediaExpiresAt;
        string oldPreviousUrls = before.PreviousUrls;
        long oldProgress = paused.ProgressBytes;
        long oldCommittedBytes = paused.CommittedBytes;
        JobState oldState = before.State;

        engineLock = typeof(DownloadEngine).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Engine)!;

        Task<RescueResult> refresh = Task.Run(() => harness.Engine.RefreshMediaSourceAsync(
            added.JobId,
            new ResolvedMediaSource("fixture", "live-one", "transport-stream", server.Url(newPath),
                DateTimeOffset.UtcNow.AddMinutes(5)),
            cancellation.Token));

        try
        {
            Thread refreshThread = await beforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool blockedOnEngineLock = SpinWait.SpinUntil(
                () => (refreshThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            Assert.True(blockedOnEngineLock, "The refresh did not wait for the held engine lock.");

            cancellation.Cancel();
            releaseLock.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        finally
        {
            cancellation.Cancel();
            releaseLock.Set();
            Assert.True(lockOwner?.Join(TimeSpan.FromSeconds(5)) ?? true, "The test lock owner did not exit.");
        }

        JobSnapshot after = harness.Engine.Snapshot(added.JobId)!;
        DownloadJob afterJob = GetJob(harness.Engine, added.JobId);
        Assert.Equal(oldSourceUrl, after.SourceUrl);
        Assert.Equal(oldMediaState, afterJob.MediaState);
        Assert.Equal(oldExpiry, afterJob.MediaExpiresAt);
        Assert.Equal(oldPreviousUrls, afterJob.PreviousUrls);
        Assert.Equal(oldProgress, after.ProgressBytes);
        Assert.Equal(oldCommittedBytes, after.CommittedBytes);
        Assert.Equal(oldState, after.State);
        Assert.Equal(1, server.Hits(newPath));
        Assert.Equal(0, server.Hits("/hls/refresh-cancel/new/s101.ts"));
    }

    private static DownloadJob GetJob(DownloadEngine engine, Guid id)
    {
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine)!;
        return jobs[id];
    }

    private static string LivePlaylist(long sequence, params string[] segments)
    {
        var lines = new List<string> { "#EXTM3U", "#EXT-X-TARGETDURATION:1", $"#EXT-X-MEDIA-SEQUENCE:{sequence}" };
        foreach (string segment in segments)
        {
            lines.Add("#EXTINF:1,");
            lines.Add(segment);
        }

        return string.Join('\n', lines) + "\n";
    }
}
