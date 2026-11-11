using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaTrackProbeTests
{
    private static void Probe(byte[] bytes, string format)
    {
        string path = Path.Combine(Path.GetTempPath(), "vesper-track-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(path, bytes); using var file = PartialFile.Open(path); MediaTrackProbe.RequireAudio(file, format, CancellationToken.None); }
        finally { File.Delete(path); }
    }
    [Fact]
    public void MuxedMp4_HasDeclaredAudio()
    {
        byte[] bytes = [.. DashTransferTests.Fixture("av-init.mp4"), .. DashTransferTests.Fixture("av-1.m4s")];
        Probe(bytes, "mp4");
    }
    [Fact]
    public void AudioOnlyMp4_IsNotMistakenForSilentVideo()
    {
        byte[] bytes = [.. DashTransferTests.Fixture("audio-init.mp4"), .. DashTransferTests.Fixture("audio-1.m4s")];
        Probe(bytes, "mp4");
    }
    [Fact]
    public void VideoOnlyMp4_IsNotPublishedAsVideoWithAudio() => Assert.Throws<MediaContainerException>(() => Probe(DashTransferTests.Fixture("video-only.mp4"), "mp4"));
    [Fact]
    public void TruncatedMp4_IsRejected() => Assert.Throws<MediaContainerException>(() => Probe(new byte[10], "mp4"));
    [Fact]
    public void InitializationWithoutMedia_IsNotACompleteMovie() => Assert.Throws<MediaContainerException>(() => Probe(DashTransferTests.Fixture("av-init.mp4"), "mp4"));
    [Fact]
    public void MuxedWebm_HasDeclaredAudio() => Probe(DashTransferTests.Fixture("muxed.webm"), "webm");
    [Fact]
    public void VideoOnlyWebm_IsRejected() => Assert.Throws<MediaContainerException>(() => Probe(DashTransferTests.Fixture("video-only.webm"), "webm"));
    [Fact]
    public void InvalidEbml_IsRejected() => Assert.Throws<MediaContainerException>(() => Probe(new byte[10], "webm"));
    [Fact]
    public async Task HttpMediaHandoff_StopsBeforePublishingASilentAdaptiveFile()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(server.Url("/media/video-only.mp4"), FileName: "video.mp4", MediaProvider: "generic"));
        var stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);
        Assert.Equal("media-validation", stopped.Reason); Assert.Null(stopped.FinalPath);
        Assert.True(stopped.CommittedBytes > 0); Assert.True(File.Exists(stopped.PartialPath));
    }
    [Fact]
    public async Task OrdinaryHttpDownload_CanStillSaveAnIntentionallyVideoOnlyFile()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var done = await h.WaitForStateAsync(h.Add(server.Url("/media/video-only.mp4"), "intentional.mp4"), JobState.Completed);
        Assert.Equal(DashTransferTests.Fixture("video-only.mp4"), await File.ReadAllBytesAsync(done.FinalPath!));
    }
}
