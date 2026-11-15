using System.Runtime.ExceptionServices;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

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

    [Fact]
    public void BatchReviewShowsTheFullPageHashAndRequiresAnExplicitTrustChoice()
    {
        OnSta(() =>
        {
            string hash = new('a', 64);
            var item = new BridgeItem("https://cdn.example.test/archive.zip", "archive.zip", "http",
                "https://publisher.example/downloads", null, hash, null, null);
            using var form = new BatchReviewForm(new VesperPreferences(), [item]);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(520, 360);
            _ = form.Handle;
            Application.DoEvents();

            var files = Assert.IsType<CheckedListBox>(Assert.Single(form.Controls.Find("filesToReview", true)));
            var trust = Assert.IsType<CheckBox>(Assert.Single(form.Controls.Find("trustPageChecksums", true)));
            var details = Assert.IsType<TextBox>(Assert.Single(form.Controls.Find("pageChecksumDetails", true)));
            Assert.Empty(form.TrustedChecksumIndices);
            Assert.Contains("page SHA-256 available", files.Items[0]!.ToString() ?? "");

            files.SetItemChecked(0, true);
            files.SelectedIndex = 0;
            Application.DoEvents();
            Assert.True(trust.Enabled);
            Assert.Contains(hash, details.Text);
            Assert.Contains("https://publisher.example", details.Text);
            Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(details.RectangleToScreen(details.ClientRectangle)),
                "The checksum detail is clipped at the minimum review size.");
            Assert.Empty(form.TrustedChecksumIndices);

            trust.Checked = true;
            Assert.Equal(0, Assert.Single(form.TrustedChecksumIndices));
            trust.Checked = false;
            Assert.Empty(form.TrustedChecksumIndices);
        });
    }

    [Fact]
    public void BridgeForwardsOnlyChecksumsWhoseReviewIndexWasConfirmed()
    {
        var item = new BridgeItem("https://cdn.example.test/archive.zip", "archive.zip", "http",
            "https://publisher.example/downloads", null, new string('b', 64), null, null);

        Assert.True(BrowserBridgeHandler.RequiresNativeReview(new BridgeRequest("request-12345678", "submit", "download", null, [item])));
        Assert.False(BrowserBridgeHandler.RequiresNativeReview(new BridgeRequest("request-12345678", "submit", "download", null, [item with { Sha256 = null }])));
        Assert.Null(BrowserBridgeHandler.ConfirmedChecksum(item, 0, new HashSet<int>()));
        Assert.Null(BrowserBridgeHandler.ConfirmedChecksum(item, 1, new HashSet<int> { 0 }));
        Assert.Equal(item.Sha256, BrowserBridgeHandler.ConfirmedChecksum(item, 0, new HashSet<int> { 0 }));
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

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The browser review UI test did not finish.");
        error?.Throw();
    }

}
