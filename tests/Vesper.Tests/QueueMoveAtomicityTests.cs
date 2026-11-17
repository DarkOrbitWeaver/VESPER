using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>A queue reorder either changes every affected row or changes none.</summary>
public sealed class QueueMoveAtomicityTests
{
    private static SchedulePolicy ClosedWindow()
    {
        TimeOnly now = TimeOnly.FromDateTime(DateTime.Now);
        return new SchedulePolicy(true, now.AddHours(1), now.AddHours(2));
    }

    [Theory]
    [InlineData(QueueMove.Up)]
    [InlineData(QueueMove.Down)]
    public void Failure_on_second_job_update_rolls_back_both_queue_positions(QueueMove move)
    {
        using var harness = new Harness(tweak: o => o.Schedule = ClosedWindow());
        Guid first = harness.Add("http://127.0.0.1:65530/queue-first", "a.bin", startNow: false);
        Guid middle = harness.Add("http://127.0.0.1:65530/queue-middle", "b.bin", startNow: false);
        Guid last = harness.Add("http://127.0.0.1:65530/queue-last", "c.bin", startNow: false);
        Guid adjacent = move == QueueMove.Up ? first : last;
        string database = Path.Combine(harness.Data, "vesper.db");
        Dictionary<Guid, long> before = SavedOrders(database);
        Assert.Equal(3, before.Count);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Before the fix, the neighbor's full row is committed independently, then
            // an error on the selected job leaves SQLite and the UI inconsistent.
            command.CommandText = $"""
                CREATE TRIGGER reject_second BEFORE UPDATE OF queue_order ON jobs
                WHEN NEW.id = '{middle:D}'
                BEGIN SELECT RAISE(ABORT, 'second queue write refused'); END;
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            Assert.Throws<LocalStorageException>(() => harness.Engine.MoveInQueue(middle, move));
            Assert.Equal(before, SavedOrders(database));
            foreach ((Guid id, long order) in before)
                Assert.Equal(order, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id)).QueueOrder);
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_second";
            command.ExecuteNonQuery();
        }

        var changed = new List<Guid>();
        harness.Engine.JobChanged += state => changed.Add(state.Id);
        harness.Engine.MoveInQueue(middle, move);
        Assert.Contains(middle, changed);
        Assert.Contains(adjacent, changed);
        Dictionary<Guid, long> after = SavedOrders(database);
        Assert.Equal(before[adjacent], after[middle]);
        Assert.Equal(before[middle], after[adjacent]);
        foreach ((Guid id, long order) in after)
            Assert.Equal(order, Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id)).QueueOrder);
    }

    [Theory]
    [InlineData(QueueMove.Top)]
    [InlineData(QueueMove.Bottom)]
    public void Refused_boundary_move_keeps_queue_order_and_allows_retry(QueueMove move)
    {
        using var harness = new Harness(tweak: o => o.Schedule = ClosedWindow());
        Guid first = harness.Add("http://127.0.0.1:65530/top-first", "a.bin", startNow: false);
        harness.Add("http://127.0.0.1:65530/top-last", "b.bin", startNow: false);
        string database = Path.Combine(harness.Data, "vesper.db");
        Dictionary<Guid, long> before = SavedOrders(database);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_boundary BEFORE UPDATE OF queue_order ON jobs BEGIN SELECT RAISE(ABORT, 'queue write refused'); END;";
            command.ExecuteNonQuery();
        }
        try
        {
            Assert.Throws<LocalStorageException>(() => harness.Engine.MoveInQueue(first, move));
            Assert.Equal(before, SavedOrders(database));
            Assert.Equal(before[first], Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(first)).QueueOrder);
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_boundary";
            command.ExecuteNonQuery();
        }
        harness.Engine.MoveInQueue(first, move);
        Assert.Equal(SavedOrders(database)[first], Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(first)).QueueOrder);
    }

    private static Dictionary<Guid, long> SavedOrders(string database)
    {
        using var store = JobStore.Open(database);
        return store.LoadAll().ToDictionary(j => j.Id, j => j.QueueOrder);
    }
}
