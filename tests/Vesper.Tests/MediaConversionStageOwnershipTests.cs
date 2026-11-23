using System.Runtime.Versioning;
using Vesper.Engine;
using Xunit;

namespace Vesper.Tests;

[SupportedOSPlatform("windows")]
public sealed class MediaConversionStageOwnershipTests
{
    private const string Marker = "belongs to another stage owner";

    [WindowsFact]
    public async Task Conversion_does_not_delete_a_tag_stage_it_never_created()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = Audio();
        server.Extra["/audio.mp4"] = sourceBytes;
        await server.StartAsync();

        string? unrelatedStage = null;
        using var harness = new Harness(convertMedia: (sourcePath, _, destinationPath, _, ct) =>
        {
            unrelatedStage = sourcePath + ".convert-tagged.mp4";
            File.WriteAllText(unrelatedStage, Marker);

            using var source = File.OpenRead(sourcePath);
            DemuxedTrack audio = Assert.Single(MediaMerger.Demux(source, source.Length, ct));
            using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            MediaMerger.Merge([(audio, source)], "m4a", destination, ct);
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.m4a", done.FileName);
        Assert.NotNull(unrelatedStage);
        Assert.Equal(Marker, File.ReadAllText(unrelatedStage));
    }

    [WindowsFact]
    public async Task Conversion_preserves_a_replacement_at_cleanup_path()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();

        string? stagePath = null;
        using var harness = new Harness(failpoint: point =>
        {
            if (point != "conversion.beforeTemporaryCleanup" || stagePath is null) return;
            File.Move(stagePath, stagePath + ".saved");
            File.WriteAllText(stagePath, Marker);
        }, convertMedia: (_, _, target, _, _) =>
        {
            stagePath = target;
            File.WriteAllBytes(target, [1, 2, 3]);
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.NotNull(stagePath);
        Assert.Equal(Marker, File.ReadAllText(stagePath));
        Assert.Equal(Audio(), File.ReadAllBytes(done.FinalPath!));
    }

    [WindowsFact]
    public async Task Conversion_stage_collision_is_preserved()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = Audio();
        server.Extra["/audio.mp4"] = sourceBytes;
        await server.StartAsync();

        DateTime localNow = DateTime.Now;
        var closedWindow = new SchedulePolicy(true,
            TimeOnly.FromDateTime(localNow.AddMinutes(-20)), TimeOnly.FromDateTime(localNow.AddMinutes(-10)));
        using var harness = new Harness(tweak: options => options.Schedule = closedWindow);
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a, StartNow: false));
        Assert.True(added.Ok, added.Message);

