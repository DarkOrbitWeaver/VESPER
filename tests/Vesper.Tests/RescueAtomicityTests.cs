using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class RescueAtomicityTests
{
    private static SchedulePolicy ClosedWindow()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        return new SchedulePolicy(true, now.AddHours(1), now.AddHours(2));
    }

    [Fact]
    public async Task Local_invalid_replacement_keeps_a_running_download_active()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = harness.Add(server.Url("/small-slow"), "active-invalid-rescue.bin");
        JobSnapshot running = await harness.WaitAsync(id, job => job.State == JobState.Downloading && job.CommittedBytes > 0);

        RescueResult invalidUrl = await harness.Engine.ReplaceLinkAsync(id, "not a link");
        Assert.False(invalidUrl.Accepted);
        Assert.Equal(JobState.Downloading, invalidUrl.StateAfterAttempt);
        Assert.Equal(JobState.Downloading, harness.Engine.Snapshot(id)!.State);

        RescueResult invalidChecksum = await harness.Engine.ReplaceLinkAsync(id, server.Url("/file"), new RescueInput(Sha256: "bad"));
        Assert.False(invalidChecksum.Accepted);
        Assert.Equal(JobState.Downloading, invalidChecksum.StateAfterAttempt);
        JobSnapshot stillRunning = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        Assert.Equal(JobState.Downloading, stillRunning.State);
        Assert.Equal(running.SourceUrl, stillRunning.SourceUrl);

        await harness.WaitForStateAsync(id, JobState.Completed);
    }

    [Fact]
    public async Task Rejected_remote_proof_reports_that_the_download_was_paused()
    {
        await using var original = new FaultServer();
        await using var mirror = new FaultServer();
        await original.StartAsync();
        await mirror.StartAsync();
        using var harness = new Harness();
        Guid id = harness.Add(original.Url("/small-slow"), "active-rejected-rescue.bin");
        JobSnapshot running = await harness.WaitAsync(id, job => job.State == JobState.Downloading && job.CommittedBytes > 0);

        RescueResult result = await harness.Engine.ReplaceLinkAsync(id, mirror.Url("/file"));

        Assert.False(result.Accepted);
        Assert.Equal(RescueMode.R4, result.Mode);
        Assert.Equal(JobState.Paused, result.StateAfterAttempt);
        JobSnapshot paused = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        Assert.Equal(JobState.Paused, paused.State);
        Assert.Equal(running.SourceUrl, paused.SourceUrl);
        Assert.True(paused.CommittedBytes >= running.CommittedBytes);
        Assert.True(File.Exists(paused.PartialPath));
    }

    [Fact]
    public async Task A_rescue_probe_cannot_be_applied_after_a_restart_discards_its_prefix()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        Action? restart = null;
        using var h = new Harness(point =>
        {
            if (point == "rescue.beforeCommit") restart!();
        });
        string original = server.Url("/expires-after-truncate");
        Guid id = h.Add(original, "rescue-during-restart.bin");
        JobSnapshot stalled = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stalled.CommittedBytes > 0);
        h.Engine.Options.Schedule = ClosedWindow();
        restart = () => Assert.True(h.Engine.RestartFromScratch(id));

        RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.ReopenExpired("/expires-after-truncate", "/fresh-entry"));

        Assert.False(result.Accepted);
        JobSnapshot after = h.Engine.Snapshot(id)!;
        Assert.Equal(original, after.SourceUrl);
        Assert.Equal(0, after.CommittedBytes);
        Assert.False(File.Exists(after.PartialPath));
    }

    [Fact]
    public async Task Failed_rescue_database_write_preserves_memory_duplicate_index_and_saved_link()
    {
        using var harness = new Harness(tweak: options => options.Schedule = ClosedWindow());
        const string original = "http://127.0.0.1:65530/old?token=old";
        const string replacement = "http://127.0.0.1:65530/new?token=new";
        Guid id = harness.Add(original, "unchanged.bin", startNow: false);
        JobSnapshot before = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));

        string path = Path.Combine(harness.Data, "vesper.db");
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_rescue BEFORE UPDATE ON jobs
                BEGIN SELECT RAISE(ABORT, 'simulated write failure during rescue'); END;
                """;
            command.ExecuteNonQuery();
        }

        RescueResult refused = await harness.Engine.ReplaceLinkAsync(id, replacement);
        Assert.False(refused.Accepted);
        Assert.Equal(RescueMode.R4, refused.Mode);
        Assert.Contains("could not be saved", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saved data", refused.Message, StringComparison.OrdinalIgnoreCase);
        JobSnapshot after = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        Assert.Equal(before.SourceUrl, after.SourceUrl);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.CommittedBytes, after.CommittedBytes);
        Assert.Equal(before.Identity, after.Identity);
        Assert.Equal(before.Rescue, after.Rescue);

        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_rescue";
            command.ExecuteNonQuery();
        }

        using (var persisted = JobStore.Open(path))
            Assert.Equal(before.SourceUrl, Assert.Single(persisted.LoadAll()).SourceUrl);

        AddResult duplicate = harness.Engine.Add(new AddRequest(original, FileName: "duplicate.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, duplicate.Status);
        Assert.Equal(id, duplicate.JobId);

        // A failed rescue must not poison the next legitimate attempt.
        RescueResult retried = await harness.Engine.ReplaceLinkAsync(id, replacement);
        Assert.True(retried.Accepted, retried.Message);
        Assert.Equal(replacement, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id)).SourceUrl);

        AddResult originalAgain = harness.Engine.Add(new AddRequest(original, FileName: "original-again.bin", StartNow: false));
        AddResult duplicateReplacement = harness.Engine.Add(new AddRequest(replacement, FileName: "duplicate-replacement.bin", StartNow: false));
        Assert.Equal(AddStatus.Added, originalAgain.Status);
        Assert.Equal(AddStatus.Duplicate, duplicateReplacement.Status);
        Assert.Equal(id, duplicateReplacement.JobId);
    }

    [Fact]
    public async Task Rescue_duplicate_check_uses_the_clip_range()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        AddResult AddClip(string url, double start, double end) => harness.Engine.Add(new AddRequest(url,
            FileName: "clip.webm", MediaProvider: "generic",
            MediaPlanJson: DirectMediaPlan.WebmClip(new MediaTimeRange(start, end)), InitiallyPaused: true));

        const string original = "http://127.0.0.1:65530/original.webm";
        const string differentRangeTarget = "http://127.0.0.1:65530/different-range.webm";
        AddResult source = AddClip(original, 0, 1);
        AddResult differentRange = AddClip(differentRangeTarget, 2, 3);

        RescueResult moved = await harness.Engine.ReplaceLinkAsync(source.JobId, differentRangeTarget);
        Assert.True(moved.Accepted, moved.Message);
        Assert.Equal(differentRangeTarget, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(source.JobId)).SourceUrl);
        Assert.Equal(AddStatus.Added, differentRange.Status);

        const string sameRangeSource = "http://127.0.0.1:65530/same-range-source.webm";
        const string sameRangeTarget = "http://127.0.0.1:65530/same-range-target.webm";
        AddResult anotherSource = AddClip(sameRangeSource, 4, 5);
        AddClip(sameRangeTarget, 4, 5);

        RescueResult refused = await harness.Engine.ReplaceLinkAsync(anotherSource.JobId, sameRangeTarget);
        Assert.False(refused.Accepted);
        Assert.Equal(sameRangeSource, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(anotherSource.JobId)).SourceUrl);
    }

    [Fact]
    public async Task Failed_partial_rescue_commit_keeps_the_original_bytes_and_allows_a_later_verified_retry()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = harness.Add(server.Url("/expires-after-truncate"), "resume-after-failed-rescue.bin");
        JobSnapshot stalled = await harness.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stalled.CommittedBytes > 0);
        byte[] saved = await File.ReadAllBytesAsync(stalled.PartialPath);
        string database = Path.Combine(harness.Data, "vesper.db");

        using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_partial_rescue BEFORE UPDATE ON jobs
                BEGIN SELECT RAISE(ABORT, 'simulated disk failure during partial rescue'); END;
                """;
            command.ExecuteNonQuery();
        }

        string fresh = server.ReopenExpired("/expires-after-truncate", "/fresh-entry");
        RescueResult refused = await harness.Engine.ReplaceLinkAsync(id, fresh);
        Assert.False(refused.Accepted);
        Assert.Equal(RescueMode.R4, refused.Mode);
        Assert.Equal(stalled.CommittedBytes, refused.RetainedBytes);

        JobSnapshot unchanged = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        Assert.Equal(stalled.State, unchanged.State);
        Assert.Equal(stalled.SourceUrl, unchanged.SourceUrl);
        Assert.Equal(stalled.CommittedBytes, unchanged.CommittedBytes);
        Assert.Equal(saved, await File.ReadAllBytesAsync(stalled.PartialPath));
        using (var persisted = JobStore.Open(database))
        {
            DownloadJob stored = Assert.Single(persisted.LoadAll());
            Assert.Equal(stalled.SourceUrl, stored.SourceUrl);
            Assert.Equal(stalled.CommittedBytes, stored.Extents.TotalBytes);
        }

        using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_partial_rescue";
            command.ExecuteNonQuery();
        }

        RescueResult retried = await harness.Engine.ReplaceLinkAsync(id, fresh);
        Assert.True(retried.Accepted, retried.Message);
        JobSnapshot completed = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(completed.FinalPath!));
    }
}
