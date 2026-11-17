using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>Failed SQLite writes must never make a UI command appear to have succeeded.</summary>
public sealed class JobCommandAtomicityTests
{
    private static SchedulePolicy ClosedWindow()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        return new SchedulePolicy(true, now.AddHours(1), now.AddHours(2));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("start-now")]
    [InlineData("priority")]
    [InlineData("speed")]
    [InlineData("rename")]
    [InlineData("unverified")]
    public void A_refused_update_keeps_the_visible_job_and_stored_row_consistent(string action)
    {
        using var harness = new Harness(tweak: options => options.Schedule = ClosedWindow());
        Guid id = harness.Add("http://127.0.0.1:65530/atomic-ui", "before.bin", startNow: false);
        if (action == "resume") harness.Engine.Pause(id);
        JobSnapshot original = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));

        string database = Path.Combine(harness.Data, "vesper.db");
        ToggleSqliteFailure(database, fail: true);
        try
        {
            Assert.Throws<LocalStorageException>(() => Apply(action, harness.Engine, id));
            JobSnapshot afterFailure = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
            Assert.Equal(original.FileName, afterFailure.FileName);
            Assert.Equal(original.State, afterFailure.State);
            Assert.Equal(original.Priority, afterFailure.Priority);
            Assert.Equal(original.SpeedLimitBytes, afterFailure.SpeedLimitBytes);
            Assert.Equal(original.QueueOrder, afterFailure.QueueOrder);
            Assert.Equal(original.SourceUrl, afterFailure.SourceUrl);

            using var store = JobStore.Open(database);
            DownloadJob persisted = Assert.Single(store.LoadAll());
            Assert.Equal(original.FileName, persisted.FileName);
            Assert.Equal(original.State, persisted.State);
            Assert.Equal(original.Priority, persisted.Priority);
            Assert.Equal(original.SpeedLimitBytes, persisted.SpeedLimitBytes);
            Assert.Equal(original.QueueOrder, persisted.QueueOrder);
            Assert.Equal(original.SourceUrl, persisted.SourceUrl);
        }
        finally { ToggleSqliteFailure(database, fail: false); }

        // No failed force-start can remain scheduled behind a closed transfer window.
        // All other commands must remain usable on a subsequent successful write.
        if (action == "start-now") harness.Engine.Resume(id);
        else Apply(action, harness.Engine, id);
        JobSnapshot afterRetry = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        switch (action)
        {
            case "pause": Assert.Equal(JobState.Paused, afterRetry.State); break;
            case "resume": Assert.Equal(JobState.Queued, afterRetry.State); break;
            case "start-now": Assert.Equal(JobState.Queued, afterRetry.State); break;
            case "priority": Assert.Equal(JobPriority.High, afterRetry.Priority); break;
            case "speed": Assert.Equal(12345, afterRetry.SpeedLimitBytes); break;
            case "rename": Assert.Equal("after.bin", afterRetry.FileName); break;
            case "unverified":
                using (var persisted = JobStore.Open(database))
                    Assert.True(Assert.Single(persisted.LoadAll()).UnverifiedResumeApproved);
                break;
        }
    }

    private static void Apply(string action, DownloadEngine engine, Guid id)
    {
        switch (action)
        {
            case "pause": engine.Pause(id); break;
            case "resume": engine.Resume(id); break;
            case "start-now": engine.StartNow(id); break;
            case "priority": engine.SetPriority(id, JobPriority.High); break;
            case "speed": engine.SetSpeedLimit(id, 12345); break;
            case "rename": engine.Rename(id, "after.bin"); break;
            case "unverified": engine.ApproveUnverifiedResume(id); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static void ToggleSqliteFailure(string database, bool fail)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = fail
            ? "CREATE TRIGGER reject_user_update BEFORE UPDATE ON jobs BEGIN SELECT RAISE(ABORT, 'simulated update failure'); END;"
            : "DROP TRIGGER reject_user_update;";
        command.ExecuteNonQuery();
    }
}
