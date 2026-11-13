using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Models;
using System.Text.Json;

namespace Vesper.Windows.Tests;

public sealed class BrowserBridgeTests
{
    [Fact]
    public void NativeMediaQueuePersistsDirectMp3Metadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-browser-mp3-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data");
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(downloads);
        const string metadata = "{\"Title\":\"Song One\",\"Artist\":\"Fixture Band\",\"Album\":\"Fixture Album\"}";
        try
        {
            var options = new EngineOptions { DataDirectory = data, DefaultFolder = downloads };
            using (var engine = DownloadEngine.Start(options))
            {
                var bridge = new BrowserBridgeHandler(null!, engine, () => null!);
                var choice = new MediaDownloadChoice("https://cdn.example.test/song.mp3", "song.mp3", JobKind.Http,
                    null, null, Provider: "bandcamp", MediaId: "track-1", MediaMetadataJson: metadata);

                BridgeResult result = bridge.QueueMedia(choice, queue: true, requestId: "media-choice-1");

                Assert.Equal("queued", result.Status);
                Assert.NotNull(result.JobId);
            }

            using var store = JobStore.Open(Path.Combine(data, "vesper.db"));
            DownloadJob saved = Assert.Single(store.LoadAll());
            MediaMergeMetadata? savedTags = JsonSerializer.Deserialize<MediaMergeMetadata>(saved.MediaMetadataJson!);
            Assert.NotNull(savedTags);
            Assert.Equal("Song One", savedTags!.Title);
            Assert.Equal("Fixture Band", savedTags.Artist);
            Assert.Equal("Fixture Album", savedTags.Album);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(MediaErrorCode.NoDownloadableMedia, "no-media")]
    [InlineData(MediaErrorCode.VideoUnavailable, "unavailable")]
    [InlineData(MediaErrorCode.SourceExpired, "source-expired")]
    [InlineData(MediaErrorCode.LoginRequired, "sign-in")]
    [InlineData(MediaErrorCode.DrmProtected, "protected")]
    [InlineData(MediaErrorCode.UnsupportedManifest, "unsupported")]
    [InlineData(MediaErrorCode.LiveStreamNotSupported, "unsupported")]
    [InlineData(MediaErrorCode.BrowserContextRequired, "browser-context")]
    [InlineData(MediaErrorCode.NetworkFailure, "network")]
    [InlineData(MediaErrorCode.InternalError, "resolver-error")]
    [InlineData(MediaErrorCode.ResponseTooLarge, "unsupported")]
    public void ResolverFailuresKeepTheirUserFacingReason(MediaErrorCode code, string expected)
    {
        Assert.Equal(expected, BrowserBridgeHandler.MapResolverError(code));
    }
}
