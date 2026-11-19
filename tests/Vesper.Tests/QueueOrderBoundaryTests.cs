using Microsoft.Data.Sqlite;
using System.Reflection;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class QueueOrderBoundaryTests
{
    [Fact]
    public async Task Start_now_rebases_before_moving_a_job_to_the_front()
    {
        await using var server = new FaultServer(16 * 1024 * 1024);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = h.Add(server.Url("/slow-first"), "first.bin", startNow: false);
        Guid selected = h.Add(server.Url("/slow-selected"), "selected.bin", startNow: false);
        SetQueueOrders(h, (first, long.MinValue), (selected, 0));

        h.Engine.StartNow(selected);
        Assert.Equal(-1L, h.Engine.Snapshot(selected)!.QueueOrder);
        Assert.Equal(0L, h.Engine.Snapshot(first)!.QueueOrder);

        h.Engine.Pause(selected);
        JobSnapshot paused = await h.WaitForStateAsync(selected, JobState.Paused);
        Assert.Equal(-1L, paused.QueueOrder);
        Assert.Equal(-1L, SavedQueueOrders(h)[selected]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_rebases_at_signed_queue_order_boundaries(bool startNow)
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = h.Add("https://example.com/queue-first", "first.bin", startNow: false);
        Guid middle = h.Add("https://example.com/queue-middle", "middle.bin", startNow: false);
        Guid last = h.Add("https://example.com/queue-last", "last.bin", startNow: false);
        SetQueueOrders(h, startNow
            ? [(first, long.MinValue), (middle, 0), (last, long.MaxValue)]
            : [(first, long.MinValue), (middle, long.MaxValue - 1), (last, long.MaxValue)]);

        AddResult added = h.Engine.Add(new AddRequest("https://example.com/queue-added",
            FileName: "added.bin", StartNow: startNow));

        Assert.True(added.Ok, added.Message);
        Assert.Equal(startNow ? -1 : 3, h.Engine.Snapshot(added.JobId)!.QueueOrder);
        Assert.Equal(new long[] { 0, 1, 2 }, new[] { first, middle, last }.Select(id => h.Engine.Snapshot(id)!.QueueOrder));

        Dictionary<Guid, long> saved = SavedQueueOrders(h);
        Assert.Equal(startNow ? -1L : 3L, saved[added.JobId]);
        Assert.Equal(new long[] { 0, 1, 2 }, new[] { first, middle, last }.Select(id => saved[id]));
    }

    [Fact]
    public void Refused_rebase_rolls_back_every_queue_order_and_allows_retry()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = h.Add("https://example.com/queue-first", "first.bin", startNow: false);
        Guid last = h.Add("https://example.com/queue-last", "last.bin", startNow: false);
        SetQueueOrders(h, (first, long.MaxValue - 1), (last, long.MaxValue));
        Dictionary<Guid, long> before = SavedQueueOrders(h);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(h.Data, "vesper.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER reject_queue_rebase BEFORE UPDATE OF queue_order ON jobs " +
            $"WHEN NEW.id = '{last:D}' BEGIN SELECT RAISE(ABORT, 'queue rebase refused'); END;";
        command.ExecuteNonQuery();
        try
        {
            Assert.Throws<LocalStorageException>(() => h.Engine.Add(new AddRequest("https://example.com/queue-added",
                FileName: "added.bin", StartNow: false)));

            Assert.Equal(before, SavedQueueOrders(h));
            Assert.Equal(before.Count, h.Engine.Snapshots().Count);
            foreach ((Guid id, long order) in before)
                Assert.Equal(order, h.Engine.Snapshot(id)!.QueueOrder);
        }
        finally
        {
            command.CommandText = "DROP TRIGGER reject_queue_rebase";
            command.ExecuteNonQuery();
        }

        AddResult retried = h.Engine.Add(new AddRequest("https://example.com/queue-added",
            FileName: "added.bin", StartNow: false));

        Assert.True(retried.Ok, retried.Message);
        Assert.Equal(1L, h.Engine.Snapshot(last)!.QueueOrder);
        Assert.Equal(2L, h.Engine.Snapshot(retried.JobId)!.QueueOrder);
        Assert.Equal(new long[] { 0, 1, 2 }, h.Engine.Snapshots().Select(job => job.QueueOrder));
    }

    [Fact]
    public void Duplicate_orders_are_normalized_in_creation_order_and_saved_on_restart()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid older = h.Add("https://example.com/queue-older", "older.bin", startNow: false);
        Guid newer = h.Add("https://example.com/queue-newer", "newer.bin", startNow: false);
        SetQueueOrders(h, (older, 7), (newer, 7));
        SetCreatedUtc(h, older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SetCreatedUtc(h, newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);

        IReadOnlyList<JobSnapshot> snapshots = h.Engine.Snapshots();
        Dictionary<Guid, long> saved = SavedQueueOrders(h);
        Assert.Equal(new[] { older, newer }, snapshots.Select(job => job.Id));
        Assert.Equal(new long[] { 0, 1 }, snapshots.Select(job => job.QueueOrder));
        Assert.Equal(0L, saved[older]);
        Assert.Equal(1L, saved[newer]);
    }

    [Fact]
    public void Signed_boundary_orders_are_rebased_on_restart()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid first = h.Add("https://example.com/queue-first", "first.bin", startNow: false);
        Guid last = h.Add("https://example.com/queue-last", "last.bin", startNow: false);
        SetQueueOrders(h, (first, long.MinValue), (last, long.MaxValue));

        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);

        Dictionary<Guid, long> saved = SavedQueueOrders(h);
        Assert.Equal(0L, saved[first]);
        Assert.Equal(1L, saved[last]);
        Assert.Equal(new long[] { 0, 1 }, h.Engine.Snapshots().Select(job => job.QueueOrder));
    }

    private static void SetQueueOrders(Harness h, params (Guid JobId, long Order)[] orders)
    {
        using (JobStore store = JobStore.Open(Path.Combine(h.Data, "vesper.db")))
            store.SaveQueueOrders(orders);

        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        foreach ((Guid id, long order) in orders) jobs[id].QueueOrder = order;
    }

    private static void SetCreatedUtc(Harness h, Guid id, DateTime createdUtc)
    {
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        h.Engine.UpdateJob(jobs[id], job => job.CreatedUtc = createdUtc);
    }

    private static Dictionary<Guid, long> SavedQueueOrders(Harness h)
    {
        using var store = JobStore.Open(Path.Combine(h.Data, "vesper.db"));
        return store.LoadAll().ToDictionary(job => job.Id, job => job.QueueOrder);
    }
}
