using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class EngineStartupFailureTests
{
    [Theory]
    [InlineData(JobState.Downloading)]
    [InlineData(JobState.Queued)]
    public void A_startup_write_error_releases_the_database_and_does_not_change_download_state(JobState initialState)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vesper-engine-startup-" + Guid.NewGuid().ToString("N"));
        string database = Path.Combine(dir, "vesper.db");
        Directory.CreateDirectory(dir);
        try
        {
            Guid id = Guid.NewGuid();
            using (var store = JobStore.Open(database))
            {
                store.Save(new DownloadJob
                {
                    Id = id,
                    SourceUrl = "https://example.com/unfinished.zip",
                    FileName = "unfinished.zip",
                    DestinationFolder = dir,
                    PartialPath = Path.Combine(dir, "unfinished.zip.vpart"),
                    State = initialState
                });
            }

            using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                raw.Open();
                using var trigger = raw.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER reject_recovery_update BEFORE UPDATE ON jobs
                    BEGIN SELECT RAISE(ABORT, 'simulated disk/database write failure'); END;
                    """;
                trigger.ExecuteNonQuery();
            }

            var options = new EngineOptions { DataDirectory = dir, DefaultFolder = dir };
            LocalStorageException error = Assert.Throws<LocalStorageException>(() => DownloadEngine.Start(options));
            Assert.Contains("database could not be written", error.Message, StringComparison.OrdinalIgnoreCase);

            // SQLite files must not remain held open by a failed engine startup.
            if (OperatingSystem.IsWindows())
            {
                using var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.True(exclusive.Length > 0);
            }

            using var reopened = JobStore.Open(database);
            DownloadJob retained = Assert.Single(reopened.LoadAll());
            Assert.Equal(id, retained.Id);
            Assert.Equal(initialState, retained.State);
            Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
