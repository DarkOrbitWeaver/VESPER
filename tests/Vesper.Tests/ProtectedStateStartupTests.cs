using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class ProtectedStateStartupTests
{
    [WindowsFact]
    public void Invalid_dpapi_state_surfaces_without_replacing_the_database()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-dpapi-startup-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "vesper.db");
        Directory.CreateDirectory(directory);
        try
        {
            using (JobStore store = JobStore.Open(path))
            {
                store.Save(new DownloadJob
                {
                    SourceUrl = "https://example.com/file.zip",
                    FileName = "file.zip",
                    DestinationFolder = directory,
                    PartialPath = Path.Combine(directory, "file.zip.vpart")
                });
            }

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE jobs SET source_url = 'vesper-dpapi:1:not valid base64!'";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            string originalHash = Hash(path);
            var options = new EngineOptions { DataDirectory = directory, DefaultFolder = directory };

            CryptographicException error = Assert.Throws<CryptographicException>(() => DownloadEngine.Start(options));

            Assert.Contains("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
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
