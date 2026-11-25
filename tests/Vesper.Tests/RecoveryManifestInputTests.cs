using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class RecoveryManifestInputTests
{
    [Fact]
    public void Oversized_recovery_file_is_rejected_before_deserialization()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-large-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "download.vpart.vmeta");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                file.SetLength(RecoveryManifest.MaxManifestBytes + 1L);

            Assert.False(RecoveryManifest.TryRecover(path, out _, out string problem));
            Assert.Equal("recovery file is too large", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Null_body_in_recovery_file_is_reported_as_invalid_input()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vesper-null-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "download.vpart.vmeta");
        try
        {
            File.WriteAllText(path, "{\"Body\":null,\"Check\":\"abc\"}");

            Assert.False(RecoveryManifest.TryRecover(path, out _, out string problem));
            Assert.Equal("malformed recovery file", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Invalid_destination_text_is_reported_as_bad_input()
    {
        string directory = CreateDirectory("vesper-invalid-destination-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            WriteManifest(manifest, Body("bad\0path", "file.bin"));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("the recovery file names an invalid folder", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Null_filename_uses_a_safe_fallback()
    {
        string directory = CreateDirectory("vesper-null-filename-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            WriteManifest(manifest, Body(directory, null));

            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob job, out string problem), problem);
            Assert.Equal("download", job.FileName);
            Assert.Equal("https://example.test/file?…", job.SourceUrl);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///private/file.bin")]
    [InlineData("ftp://example.test/file")]
    public void Missing_or_unsupported_source_url_is_rejected(string? url)
    {
        string directory = CreateDirectory("vesper-invalid-url-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", url: url));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery source URL", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Empty_job_id_is_rejected()
    {
        string directory = CreateDirectory("vesper-empty-manifest-id-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", jobId: Guid.Empty));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery job id", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Negative_total_length_is_rejected()
    {
        string directory = CreateDirectory("vesper-negative-manifest-total-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", total: -1));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery file length", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Unknown_identity_level_is_rejected()
    {
        string directory = CreateDirectory("vesper-invalid-manifest-identity-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", identity: int.MaxValue));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery identity", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Database_loss_recovery_preserves_valid_http_resource_identity()
    {
        string directory = CreateDirectory("vesper-resource-identity-manifest-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string identity = HttpTransport.ResourceIdentity(new Uri("https://cdn.example/file.bin?signature=private"));
        var extents = new ExtentSet();
        extents.Add(new ByteRange(0, 3));
        var original = new DownloadJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Http,
            SourceUrl = "https://source.example/entry?ticket=private",
            FileName = "download.bin",
            DestinationFolder = directory,
            PartialPath = partial,
            TotalBytes = 3,
            Extents = extents,
            State = JobState.Paused,
            FinalResourceIdentity = identity
        };

        try
        {
            RecoveryManifest.Write(original);

            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob restored, out string problem), problem);
            Assert.Equal(identity, restored.FinalResourceIdentity);
            Assert.True(HttpTransport.IsResourceIdentity(restored.FinalResourceIdentity));
            Assert.Equal(original.Generation, restored.Generation);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Negative_generation_is_rejected()
    {
        string directory = CreateDirectory("vesper-negative-manifest-generation-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", generation: -1));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery generation", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Undefined_job_kind_is_rejected()
    {
        string directory = CreateDirectory("vesper-invalid-manifest-kind-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", kind: int.MaxValue));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery job kind", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Non_hex_final_resource_identity_is_rejected()
    {
        string directory = CreateDirectory("vesper-invalid-resource-identity-");
        string manifest = RecoveryManifest.PathFor(CreatePartial(directory));
        try
        {
            WriteManifest(manifest, Body(directory, "file.bin", finalResourceIdentity: "not-hex"));

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("invalid recovery resource identity", problem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_write_does_not_truncate_a_preplanted_stage_file()
    {
        string directory = CreateDirectory("vesper-planted-manifest-stage-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string stage = manifest + ".tmp";
        byte[] marker = "unrelated stage bytes"u8.ToArray();
        try
        {
            File.WriteAllBytes(stage, marker);

            Assert.ThrowsAny<IOException>(() => RecoveryManifest.Write(WriteJob(directory, partial)));

            Assert.Equal(marker, File.ReadAllBytes(stage));
            Assert.False(File.Exists(manifest));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_write_preserves_an_invalid_existing_manifest_and_unrelated_file()
    {
        string directory = CreateDirectory("vesper-invalid-prior-manifest-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string unrelated = Path.Combine(directory, "keep.bin");
        byte[] invalidManifest = "invalid prior manifest"u8.ToArray();
        byte[] unrelatedBytes = [2, 4, 6, 8];
        try
        {
            File.WriteAllBytes(manifest, invalidManifest);
            File.WriteAllBytes(unrelated, unrelatedBytes);

            Assert.ThrowsAny<IOException>(() => RecoveryManifest.Write(WriteJob(directory, partial)));

            Assert.Equal(invalidManifest, File.ReadAllBytes(manifest));
            Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_write_replaces_a_valid_manifest_for_the_same_job()
    {
        string directory = CreateDirectory("vesper-same-job-manifest-update-");
        string partial = CreatePartial(directory);
        try
        {
            File.WriteAllBytes(partial, [1, 2, 3, 4]);
            DownloadJob job = WriteJob(directory, partial);
            RecoveryManifest.Write(job);

            job.TotalBytes = 4;
            var updated = new ExtentSet();
            updated.Add(new ByteRange(0, 4));
            job.Extents = updated;
            RecoveryManifest.Write(job);

            Assert.True(RecoveryManifest.TryRecover(RecoveryManifest.PathFor(partial), out DownloadJob restored,
                out string problem), problem);
            Assert.Equal(job.Id, restored.Id);
            Assert.Equal(4, restored.TotalBytes);
            Assert.Equal("0-4", restored.Extents.Serialize());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_write_refuses_a_valid_manifest_for_another_job()
    {
        string directory = CreateDirectory("vesper-other-job-manifest-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            RecoveryManifest.Write(WriteJob(directory, partial));
            byte[] priorManifest = File.ReadAllBytes(manifest);

            Assert.ThrowsAny<IOException>(() => RecoveryManifest.Write(WriteJob(directory, partial)));

            Assert.Equal(priorManifest, File.ReadAllBytes(manifest));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_delete_removes_only_a_manifest_for_the_same_job()
    {
        string directory = CreateDirectory("vesper-owned-manifest-delete-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            using (SafeFileHandle handle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                owner.PartialIdentity = PathSafety.TemporaryFileIdentity(handle);
            RecoveryManifest.Write(owner);
            DownloadJob other = WriteJob(directory, partial);

            Assert.False(RecoveryManifest.Delete(other));
            Assert.True(File.Exists(manifest));
            if (owner.PartialIdentity is null)
            {
                Assert.False(RecoveryManifest.Delete(owner));
                Assert.True(File.Exists(manifest));
            }
            else
            {
                Assert.True(RecoveryManifest.Delete(owner));
                Assert.False(File.Exists(manifest));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_delete_refuses_a_null_identity_manifest_after_partial_replacement()
    {
        string directory = CreateDirectory("vesper-null-identity-delete-");
        string partial = CreatePartial(directory);
        string moved = partial + ".original";
        string manifest = RecoveryManifest.PathFor(partial);
        byte[] replacement = [4, 5, 6];
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            Assert.Null(owner.PartialIdentity);
            RecoveryManifest.Write(owner);
            File.Move(partial, moved);
            File.WriteAllBytes(partial, replacement);

            Assert.False(RecoveryManifest.Delete(owner));
            Assert.True(File.Exists(manifest));
            Assert.Equal(replacement, File.ReadAllBytes(partial));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(moved));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void New_recovery_manifest_recovers_and_cleans_with_its_saved_file_identity()
    {
        string directory = CreateDirectory("vesper-manifest-self-identity-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            owner.PartialIdentity = TemporaryIdentity(partial);
            RecoveryManifest.Write(owner);

            using JsonDocument envelope = JsonDocument.Parse(File.ReadAllText(manifest));
            string bodyJson = envelope.RootElement.GetProperty("Body").GetString()!;
            using JsonDocument body = JsonDocument.Parse(bodyJson);
            Assert.Equal(TemporaryIdentity(manifest), body.RootElement.GetProperty("ManifestIdentity").GetString());

            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob recovered, out string problem), problem);
            Assert.Equal(owner.Id, recovered.Id);
            Assert.True(RecoveryManifest.Delete(owner));
            Assert.False(File.Exists(manifest));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(partial));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Copied_recovery_manifest_is_rejected_and_preserved_when_its_file_identity_changes()
    {
        string directory = CreateDirectory("vesper-manifest-identity-copy-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string original = manifest + ".original";
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            owner.PartialIdentity = TemporaryIdentity(partial);
            RecoveryManifest.Write(owner);
            byte[] manifestBytes = File.ReadAllBytes(manifest);
            string savedIdentity = TemporaryIdentity(manifest);

            File.Move(manifest, original);
            File.WriteAllBytes(manifest, manifestBytes);

            Assert.NotEqual(savedIdentity, TemporaryIdentity(manifest));
            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out string problem));
            Assert.Equal("recovery file identity mismatch", problem);
            Assert.False(RecoveryManifest.Delete(owner));
            Assert.Equal(manifestBytes, File.ReadAllBytes(manifest));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Recovery_delete_accepts_the_same_partial_after_publication_move()
    {
        string directory = CreateDirectory("vesper-published-manifest-delete-");
        string partial = CreatePartial(directory);
        string final = Path.Combine(directory, "download.bin");
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            using (SafeFileHandle handle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                owner.PartialIdentity = PathSafety.TemporaryFileIdentity(handle);
            RecoveryManifest.Write(owner);
            File.Move(partial, final);
            owner.FinalPath = final;
            owner.PublishedFileIdentity = owner.PartialIdentity;

            Assert.True(RecoveryManifest.Delete(owner));
            Assert.False(File.Exists(manifest));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(final));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Recovery_delete_accepts_a_converted_publication_after_the_source_partial_is_removed()
    {
        string directory = CreateDirectory("vesper-converted-manifest-delete-");
        string partial = CreatePartial(directory);
        string final = Path.Combine(directory, "download.m4a");
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            owner.PartialIdentity = TemporaryIdentity(partial);
            RecoveryManifest.Write(owner);
            File.WriteAllBytes(final, [7, 8, 9, 10]);
            owner.FinalPath = final;
            owner.PublishedFileIdentity = TemporaryIdentity(final);
            File.Delete(partial);

            Assert.True(RecoveryManifest.Delete(owner));
            Assert.False(File.Exists(manifest));
            Assert.Equal([7, 8, 9, 10], File.ReadAllBytes(final));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Recovery_delete_keeps_the_manifest_when_published_path_identity_changed()
    {
        string directory = CreateDirectory("vesper-replaced-published-manifest-");
        string partial = CreatePartial(directory);
        string final = Path.Combine(directory, "download.bin");
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob owner = WriteJob(directory, partial);
            using (SafeFileHandle handle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                owner.PartialIdentity = PathSafety.TemporaryFileIdentity(handle);
            RecoveryManifest.Write(owner);
            File.Move(partial, final);
            owner.FinalPath = final;
            owner.PublishedFileIdentity = owner.PartialIdentity;
            File.Delete(final);
            File.WriteAllBytes(final, [1, 2, 3]);

            Assert.False(RecoveryManifest.Delete(owner));
            Assert.True(File.Exists(manifest));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(final));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Recovery_delete_accepts_a_stale_merge_manifest_only_when_the_mux_cursor_proves_it()
    {
        string directory = CreateDirectory("vesper-stale-merge-manifest-");
        string partial = CreatePartial(directory);
        string tracks = partial + ".tracks";
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            File.WriteAllBytes(tracks, [4, 5, 6]);
            string muxIdentity = TemporaryIdentity(partial);
            string tracksIdentity = TemporaryIdentity(tracks);
            var plan = new MediaMergePlan
            {
                OutputContainer = "mp4",
                RequireVideo = true,
                Tracks =
                [
                    new MergeTrack("video", "video-id", "https://example.test/video", 3, "mp4", "h264", "video-proof"),
                    new MergeTrack("audio", "audio-id", "https://example.test/audio", 3, "mp4", "aac", "audio-proof")
                ]
            };
            byte[] muxBytes = File.ReadAllBytes(partial);
            byte[] trackBytes = File.ReadAllBytes(tracks);
            string cursor = JsonSerializer.Serialize(new
            {
                Version = 1,
                Fingerprint = plan.Fingerprint,
                Phase = "muxed",
                MuxLength = muxBytes.LongLength,
                MuxSha256 = Convert.ToHexString(SHA256.HashData(muxBytes)),
                TracksLength = trackBytes.LongLength,
                TracksSha256 = Convert.ToHexString(SHA256.HashData(trackBytes)),
                MuxIdentity = muxIdentity,
                TracksIdentity = tracksIdentity
            });

            DownloadJob owner = WriteJob(directory, partial);
            owner.Kind = JobKind.Merge;
            owner.Generation = 2;
            owner.PartialIdentity = muxIdentity;
            owner.MediaPlanJson = plan.ToJson();
            owner.MediaState = cursor;
            DownloadJob previousCheckpoint = owner.CopyForUpdate();
            previousCheckpoint.Generation = 1;
            previousCheckpoint.PartialIdentity = tracksIdentity;
            RecoveryManifest.Write(previousCheckpoint);

            Assert.False(RecoveryManifest.TryRecover(manifest, out _, out _));
            owner.MediaState = null;
            Assert.False(RecoveryManifest.Delete(owner));
            Assert.True(File.Exists(manifest));

            owner.MediaState = cursor;
            RecoveryManifest.Write(owner);
            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob refreshed, out string problem), problem);
            Assert.Equal(muxIdentity, refreshed.PartialIdentity);
            Assert.True(RecoveryManifest.Delete(owner));
            Assert.False(File.Exists(manifest));
            Assert.True(File.Exists(partial));
            Assert.True(File.Exists(tracks));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Interrupted_manifest_replacement_restores_only_into_an_empty_primary_path()
    {
        string directory = CreateDirectory("vesper-manifest-previous-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string previous = manifest + ".previous";
        try
        {
            DownloadJob job = WriteJob(directory, partial);
            using (SafeFileHandle handle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                job.PartialIdentity = PathSafety.TemporaryFileIdentity(handle);
            RecoveryManifest.Write(job);
            File.Move(manifest, previous);

            RecoveryManifest.RecoverInterruptedReplacements(directory);

            Assert.True(File.Exists(manifest));
            Assert.False(File.Exists(previous));
            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob restored, out string problem), problem);
            Assert.Equal(job.Id, restored.Id);

            File.Move(manifest, previous);
            File.WriteAllText(manifest, "user data");
            RecoveryManifest.RecoverInterruptedReplacements(directory);

            Assert.Equal("user data", File.ReadAllText(manifest));
            Assert.True(File.Exists(previous));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Interrupted_manifest_restore_does_not_move_a_replacement_at_the_backup_path()
    {
        string directory = CreateDirectory("vesper-manifest-backup-swap-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        string previous = manifest + ".previous";
        string displaced = previous + ".saved";
        byte[] replacement = "user data"u8.ToArray();
        try
        {
            DownloadJob job = WriteJob(directory, partial);
            using (SafeFileHandle handle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                job.PartialIdentity = PathSafety.TemporaryFileIdentity(handle);
            RecoveryManifest.Write(job);
            byte[] priorManifest = File.ReadAllBytes(manifest);
            File.Move(manifest, previous);

            RecoveryManifest.RecoverInterruptedReplacements(directory, beforeRestore: path =>
            {
                Assert.Equal(previous, path);
                File.Move(path, displaced);
                File.WriteAllBytes(path, replacement);
            });

            Assert.False(File.Exists(manifest));
            Assert.Equal(replacement, File.ReadAllBytes(previous));
            Assert.Equal(priorManifest, File.ReadAllBytes(displaced));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void Recovery_manifest_can_be_refreshed_while_the_owner_holds_the_partial_handle()
    {
        string directory = CreateDirectory("vesper-live-manifest-refresh-");
        string partial = CreatePartial(directory);
        string manifest = RecoveryManifest.PathFor(partial);
        try
        {
            DownloadJob job = WriteJob(directory, partial);
            long generation;
            using (SafeFileHandle partialHandle = PathSafety.OpenTemporaryHandle(partial, FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, allowDelete: true))
            {
                job.PartialIdentity = PathSafety.TemporaryFileIdentity(partialHandle);
                RecoveryManifest.Write(job, partialHandle);
                job.Generation++;
                generation = job.Generation;
                RecoveryManifest.Write(job, partialHandle);
            }

            Assert.True(RecoveryManifest.TryRecover(manifest, out DownloadJob recovered, out string problem), problem);
            Assert.Equal(generation, recovered.Generation);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_preserves_trusted_verification_obligations(bool pieces)
    {
        string directory = CreateDirectory("vesper-manifest-integrity-");
        string partial = CreatePartial(directory);
        try
        {
            DownloadJob job = WriteJob(directory, partial);
            job.ExpectedSha256 = new string('a', 64);
            job.RescueCandidate = !pieces;
            job.UnverifiedResumeApproved = true;
            job.RescueMode = pieces ? RescueMode.R1b : RescueMode.R1;
            if (pieces) job.PieceMapJson = new PieceMap(16384, 3, [new string('b', 64)]).ToJson();
            RecoveryManifest.Write(job);
            Assert.True(RecoveryManifest.TryRecover(RecoveryManifest.PathFor(partial), out DownloadJob restored,
                out string problem), problem);
            Assert.Equal(job.ExpectedSha256, restored.ExpectedSha256);
            Assert.Equal(job.PieceMapJson, restored.PieceMapJson);
            Assert.Equal(job.RescueMode, restored.RescueMode);
            Assert.Equal(job.RescueCandidate, restored.RescueCandidate);
            Assert.False(restored.Sha256Verified);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreatePartial(string directory)
    {
        string partial = Path.Combine(directory, "download.vpart");
        File.WriteAllBytes(partial, [1, 2, 3]);
        return partial;
    }

    private static string TemporaryIdentity(string path)
    {
        using SafeFileHandle handle = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PathSafety.TemporaryFileIdentity(handle)!;
    }

    private static DownloadJob WriteJob(string directory, string partial)
    {
        var extents = new ExtentSet();
        extents.Add(new ByteRange(0, 3));
        return new DownloadJob
        {
            Id = Guid.NewGuid(),
            SourceUrl = "https://example.test/file?token=private",
            FileName = "download.bin",
            DestinationFolder = directory,
            PartialPath = partial,
            TotalBytes = 3,
            Extents = extents
        };
    }

    private static object Body(string destination, string? fileName, string? url = "https://example.test/file?token=private",
        Guid? jobId = null, long? total = 3, int identity = 0, string? finalResourceIdentity = null,
        long generation = 1, int kind = 0) => new
    {
        V = 1,
        JobId = jobId ?? Guid.NewGuid(),
        Generation = generation,
        Url = url,
        FileName = fileName,
        Total = total,
        Extents = "0-3",
        ETag = (string?)null,
        LastModified = (string?)null,
        Identity = identity,
        Destination = destination,
        Kind = kind,
        MediaMetadataJson = (string?)null,
        MediaThumbnailUrl = (string?)null,
        PartialIdentity = (string?)null,
        FinalResourceIdentity = finalResourceIdentity
    };

    private static void WriteManifest(string manifestPath, object body)
    {
        string bodyJson = JsonSerializer.Serialize(body);
        string check = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyJson)));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { Check = check, Body = bodyJson }));
    }
}
