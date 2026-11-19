using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class EnginePersistenceFailureTests
{
    [Fact]
    public async Task Failed_run_completion_write_retries_after_database_recovers()
    {
        await using var server = new FaultServer(16 * 1024 * 1024);
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = harness.Add(server.Url("/slow"), "persist-pause.bin");
        JobSnapshot downloading = await harness.WaitAsync(id,
            job => job.State == JobState.Downloading && job.ProgressBytes > 0, seconds: 15);
        string database = Path.Combine(harness.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER refuse_pause BEFORE UPDATE ON jobs WHEN NEW.state = 3 BEGIN SELECT RAISE(ABORT, 'pause state save refused'); END;";
        command.ExecuteNonQuery();

        harness.Engine.Pause(id);
        JobSnapshot stopped = await harness.WaitAsync(id,
            job => job.State == JobState.Downloading && job.Connections == 0
                && harness.Engine.ReadLog().Any(line => line.Contains("save-failed", StringComparison.Ordinal)), seconds: 15);
        using (var store = JobStore.Open(database))
        {
            DownloadJob saved = Assert.Single(store.LoadAll());
            Assert.Equal(JobState.Downloading, saved.State);
            Assert.Equal(saved.Extents.TotalBytes, stopped.CommittedBytes);
        }
        Assert.True(File.Exists(downloading.PartialPath));

        command.CommandText = "DROP TRIGGER refuse_pause";
        command.ExecuteNonQuery();

        JobSnapshot paused = await harness.WaitForStateAsync(id, JobState.Paused, seconds: 10);
        Assert.Equal(stopped.CommittedBytes, paused.CommittedBytes);
        Assert.True(File.Exists(paused.PartialPath));
        using var recoveredStore = JobStore.Open(database);
        DownloadJob recovered = Assert.Single(recoveredStore.LoadAll());
        Assert.Equal(JobState.Paused, recovered.State);
        Assert.Equal(paused.CommittedBytes, recovered.Extents.TotalBytes);
    }

    [Fact]
    public void Failed_wake_write_keeps_the_saved_wait_state_and_does_not_throw()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        AddResult added = harness.Engine.Add(new AddRequest("http://127.0.0.1:65530/wake-save-failure",
            FileName: "wake.bin", StartNow: false));
        DownloadJob live = ((Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(harness.Engine)!)[added.JobId];
        harness.Engine.UpdateJob(live, job =>
        {
            job.State = JobState.WaitingForNetwork;
            job.NextRetryUtc = DateTime.UtcNow.AddMinutes(2);
        });

        string database = Path.Combine(harness.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER refuse_wake BEFORE UPDATE ON jobs WHEN NEW.state = 0 BEGIN SELECT RAISE(ABORT, 'wake state save refused'); END;";
        command.ExecuteNonQuery();

        harness.Engine.WakeWaiting();

        Assert.Equal(JobState.WaitingForNetwork, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(added.JobId)).State);
        using var store = JobStore.Open(database);
        Assert.Equal(JobState.WaitingForNetwork, Assert.Single(store.LoadAll()).State);
    }

    [Fact]
    public async Task Wake_timer_retries_a_queued_download_after_the_start_write_is_refused()
    {
        await using var server = new FaultServer(128 * 1024);
        await server.StartAsync();
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = harness.Add(server.Url("/file"), "wake-retry.bin", startNow: false);
        DownloadJob live = ((Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(harness.Engine)!)[id];
        harness.Engine.UpdateJob(live, job =>
        {
            job.State = JobState.WaitingForRetry;
            job.NextRetryUtc = DateTime.UtcNow;
        });

        string database = Path.Combine(harness.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER refuse_start BEFORE UPDATE ON jobs
            WHEN OLD.state = 0 AND NEW.state = 1
            BEGIN SELECT RAISE(ABORT, 'start state save refused'); END;
            """;
        command.ExecuteNonQuery();

        harness.Engine.Options.MaxConcurrentJobs = 1;
        harness.Engine.ScheduleNext();

        await harness.WaitAsync(id, job => job.State == JobState.Queued
            && harness.Engine.ReadLog().Any(line => line.Contains("wake-start-failed", StringComparison.Ordinal)), seconds: 10);
        Assert.Equal(0, server.Hits("/file"));
        using (var store = JobStore.Open(database))
            Assert.Equal(JobState.Queued, Assert.Single(store.LoadAll()).State);

        command.CommandText = "DROP TRIGGER refuse_start;";
        command.ExecuteNonQuery();

        JobSnapshot complete = await harness.WaitForStateAsync(id, JobState.Completed, seconds: 10);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(complete.FinalPath!));
    }
}
