using System.Reflection;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class CancelDownloadTests
{
    [Fact]
    public async Task CancelRemovesAnUnfinishedJobAndOnlyItsExactTemporaryFiles()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = h.Add("https://example.com/first", "first.bin", startNow: false);
        Guid second = h.Add("https://example.com/second", "second.bin", startNow: false);
        JobSnapshot mine = h.Engine.Snapshot(first)!;
        JobSnapshot other = h.Engine.Snapshot(second)!;

        // The real sidecar names, not a wildcard that could match someone else's data.
        string[] owned =
        [
            mine.PartialPath, mine.PartialPath + ".vmeta", mine.PartialPath + ".vmeta.tmp",
            mine.PartialPath + ".tracks", mine.PartialPath + ".merging", mine.PartialPath + ".samples",
            mine.PartialPath + ".convert.mp4", mine.PartialPath + ".convert.opus",
            mine.PartialPath + ".convert.wav", mine.PartialPath + ".convert-audio.mp4",
            mine.PartialPath + ".convert-tagged.mp4", mine.PartialPath + ".tagged.mp3"
        ];
        foreach (string path in owned) await File.WriteAllTextAsync(path, "owned temporary content");
        await File.WriteAllTextAsync(other.PartialPath, "other job");
        string unrelated = Path.Combine(h.Downloads, "keep-original.zip");
        await File.WriteAllTextAsync(unrelated, "personal file");
        var removed = new List<Guid>();
        h.Engine.JobRemoved += removed.Add;

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(first);

        Assert.True(result.Cancelled, result.Message);
        Assert.Null(h.Engine.Snapshot(first));
        Assert.Contains(first, removed);
        Assert.All(owned, path => Assert.False(File.Exists(path), path));
        Assert.True(File.Exists(other.PartialPath));
        Assert.True(File.Exists(unrelated));
        Assert.NotNull(h.Engine.Snapshot(second));
        h.Restart();
        Assert.Null(h.Engine.Snapshot(first));
        Assert.NotNull(h.Engine.Snapshot(second));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task CancelStopsAnActiveTransferBeforeDeletingItsPartials()
    {
        await using var server = new FaultServer(16 * 1024 * 1024);
        await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/slow"), "slow.bin");
        JobSnapshot running = await h.WaitAsync(id, s => s.State == JobState.Downloading && s.ProgressBytes > 0, seconds: 15);
        string partial = running.PartialPath;

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);

        Assert.True(result.Cancelled, result.Message);
        Assert.Null(h.Engine.Snapshot(id));
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".vmeta"));
        Assert.False(File.Exists(Path.Combine(h.Downloads, "slow.bin")));
        h.Restart();
        Assert.Null(h.Engine.Snapshot(id));
    }

    [Fact]
    public async Task Active_cancel_keeps_committed_bytes_if_cleanup_cannot_be_saved()
    {
        await using var server = new FaultServer(16 * 1024 * 1024);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConnectionsPerJob = 1);
        Guid id = h.Add(server.Url("/slow"), "slow.bin");
        JobSnapshot running = await h.WaitAsync(id, s => s.State == JobState.Downloading && s.CommittedBytes > 0);

        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER refuse_cancel BEFORE UPDATE ON jobs WHEN NEW.state = 13
            BEGIN SELECT RAISE(ABORT, 'cancellation write refused'); END;
            CREATE TRIGGER refuse_delete BEFORE DELETE ON jobs
            BEGIN SELECT RAISE(ABORT, 'job removal refused'); END;
            """;
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<LocalStorageException>(() => h.Engine.CancelDownloadAsync(id));
        byte[] partial = await File.ReadAllBytesAsync(running.PartialPath);
        Assert.Equal(server.V1[..(int)running.CommittedBytes], partial[..(int)running.CommittedBytes]);
        using (JobStore store = JobStore.Open(database))
        {
            DownloadJob saved = Assert.Single(store.LoadAll());
            Assert.Equal(id, saved.Id);
            Assert.True(saved.Extents.TotalBytes >= running.CommittedBytes);
            Assert.NotEqual(JobState.Cancelled, saved.State);
        }

        command.CommandText = "DROP TRIGGER refuse_cancel; DROP TRIGGER refuse_delete;";
        command.ExecuteNonQuery();
        Assert.True((await h.Engine.CancelDownloadAsync(id)).Cancelled);
        Assert.False(File.Exists(running.PartialPath));
    }

    [Fact]
    public async Task Refused_row_delete_leaves_cleanup_pending_until_cancel_is_retried()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/hls/live/index.m3u8"),
            FileName: "capture", Kind: JobKind.Hls));
        Assert.True(added.Ok, added.Message);
        Guid id = added.JobId;
        JobSnapshot recording = await h.WaitAsync(id, s => s.IsLiveRecording && s.CommittedBytes > 0);

        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER refuse_delete BEFORE DELETE ON jobs BEGIN SELECT RAISE(ABORT, 'job removal refused'); END;";
        command.ExecuteNonQuery();

        CancelDownloadResult refused = await h.Engine.CancelDownloadAsync(id);
        Assert.False(refused.Cancelled);
        Assert.Contains("retry", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(recording.PartialPath));

        h.Restart();
        JobSnapshot parked = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        Assert.Equal(JobState.Cancelled, parked.State);
        Assert.True(parked.IsCleanupPending);
        h.Engine.Resume(id);
        h.Engine.StartNow(id);
        Assert.False(h.Engine.StopAndKeepLive(id));
        Assert.False(h.Engine.RemoveFromList(id).Removed);
        Assert.Equal(JobState.Cancelled, h.Engine.Snapshot(id)!.State);

        command.CommandText = "DROP TRIGGER refuse_delete;";
        command.ExecuteNonQuery();
        Assert.True((await h.Engine.CancelDownloadAsync(id)).Cancelled);
        Assert.Null(h.Engine.Snapshot(id));
    }

    [Fact]
    public async Task CancelRefusesToDeleteACompletedDownload()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file"), "finished.bin");
        JobSnapshot finished = await h.WaitForStateAsync(id, JobState.Completed);

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);

        Assert.False(result.Cancelled);
        Assert.NotNull(h.Engine.Snapshot(id));
        Assert.True(File.Exists(finished.FinalPath));
    }

    [Fact]
    public async Task CancelRefusesATamperedPathAndKeepsTheRecordForSafeRetry()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/file", "safe.bin", startNow: false);
        JobSnapshot snapshot = h.Engine.Snapshot(id)!;
        await File.WriteAllTextAsync(snapshot.PartialPath, "unfinished data");
        string other = Path.Combine(h.Downloads, "unrelated.bin");
        await File.WriteAllTextAsync(other, "user owned");

        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Engine)!;
        jobs[id].PartialPath = other;
        CancelDownloadResult refused = await h.Engine.CancelDownloadAsync(id);

        Assert.False(refused.Cancelled);
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(snapshot.PartialPath));
        Assert.NotNull(h.Engine.Snapshot(id));

        jobs[id].PartialPath = snapshot.PartialPath;
        CancelDownloadResult corrected = await h.Engine.CancelDownloadAsync(id);
        Assert.True(corrected.Cancelled, corrected.Message);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task CancellationFailureDoesNotClaimSuccessOrRemoveTheEntry()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/file", "blocked.bin", startNow: false);
        JobSnapshot snapshot = h.Engine.Snapshot(id)!;
        await File.WriteAllTextAsync(snapshot.PartialPath, "partial bytes");
        string blocked = snapshot.PartialPath + ".vmeta";
        Directory.CreateDirectory(blocked);
        try
        {
            CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);
            Assert.False(result.Cancelled);
            Assert.NotNull(h.Engine.Snapshot(id));
            Assert.Equal(JobState.Cancelled, h.Engine.Snapshot(id)!.State);
            Assert.True(Directory.Exists(blocked));
        }
        finally { Directory.Delete(blocked); }

        CancelDownloadResult retry = await h.Engine.CancelDownloadAsync(id);
        Assert.True(retry.Cancelled, retry.Message);
        Assert.Null(h.Engine.Snapshot(id));
    }
}
