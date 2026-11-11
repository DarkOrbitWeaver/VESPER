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
                    MediaExpiresAt = expiry
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
    }
}
