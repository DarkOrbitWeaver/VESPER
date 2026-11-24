using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vesper.Engine;
using Vesper.MediaResolver.Protocols.Dash;

namespace Vesper.Tests;

public sealed class DashTransferTests
{
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));
    private static int Tag(byte[] bytes, string tag) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(tag));
    private static byte[] Expected => [.. Fixture("av-init.mp4"), .. Fixture("av-1.m4s"), .. Fixture("av-2.m4s"), .. Fixture("av-3.m4s")];

    [Fact]
    public void DashTimeline_StoresPresentationBoundariesAndDetectsGaps()
    {
        const string xml = """
            <MPD mediaPresentationDuration="PT14S"><Period start="PT10S" duration="PT4S">
              <AdaptationSet mimeType="video/mp4" contentType="video">
                <SegmentTemplate timescale="1000" presentationTimeOffset="5000" initialization="init.mp4" media="s$Time$.m4s">
                  <SegmentTimeline><S t="5000" d="2000"/><S t="8000" d="2000"/></SegmentTimeline>
                </SegmentTemplate>
                <Representation id="v" codecs="avc1.4d401e"/>
              </AdaptationSet>
            </Period></MPD>
            """;

        var plan = Assert.Single(DashParser.Parse(xml, new Uri("https://cdn.example.com/manifest.mpd"))
            .Representations).Segments!;

        Assert.Equal([10d, 13d], plan.Segments.Select(segment => segment.StartSeconds));
        Assert.True(plan.HasDiscontinuities);
    }

    [Fact]
    public void ClearSourceMuxedFragments_HaveBothTracksAndContinuousTimelines()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.True(mp4.HasAudio); Assert.True(mp4.HasVideo);
        for (int i = 1; i <= 3; i++) mp4.ValidateFragment(Fixture($"av-{i}.m4s"));
        mp4.Finish(requireAudio: true, requireVideo: true);
        Assert.All(mp4.Snapshot().Tracks, t => Assert.True(t.Samples > 0));
    }

    [Fact]
    public void ClearFragments_DemuxAndRemuxWithoutLosingSamples()
    {
        byte[] initialization = Fixture("av-init.mp4");
        var mp4 = FragmentedMp4.Open(initialization, captureSamples: true);
        using var source = new MemoryStream();
        source.Write(initialization);
        for (int i = 1; i <= 3; i++)
        {
            byte[] fragment = Fixture($"av-{i}.m4s");
            mp4.ValidateFragment(fragment, source.Position);
            source.Write(fragment);
        }
        mp4.Finish(requireAudio: true, requireVideo: true);
        List<DemuxedTrack> tracks = mp4.GetTracks();

        Assert.Collection(tracks.OrderBy(track => track.Kind),
            track => { Assert.Equal(TrackKind.Video, track.Kind); Assert.Equal("h264", track.Codec); Assert.NotEmpty(track.Samples); },
            track => { Assert.Equal(TrackKind.Audio, track.Kind); Assert.Equal("aac", track.Codec); Assert.NotEmpty(track.Samples); });

        using var output = new MemoryStream();
        var inputs = tracks.Select(track => (Track: track, Source: (Stream)source)).ToList();
        MediaMerger.Merge(inputs, "mp4", output, CancellationToken.None);
        MediaMerger.Validate(output, output.Length, tracks, CancellationToken.None);
    }

    [Fact]
    public void FragmentMetadataReaderPreservesEverySampleWithoutReadingPayloads()
    {
        byte[] initialization = Fixture("av-init.mp4");
        var original = FragmentedMp4.Open(initialization, captureSamples: true);
        var optimized = FragmentedMp4.Open(initialization, captureSamples: true);
        using var source = new CountingMediaStream();
        source.Write(initialization);
        long totalBytes = 0, inspectedBytes = 0;
        for (int i = 1; i <= 3; i++)
        {
            byte[] fragment = Fixture($"av-{i}.m4s");
            // Extended-size padding before the moof exercises non-zero box offsets.
            byte[] padding = new byte[64];
            BinaryPrimitives.WriteUInt32BigEndian(padding, 1);
            "free"u8.CopyTo(padding.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(padding.AsSpan(8), (ulong)padding.Length);
            byte[] part = [.. padding, .. fragment];
            long offset = source.Length;
            source.Position = offset;
            source.Write(part);
            original.ValidateFragment(part, offset);
            long before = source.BytesRead;
            optimized.ValidateFragment(source, offset, part.Length, default);
            inspectedBytes += source.BytesRead - before;
            totalBytes += part.Length;
        }
        original.Finish(true, true);
        optimized.Finish(true, true);
        var oldTracks = original.GetTracks();
        var newTracks = optimized.GetTracks();
        for (int i = 0; i < oldTracks.Count; i++)
            Assert.Equal(oldTracks[i].Samples.ToArray(), newTracks[i].Samples.ToArray());
        Assert.Equal(original.Snapshot().Sequence, optimized.Snapshot().Sequence);
        Assert.Equal(original.Snapshot().Tracks.ToArray(), optimized.Snapshot().Tracks.ToArray());
        Assert.True(inspectedBytes < totalBytes / 4, $"Read {inspectedBytes} of {totalBytes} bytes.");
        Console.WriteLine($"fmp4 metadata reads: old={totalBytes}; new={inspectedBytes}; saved={totalBytes - inspectedBytes}");
        using var output = new MemoryStream();
        var inputs = newTracks.Select(track => (Track: track, Source: (Stream)source)).ToList();
        MediaMerger.Merge(inputs, "mp4", output, default);
        MediaMerger.ValidateRemux(output, output.Length, inputs, default);
    }

    [Fact]
    public void FragmentMetadataReaderRejectsTruncationAndCancellation()
    {
        byte[] init = Fixture("av-init.mp4"), fragment = Fixture("av-1.m4s");
        using var source = new MemoryStream(fragment);
        Assert.Throws<MediaContainerException>(() => FragmentedMp4.Open(init).ValidateFragment(source, 0, fragment.Length - 1, default));
        Assert.Throws<MediaContainerException>(() => FragmentedMp4.Open(init).ValidateFragment(source, 1, fragment.Length, default));
        Assert.Throws<OperationCanceledException>(() => FragmentedMp4.Open(init).ValidateFragment(source, 0, fragment.Length, new CancellationToken(true)));
    }

    private sealed class CountingMediaStream : MemoryStream
    {
        public long BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            BytesRead += read;
            return read;
        }
    }

    [Fact]
    public void AudioOnly_IsSupported_ButCannotPretendToContainVideo()
    {
        var mp4 = FragmentedMp4.Open(Fixture("audio-init.mp4"));
        mp4.ValidateFragment(Fixture("audio-1.m4s"));
        mp4.Finish(true, false);
        Assert.Throws<MediaContainerException>(() => mp4.Finish(true, true));
    }

    [Fact]
    public void InvalidBoxLength_IsRejected()
    {
        byte[] init = Fixture("av-init.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(init, uint.MaxValue);
        Assert.Throws<MediaContainerException>(() => FragmentedMp4.Open(init));
    }

    [Fact]
    public void AbsoluteOffsets_AreNotConcatenatedWithoutRemuxing()
    {
        byte[] fragment = Fixture("av-1.m4s");
        fragment[Tag(fragment, "tfhd") + 7] |= 1;
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void SampleOffsetsOutsideMdat_AreRejected()
    {
        byte[] fragment = Fixture("av-1.m4s");
        BinaryPrimitives.WriteInt32BigEndian(fragment.AsSpan(Tag(fragment, "trun") + 12), int.MaxValue);
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void TimelineOverlapOrGap_IsRejected()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        mp4.ValidateFragment(Fixture("av-1.m4s"));
        byte[] fragment = Fixture("av-2.m4s");
        int tfdt = Tag(fragment, "tfdt");
        BinaryPrimitives.WriteUInt64BigEndian(fragment.AsSpan(tfdt + 8), 0);
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(fragment));
    }

    [Fact]
    public void DuplicateOrOutOfOrderFragments_AreRejected()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        mp4.ValidateFragment(Fixture("av-1.m4s"));
        Assert.Throws<MediaContainerException>(() => mp4.ValidateFragment(Fixture("av-1.m4s")));
    }

    [Fact]
    public void RequiredTracksWithoutSamples_AreNeverSuccessful()
    {
        var mp4 = FragmentedMp4.Open(Fixture("av-init.mp4"));
        Assert.Throws<MediaContainerException>(() => mp4.Finish(true, true));
    }

    [Fact]
    public void PlansRejectPrivateTargetsAndMalformedRanges()
    {
        var plan = new DashDownloadPlan
        {
            RepresentationId = "a", Initialization = new("http://127.0.0.1/init.mp4"),
            Fragments = [new("https://public.example.com/1.m4s")]
        };
        Assert.False(DashDownloadPlan.TryParse(plan.ToJson(), false, out _, out _));
        Assert.False(DashDownloadPlan.TryParse((plan with { Initialization = new("https://public.example.com/init.mp4", -1, 10) }).ToJson(), false, out _, out _));
        Assert.False(DashDownloadPlan.TryParse((plan with { Initialization = new("https://public.example.com/init.mp4", long.MaxValue, 10) }).ToJson(), false, out _, out _));
    }

    [Fact]
    public async Task DashTransfersProduceTheExactIndependentlyValidatedSourceMuxedFile()
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server), JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
        Assert.EndsWith(".mp4", job.FinalPath);
        Assert.Equal(Expected.Length, job.CommittedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedDashAndSegmentedMergeKeepCredentialsOnTheConsentedOrigin(bool merge)
    {
        await using var source = await FixtureServer.StartAsync();
        await using var remote = await FixtureServer.StartAsync();
        const string secret = "session=fixture-private";
        source.RequiredCookie = secret;
        remote.RejectCredentials = true;
        var credentials = new Dictionary<string, string> { ["Cookie"] = secret, ["Authorization"] = "Bearer fixture-private" };
        DashDownloadPlan cross(DashDownloadPlan plan) => plan with
        {
            Fragments = plan.Fragments.Select((part, i) => i == 1
                ? part with { Url = remote.Url("/av-2.m4s") } : part).ToArray()
        };
        string planJson;
        if (merge)
        {
            var plan = SegmentedPlan(source);
            planJson = (plan with { Tracks = plan.Tracks.Select(t => t with
                { Source = t.Source! with { Dash = cross(t.Source!.Dash!) } }).ToArray() }).ToJson();
        }
        else planJson = cross(source.Plan(ranges: false)).ToJson();

        using var h = new Harness();
        var added = h.Engine.Add(new AddRequest(source.Url("/manifest.mpd"), FileName: "authenticated.mp4",
            Kind: merge ? JobKind.Merge : JobKind.Dash, MediaPlanJson: planJson, Secrets: credentials));
        Assert.True(added.Ok, added.Message);
        var done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.NeedsLink or JobState.Failed);
        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        Assert.NotNull(done.FinalPath);
        Assert.True(source.Hits("/av-init.mp4") > 0);
        Assert.True(remote.Hits("/av-2.m4s") > 0);
        Assert.Equal(0, source.BadCredentialRequests);
        Assert.Equal(0, remote.BadCredentialRequests);
        if (!merge) Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        else
        {
            using var output = File.OpenRead(done.FinalPath!);
            Assert.Equal([TrackKind.Video, TrackKind.Audio], MediaMerger.Demux(output, output.Length, default).Select(t => t.Kind));
        }
    }

    [Fact]
    public async Task DashTimeRange_UsesWholeFragmentsAndPreservesTheInitialization()
    {
        await using var server = await FixtureServer.StartAsync();
        DashDownloadPlan plan = server.Plan(ranges: false) with
        {
            Fragments = server.Plan(ranges: false).Fragments
                .Select(fragment => fragment with { DurationSeconds = 1 }).ToArray(),
            ClipRange = new MediaTimeRange(1.1, 1.8)
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "dash-clip.mp4",
            Kind: JobKind.Dash, MediaPlanJson: plan.ToJson(), MediaProvider: "fixture", MediaId: "av",
            MediaFormatId: plan.RepresentationId));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention);

        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        byte[] expected = [.. Fixture("av-init.mp4"), .. Fixture("av-2.m4s")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(0, server.Hits("/av-1.m4s"));
        Assert.Equal(1, server.Hits("/av-2.m4s"));
        Assert.Equal(0, server.Hits("/av-3.m4s"));
        Assert.Contains("Segment-aligned 00:00:01.000–00:00:02.000", done.MediaQuality);
    }

    [Fact]
    public async Task DashTimeRange_RefusesToJoinFragmentsAcrossATimelineGap()
    {
        await using var server = await FixtureServer.StartAsync();
        DashDownloadPlan plan = server.Plan(ranges: false) with
        {
            Fragments =
            [
                new DashFragment(server.Url("/av-1.m4s"), DurationSeconds: 1, StartSeconds: 0),
                new DashFragment(server.Url("/av-2.m4s"), DurationSeconds: 1, StartSeconds: 2)
            ],
            ClipRange = new MediaTimeRange(0.5, 2.5)
        };
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "dash-gap.mp4",
            Kind: JobKind.Dash, MediaPlanJson: plan.ToJson(), MediaProvider: "fixture", MediaId: "av",
            MediaFormatId: plan.RepresentationId));
        Assert.True(added.Ok, added.Message);

        JobSnapshot rejected = await h.WaitForStateAsync(added.JobId, JobState.NeedsAttention);

        Assert.Equal("clip-range", rejected.Reason);
        Assert.Equal(0, rejected.CommittedBytes);
        Assert.Equal(0, server.Hits("/av-init.mp4"));
        Assert.Equal(0, server.Hits("/av-1.m4s"));
        Assert.Equal(0, server.Hits("/av-2.m4s"));
    }

    [Fact]
    public async Task SegmentedMerge_OverlapsClearRequestsAndCommitsInOrder()
    {
        await using var server = await FixtureServer.StartAsync();
        server.FragmentDelayMs = 100;
        using var h = new Harness();
        MediaMergePlan plan = SegmentedPlan(server);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "pipelined.mp4",
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        var done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);
        Assert.True(done.State == JobState.Completed, done.Message);
        Assert.Equal(2, server.MaxActiveFragmentRequests);
        foreach (string path in new[] { "/av-1.m4s", "/av-2.m4s", "/av-3.m4s" })
            Assert.Equal(2, server.Hits(path));
        using var output = File.OpenRead(done.FinalPath!);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], MediaMerger.Demux(output, output.Length, default).Select(track => track.Kind));
    }

    [Fact]
    public async Task SegmentedMerge_PauseDrainsLookaheadAndResumesCommittedPrefix()
    {
        await using var server = await FixtureServer.StartAsync();
        server.SlowFirst = true;
        using var h = new Harness();
        MediaMergePlan plan = SegmentedPlan(server);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "paused.mp4",
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        await server.FirstFragmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await h.WaitAsync(added.JobId, _ => server.Hits("/av-2.m4s") == 1);
        h.Engine.Pause(added.JobId);
        var paused = await h.WaitForStateAsync(added.JobId, JobState.Paused);
        Assert.Null(paused.FinalPath);
        Assert.Equal(Fixture("av-init.mp4").LongLength, paused.CommittedBytes);
        Assert.Equal(Fixture("av-init.mp4"), await File.ReadAllBytesAsync(paused.PartialPath));
        server.SlowFirst = false;
        h.Restart();
        h.Engine.Resume(added.JobId);
        var done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.NotNull(done.FinalPath);
        Assert.Equal(2, server.Hits("/av-init.mp4")); // once for each rendition
        Assert.Equal(3, server.Hits("/av-2.m4s")); // uncommitted lookahead is fetched again
    }

    [Fact]
    public async Task SegmentedMerge_ResumesSeparateDashRenditionsAndRemuxesThem()
    {
        await using var server = await FixtureServer.StartAsync();
        server.ExpireSecond = true;
        using var h = new Harness();
        MediaMergePlan plan = SegmentedPlan(server);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "dash-merged.mp4",
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot waiting = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        Assert.True(waiting.CommittedBytes > 0);
        Assert.Equal(1, server.Hits("/av-1.m4s"));

        h.Restart();
        server.ExpireSecond = false;
        h.Engine.Resume(added.JobId);
        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        Assert.Equal(2, server.Hits("/av-1.m4s"));
        using var output = File.OpenRead(done.FinalPath!);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], MediaMerger.Demux(output, output.Length, default).Select(track => track.Kind));
        Assert.False(File.Exists(done.PartialPath + ".tracks"));
    }

    [Fact]
    public async Task SegmentedDashMerge_UsesTheSameTimeWindowForBothRenditions()
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness();
        MediaMergePlan plan = SegmentedPlan(server) with { ClipRange = new MediaTimeRange(1.1, 1.8) };
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd"), FileName: "dash-clip.mp4",
            Kind: JobKind.Merge, MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitAsync(added.JobId, job => job.State is JobState.Completed or JobState.NeedsAttention);

        Assert.True(done.State == JobState.Completed, $"{done.State} {done.Reason}: {done.Message}");
        Assert.Contains("Segment-aligned 00:00:01.000–00:00:02.000", done.MediaQuality);
        foreach (string path in new[] { "/av-1.m4s", "/av-3.m4s" }) Assert.Equal(0, server.Hits(path));
        Assert.Equal(2, server.Hits("/av-2.m4s"));
    }

    [Fact]
    public async Task ExactByteRanges_AreAssembledInOrder()
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server, ranges: true), JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
        Assert.Equal(4, server.Hits("/all.mp4"));
    }

    [Fact]
    public async Task IgnoredRanges_NeverCommitBytes()
    {
        await using var server = await FixtureServer.StartAsync(); server.IgnoreRanges = true;
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server, ranges: true), JobState.NeedsAttention);
        Assert.Equal(0, job.CommittedBytes); Assert.Null(job.FinalPath);
    }

    [Fact]
    public async Task ChunkedRangeBodyLongerThanRequested_IsRejectedBeforeCommit()
    {
        await using var server = await FixtureServer.StartAsync(); server.OversizedRangeBody = true;
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server, ranges: true), JobState.NeedsAttention);
        Assert.Equal(0, job.CommittedBytes);
        Assert.Null(job.FinalPath);
    }

    [Fact]
    public async Task InvalidMedia_IsNotPublishedOrCommitted()
    {
        await using var server = await FixtureServer.StartAsync(); server.InvalidFragment = true;
        using var h = new Harness();
        var job = await h.WaitForStateAsync(Add(h, server), JobState.NeedsAttention);
        Assert.Equal("invalid-container", job.Reason);
        Assert.Equal(0, job.CommittedBytes); Assert.Null(job.FinalPath);
    }

    [Fact]
    public async Task InterruptedTransfer_ResumesWithoutFetchingCommittedFragmentsAgain()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stopped.CommittedBytes > 0);
        h.Restart(); server.ExpireSecond = false; h.Engine.Resume(id);
        var done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(1, server.Hits("/av-1.m4s"));
        Assert.Equal(1, server.Hits("/av-init.mp4"));
    }

    [Fact]
    public async Task DashResume_discards_uncommitted_bytes_before_retrying_a_fragment()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        JobSnapshot waiting = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(waiting.CommittedBytes > 0);

        h.Restart();
        await AppendUncommittedTailAsync(waiting.PartialPath);
        h.Engine.Resume(id);
        await h.WaitAsync(id, job => job.State == JobState.NeedsLink && server.Hits("/av-2.m4s") >= 2);

        Assert.Equal(waiting.CommittedBytes, new FileInfo(waiting.PartialPath).Length);
    }

    private static async Task AppendUncommittedTailAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] tail = [0xA5, 0x5A, 0xC3, 0x3C];
        await file.WriteAsync(tail);
    }

    [Fact]
    public async Task CorruptCommittedPrefix_IsKeptButNeverResumedOrPublished()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        h.Restart();
        byte[] bytes = await File.ReadAllBytesAsync(stopped.PartialPath); bytes[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(stopped.PartialPath, bytes);
        server.ExpireSecond = false; h.Engine.Resume(id);
        var refused = await h.WaitForStateAsync(id, JobState.NeedsAttention);
        Assert.Equal("dash-integrity", refused.Reason);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Null(refused.FinalPath);
    }

    [Fact]
    public async Task AcquisitionSwitchFromDashToHttpKeepsTheSameJobAndVerifiesEverySavedByte()
    {
        await using var server = await FixtureServer.StartAsync();
        server.ExpireSecond = true;
        using var h = new Harness();
        DashDownloadPlan plan = server.Plan(ranges: false);
        string sourceUrl = server.Url("/manifest.mpd");
        const string quality = "720p h264 with audio";
        AddResult added = h.Engine.Add(new AddRequest(sourceUrl, FileName: "switch.mp4", Kind: JobKind.Dash,
            MediaProvider: "fixture", MediaId: "av", MediaFormatId: plan.RepresentationId, MediaQuality: quality,
            MediaPlanJson: plan.ToJson()));
        Assert.True(added.Ok, added.Message);
        JobSnapshot stopped = await h.WaitForStateAsync(added.JobId, JobState.NeedsLink);
        Assert.True(stopped.CommittedBytes > 0);
        byte[] prefix = await File.ReadAllBytesAsync(stopped.PartialPath);
        var choice = new MediaAcquisitionReplacement(new ResolvedMediaSource("fixture", "av", plan.RepresentationId,
            server.Url("/all.mp4")), JobKind.Http, "switch.mp4", quality, ExactSize: Expected.Length);
        RescueResult wrongQuality = await h.Engine.SwitchMediaAcquisitionAsync(added.JobId, choice with { Quality = "360p" }, sourceUrl);
        Assert.False(wrongQuality.Accepted);
        RescueResult wrongSize = await h.Engine.SwitchMediaAcquisitionAsync(added.JobId, choice with { ExactSize = Expected.Length + 1 }, sourceUrl);
        Assert.False(wrongSize.Accepted);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(stopped.PartialPath));
        Assert.Equal(JobKind.Dash, h.Engine.Snapshot(added.JobId)!.Kind);
        h.Engine.Options.MaxConcurrentJobs = 0;
        h.Engine.SettingsChanged();
        server.ExpireSecond = false;
        RescueResult accepted = await h.Engine.SwitchMediaAcquisitionAsync(added.JobId, choice, sourceUrl);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Equal(stopped.CommittedBytes, accepted.RetainedBytes);
        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);
        Assert.Equal(JobKind.Http, h.Engine.Snapshot(added.JobId)!.Kind);
        Assert.Equal(stopped.CommittedBytes, h.Engine.Snapshot(added.JobId)!.CommittedBytes);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(stopped.PartialPath));
        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);
        Assert.Equal(JobKind.Http, done.Kind);
        Assert.Equal(added.JobId, done.Id);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Single(h.Engine.Snapshots());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionSwitchFromHttpToDashRequiresAnExactFragmentBoundary(bool misaligned)
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        DashDownloadPlan plan = server.Plan(ranges: false);
        const string quality = "720p h264 with audio";
        string oldUrl = server.Url("/all.mp4");
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "switch.mp4", Kind: JobKind.Http,
            MediaProvider: "fixture", MediaId: "av", MediaFormatId: plan.RepresentationId, MediaQuality: quality));
        Assert.True(added.Ok, added.Message);
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        DownloadJob job = jobs[added.JobId];
        int count = Fixture("av-init.mp4").Length + Fixture("av-1.m4s").Length + (misaligned ? 1 : 0);
        byte[] prefix = Expected[..count];
        await File.WriteAllBytesAsync(job.PartialPath, prefix);
        h.OwnFixturePartial(job.Id);
        var extents = new ExtentSet();
        extents.Add(new Vesper.Engine.ByteRange(0, count));
        h.Engine.UpdateJob(job, current =>
        {
            current.Extents = extents;
            current.State = JobState.NeedsLink;
            current.Reason = "expired";
        });
        var choice = new MediaAcquisitionReplacement(new ResolvedMediaSource("fixture", "av", plan.RepresentationId,
            server.Url("/manifest.mpd")), JobKind.Dash, "switch.mp4", quality, plan.ToJson());
        RescueResult result = await h.Engine.SwitchMediaAcquisitionAsync(job.Id, choice, oldUrl);
        Assert.Equal(!misaligned, result.Accepted);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(job.PartialPath));
        if (misaligned)
        {
            Assert.Equal(JobKind.Http, h.Engine.Snapshot(job.Id)!.Kind);
            return;
        }
        Assert.Equal(count, result.RetainedBytes);
        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);
        Assert.Equal(JobKind.Dash, h.Engine.Snapshot(job.Id)!.Kind);
        Assert.Equal(count, h.Engine.Snapshot(job.Id)!.CommittedBytes);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(job.PartialPath));
        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(job.Id, JobState.Completed);
        Assert.Equal(JobKind.Dash, done.Kind);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(1, server.Hits("/av-1.m4s"));
    }

    [Theory]
    [InlineData(JobKind.Http)]
    [InlineData(JobKind.Dash)]
    public async Task AcquisitionSwitchImportsACompleteHlsPrefixAndSurvivesRestart(JobKind originalKind)
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        const string quality = "720p h264 with audio";
        var plan = server.Plan(ranges: false);
        string oldUrl = server.Url(originalKind == JobKind.Http ? "/all.mp4" : "/manifest.mpd");
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "switch.mp4", Kind: originalKind,
            MediaProvider: "fixture", MediaId: "av", MediaFormatId: plan.RepresentationId, MediaQuality: quality,
            MediaPlanJson: originalKind == JobKind.Dash ? plan.ToJson() : null));
        Assert.True(added.Ok, added.Message);
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        DownloadJob job = jobs[added.JobId];
        int count = Fixture("av-init.mp4").Length + Fixture("av-1.m4s").Length;
        byte[] prefix = Expected[..count];
        await File.WriteAllBytesAsync(job.PartialPath, prefix);
        h.OwnFixturePartial(job.Id);
        var extents = new ExtentSet();
        extents.Add(new Vesper.Engine.ByteRange(0, count));
        h.Engine.UpdateJob(job, current =>
        {
            current.Extents = extents;
            current.State = JobState.NeedsLink;
            current.Reason = "expired";
        });
        var choice = new MediaAcquisitionReplacement(new ResolvedMediaSource("fixture", "av", plan.RepresentationId,
            server.Url("/hls.m3u8")), JobKind.Hls, "switch.mp4", quality);
        RescueResult wrong = await h.Engine.SwitchMediaAcquisitionAsync(job.Id, choice with
        {
            Source = choice.Source with { Url = server.Url("/wrong.m3u8") }
        }, oldUrl);
        Assert.False(wrong.Accepted);
        Assert.Equal(originalKind, h.Engine.Snapshot(job.Id)!.Kind);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(job.PartialPath));
        RescueResult result = await h.Engine.SwitchMediaAcquisitionAsync(job.Id, choice, oldUrl);
        Assert.True(result.Accepted, result.Message);
        Assert.Equal(count, result.RetainedBytes);
        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);
        Assert.Equal(JobKind.Hls, h.Engine.Snapshot(job.Id)!.Kind);
        Assert.Equal(count, h.Engine.Snapshot(job.Id)!.CommittedBytes);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(job.PartialPath));
        int firstHits = server.Hits("/av-1.m4s");
        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(job.Id, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(firstHits, server.Hits("/av-1.m4s"));
        Assert.Single(h.Engine.Snapshots());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpMirrorRetainsSparseRangesOnlyWhenEveryRangeMatches(bool changeValidator)
    {
        await using var server = await FixtureServer.StartAsync();
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        const string quality = "720p h264 with audio";
        string oldUrl = server.Url("/all.mp4");
        AddResult added = h.Engine.Add(new AddRequest(oldUrl, FileName: "mirror.mp4", Kind: JobKind.Http,
            MediaProvider: "fixture", MediaId: "av", MediaFormatId: "av-source-muxed", MediaQuality: quality));
        Assert.True(added.Ok, added.Message);
        var jobs = (Dictionary<Guid, DownloadJob>)typeof(DownloadEngine)
            .GetField("_jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Engine)!;
        DownloadJob job = jobs[added.JobId];
        byte[] partial = new byte[Expected.Length];
        int block = Expected.Length / 5;
        Expected.AsSpan(0, block).CopyTo(partial);
        Expected.AsSpan(block * 3, block).CopyTo(partial.AsSpan(block * 3));
        await File.WriteAllBytesAsync(job.PartialPath, partial);
        h.OwnFixturePartial(job.Id);
        var extents = new ExtentSet();
        extents.Add(new Vesper.Engine.ByteRange(0, block));
        extents.Add(new Vesper.Engine.ByteRange(block * 3, block * 4));
        h.Engine.UpdateJob(job, current =>
        {
            current.Extents = extents;
            current.TotalBytes = Expected.Length;
            current.State = JobState.NeedsLink;
            current.Reason = "expired";
        });
        var choice = new MediaAcquisitionReplacement(new ResolvedMediaSource("fixture", "av", "av-source-muxed",
            server.Url("/mirror.mp4")), JobKind.Http, "mirror.mp4", quality, ExactSize: Expected.Length);
        RescueResult wrong = await h.Engine.SwitchMediaAcquisitionAsync(job.Id, choice with
        {
            Source = choice.Source with { Url = server.Url("/wrong.mp4") }
        }, oldUrl);
        Assert.False(wrong.Accepted);
        Assert.Equal(partial, await File.ReadAllBytesAsync(job.PartialPath));
        server.ChangeMirrorValidator = changeValidator;
        RescueResult result = await h.Engine.SwitchMediaAcquisitionAsync(job.Id, choice, oldUrl);
        Assert.Equal(!changeValidator, result.Accepted);
        Assert.Equal(partial, await File.ReadAllBytesAsync(job.PartialPath));
        if (changeValidator)
        {
            Assert.Equal(oldUrl, h.Engine.Snapshot(job.Id)!.SourceUrl);
            return;
        }
        Assert.Equal(block * 2, result.RetainedBytes);
        h.Restart(tweak: options => options.MaxConcurrentJobs = 0);
        Assert.Equal(block * 2, h.Engine.Snapshot(job.Id)!.CommittedBytes);
        h.Engine.Options.MaxConcurrentJobs = 3;
        h.Engine.SettingsChanged();
        JobSnapshot done = await h.WaitForStateAsync(job.Id, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Single(h.Engine.Snapshots());
    }

    [Fact]
    public async Task AutomaticDashRefreshRebasesOnlyAnIdenticalCommittedPrefix()
    {
        await using var server = await FixtureServer.StartAsync();
        server.ExpireSecond = true;
        using var h = new Harness();
        Guid id = Add(h, server);
        JobSnapshot stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stopped.CommittedBytes > 0);
        byte[] before = await File.ReadAllBytesAsync(stopped.PartialPath);
        DashDownloadPlan original = server.Plan(ranges: false);
        DashDownloadPlan fresh = original with
        {
            Initialization = original.Initialization with { Url = original.Initialization.Url + "?fresh=1" },
            Fragments = original.Fragments.Select(part => part with { Url = part.Url + "?fresh=1" }).ToArray()
        };
        var source = new ResolvedMediaSource("fixture", "av", original.RepresentationId,
            server.Url("/manifest.mpd?fresh=1"));
        RescueResult stale = await h.Engine.RefreshDashPlanAsync(id, source, fresh.ToJson(),
            expectedExpiredSource: stopped.SourceUrl + "?stale=1");
        Assert.False(stale.Accepted);
        var mismatched = fresh with { Fragments = fresh.Fragments.Select((part, index) =>
            index == 0 ? part with { Url = server.Url("/audio-1.m4s") } : part).ToArray() };
        RescueResult refused = await h.Engine.RefreshDashPlanAsync(id, source, mismatched.ToJson(),
            expectedExpiredSource: stopped.SourceUrl);
        Assert.False(refused.Accepted);
        Assert.Equal(stopped.SourceUrl, h.Engine.Snapshot(id)!.SourceUrl);
        Assert.Equal(before, await File.ReadAllBytesAsync(stopped.PartialPath));
        server.ExpireSecond = false;
        RescueResult accepted = await h.Engine.RefreshDashPlanAsync(id, source, fresh.ToJson(),
            expectedExpiredSource: stopped.SourceUrl);
        Assert.True(accepted.Accepted, accepted.Message);
        Assert.Equal(stopped.CommittedBytes, accepted.RetainedBytes);
        var done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task FileLinkRescue_CannotReplaceAFragmentManifest()
    {
        await using var server = await FixtureServer.StartAsync(); server.ExpireSecond = true;
        using var h = new Harness(); Guid id = Add(h, server);
        var stopped = await h.WaitForStateAsync(id, JobState.NeedsLink);
        var decision = await h.Engine.ReplaceLinkAsync(id, server.Url("/different.mp4"));
        Assert.False(decision.Accepted);
        Assert.Equal(stopped.CommittedBytes, h.Engine.Snapshot(id)!.CommittedBytes);
        Assert.Equal(0, server.Hits("/different.mp4"));
    }

    [Fact]
    public async Task CancellationDuringBodyRead_IsPromptAndPublishesNothing()
    {
        await using var server = await FixtureServer.StartAsync(); server.SlowFirst = true;
        using var h = new Harness(); Guid id = Add(h, server);
        await server.FirstFragmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = System.Diagnostics.Stopwatch.StartNew(); h.Engine.Pause(id);
        var paused = await h.WaitForStateAsync(id, JobState.Paused);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2)); Assert.Null(paused.FinalPath);

        server.SlowFirst = false;
        Guid next = Add(h, server, suffix: "?after-cancel=1");
        var completed = await h.WaitForStateAsync(next, JobState.Completed);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(completed.FinalPath!));
    }

    [Fact]
    public async Task ConcurrentDashJobsShareThreePrefetchBuffers()
    {
        await using var server = await FixtureServer.StartAsync();
        server.FragmentDelayMs = 60;
        using var h = new Harness(tweak: options =>
        {
            options.MaxConcurrentJobs = 3;
            options.MaxJobsPerHost = 3;
        });
        Guid[] ids = [Add(h, server, suffix: "?job=1"), Add(h, server, suffix: "?job=2"), Add(h, server, suffix: "?job=3")];

        var completed = await Task.WhenAll(ids.Select(id => h.WaitForStateAsync(id, JobState.Completed)));

        Assert.InRange(server.MaxActiveFragmentRequests, 2, 3);
        foreach (var job in completed) Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
    }

    [Fact]
    public async Task ConcurrentJobs_NeverOverwriteAFileWithTheSameName()
    {
        await using var server = await FixtureServer.StartAsync(); using var h = new Harness();
        Guid a = Add(h, server), b = Add(h, server, suffix: "?other=1");
        var results = await Task.WhenAll(h.WaitForStateAsync(a, JobState.Completed), h.WaitForStateAsync(b, JobState.Completed));
        Assert.NotEqual(results[0].FinalPath, results[1].FinalPath);
        foreach (var job in results) Assert.Equal(Expected, await File.ReadAllBytesAsync(job.FinalPath!));
    }

    private static Guid Add(Harness h, FixtureServer server, bool ranges = false, string suffix = "")
    {
        var plan = server.Plan(ranges);
        var added = h.Engine.Add(new AddRequest(server.Url("/manifest.mpd") + suffix, FileName: "fixture.mp4", Kind: JobKind.Dash,
            MediaPlanJson: plan.ToJson(), MediaProvider: "fixture", MediaId: "av", MediaFormatId: plan.RepresentationId));
        Assert.True(added.Ok, added.Message); return added.JobId;
    }

    private static MediaMergePlan SegmentedPlan(FixtureServer server)
    {
        string source = server.Url("/manifest.mpd");
        DashDownloadPlan video = server.Plan(ranges: false) with
        {
            RepresentationId = "video-source",
            Fragments = server.Plan(ranges: false).Fragments
                .Select(fragment => fragment with { DurationSeconds = 1 }).ToArray(),
            RequireAudio = false,
            RequireVideo = true
        };
        var audio = new DashDownloadPlan
        {
            RepresentationId = "audio-source",
            Initialization = new DashFragment(server.Url("/av-init.mp4")),
            Fragments = Enumerable.Range(1, 3)
                .Select(i => new DashFragment(server.Url($"/av-{i}.m4s"), DurationSeconds: 1)).ToArray(),
            RequireAudio = true,
            RequireVideo = false
        };
        return new MediaMergePlan
        {
            Version = 2, OutputContainer = "mp4", RequireAudio = true, RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", video.RepresentationId, source, 0, "mp4", "h264", Source: new MergeMediaSource("dash", Dash: video)),
                new MergeTrack("audio", audio.RepresentationId, source, 0, "m4a", "aac", Source: new MergeMediaSource("dash", Dash: audio))
            ]
        };
    }

    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentDictionary<string, int> _hits = new();
        private readonly Dictionary<string, byte[]> _objects = new();
        private int _activeFragmentRequests;
        private int _maxActiveFragmentRequests;
        private string _base = "";
        internal bool IgnoreRanges, InvalidFragment, ExpireSecond, SlowFirst, OversizedRangeBody, ChangeMirrorValidator;
        internal int FragmentDelayMs;
        internal string? RequiredCookie;
        internal bool RejectCredentials;
        internal int BadCredentialRequests;
        internal int MaxActiveFragmentRequests => Volatile.Read(ref _maxActiveFragmentRequests);
        internal TaskCompletionSource FirstFragmentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Url(string path) => _base + path;
        internal int Hits(string path) => _hits.GetValueOrDefault(path);
        private FixtureServer()
        {
            foreach (string name in new[] { "av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s", "audio-init.mp4", "audio-1.m4s" }) _objects["/" + name] = Fixture(name);
            _objects["/all.mp4"] = Expected;
            _objects["/mirror.mp4"] = Expected;
            byte[] wrong = Expected.ToArray(); wrong[wrong.Length * 3 / 5 + 1] ^= 0x5a;
            _objects["/wrong.mp4"] = wrong;
            byte[] wrongFragment = Fixture("av-1.m4s").ToArray(); wrongFragment[^1] ^= 0x5a;
            _objects["/wrong-1.m4s"] = wrongFragment;
            string playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MAP:URI=\"av-init.mp4\"\n"
                + "#EXTINF:1,\nav-1.m4s\n#EXTINF:1,\nav-2.m4s\n#EXTINF:1,\nav-3.m4s\n#EXT-X-ENDLIST\n";
            _objects["/hls.m3u8"] = Encoding.UTF8.GetBytes(playlist);
            _objects["/wrong.m3u8"] = Encoding.UTF8.GetBytes(playlist.Replace("av-1.m4s", "wrong-1.m4s"));
            var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            _app = builder.Build(); _app.Run(ServeAsync);
        }
        internal static async Task<FixtureServer> StartAsync()
        {
            var server = new FixtureServer(); await server._app.StartAsync(); server._base = server._app.Urls.Single(); return server;
        }
        internal DashDownloadPlan Plan(bool ranges)
        {
            var init = new DashFragment(Url("/av-init.mp4"));
            var fragments = Enumerable.Range(1, 3).Select(i => new DashFragment(Url($"/av-{i}.m4s"))).ToArray();
            if (ranges)
            {
                long offset = Fixture("av-init.mp4").Length; init = new(Url("/all.mp4"), 0, offset);
                for (int i = 0; i < 3; i++) { int size = Fixture($"av-{i + 1}.m4s").Length; fragments[i] = new(Url("/all.mp4"), offset, size); offset += size; }
            }
            return new() { RepresentationId = "av-source-muxed", Provider = "fixture", MediaId = "av", Initialization = init,
                Fragments = fragments, RequireAudio = true, RequireVideo = true };
        }
        private async Task ServeAsync(HttpContext context)
        {
            string path = context.Request.Path.Value!; _hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (RequiredCookie is { } required && (context.Request.Headers.Cookie != required
                    || context.Request.Headers.Authorization != "Bearer fixture-private")
                || RejectCredentials && (context.Request.Headers.ContainsKey("Cookie") || context.Request.Headers.ContainsKey("Authorization")))
            {
                Interlocked.Increment(ref BadCredentialRequests);
                context.Response.StatusCode = 403;
                return;
            }
            if (path == "/av-2.m4s" && ExpireSecond) { context.Response.StatusCode = 403; return; }
            if (!_objects.TryGetValue(path, out var data)) { context.Response.StatusCode = 404; return; }
            bool isFragment = path.EndsWith(".m4s", StringComparison.Ordinal);
            if (isFragment)
            {
                int active = Interlocked.Increment(ref _activeFragmentRequests);
                int currentMax;
                while (active > (currentMax = Volatile.Read(ref _maxActiveFragmentRequests))
                    && Interlocked.CompareExchange(ref _maxActiveFragmentRequests, active, currentMax) != currentMax) { }
                try
                {
                    if (path == "/av-1.m4s")
                    {
                        FirstFragmentStarted.TrySetResult();
                        if (SlowFirst) { try { await Task.Delay(20_000, context.RequestAborted); } catch (OperationCanceledException) { return; } }
                        if (InvalidFragment) data = Encoding.UTF8.GetBytes("not a media container");
                    }
                    if (FragmentDelayMs > 0 && !(path == "/av-1.m4s" && SlowFirst))
                        await Task.Delay(FragmentDelayMs, context.RequestAborted);
                    int fragmentStart = 0, fragmentEnd = data.Length - 1;
                    if (!IgnoreRanges && context.Request.Headers.Range.ToString() is { Length: > 6 } fragmentRange)
                    {
                        var parts = fragmentRange[6..].Split('-'); fragmentStart = int.Parse(parts[0]); fragmentEnd = int.Parse(parts[1]);
                        context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {fragmentStart}-{fragmentEnd}/{data.Length}";
                    }
                    context.Response.ContentType = "video/mp4"; context.Response.ContentLength = fragmentEnd - fragmentStart + 1;
                    await context.Response.Body.WriteAsync(data.AsMemory(fragmentStart, fragmentEnd - fragmentStart + 1), context.RequestAborted);
                }
                finally { Interlocked.Decrement(ref _activeFragmentRequests); }
                return;
            }
            int start = 0, end = data.Length - 1;
            if (!IgnoreRanges && context.Request.Headers.Range.ToString() is { Length: > 6 } range)
            {
                var parts = range[6..].Split('-'); start = int.Parse(parts[0]); end = parts[1].Length == 0 ? data.Length - 1 : int.Parse(parts[1]);
                context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {start}-{end}/{data.Length}";
                if (OversizedRangeBody)
                {
                    context.Response.ContentType = "video/mp4";
                    await context.Response.Body.WriteAsync(data.AsMemory(start, end - start + 1), context.RequestAborted);
                    await context.Response.Body.WriteAsync(new byte[] { 0xA5 }, context.RequestAborted);
                    return;
                }
            }
            context.Response.Headers.ETag = path == "/mirror.mp4" && ChangeMirrorValidator && start > 0 ? "\"changed\"" : "\"fixture-full-file\"";
            context.Response.ContentType = "video/mp4"; context.Response.ContentLength = end - start + 1;
            await context.Response.Body.WriteAsync(data.AsMemory(start, end - start + 1), context.RequestAborted);
        }
        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
