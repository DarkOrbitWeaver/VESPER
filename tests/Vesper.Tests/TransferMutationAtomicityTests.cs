using System.Reflection;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>Transfer metadata, validators and extents must never become visible before SQLite commits.</summary>
public sealed class TransferMutationAtomicityTests
{
    private static SchedulePolicy ClosedWindow()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        return new SchedulePolicy(true, now.AddHours(1), now.AddHours(2));
    }

    private static DownloadJob LiveJob(Harness harness, Guid id) =>
        ((Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Engine)!)[id];

    private static void RejectUpdates(string path, bool enable)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = enable
            ? "CREATE TRIGGER refuse_transfer_update BEFORE UPDATE ON jobs BEGIN SELECT RAISE(ABORT, 'simulated filesystem failure'); END;"
            : "DROP TRIGGER refuse_transfer_update;";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Failed_transfer_metadata_save_retains_live_job_duplicate_lookup_extents_and_bytes()
    {
        using var harness = new Harness(tweak: o => o.Schedule = ClosedWindow());
        const string original = "http://127.0.0.1:65530/transfer-original?token=old";
        const string replacement = "http://127.0.0.1:65530/transfer-replaced?token=new";
        Guid id = harness.Add(original, "protected-partial.bin", startNow: false);
        DownloadJob job = LiveJob(harness, id);
        byte[] bytes = Payloads.Make(2048, 87);
        File.WriteAllBytes(job.PartialPath, bytes);
        var initial = new ExtentSet();
        initial.Add(new ByteRange(0, bytes.LongLength));
        harness.Engine.UpdateJob(job, j => j.Extents = initial);
        JobSnapshot before = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
        string database = Path.Combine(harness.Data, "vesper.db");

        var proposedExtents = new ExtentSet();
        proposedExtents.Add(new ByteRange(0, 400));
        int invoked = 0;
        void Update(DownloadJob j)
        {
            invoked++;
            j.Extents = proposedExtents;
            j.SourceUrl = replacement;
            j.Message = "changed despite refused write";
            j.State = JobState.NeedsLink;
            j.CompletedUtc = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        }

        RejectUpdates(database, true);
        try
        {
            Assert.Throws<LocalStorageException>(() => harness.Engine.UpdateJob(job, Update));
            Assert.Equal(1, invoked);
            Assert.Same(job, LiveJob(harness, id));
            JobSnapshot after = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.SourceUrl, after.SourceUrl);
            Assert.Equal(before.CommittedBytes, after.CommittedBytes);
            Assert.Equal(before.PartialPath, after.PartialPath);
            Assert.Equal("", job.Message);
            Assert.Null(job.CompletedUtc);
            Assert.Equal(bytes, File.ReadAllBytes(job.PartialPath));
            using var stored = JobStore.Open(database);
            DownloadJob saved = Assert.Single(stored.LoadAll());
            Assert.Equal(original, saved.SourceUrl);
            Assert.Equal(initial.TotalBytes, saved.Extents.TotalBytes);
            Assert.Equal(before.State, saved.State);
        }
        finally { RejectUpdates(database, false); }

        AddResult oldDuplicate = harness.Engine.Add(new AddRequest(original, FileName: "other.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, oldDuplicate.Status);
        Assert.Equal(id, oldDuplicate.JobId);

        // Retrying after a recovered database accepts exactly one mutation; the live job stays
        // the same instance and the durable value matches (including non-idempotent fields).
        harness.Engine.UpdateJob(job, Update);
        Assert.Equal(2, invoked);
        Assert.Same(job, LiveJob(harness, id));
        Assert.Equal(replacement, job.SourceUrl);
        Assert.Equal(proposedExtents.TotalBytes, job.Extents.TotalBytes);
        using (var stored = JobStore.Open(database))
        {
            DownloadJob saved = stored.LoadAll().Single(j => j.Id == id);
            Assert.Equal(job.SourceUrl, saved.SourceUrl);
            Assert.Equal(job.Extents.TotalBytes, saved.Extents.TotalBytes);
            Assert.Equal(job.CompletedUtc, saved.CompletedUtc);
        }
        Assert.Equal(bytes, File.ReadAllBytes(job.PartialPath));
        AddResult newDuplicate = harness.Engine.Add(new AddRequest(replacement, FileName: "again.bin", StartNow: false));
        Assert.Equal(AddStatus.Duplicate, newDuplicate.Status);
        Assert.Equal(id, newDuplicate.JobId);
    }

    [Fact]
    public void Metadata_update_after_a_durable_checkpoint_must_not_erase_committed_extents()
    {
        using var harness = new Harness(tweak: o => o.Schedule = ClosedWindow());
        Guid id = harness.Add("http://127.0.0.1:65530/checkpoint-race", "checkpoint.bin", startNow: false);
        DownloadJob live = LiveJob(harness, id);
        byte[] bytes = Payloads.Make(8192, 29);
        using (var stream = new FileStream(live.PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        var committedRange = new ExtentSet();
        committedRange.Add(new ByteRange(0, bytes.Length));
        DownloadJob committed = harness.Engine.CommitCheckpoint(live, committedRange, null);
        Assert.Equal(bytes.Length, committed.Extents.TotalBytes);

        // Exercise the precise gap between the DB commit and the former later in-memory
        // update: a normal metadata save must not roll back the just-committed extents.
        harness.Engine.UpdateJob(live, j => j.Message = "Preparing next transfer");
        using var persisted = JobStore.Open(Path.Combine(harness.Data, "vesper.db"));
        DownloadJob saved = Assert.Single(persisted.LoadAll());
        Assert.Equal(bytes.Length, saved.Extents.TotalBytes);
        Assert.Equal(committed.Generation, saved.Generation);
        Assert.Equal(committed.Extents.TotalBytes, live.Extents.TotalBytes);
        Assert.Equal(bytes, File.ReadAllBytes(live.PartialPath));
    }

    [Fact]
    public async Task Progress_counts_committed_checkpoint_bytes_once()
    {
        await using var server = new FaultServer(128 * 1024);
        await server.StartAsync();
        var progress = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness? active = null;
        using var h = active = new Harness(point =>
        {
            if (point == "checkpoint.afterCommit")
                progress.TrySetResult(Assert.Single(active!.Engine.Snapshots()));
        });
        Guid id = h.Add(server.Url("/file"), "progress.bin");

        JobSnapshot snapshot = await progress.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(snapshot.CommittedBytes, 1, server.V1.Length);
        Assert.Equal(snapshot.CommittedBytes, snapshot.ProgressBytes);
        await h.WaitForStateAsync(id, JobState.Completed);
    }

    [Fact]
    public void Successful_update_invokes_mutator_once_and_keeps_saved_timestamp_identical()
    {
        using var harness = new Harness(tweak: o => o.Schedule = ClosedWindow());
        Guid id = harness.Add("http://127.0.0.1:65530/single-mutation", "single.bin", startNow: false);
        DownloadJob job = LiveJob(harness, id);
        int calls = 0;
        harness.Engine.UpdateJob(job, j =>
        {
            calls++;
            j.CompletedUtc = new DateTime(2024, 1, 1, 0, 0, calls, DateTimeKind.Utc);
            j.Generation = calls;
        });
        Assert.Equal(1, calls);
        using var store = JobStore.Open(Path.Combine(harness.Data, "vesper.db"));
        DownloadJob saved = Assert.Single(store.LoadAll());
        Assert.Equal(saved.CompletedUtc, job.CompletedUtc);
        Assert.Equal(saved.Generation, job.Generation);
        Assert.Same(job, LiveJob(harness, id));
    }

    [Fact]
    public void Publishing_a_staged_job_copies_every_public_mutable_property()
    {
        var live = new DownloadJob();
        DownloadJob staged = live.CopyForUpdate();
        foreach (PropertyInfo property in typeof(DownloadJob).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name == nameof(DownloadJob.Id) || !property.CanWrite) continue;
            object updated = DifferentValue(property.PropertyType, property.Name);
            property.SetValue(staged, updated);
        }
        live.ApplyPersistedState(staged);
        foreach (PropertyInfo property in typeof(DownloadJob).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name == nameof(DownloadJob.Id) || !property.CanWrite) continue;
            Assert.Equal(property.GetValue(staged), property.GetValue(live));
        }
    }

    private static object DifferentValue(Type type, string name)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "updated-" + name;
        if (t == typeof(long)) return 12345L;
        if (t == typeof(int)) return 34;
        if (t == typeof(bool)) return true;
        if (t == typeof(DateTime)) return new DateTime(2024, 3, 2, 1, 2, 3, DateTimeKind.Utc);
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2024, 3, 2, 1, 2, 3, TimeSpan.Zero);
        if (t == typeof(ExtentSet))
        {
            var ranges = new ExtentSet();
            ranges.Add(new ByteRange(0, 50));
            return ranges;
        }
        if (t == typeof(BrowserTransferProof)) return new BrowserTransferProof(321, "\"etag\"", "video/mp4");
        if (t.IsEnum) return Enum.GetValues(t).GetValue(Enum.GetValues(t).Length - 1)!;
        throw new InvalidOperationException("Uncovered mutable DownloadJob property: " + name + " of type " + type);
    }
}
