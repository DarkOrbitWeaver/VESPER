using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaDeliveryHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JobKind[] Both = [JobKind.Http, JobKind.Hls];

    private static DownloadJob Job(string provider, JobKind kind, bool completed, DateTime? at = null) => new()
    {
        MediaProvider = provider, Kind = kind, State = completed ? JobState.Completed : JobState.NeedsAttention,
        CompletedUtc = completed ? at ?? Now : null, UpdatedUtc = at ?? Now,
        PublishedSha256 = completed ? new string('a', 64) : null, TotalBytes = 1000,
        Reason = completed ? "" : "invalid-container"
    };

    [Fact]
    public void LearnsOnlyFromStronglyDifferentNativeOutcomesAndPreservesLocalEvidence()
    {
        var history = new MediaDeliveryHistory();
        for (int i = 0; i < 6; i++)
        {
            history.Observe(Job("adapter-a", JobKind.Http, true));
            history.Observe(Job("adapter-a", JobKind.Hls, false));
        }
        Assert.Equal(JobKind.Http, history.Preferred("adapter-a", Both, Now));
        Assert.Null(history.Preferred("unseen", Both, Now)); // one site family cannot generalize
        for (int i = 0; i < 6; i++)
        {
            history.Observe(Job("adapter-b", JobKind.Http, true));
            history.Observe(Job("adapter-b", JobKind.Hls, false));
        }
        Assert.Equal(JobKind.Http, history.Preferred("unseen", Both, Now));
        Assert.Null(history.Preferred("unseen", [JobKind.Http, JobKind.Hls, JobKind.Dash], Now));
        for (int i = 0; i < 6; i++)
        {
            history.Observe(Job("adapter-c", JobKind.Http, false));
            history.Observe(Job("adapter-c", JobKind.Hls, true));
        }
        Assert.Equal(JobKind.Hls, history.Preferred("adapter-c", Both, Now));
    }

    [Fact]
    public void NoEvidenceFromQueueAcceptanceChromeUserCancellationOrMissingPublication()
    {
        var history = new MediaDeliveryHistory();
        var queued = Job("adapter", JobKind.Http, true);
        queued.State = JobState.Queued;
        var cancelled = Job("adapter", JobKind.Http, false);
        cancelled.State = JobState.Cancelled;
        var chrome = Job("adapter", JobKind.BrowserFile, true);
        var noDigest = Job("adapter", JobKind.Http, true);
        noDigest.PublishedSha256 = null;
        var local = Job("adapter", JobKind.Http, false);
        local.Reason = "destination";
        foreach (var row in new[] { queued, cancelled, chrome, noDigest, local }) history.Observe(row);
        Assert.Equal(0, history.Count);
        var success = Job("adapter", JobKind.Http, true);
        history.Observe(success);
        history.Observe(success);
        Assert.Equal(1, history.Count);
        success.State = JobState.Queued;
        history.Observe(success);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void SpeedPreferenceRequiresComparableIndependentSuccessfulRuns()
    {
        var history = new MediaDeliveryHistory();
        const long size = 4 * 1024 * 1024;
        for (int i = 0; i < 6; i++)
        {
            var fast = Job("adapter", JobKind.Http, true);
            var slow = Job("adapter", JobKind.Hls, true);
            fast.TotalBytes = slow.TotalBytes = size;
            history.Observe(fast);
            history.Observe(slow);
            history.ObserveTiming(fast, TimeSpan.FromSeconds(2));
            history.ObserveTiming(slow, TimeSpan.FromSeconds(5));
            history.Observe(fast);
            history.Observe(slow);
            Assert.Equal(i == 5 ? JobKind.Http : (JobKind?)null,
                history.Preferred("adapter", Both, Now, size));
        }
        Assert.Null(history.Preferred("adapter", Both, Now));
        Assert.Null(history.Preferred("adapter", Both, Now, size * 8));
        Assert.Null(history.Preferred("unseen", Both, Now, size));
        history.Observe(Job("adapter", JobKind.Http, false));
        Assert.Null(history.Preferred("adapter", Both, Now, size));
    }

    [Fact]
    public void BoundedHistoryForgetsRemovedRecordsAndIgnoresOldObservations()
    {
        var history = new MediaDeliveryHistory();
        for (int i = 0; i < 300; i++) history.Observe(Job("adapter", JobKind.Http, true, Now.AddDays(-60).AddSeconds(i)));
        Assert.Equal(256, history.Count);
        var records = Enumerable.Range(0, 6).Select(_ => Job("adapter", JobKind.Http, true)).ToArray();
        foreach (var record in records) history.Observe(record);
        for (int i = 0; i < 6; i++) history.Observe(Job("adapter", JobKind.Hls, false));
        Assert.Equal(JobKind.Http, history.Preferred("adapter", Both, Now));
        history.Forget(records[0].Id);
        Assert.Null(history.Preferred("adapter", Both, Now));
        Assert.Equal(255, history.Count);
        Assert.Null(history.Preferred("adapter", Both, Now.AddDays(31)));
    }
}
