using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaProvenanceTests
{
    [Fact]
    public void Media_source_and_quality_survive_database_restart_without_credentials()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vesper-media-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "jobs.db");
        Guid id = Guid.NewGuid();
        DateTimeOffset expiry = new(2027, 4, 11, 10, 30, 0, TimeSpan.Zero);
        try
        {
            using (var store = JobStore.Open(path))
            {
                var job = new DownloadJob
                {
                    Id = id, SourceUrl = "https://cdn.example.com/temporary.mp4?token=private",
                    FileName = "video.mp4", DestinationFolder = dir,
                    PartialPath = Path.Combine(dir, "video.vpart"),
                    OriginalPageUrl = "https://www.youtube.com/watch?v=EK3tCffac5k",
                    MediaProvider = "youtube", MediaId = "EK3tCffac5k",
                    MediaQuality = "1080p · MP4", MediaThumbnailUrl = "https://i.ytimg.com/vi/EK3tCffac5k/hqdefault.jpg",
                    MediaExpiresAt = expiry,
                    MediaMetadataJson = "{\"Title\":\"Song\",\"Artist\":\"Band\",\"Album\":\"Record\"}"
                };
                store.Save(job);
            }
            using (var reopened = JobStore.Open(path))
            {
                DownloadJob stored = Assert.Single(reopened.LoadAll());
                Assert.Equal(id, stored.Id);
                Assert.Equal("https://www.youtube.com/watch?v=EK3tCffac5k", stored.OriginalPageUrl);
                Assert.Equal("youtube", stored.MediaProvider);
                Assert.Equal("EK3tCffac5k", stored.MediaId);
                Assert.Equal("1080p · MP4", stored.MediaQuality);
                Assert.Equal(expiry, stored.MediaExpiresAt);
                Assert.Equal("{\"Title\":\"Song\",\"Artist\":\"Band\",\"Album\":\"Record\"}", stored.MediaMetadataJson);
                Assert.Null(stored.UserAgent);
            }
        }
        finally
        {
            // SQLite may have created WAL and backup files; clean only our disposable folder.
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ordinary_downloads_have_no_media_metadata()
    {
        var job = new DownloadJob { SourceUrl = "https://example.com/document.pdf" };
        Assert.Null(job.OriginalPageUrl);
        Assert.Null(job.MediaProvider);
        Assert.Null(job.MediaExpiresAt);
        Assert.Null(job.MediaMetadataJson);
    }

    [Fact]
    public void Recovery_manifest_keeps_valid_audio_tags_without_claiming_uncommitted_bytes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vesper-mp3-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string partialPath = Path.Combine(dir, "song.mp3.part");
        byte[] partial = new byte[128];
        File.WriteAllBytes(partialPath, partial);
        const string metadataJson = "{\"Title\":\"Song\",\"Artist\":\"Band\",\"Album\":\"Record\"}";
        try
        {
            var job = new DownloadJob
            {
                Kind = JobKind.Http, SourceUrl = "https://cdn.example.com/song.mp3", FileName = "song.mp3",
                DestinationFolder = dir, PartialPath = partialPath, TotalBytes = 512, MediaMetadataJson = metadataJson,
                MediaThumbnailUrl = "https://cdn.example.com/cover.png?signature=private"
            };
            job.Extents.Add(new ByteRange(0, partial.Length));
            RecoveryManifest.Write(job);

            Assert.True(RecoveryManifest.TryRecover(RecoveryManifest.PathFor(partialPath), out DownloadJob recovered, out string problem), problem);
            Assert.True(MediaMergePlan.TryParseMetadata(recovered.MediaMetadataJson, out MediaMergeMetadata? recoveredMetadata));
            Assert.Equal("Song", recoveredMetadata!.Title);
            Assert.Equal("Band", recoveredMetadata.Artist);
            Assert.Equal("Record", recoveredMetadata.Album);
            Assert.Equal("https://cdn.example.com/cover.png", recovered.MediaThumbnailUrl);
            Assert.DoesNotContain("signature=private", File.ReadAllText(RecoveryManifest.PathFor(partialPath)), StringComparison.Ordinal);
            Assert.Equal(partial.Length, recovered.Extents.End);
            Assert.False(recovered.Extents.Covers(0, 512));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Version5DatabaseMigratesMp3MetadataWithoutLosingExistingJobs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vesper-mp3-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "jobs.db");
        Guid id = Guid.NewGuid();
        try
        {
            using (var store = JobStore.Open(path))
            {
                store.Save(new DownloadJob
                {
                    Id = id, SourceUrl = "https://cdn.example.com/song.mp3", FileName = "song.mp3",
                    DestinationFolder = dir, PartialPath = Path.Combine(dir, "song.vpart"), MediaProvider = "bandcamp"
                });
            }
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                using var command = raw.CreateCommand();
                command.CommandText = "ALTER TABLE jobs DROP COLUMN media_metadata_json; PRAGMA user_version = 5;";
                command.ExecuteNonQuery();
            }

            using var migrated = JobStore.Open(path);
            DownloadJob retained = Assert.Single(migrated.LoadAll());
            Assert.Equal(id, retained.Id);
            Assert.Equal("bandcamp", retained.MediaProvider);
            Assert.Null(retained.MediaMetadataJson);
            retained.MediaMetadataJson = "{\"Title\":\"Song\",\"Artist\":\"Band\",\"Album\":\"Record\"}";
            migrated.Save(retained);

            DownloadJob roundTrip = Assert.Single(migrated.LoadAll());
            Assert.Equal(retained.MediaMetadataJson, roundTrip.MediaMetadataJson);
            Assert.Equal(6, JobStore.SchemaVersion);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
