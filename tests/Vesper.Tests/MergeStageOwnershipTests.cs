using System.Security.Cryptography;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MergeStageOwnershipTests
{
    [Theory]
    [InlineData(".merging")]
    [InlineData(".samples")]
    public void Cleanup_preserves_a_replacement_at_a_merge_sidecar_path(string suffix)
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-merge-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "job.vpart" + suffix);
        string originalPath = path + ".original";
        byte[] bytes = [1, 2, 3, 4, 5];
        string identity;

        try
        {
            using (FileStream stage = MergeTransfer.CreateNewSidecar(path))
            {
                stage.Write(bytes);
                stage.Flush(flushToDisk: true);
                identity = MergeTransfer.CaptureSidecarIdentity(stage);
            }

            File.Move(path, originalPath);
            File.WriteAllBytes(path, bytes);

            Assert.False(MergeTransfer.TryDeleteSidecar(path, identity, out string error));
            Assert.NotEmpty(error);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(originalPath));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(originalPath)) File.Delete(originalPath);
            Directory.Delete(folder);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_rebuilds_only_the_owned_merge_stage(bool replaceStage)
    {
        await using var server = new FaultServer();
        await server.StartAsync();

        byte[] video = Fixture("video-only.mp4");
        byte[] audio = [.. Fixture("audio-init.mp4"), .. Fixture("audio-1.m4s")];
        server.Extra["/m/video.mp4"] = video;
        server.Extra["/m/audio.m4a"] = audio;

        var plan = new MediaMergePlan
        {
            Provider = "test",
            MediaId = "stage-restart",
            OutputContainer = "mp4",
            RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), video.Length, "mp4", "h264", "v-id"),
                new MergeTrack("audio", "a1", server.Url("/m/audio.m4a"), audio.Length, "m4a", "aac", "a-id")
            ]
        };

        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(point =>
        {
            if (point != "merge.afterStageWrite") return;
            interrupted.TrySetResult();
            throw new FailpointCrash(point);
        });

        AddResult added = harness.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merge-stage.mp4",
            MediaPlanJson: plan.ToJson(), Kind: JobKind.Merge));
        Assert.True(added.Ok, added.Message);
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        string partial = harness.Engine.Snapshot(added.JobId)!.PartialPath;
        string stage = partial + ".merging";
        byte[] stagedOutput = await File.ReadAllBytesAsync(stage);
        Assert.NotEmpty(stagedOutput);

        if (replaceStage)
        {
            File.Move(stage, stage + ".original");
            File.WriteAllBytes(stage, stagedOutput);
        }

        harness.Restart();

        if (replaceStage)
        {
            JobSnapshot stopped = await harness.WaitForStateAsync(added.JobId, JobState.NeedsAttention);
            Assert.Equal("merge-integrity", stopped.Reason);
            Assert.Contains("no longer belongs", stopped.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(stagedOutput, await File.ReadAllBytesAsync(stage));
            Assert.Null(stopped.FinalPath);
        }
        else
        {
            JobSnapshot completed = await harness.WaitForStateAsync(added.JobId, JobState.Completed);
            Assert.Equal(stagedOutput, await File.ReadAllBytesAsync(completed.FinalPath!));
            Assert.False(File.Exists(stage));
        }
    }

    [Fact]
    public async Task Pause_after_stage_write_removes_the_owned_unjournaled_merge_output()
    {
        await using var server = new FaultServer();
        await server.StartAsync();

        byte[] video = Fixture("video-only.mp4");
        byte[] audio = [.. Fixture("audio-init.mp4"), .. Fixture("audio-1.m4s")];
        server.Extra["/m/video.mp4"] = video;
        server.Extra["/m/audio.m4a"] = audio;

        var plan = new MediaMergePlan
        {
            Provider = "test",
            MediaId = "stage-pause",
            OutputContainer = "mp4",
            RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), video.Length, "mp4", "h264", "v-id"),
                new MergeTrack("audio", "a1", server.Url("/m/audio.m4a"), audio.Length, "m4a", "aac", "a-id")
            ]
        };

        using var allowPause = new ManualResetEventSlim(false);
        var stageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness? activeHarness = null;
        Guid jobId = Guid.Empty;
        using var harness = new Harness(point =>
        {
            if (point != "merge.afterStageWrite") return;
            stageReady.TrySetResult();
            if (!allowPause.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test did not allow the merge cancellation to proceed.");
            activeHarness!.Engine.Pause(jobId);
        });
        activeHarness = harness;

        AddResult added = harness.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merge-stage-pause.mp4",
            MediaPlanJson: plan.ToJson(), Kind: JobKind.Merge));
        Assert.True(added.Ok, added.Message);
        jobId = added.JobId;
        await stageReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
        string partial = harness.Engine.Snapshot(jobId)!.PartialPath;
        allowPause.Set();

        JobSnapshot paused = await harness.WaitForStateAsync(jobId, JobState.Paused);

        Assert.Equal(JobState.Paused, paused.State);
        Assert.False(File.Exists(partial + ".merging"));
        Assert.False(File.Exists(partial + ".tracks"));
    }

    [Fact]
    public async Task Swap_does_not_move_a_same_byte_replacement_instead_of_the_owned_partial()
    {
        await using var server = new FaultServer();
        await server.StartAsync();

        byte[] video = Fixture("video-only.mp4");
        byte[] audio = [.. Fixture("audio-init.mp4"), .. Fixture("audio-1.m4s")];
        byte[] partialBytes = [.. video, .. audio];
        server.Extra["/m/video.mp4"] = video;
        server.Extra["/m/audio.m4a"] = audio;

        var plan = new MediaMergePlan
        {
            Provider = "test",
            MediaId = "stage-replacement",
            OutputContainer = "mp4",
            RequireVideo = true,
            Tracks =
            [
                new MergeTrack("video", "v1", server.Url("/m/video.mp4"), video.Length, "mp4", "h264", "v-id"),
                new MergeTrack("audio", "a1", server.Url("/m/audio.m4a"), audio.Length, "m4a", "aac", "a-id")
            ]
        };

        using var allowReplacement = new ManualResetEventSlim(false);
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? partialPath = null;
        string? savedOriginalPath = null;
        using var harness = new Harness(point =>
        {
            if (point != "merge.beforeTracksMove") return;
            if (!allowReplacement.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test did not allow the swap race to proceed.");

            string path = partialPath ?? throw new InvalidOperationException("The partial path was not set.");
            savedOriginalPath = path + ".owned";
            File.Move(path, savedOriginalPath);
            File.WriteAllBytes(path, partialBytes);
            replaced.TrySetResult();
        });

        AddResult added = harness.Engine.Add(new AddRequest(plan.Tracks[0].Url, FileName: "merge-stage-race.mp4",
            MediaPlanJson: plan.ToJson(), Kind: JobKind.Merge));
        Assert.True(added.Ok, added.Message);
        string activePartialPath = harness.Engine.Snapshot(added.JobId)!.PartialPath;
        partialPath = activePartialPath;
        allowReplacement.Set();

        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(15));
        JobSnapshot stopped = await harness.WaitForStateAsync(added.JobId, JobState.NeedsAttention);

        Assert.Equal("merge-integrity", stopped.Reason);
        Assert.Equal(partialBytes, await File.ReadAllBytesAsync(activePartialPath));
        Assert.Equal(partialBytes, await File.ReadAllBytesAsync(savedOriginalPath!));
        Assert.False(File.Exists(activePartialPath + ".tracks"));
        Assert.True(File.Exists(activePartialPath + ".merging"));
        Assert.Null(stopped.FinalPath);
    }

    [Fact]
    public void Delete_sidecars_preserves_a_same_content_replacement_of_saved_tracks()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-merge-tracks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, "job.vpart");
        string tracks = MergeTransfer.TracksPath(new DownloadJob { PartialPath = partial });
        string original = tracks + ".original";
        byte[] bytes = [9, 8, 7, 6, 5];

        try
        {
            string tracksIdentity;
            using (FileStream file = MergeTransfer.CreateNewSidecar(tracks))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                tracksIdentity = MergeTransfer.CaptureSidecarIdentity(file);
            }

            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            var cursor = new MergeTransfer.Cursor(1, "test", "muxed", MuxLength: bytes.Length, MuxSha256: digest,
                TracksLength: bytes.Length, TracksSha256: digest, MuxIdentity: tracksIdentity, TracksIdentity: tracksIdentity);
            var job = new DownloadJob { PartialPath = partial, MediaState = JsonSerializer.Serialize(cursor) };

            File.Move(tracks, original);
            File.WriteAllBytes(tracks, bytes);

            Assert.False(MergeTransfer.DeleteSidecars(job));
            Assert.Equal(bytes, File.ReadAllBytes(tracks));
            Assert.Equal(bytes, File.ReadAllBytes(original));
        }
        finally
        {
            if (File.Exists(tracks)) File.Delete(tracks);
            if (File.Exists(original)) File.Delete(original);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Delete_sidecars_keeps_legacy_muxed_tracks_without_saved_identity()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-legacy-merge-tracks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, "job.vpart");
        string tracks = MergeTransfer.TracksPath(new DownloadJob { PartialPath = partial });
        byte[] bytes = [9, 8, 7, 6, 5];

        try
        {
            File.WriteAllBytes(tracks, bytes);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            var cursor = new MergeTransfer.Cursor(1, "test", "muxed", MuxLength: bytes.Length, MuxSha256: digest,
                TracksLength: bytes.Length, TracksSha256: digest, MuxIdentity: digest);
            var job = new DownloadJob { PartialPath = partial, MediaState = JsonSerializer.Serialize(cursor) };

            Assert.False(MergeTransfer.DeleteSidecars(job));
            Assert.Equal(bytes, File.ReadAllBytes(tracks));
        }
        finally
        {
            if (File.Exists(tracks)) File.Delete(tracks);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Delete_sidecars_does_not_claim_a_merge_stage_in_muxed_phase()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-muxed-merge-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, "job.vpart");
        string tracks = MergeTransfer.TracksPath(new DownloadJob { PartialPath = partial });
        string merging = MergeTransfer.MergingPath(new DownloadJob { PartialPath = partial });
        byte[] bytes = [1, 2, 3];
        string tracksIdentity;
        string mergingIdentity;

        try
        {
            using (FileStream file = MergeTransfer.CreateNewSidecar(tracks))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                tracksIdentity = MergeTransfer.CaptureSidecarIdentity(file);
            }
            using (FileStream file = MergeTransfer.CreateNewSidecar(merging))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                mergingIdentity = MergeTransfer.CaptureSidecarIdentity(file);
            }

            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            var cursor = new MergeTransfer.Cursor(1, "test", "muxed", MuxLength: bytes.Length, MuxSha256: digest,
                TracksLength: bytes.Length, TracksSha256: digest, MuxIdentity: mergingIdentity, TracksIdentity: tracksIdentity);
            var job = new DownloadJob { PartialPath = partial, MediaState = JsonSerializer.Serialize(cursor) };

            Assert.False(MergeTransfer.DeleteSidecars(job));
            Assert.Equal(bytes, File.ReadAllBytes(tracks));
            Assert.Equal(bytes, File.ReadAllBytes(merging));
        }
        finally
        {
            if (File.Exists(tracks)) File.Delete(tracks);
            if (File.Exists(merging)) File.Delete(merging);
            Directory.Delete(folder);
        }
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Media", name));
}
