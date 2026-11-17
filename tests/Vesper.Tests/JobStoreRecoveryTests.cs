using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class JobStoreRecoveryTests
{
    [Fact]
    public void An_operational_migration_error_does_not_quarantine_or_replace_the_database()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-db-recovery-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        Directory.CreateDirectory(directory);
        try
        {
            using (JobStore.Open(path)) { }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 4; PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            string originalHash = Hash(path);
            SqliteException error = Assert.Throws<SqliteException>(() => JobStore.Open(path));

            Assert.NotEqual(11, error.SqliteErrorCode);
            Assert.NotEqual(26, error.SqliteErrorCode);
            Assert.True(File.Exists(path));
            Assert.Equal(originalHash, Hash(path));
            Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_exclusively_locked_database_is_never_quarantined()
    {
        // Windows file-sharing denial is an operational failure, not evidence of corrupt data.
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), "vesper-db-locked-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        Directory.CreateDirectory(directory);
        try
        {
            using (JobStore.Open(path)) { }
            string originalHash = Hash(path);
            using (var lockOwner = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                SqliteException error = Assert.Throws<SqliteException>(() => JobStore.Open(path));
                Assert.NotEqual(11, error.SqliteErrorCode);
                Assert.NotEqual(26, error.SqliteErrorCode);
                Assert.True(File.Exists(path));
                Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
            }

            Assert.Equal(originalHash, Hash(path));
            using var reopened = JobStore.Open(path);
            Assert.False(reopened.RecoveredFromCorruption);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Genuinely_invalid_database_is_preserved_in_quarantine_and_recreated()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-db-corrupt-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        Directory.CreateDirectory(directory);
        byte[] original = "This is not a SQLite database"u8.ToArray();
        try
        {
            File.WriteAllBytes(path, original);

            string backup;
            using (JobStore recovered = JobStore.Open(path))
            {
                Assert.True(recovered.RecoveredFromCorruption);
                backup = Assert.IsType<string>(recovered.CorruptBackupPath);
                Assert.Empty(recovered.LoadAll());
            }

            Assert.StartsWith(path + ".corrupt-", backup, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.True(File.Exists(path));
            using var reopened = JobStore.Open(path);
            Assert.False(reopened.RecoveredFromCorruption);
            Assert.Empty(reopened.LoadAll());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
