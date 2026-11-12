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

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
