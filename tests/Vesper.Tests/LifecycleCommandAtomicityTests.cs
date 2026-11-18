using System.Reflection;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>Cancellation and live-recording stop must not become visible before persistence.</summary>
public sealed class LifecycleCommandAtomicityTests
{
    private static DownloadJob Live(Harness h, Guid id) =>
        ((Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Engine)!)[id];

    private static void RefuseStateWrites(string database, bool enable)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = enable
            ? "CREATE TRIGGER block_lifecycle BEFORE UPDATE ON jobs BEGIN SELECT RAISE(ABORT, 'lifecycle storage error'); END;"
            : "DROP TRIGGER block_lifecycle;";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Failed_idle_cancel_does_not_change_a_job_or_delete_its_partial()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/cancel-write-error", "keep.bin", startNow: false);
        DownloadJob job = Live(h, id);
        byte[] savedBytes = Payloads.Make(3_333, 34);
        await File.WriteAllBytesAsync(job.PartialPath, savedBytes);
        h.OwnFixturePartial(id);
        string database = Path.Combine(h.Data, "vesper.db");
        JobState before = job.State;
        RefuseStateWrites(database, true);
        try
        {
            await Assert.ThrowsAsync<LocalStorageException>(() => h.Engine.CancelDownloadAsync(id));
            Assert.Equal(before, job.State);
            Assert.Equal(savedBytes, await File.ReadAllBytesAsync(job.PartialPath));
            using var persisted = JobStore.Open(database);
            Assert.Equal(before, Assert.Single(persisted.LoadAll()).State);
        }
        finally { RefuseStateWrites(database, false); }

        CancelDownloadResult result = await h.Engine.CancelDownloadAsync(id);
        Assert.True(result.Cancelled, result.Message);
        Assert.False(File.Exists(job.PartialPath));
        Assert.Null(h.Engine.Snapshot(id));
    }

    [Fact]
    public void Failed_idle_live_stop_does_not_lose_the_recording_cursor_or_queue_state()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        AddResult added = h.Engine.Add(new AddRequest("https://example.com/recording.m3u8",
            FileName: "recording.ts", StartNow: false, Kind: JobKind.Hls));
        Assert.True(added.Ok, added.Message);
        Guid id = added.JobId;
        DownloadJob job = Live(h, id);
        const string cursor = "{\"Playlist\":\"https://example.com/recording.m3u8\",\"Mode\":\"live-hls\",\"Offset\":4096}";
        h.Engine.UpdateJob(job, j =>
        {
            j.MediaState = cursor;
            j.State = JobState.Paused;
            j.Message = "Previous recording state";
        });
        string database = Path.Combine(h.Data, "vesper.db");
        RefuseStateWrites(database, true);
        try
        {
            Assert.Throws<LocalStorageException>(() => h.Engine.StopAndKeepLive(id));
            Assert.Equal(cursor, job.MediaState);
            Assert.Equal(JobState.Paused, job.State);
            Assert.Equal("Previous recording state", job.Message);
            using var persisted = JobStore.Open(database);
            DownloadJob saved = Assert.Single(persisted.LoadAll());
            Assert.Equal(cursor, saved.MediaState);
            Assert.Equal(JobState.Paused, saved.State);
        }
        finally { RefuseStateWrites(database, false); }

        Assert.True(h.Engine.StopAndKeepLive(id));
        Assert.Contains("\"StopRequested\":true", job.MediaState);
        Assert.Equal(JobState.Queued, job.State);
        using var accepted = JobStore.Open(database);
        DownloadJob completed = Assert.Single(accepted.LoadAll());
        Assert.Equal(job.MediaState, completed.MediaState);
        Assert.Equal(job.State, completed.State);
    }
    [Fact]
    public async Task Invalid_later_sidecar_is_detected_before_deleting_the_partial()
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://example.com/preflight-cleanup", "preflight.bin", startNow: false);
        JobSnapshot snapshot = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
        byte[] bytes = Payloads.Make(2_048, 99);
        await File.WriteAllBytesAsync(snapshot.PartialPath, bytes);
        h.OwnFixturePartial(id);
        string invalidSidecar = snapshot.PartialPath + ".vmeta";
        Directory.CreateDirectory(invalidSidecar);
        try
        {
            CancelDownloadResult refused = await h.Engine.CancelDownloadAsync(id);
            Assert.False(refused.Cancelled);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.PartialPath));
            Assert.Equal(JobState.Cancelled, h.Engine.Snapshot(id)!.State);
            Assert.True(Directory.Exists(invalidSidecar));
        }
        finally { Directory.Delete(invalidSidecar); }

        CancelDownloadResult accepted = await h.Engine.CancelDownloadAsync(id);
        Assert.True(accepted.Cancelled, accepted.Message);
        Assert.False(File.Exists(snapshot.PartialPath));
    }

    [Fact]
    public async Task Refused_running_live_stop_keeps_the_capture_active_until_a_successful_retry()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var h = new Harness();
        AddResult accepted = h.Engine.Add(new AddRequest(server.Url("/hls/live/index.m3u8"),
            FileName: "capture", Kind: JobKind.Hls));
        Assert.True(accepted.Ok, accepted.Message);
        Guid id = accepted.JobId;
        byte[] expected = FaultServer.HlsExpected();
        await h.WaitAsync(id, snapshot => snapshot.State == JobState.Downloading
            && snapshot.IsLiveRecording && snapshot.CommittedBytes == expected.Length, seconds: 20);

        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER refuse_live_stop BEFORE UPDATE ON jobs
            WHEN NEW.message = 'Finishing the recording'
            BEGIN SELECT RAISE(ABORT, 'stop request not durable'); END;
            """;
        command.ExecuteNonQuery();
        try
        {
            Assert.Throws<LocalStorageException>(() => h.Engine.StopAndKeepLive(id));
            Assert.DoesNotContain("\"StopRequested\":true", Live(h, id).MediaState);
            JobSnapshot recording = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));
            Assert.True(recording.IsLiveRecording);
            Assert.Equal(JobState.Downloading, recording.State);
        }
        finally
        {
            command.CommandText = "DROP TRIGGER refuse_live_stop";
            command.ExecuteNonQuery();
        }

        Assert.True(h.Engine.StopAndKeepLive(id));
        JobSnapshot completed = await h.WaitForStateAsync(id, JobState.Completed, seconds: 25);
        Assert.Equal("live-stopped", completed.Reason);
        Assert.Equal(expected, await File.ReadAllBytesAsync(completed.FinalPath!));
    }

}
