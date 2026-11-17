using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class RestartAtomicityTests
{
    [Fact]
    public async Task Database_refusal_before_restart_never_deletes_the_saved_bytes()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/atomic-restart", "keep.bin", startNow: false);
        JobSnapshot original = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        byte[] bytes = Payloads.Make(4_096, 81);
        await File.WriteAllBytesAsync(original.PartialPath, bytes);
        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER block_restart BEFORE UPDATE ON jobs BEGIN SELECT RAISE(ABORT, 'storage offline'); END;";
        command.ExecuteNonQuery();
        try
        {
            Assert.Throws<LocalStorageException>(() => h.Engine.RestartFromScratch(id));
            Assert.Equal(original.State, Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id)).State);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original.PartialPath));
            using var persisted = JobStore.Open(database);
            Assert.Equal(original.State, Assert.Single(persisted.LoadAll()).State);
        }
        finally
        {
            command.CommandText = "DROP TRIGGER block_restart";
            command.ExecuteNonQuery();
        }

        Assert.True(h.Engine.RestartFromScratch(id));
        Assert.False(File.Exists(original.PartialPath));
        Assert.Equal(JobState.Queued, Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id)).State);
    }

    [Fact]
    public async Task Failed_final_queue_save_parks_discarded_job_until_user_retries_after_restart()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        Guid id = h.Add("http://127.0.0.1:65530/atomic-restart-finish", "discard.bin", startNow: false);
        JobSnapshot original = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        await File.WriteAllBytesAsync(original.PartialPath, Payloads.Make(2_048, 88));
        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER block_queue_restart BEFORE UPDATE ON jobs WHEN NEW.state = 0 BEGIN SELECT RAISE(ABORT, 'final queue commit refused'); END;";
        command.ExecuteNonQuery();
        try
        {
            Assert.False(h.Engine.RestartFromScratch(id));
            Assert.False(File.Exists(original.PartialPath));
            JobSnapshot parked = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
            Assert.Equal(JobState.NeedsAttention, parked.State);
            Assert.Equal("restart-cleanup", parked.Reason);
            using var persisted = JobStore.Open(database);
            DownloadJob saved = Assert.Single(persisted.LoadAll());
            Assert.Equal(JobState.NeedsAttention, saved.State);
            Assert.Equal("restart-cleanup", saved.Reason);
        }
        finally
        {
            command.CommandText = "DROP TRIGGER block_queue_restart";
            command.ExecuteNonQuery();
        }

        h.Restart();
        JobSnapshot recovered = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        Assert.Equal(JobState.NeedsAttention, recovered.State);
        Assert.Equal("restart-cleanup", recovered.Reason);
        Assert.False(File.Exists(original.PartialPath));
        h.Engine.Resume(id);
        h.Engine.StartNow(id);
        Assert.Equal(JobState.NeedsAttention, h.Engine.Snapshot(id)!.State);
        RescueResult refused = await h.Engine.ReplaceLinkAsync(id, "http://127.0.0.1:65530/updated");
        Assert.False(refused.Accepted);
        Assert.Contains("Restart from scratch", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("restart-cleanup", h.Engine.Snapshot(id)!.Reason);
    }

    [Fact]
    public async Task Unsafe_temporary_path_is_never_deleted_when_restarting()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/restart-tamper", "safe.bin", startNow: false);
        JobSnapshot snapshot = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        await File.WriteAllTextAsync(snapshot.PartialPath, "genuine partial");
        string outside = Path.Combine(h.Downloads, "keep-my-own-file.bin");
        await File.WriteAllTextAsync(outside, "unrelated");
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        jobs[id].PartialPath = outside;

        Assert.False(h.Engine.RestartFromScratch(id));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(outside));
        Assert.Equal("genuine partial", await File.ReadAllTextAsync(snapshot.PartialPath));
        Assert.Equal(JobState.NeedsAttention, h.Engine.Snapshot(id)!.State);
    }

    [Fact]
    public void A_parked_restart_does_not_accept_a_live_Hls_stop_as_a_backdoor_requeue()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        AddResult added = h.Engine.Add(new AddRequest("http://127.0.0.1:65530/live.m3u8",
            FileName: "capture.ts", Kind: JobKind.Hls, StartNow: false));
        Assert.True(added.Ok, added.Message);
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        DownloadJob job = jobs[added.JobId];
        const string cursor = "{\"Playlist\":\"http://127.0.0.1:65530/live.m3u8\",\"Mode\":\"live-hls\",\"Offset\":1024}";
        h.Engine.UpdateJob(job, j =>
        {
            j.State = JobState.NeedsAttention;
            j.Reason = "restart-cleanup";
            j.MediaState = cursor;
        });

        Assert.False(h.Engine.StopAndKeepLive(added.JobId));
        Assert.Equal(JobState.NeedsAttention, h.Engine.Snapshot(added.JobId)!.State);
        Assert.Equal("restart-cleanup", job.Reason);
        Assert.Equal(cursor, job.MediaState);
    }

}
