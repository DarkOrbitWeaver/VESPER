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
    // A long video carries many megabytes of sample tables in its movie metadata. The check used to load all
    // of it and refuse anything above 8 MiB, which stopped a finished download from being saved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongVideoMetadata_IsCheckedWithoutASizeLimit(bool longSizeField) =>
        Probe(WithLargeMovieMetadata(MuxedMovie(), 9 * 1024 * 1024, longSizeField), "mp4");

    [Fact]
    public void LongVideoMetadata_StillRefusesAVideoWithoutSound()
    {
        var error = Assert.Throws<MediaContainerException>(() =>
            Probe(WithLargeMovieMetadata(DashTransferTests.Fixture("video-only.mp4"), 9 * 1024 * 1024, false), "mp4"));
        Assert.Contains("video-only", error.Message);
    }

    [Fact]
    public void TwoSetsOfMovieMetadata_AreRefused()
    {
        byte[] movie = MuxedMovie();
        (int start, int size) = FindTopLevelBox(movie, "moov");
        byte[] doubled = [.. movie, .. movie.AsSpan(start, size)];
        Assert.Throws<MediaContainerException>(() => Probe(doubled, "mp4"));
    }

    private static byte[] MuxedMovie() => [.. DashTransferTests.Fixture("av-init.mp4"), .. DashTransferTests.Fixture("av-1.m4s")];

    /// <summary>Grows the movie metadata with an unused box, the way hours of sample tables would.</summary>
    private static byte[] WithLargeMovieMetadata(byte[] movie, int extraBytes, bool longSizeField)
    {
        (int start, int size) = FindTopLevelBox(movie, "moov");
        int header = longSizeField ? 16 : 8;
        byte[] filler = new byte[extraBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(filler, (uint)extraBytes);
        "free"u8.CopyTo(filler.AsSpan(4));
        byte[] grown = new byte[movie.Length + extraBytes + (header - 8)];
        movie.AsSpan(0, start).CopyTo(grown);
        long newSize = size + extraBytes + (header - 8);
        if (longSizeField)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(start), 1);
            "moov"u8.CopyTo(grown.AsSpan(start + 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(grown.AsSpan(start + 8), (ulong)newSize);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(start), (uint)newSize);
            "moov"u8.CopyTo(grown.AsSpan(start + 4));
        }
        movie.AsSpan(start + 8, size - 8).CopyTo(grown.AsSpan(start + header));
        filler.CopyTo(grown.AsSpan(start + header + size - 8));
        movie.AsSpan(start + size).CopyTo(grown.AsSpan(start + header + size - 8 + extraBytes));
        return grown;
    }

    private static (int Start, int Size) FindTopLevelBox(byte[] movie, string type)
    {
        for (int offset = 0; offset + 8 <= movie.Length;)
        {
            int size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(offset));
            if (System.Text.Encoding.ASCII.GetString(movie, offset + 4, 4) == type) return (offset, size);
            if (size < 8) break;
            offset += size;
        }
        throw new InvalidOperationException("The fixture has no " + type + " box.");
    }

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
    public async Task HttpMediaHandoff_KeepsFragmentedMp4SegmentWithoutPublishingIt()
    {
        byte[] fragment = DashTransferTests.Fixture("av-1.m4s");
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        server.Extra["/media/instagram.mp4"] = fragment;
        var added = h.Engine.Add(new AddRequest(server.Url("/media/instagram.mp4"), FileName: "instagram.mp4", MediaProvider: "generic"));

        var stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);

        Assert.Equal("media-validation", stopped.Reason);
        Assert.Null(stopped.FinalPath);
        Assert.Contains("The site sent only part of the video.", stopped.Message);
        Assert.Contains("choose another format", stopped.Message);
        Assert.Contains("Saved data was kept.", stopped.Message);
        Assert.Equal(fragment, await File.ReadAllBytesAsync(stopped.PartialPath));
    }
    [Fact]
    public async Task HttpMediaHandoff_AllowsOptedInProgressiveVideoOnlyMp4()
    {
        byte[] video = DashTransferTests.Fixture("video-only.mp4");
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        server.Extra["/media/video-only.mp4"] = video;

        var added = h.Engine.Add(new AddRequest(server.Url("/media/video-only.mp4"), FileName: "video.mp4",
            MediaProvider: "generic", MediaPlanJson: DirectMediaPlan.AudioIfPresent, InitiallyPaused: true));
        Assert.Equal(AddStatus.Added, added.Status);
        h.Restart();
        h.Engine.Resume(added.JobId);
        var done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal(video, await File.ReadAllBytesAsync(done.FinalPath!));
    }
    [Fact]
    public async Task HttpMediaHandoff_RejectsFragmentedMp4WithOptionalAudioPlan()
    {
        byte[] fragment = DashTransferTests.Fixture("av-1.m4s");
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        server.Extra["/media/instagram.mp4"] = fragment;

        var added = h.Engine.Add(new AddRequest(server.Url("/media/instagram.mp4"), FileName: "instagram.mp4",
            MediaProvider: "generic", MediaPlanJson: DirectMediaPlan.AudioIfPresent));
        Assert.Equal(AddStatus.Added, added.Status);
        var stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);

        Assert.Equal("media-validation", stopped.Reason);
        Assert.Null(stopped.FinalPath);
        Assert.Contains("The site sent only part of the video.", stopped.Message);
        Assert.Equal(fragment, await File.ReadAllBytesAsync(stopped.PartialPath));
    }
    [Fact]
    public async Task OrdinaryHttpDownload_CanStillSaveAnIntentionallyVideoOnlyFile()
    {
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        var done = await h.WaitForStateAsync(h.Add(server.Url("/media/video-only.mp4"), "intentional.mp4"), JobState.Completed);
        Assert.Equal(DashTransferTests.Fixture("video-only.mp4"), await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task CompleteSilentWebmDownloadsAndTrimsWithoutEncoding()
    {
        byte[] video = DashTransferTests.Fixture("video-only.webm");
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        server.Extra["/media/preview.webm"] = video;
        var added = h.Engine.Add(new AddRequest(server.Url("/media/preview.webm"), FileName: "preview.webm",
            MediaProvider: "generic", MediaPlanJson: DirectMediaPlan.AudioIfPresent));
        Assert.True(added.Ok, added.Message);
        var done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(video, await File.ReadAllBytesAsync(done.FinalPath!));

        var clipped = h.Engine.Add(new AddRequest(server.Url("/media/preview.webm"), FileName: "clip.webm",
            MediaProvider: "generic", MediaPlanJson: DirectMediaPlan.WebmClip(new MediaTimeRange(0, .6)), InitiallyPaused: true));
        Assert.True(clipped.Ok, clipped.Message);
        h.Restart();
        h.Engine.Resume(clipped.JobId);
        var clip = await h.WaitForStateAsync(clipped.JobId, JobState.Completed);
        using var source = new MemoryStream(video);
        var original = Assert.Single(WebmDemuxer.Read(source, source.Length, CancellationToken.None));
        using var result = File.OpenRead(clip.FinalPath!);
        var track = Assert.Single(WebmDemuxer.Read(result, result.Length, CancellationToken.None));
        Assert.True(track.Samples.Count < original.Samples.Count);
        Assert.InRange(track.DurationSeconds, .59, .81);
        Assert.True(track.Samples[0].Key);
        Assert.Contains("Keyframe-aligned", clip.Message);
    }

    [Fact]
    public async Task UnsupportedWebmCutNeverPublishesTheFullSource()
    {
        byte[] video = DashTransferTests.Fixture("video-only.webm");
        await using var server = new FaultServer(); await server.StartAsync(); using var h = new Harness();
        server.Extra["/media/preview.webm"] = video;
        var added = h.Engine.Add(new AddRequest(server.Url("/media/preview.webm"), FileName: "preview.webm",
            MediaProvider: "generic", MediaPlanJson: DirectMediaPlan.WebmClip(new MediaTimeRange(0, 10))));
        Assert.True(added.Ok, added.Message);
        var stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);
        Assert.Equal("trim-failed", stopped.Reason);
        Assert.Null(stopped.FinalPath);
        Assert.Equal(video, await File.ReadAllBytesAsync(stopped.PartialPath));
    }

    [Fact]
    public void DifferentWebmClipRangesDoNotCollideAndReplayKeepsItsOriginalReceipt()
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        const string url = "https://example.com/preview.webm";
        string firstRange = DirectMediaPlan.WebmClip(new MediaTimeRange(0, 1));
        string secondRange = DirectMediaPlan.WebmClip(new MediaTimeRange(2, 3));

        AddResult first = harness.Engine.Add(new AddRequest(url, FileName: "first.webm", MediaProvider: "generic",
            MediaPlanJson: firstRange, RequestId: "webm-clip-receipt", InitiallyPaused: true));
        AddResult second = harness.Engine.Add(new AddRequest(url, FileName: "second.webm", MediaProvider: "generic",
            MediaPlanJson: secondRange, InitiallyPaused: true));
        AddResult duplicate = harness.Engine.Add(new AddRequest(url, FileName: "same.webm", MediaProvider: "generic",
            MediaPlanJson: """{"webmClip":{"EndSeconds":1,"Version":1,"StartSeconds":0}}""", InitiallyPaused: true));
        AddResult replay = harness.Engine.Add(new AddRequest(url, FileName: "changed.webm", MediaProvider: "generic",
            MediaPlanJson: secondRange, RequestId: "webm-clip-receipt", InitiallyPaused: true));
        AddResult otherKind = harness.Engine.Add(new AddRequest(url, FileName: "manifest.webm", Kind: JobKind.Hls,
            InitiallyPaused: true));

        Assert.Equal(AddStatus.Added, first.Status);
        Assert.Equal(AddStatus.Added, second.Status);
        Assert.Equal(AddStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.Equal(AddStatus.AlreadySubmitted, replay.Status);
        Assert.Equal(first.JobId, replay.JobId);
        Assert.Equal(AddStatus.Added, otherKind.Status);
    }

    [Theory]
    [InlineData(MediaOutput.Wav, "clip.wav")]
    [InlineData(MediaOutput.M4a, "clip.m4a")]
    public void DifferentAudioClipRangesDoNotCollide(MediaOutput output, string fileName)
    {
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        const string url = "https://example.com/audio.mp3";
        AddResult first = harness.Engine.Add(new AddRequest(url, FileName: fileName, MediaProvider: "generic",
            Output: output, MediaPlanJson: """{"StartSeconds":0,"EndSeconds":1,"Version":1}""", InitiallyPaused: true));
        AddResult second = harness.Engine.Add(new AddRequest(url, FileName: fileName, MediaProvider: "generic",
            Output: output, MediaPlanJson: """{"StartSeconds":2,"EndSeconds":3,"Version":1}""", InitiallyPaused: true));
        AddResult duplicate = harness.Engine.Add(new AddRequest(url, FileName: fileName, MediaProvider: "generic",
            Output: output, MediaPlanJson: """{"EndSeconds":1,"Version":1,"StartSeconds":0}""", InitiallyPaused: true));

        Assert.Equal(AddStatus.Added, first.Status);
        Assert.Equal(AddStatus.Added, second.Status);
        Assert.Equal(AddStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.JobId, duplicate.JobId);
    }
}
