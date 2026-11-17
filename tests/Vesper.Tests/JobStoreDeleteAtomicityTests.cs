using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class JobStoreDeleteAtomicityTests
{
    [Theory]
    [InlineData("jobs")]
    [InlineData("submissions")]
    public void Failed_delete_does_not_separate_a_browser_submission_from_its_job(string failingTable)
    {
        using var h = new Harness(tweak: o => o.MaxConcurrentJobs = 0);
        const string requestId = "submission-for-atomic-delete";
        AddResult added = h.Engine.Add(new AddRequest("https://example.com/delete-transaction",
            FileName: "retained.bin", StartNow: false, RequestId: requestId));
        Assert.True(added.Ok, added.Message);
        Guid id = added.JobId;
        string database = Path.Combine(h.Data, "vesper.db");
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var trigger = db.CreateCommand();
        trigger.CommandText = $"CREATE TRIGGER block_delete BEFORE DELETE ON {failingTable} BEGIN SELECT RAISE(ABORT, 'database delete refused'); END;";
        trigger.ExecuteNonQuery();

        using var store = JobStore.Open(database);
        try
        {
            Assert.Throws<SqliteException>(() => store.Delete(id));
            Assert.Equal(id, Assert.Single(store.LoadAll()).Id);
            Assert.True(store.TryGetSubmission(requestId, out Guid savedId));
            Assert.Equal(id, savedId);
        }
        finally
        {
            trigger.CommandText = "DROP TRIGGER block_delete";
            trigger.ExecuteNonQuery();
        }

        store.Delete(id);
        Assert.Empty(store.LoadAll());
        Assert.False(store.TryGetSubmission(requestId, out _));
    }
}
