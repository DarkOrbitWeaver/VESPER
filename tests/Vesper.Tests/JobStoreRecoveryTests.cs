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
                command.CommandText = "PRAGMA user_version = 8; PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            string originalHash = Hash(path);
            SqliteException error = Assert.Throws<SqliteException>(() => JobStore.Open(path));

            Assert.Contains("duplicate column name: final_resource_identity", error.Message, StringComparison.OrdinalIgnoreCase);
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
                recovered.FinishQuarantineRecovery();
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

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void A_locked_sidecar_cannot_leave_the_database_separated_from_its_journal(string lockedSuffix)
    {
        // FileShare.None is enforced for moves on Windows. No installed application or real database is touched.
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), "vesper-quarantine-rollback-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        byte[] database = "original database bytes"u8.ToArray();
        byte[] wal = "original WAL bytes"u8.ToArray();
        byte[] shm = "original SHM bytes"u8.ToArray();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, database);
            File.WriteAllBytes(path + "-wal", wal);
            File.WriteAllBytes(path + "-shm", shm);

            using (var lockOwner = new FileStream(path + lockedSuffix, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => JobStore.Quarantine(path));
                Assert.True(File.Exists(path)); // Failure must not detach the authoritative database.
            }

            Assert.Equal(database, File.ReadAllBytes(path));
            Assert.Equal(wal, File.ReadAllBytes(path + "-wal"));
            Assert.Equal(shm, File.ReadAllBytes(path + "-shm"));
            Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Successful_quarantine_archives_the_database_and_all_existing_sidecars_with_unique_names()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-quarantine-complete-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        byte[] database = "database backup"u8.ToArray();
        byte[] wal = "WAL backup"u8.ToArray();
        byte[] shm = "SHM backup"u8.ToArray();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, database);
            File.WriteAllBytes(path + "-wal", wal);
            File.WriteAllBytes(path + "-shm", shm);

            string first = JobStore.Quarantine(path);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + "-wal"));
            Assert.False(File.Exists(path + "-shm"));
            Assert.Equal(database, File.ReadAllBytes(first));
            Assert.Equal(wal, File.ReadAllBytes(first + "-wal"));
            Assert.Equal(shm, File.ReadAllBytes(first + "-shm"));

            File.WriteAllBytes(path, "second corrupt database"u8.ToArray());
            string second = JobStore.Quarantine(path);
            Assert.NotEqual(first, second);
            Assert.Equal(database, File.ReadAllBytes(first));
            Assert.Equal("second corrupt database"u8.ToArray(), File.ReadAllBytes(second));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Migration_backup_includes_committed_pages_held_in_the_wal()
    {
        using var h = new Harness();
        h.Engine.Dispose();
        string path = Path.Combine(h.Data, "vesper.db");
        using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
        writer.Open();
        Execute(writer, """
            ALTER TABLE jobs DROP COLUMN published_file_identity;
            ALTER TABLE jobs DROP COLUMN final_resource_identity;
            ALTER TABLE jobs DROP COLUMN pending_conversion_identity;
            ALTER TABLE jobs DROP COLUMN publication_copy_identity;
            ALTER TABLE jobs DROP COLUMN partial_identity;
            ALTER TABLE jobs DROP COLUMN published_sha256;
            ALTER TABLE jobs DROP COLUMN media_metadata_json;
            PRAGMA user_version = 5;
            PRAGMA wal_checkpoint(TRUNCATE);
            """);

        using var reader = new SqliteConnection($"Data Source={path};Pooling=False");
        reader.Open();
        Execute(reader, "BEGIN; SELECT COUNT(*) FROM events;");
        Execute(writer, "INSERT INTO events(utc, kind, detail) VALUES ('2026-01-01', 'backup', 'committed in WAL');");
        Assert.True(new FileInfo(path + "-wal").Length > 0);

        using var upgraded = JobStore.Open(path);
        using var backup = new SqliteConnection($"Data Source={path}.bak-v5;Mode=ReadOnly;Pooling=False");
        backup.Open();
        using var command = backup.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(5L, command.ExecuteScalar());
        command.CommandText = "SELECT detail FROM events WHERE kind = 'backup';";
        Assert.Equal("committed in WAL", command.ExecuteScalar());
    }

    [Fact]
    public void Interrupted_quarantine_restores_the_wal_before_opening_the_database()
    {
        using var h = new Harness();
        h.Engine.Dispose();
        string original = Path.Combine(h.Data, "vesper.db");
        using var writer = new SqliteConnection($"Data Source={original};Pooling=False");
        writer.Open();
        Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA wal_autocheckpoint = 0;");
        Execute(writer, "INSERT INTO events(utc, kind, detail) VALUES ('2026-01-01', 'recovery', 'committed in WAL');");

        string restoredDirectory = Path.Combine(h.Root, "interrupted");
        Directory.CreateDirectory(restoredDirectory);
        string path = Path.Combine(restoredDirectory, "vesper.db");
        string archive = path + ".corrupt-20261118120101-" + Guid.NewGuid().ToString("N");
        File.Copy(original, path);
        File.Copy(original + "-wal", archive + "-wal");
        File.WriteAllText(path + ".quarantine-pending", Path.GetFileName(archive));

        using var restored = JobStore.Open(path);
        Assert.False(restored.RecoveredFromCorruption);
        Assert.Contains(restored.ReadEvents(), line => line.Contains("committed in WAL", StringComparison.Ordinal));
        Assert.False(File.Exists(archive + "-wal"));
        Assert.False(File.Exists(path + ".quarantine-pending"));
    }

    [Fact]
    public void Startup_finishes_a_quarantine_interrupted_before_initializing_the_new_database()
    {
        using var h = new Harness();
        h.Engine.Dispose();
        string path = Path.Combine(h.Data, "vesper.db");
        byte[] original = "Damaged database"u8.ToArray();
        File.WriteAllBytes(path, original);
        string archive = JobStore.Quarantine(path, keepMarker: true);

        using var fresh = JobStore.Open(path);
        Assert.True(fresh.RecoveredFromCorruption);
        Assert.Equal(archive, fresh.CorruptBackupPath);
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.True(File.Exists(path + ".quarantine-pending"));
        Assert.Empty(fresh.LoadAll());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
