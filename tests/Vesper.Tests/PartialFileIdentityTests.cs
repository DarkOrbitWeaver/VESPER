using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class PartialFileIdentityTests
{
    [Fact]
    public async Task Refused_identity_save_removes_the_new_partial_and_allows_a_fresh_retry()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add(server.Url("/file"), "identity-retry.bin", startNow: false);
        JobSnapshot queued = Assert.IsType<JobSnapshot>(h.Engine.Snapshot(id));

        string database = Path.Combine(h.Data, "vesper.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER refuse_partial_identity BEFORE UPDATE OF partial_identity ON jobs WHEN NEW.partial_identity IS NOT NULL BEGIN SELECT RAISE(ABORT, 'identity save refused'); END;";
        command.ExecuteNonQuery();

        h.Engine.StartNow(id);

        JobSnapshot refused = await h.WaitForStateAsync(id, JobState.NeedsAttention);
        Assert.Equal("storage", refused.Reason);
        Assert.False(File.Exists(queued.PartialPath));
        Assert.Equal(0, server.Hits("/file"));
        using (var store = JobStore.Open(database))
        {
            DownloadJob saved = Assert.Single(store.LoadAll());
            Assert.Equal(JobState.NeedsAttention, saved.State);
            Assert.Null(saved.PartialIdentity);
        }

        command.CommandText = "DROP TRIGGER refuse_partial_identity";
        command.ExecuteNonQuery();

        h.Engine.StartNow(id);
        JobSnapshot completed = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(completed.FinalPath!));
    }
}