        JobSnapshot queued = harness.Engine.Snapshot(added.JobId)!;
        string collision = queued.PartialPath + ".convert.mp4";
        File.WriteAllText(collision, Marker);
        harness.Restart(tweak: options => options.Schedule = SchedulePolicy.Always);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal(Marker, File.ReadAllText(collision));
        Assert.Equal(sourceBytes, File.ReadAllBytes(done.FinalPath!));
    }

    [WindowsFact]
    public async Task Tag_stage_collision_is_preserved_and_converted_output_is_kept()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = Audio();
        server.Extra["/audio.mp4"] = sourceBytes;
        await server.StartAsync();

        string? collision = null;
        byte[]? convertedBytes = null;
        using var harness = new Harness(convertMedia: (sourcePath, _, destinationPath, _, ct) =>
        {
            collision = sourcePath + ".convert-tagged.mp4";
            File.WriteAllText(collision, Marker);
            convertedBytes = WriteConvertedM4a(sourcePath, destinationPath, ct);
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a,
            MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(new MediaMergeMetadata { Title = "Track" })));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.NotNull(collision);
        Assert.Equal(Marker, File.ReadAllText(collision));
        Assert.Equal("audio.m4a", done.FileName);
        Assert.NotNull(convertedBytes);
        Assert.Equal(convertedBytes, File.ReadAllBytes(done.FinalPath!));
    }

    [WindowsFact]
    public async Task Failed_prepared_move_restores_the_previous_conversion_stage()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();

        string? taggedStage = null;
        byte[]? convertedBytes = null;
        using var harness = new Harness(failpoint: point =>
        {
            if (point == "conversion.afterStageBackup") File.Delete(taggedStage!);
        }, convertMedia: (sourcePath, _, destinationPath, _, ct) =>
        {
            taggedStage = sourcePath + ".convert-tagged.mp4";
            convertedBytes = WriteConvertedM4a(sourcePath, destinationPath, ct);
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a,
            MediaMetadataJson: MetadataJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.m4a", done.FileName);
        Assert.NotNull(convertedBytes);
        Assert.Equal(convertedBytes, File.ReadAllBytes(done.FinalPath!));
        Assert.DoesNotContain(harness.Files(), path => Path.GetFileName(path).StartsWith(".vesper-rollback-", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Rollback_collision_preserves_old_stage_and_new_target()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = Audio();
        server.Extra["/audio.mp4"] = sourceBytes;
        await server.StartAsync();

        string? stagePath = null;
        string? taggedStage = null;
        string? stageIdentity = null;
        byte[]? convertedBytes = null;
        using var harness = new Harness(failpoint: point =>
        {
            if (point != "conversion.afterStageBackup") return;
            File.Delete(taggedStage!);
            File.WriteAllText(stagePath!, Marker);
        }, convertMedia: (sourcePath, _, destinationPath, _, ct) =>
        {
            stagePath = destinationPath;
            taggedStage = sourcePath + ".convert-tagged.mp4";
            convertedBytes = WriteConvertedM4a(sourcePath, destinationPath, ct);
            using var stage = File.OpenHandle(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stageIdentity = PathSafety.TemporaryFileIdentity(stage);
        });

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a,
            MediaMetadataJson: MetadataJson()));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.NotNull(stagePath);
        Assert.NotNull(stageIdentity);
        Assert.Equal(Marker, File.ReadAllText(stagePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(done.FinalPath!));
        string backup = Path.Combine(Path.GetDirectoryName(stagePath)!, $".vesper-rollback-{added.JobId:N}-{stageIdentity}");
        Assert.Equal(convertedBytes, File.ReadAllBytes(backup));
        using var backupFile = File.OpenHandle(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(stageIdentity, PathSafety.TemporaryFileIdentity(backupFile));
    }

    [WindowsFact]
    public async Task Failed_wav_clip_swap_needs_attention_and_keeps_owned_stages()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = Audio();
        server.Extra["/audio.mp4"] = sourceBytes;
        await server.StartAsync();

        using var mediaSource = new MemoryStream(sourceBytes);
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(mediaSource, mediaSource.Length, CancellationToken.None));
        var range = new MediaTimeRange(audio.DurationSeconds * .25, audio.DurationSeconds * .75);
        DateTime localNow = DateTime.Now;
        var closedWindow = new SchedulePolicy(true,
            TimeOnly.FromDateTime(localNow.AddMinutes(-20)), TimeOnly.FromDateTime(localNow.AddMinutes(-10)));

        Guid jobId = Guid.Empty;
        string folder = "";
        string stagePath = "";
        string taggedPath = "";
        string? rollbackPath = null;
        string? previousStageIdentity = null;
        string? preparedStageIdentity = null;
        Action<string> failpoint = point =>
        {
            if (point != "conversion.afterStageBackup") return;
            rollbackPath = Directory.GetFiles(folder, ".vesper-rollback-" + jobId.ToString("N") + "-*").SingleOrDefault();
            if (rollbackPath is not null) previousStageIdentity = ReadTemporaryIdentity(rollbackPath);
            if (File.Exists(taggedPath)) preparedStageIdentity = ReadTemporaryIdentity(taggedPath);
            File.WriteAllText(stagePath, Marker);
        };
        using var harness = new Harness(tweak: options => options.Schedule = closedWindow, failpoint: failpoint,
            convertMedia: (sourcePath, _, destinationPath, _, ct) => WriteDecodedWave(sourcePath, destinationPath, ct));

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Wav,
            MediaPlanJson: System.Text.Json.JsonSerializer.Serialize(range),
            MediaMetadataJson: MetadataJson(), StartNow: false));
        Assert.True(added.Ok, added.Message);
        jobId = added.JobId;
        JobSnapshot queued = harness.Engine.Snapshot(jobId)!;
        folder = Path.GetDirectoryName(queued.PartialPath)!;
        stagePath = queued.PartialPath + ".convert.wav";
        taggedPath = queued.PartialPath + ".convert-tagged.mp4";

        harness.Restart(failpoint: failpoint, tweak: options => options.Schedule = SchedulePolicy.Always);

        JobSnapshot attention = await harness.WaitForStateAsync(jobId, JobState.NeedsAttention);

        Assert.Equal("storage", attention.Reason);
        Assert.Null(attention.FinalPath);
        Assert.Equal(sourceBytes, File.ReadAllBytes(attention.PartialPath));
        Assert.Equal(Marker, File.ReadAllText(stagePath));
        Assert.NotNull(rollbackPath);
        Assert.NotNull(previousStageIdentity);
        Assert.Equal(previousStageIdentity, ReadTemporaryIdentity(rollbackPath));
        Assert.NotNull(preparedStageIdentity);
        Assert.Equal(preparedStageIdentity, ReadTemporaryIdentity(taggedPath));
        using var previousWav = File.OpenRead(rollbackPath);
        using var clippedWav = File.OpenRead(taggedPath);
        Assert.True(WaveAudio.Read(previousWav).SampleCount > WaveAudio.Read(clippedWav).SampleCount);

        harness.Restart(failpoint: failpoint, tweak: options => options.Schedule = SchedulePolicy.Always);
        harness.Engine.Resume(jobId);
        JobSnapshot retryAttention = await harness.WaitForStateAsync(jobId, JobState.NeedsAttention);
        Assert.Null(retryAttention.FinalPath);
        Assert.Equal(sourceBytes, File.ReadAllBytes(retryAttention.PartialPath));
    }

    [WindowsFact]
    public async Task Failed_unclipped_wav_swap_publishes_the_verified_previous_wav_stage()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();

        DateTime localNow = DateTime.Now;
        var closedWindow = new SchedulePolicy(true,
            TimeOnly.FromDateTime(localNow.AddMinutes(-20)), TimeOnly.FromDateTime(localNow.AddMinutes(-10)));
        string? taggedStage = null;
        Action<string> failpoint = point =>
        {
            if (point == "conversion.afterStageBackup") File.Delete(taggedStage!);
        };
        using var harness = new Harness(tweak: options => options.Schedule = closedWindow, failpoint: failpoint,
            convertMedia: (sourcePath, _, destinationPath, _, ct) => WriteDecodedWave(sourcePath, destinationPath, ct));

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.Wav, StartNow: false));
        Assert.True(added.Ok, added.Message);
        taggedStage = harness.Engine.Snapshot(added.JobId)!.PartialPath + ".convert-tagged.mp4";

        harness.Restart(failpoint: failpoint, tweak: options => options.Schedule = SchedulePolicy.Always);

        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.wav", done.FileName);
        using var output = File.OpenRead(done.FinalPath!);
        Assert.True(WaveAudio.Read(output).SampleCount > 0);
    }

    [WindowsFact]
    public void Stream_stage_is_removed_if_identity_capture_fails()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-owned-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "stage.mp4");
        try
        {
            Assert.Throws<IOException>(() => DownloadEngine.CreateOwnedStage(path, FileShare.None, out _,
                identityReader: _ => null));
            Assert.False(File.Exists(path));

            string identity;
            using (DownloadEngine.CreateOwnedStage(path, FileShare.None, out identity)) { }
            Assert.True(PathSafety.TryDeleteTemporaryFile(path, identity, out string error), error);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Media_foundation_stage_is_removed_if_identity_capture_fails()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-mf-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "stage.mp4");
        try
        {
            Assert.Throws<IOException>(() => WindowsMediaConverter.CreateOwnedOutputStage(path, out _,
                identityReader: _ => null));
            Assert.False(File.Exists(path));

            string identity;
            using (WindowsMediaConverter.CreateOwnedOutputStage(path, out identity)) { }
            Assert.True(PathSafety.TryDeleteTemporaryFile(path, identity, out string error), error);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] Audio() =>
        [.. File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "audio-init.mp4")),
         .. File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "audio-1.m4s"))];

    private static string MetadataJson() =>
        System.Text.Json.JsonSerializer.Serialize(new MediaMergeMetadata { Title = "Track" });

    private static string? ReadTemporaryIdentity(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PathSafety.TemporaryFileIdentity(file);
    }

    private static byte[] WriteConvertedM4a(string sourcePath, string destinationPath, CancellationToken ct)
    {
        using var source = File.OpenRead(sourcePath);
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(source, source.Length, ct));
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            MediaMerger.Merge([(audio, source)], "m4a", destination, ct);
        }
        return File.ReadAllBytes(destinationPath);
    }

    private static void WriteDecodedWave(string sourcePath, string destinationPath, CancellationToken ct)
    {
        using var source = File.OpenRead(sourcePath);
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(source, source.Length, ct));
        int blockAlign = audio.Channels * sizeof(short);
        long sampleCount = checked((long)Math.Round(audio.DurationSeconds * audio.SampleRate));
        long dataBytes = checked(sampleCount * blockAlign);
        if (dataBytes > uint.MaxValue - 36) throw new IOException("The test WAV is too large.");

        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)(dataBytes + 36));
        writer.Write("WAVEfmt "u8);
        writer.Write(16u);
        writer.Write((short)1);
        writer.Write((short)audio.Channels);
        writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);

        byte[] silence = new byte[64 * 1024];
        while (dataBytes > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(silence.Length, dataBytes);
            writer.Write(silence.AsSpan(0, count));
            dataBytes -= count;
        }
        writer.Flush();
    }
}
