using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>End-to-end: the real engine, real sockets, real files, final bytes compared with known-good fixtures.</summary>
public class SequentialAndResumeTests
{
    [Fact]
    public async Task A_plain_download_produces_identical_bytes_and_the_expected_state()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file"), "plain.bin");

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(server.V1.Length, done.CommittedBytes);
        Assert.Single(h.Files());                                                  // the .vpart staging file is gone
        Assert.False(File.Exists(done.PartialPath));
    }

    [Fact]
    public async Task A_response_without_content_length_completes_with_the_right_size()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/chunked"), "chunked.bin");
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Equal(server.V1.Length, done.TotalBytes);
    }

    [Fact]
    public async Task A_mid_stream_disconnect_is_resumed_and_the_result_is_byte_exact()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/truncate-once"), "resume.bin");

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.True(server.Hits("/truncate-once") >= 2);
        Assert.Contains(server.RangeHeaders, r => r.StartsWith("/truncate-once bytes=") && !r.EndsWith("bytes=0-"));   // it really resumed, not restarted
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_is_replayed_and_verified_not_trusted_blindly()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/norange-truncate-once"), "norange.bin");
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task A_changed_remote_file_never_gets_mixed_into_saved_data()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/changes-after-truncate"), "changed.bin");

        JobSnapshot stopped = await h.WaitAsync(id, s => s.State == JobState.NeedsAttention);
        Assert.Equal("changed", stopped.Reason);
        Assert.True(stopped.CommittedBytes > 0);                                   // data kept
        Assert.True(File.Exists(stopped.PartialPath));
        byte[] saved = await File.ReadAllBytesAsync(stopped.PartialPath);
        Assert.True(saved.AsSpan(0, (int)stopped.CommittedBytes).SequenceEqual(server.V1.AsSpan(0, (int)stopped.CommittedBytes)));
        Assert.DoesNotContain(h.Files(), f => !f.EndsWith(".vpart") && !f.EndsWith(".vmeta"));   // nothing published
    }

    [Fact]
    public async Task Retry_after_is_honoured()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Guid id = h.Add(server.Url("/flaky503"), "flaky.bin");
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 40);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.8), "two 503s with Retry-After: 1 must cost at least ~2 s");
        Assert.Equal(3, server.Hits("/flaky503"));
    }

    [Fact]
    public async Task Pause_checkpoints_and_resume_continues_from_the_committed_point()
    {
        await using var server = new FaultServer(4 * 1024 * 1024); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/slow"), "pause.bin");
        await h.WaitAsync(id, s => s.State == JobState.Downloading && s.ProgressBytes > 200_000);

        h.Engine.Pause(id);
        JobSnapshot paused = await h.WaitForStateAsync(id, JobState.Paused);
        Assert.True(paused.CommittedBytes > 0 && paused.CommittedBytes < server.V1.Length);

        h.Engine.Resume(id);
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 60);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }
}

public class SegmentedTests
{
    [Fact]
    public async Task Segmented_download_is_byte_exact_and_uses_several_range_requests()
    {
        await using var server = new FaultServer(6 * 1024 * 1024); await server.StartAsync();
        using var h = new Harness(tweak: o => o.MaxConnectionsPerJob = 4);
        Guid id = h.Add(server.Url("/slow"), "segmented.bin");

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 90);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.True(server.RangeHeaders.Count(r => r.StartsWith("/slow")) >= 2, "expected the job to split into more than one range request");
    }

    [Fact]
    public async Task A_lying_content_range_is_rejected_and_nothing_wrong_is_committed()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/badrange"), "lie.bin");

        JobSnapshot stopped = await h.WaitAsync(id, s => s.State is JobState.NeedsAttention or JobState.Completed);
        if (stopped.State == JobState.Completed)   // a server that is only wrong for ranges starting after zero may never be asked
            Assert.Equal(server.V1, await File.ReadAllBytesAsync(stopped.FinalPath!));
        else
        {
            Assert.Equal("bad-range", stopped.Reason);
            byte[] saved = await File.ReadAllBytesAsync(stopped.PartialPath);
            Assert.True(saved.AsSpan(0, (int)stopped.CommittedBytes).SequenceEqual(server.V1.AsSpan(0, (int)stopped.CommittedBytes)));
        }
    }
}

public class ProtocolFaultTests
{
    [Fact]
    public async Task A_redirect_loop_is_detected()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot s = await h.WaitAsync(h.Add(server.Url("/loop-a")), x => x.State == JobState.NeedsAttention);
        Assert.Equal("redirect", s.Reason);
    }

    [Fact]
    public async Task A_redirect_to_a_real_file_is_followed()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(h.Add(server.Url("/redirect"), "r.bin"), JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task A_fresh_404_fails_but_an_expired_link_with_progress_needs_a_new_link()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot failed = await h.WaitAsync(h.Add(server.Url("/notfound")), x => x.State == JobState.Failed);
        Assert.Equal("not-found", failed.Reason);

        Guid id = h.Add(server.Url("/expires-after-truncate"), "expiring.bin");
        JobSnapshot needsLink = await h.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(needsLink.CommittedBytes > 0);
        Assert.True(File.Exists(needsLink.PartialPath));
    }

    [Fact]
    public async Task A_zero_byte_file_completes_even_when_the_server_rejects_range_requests()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(h.Add(server.Url("/empty"), "empty.bin"), JobState.Completed);
        Assert.Equal(0, new FileInfo(done.FinalPath!).Length);
    }

    [Fact]
    public async Task A_web_page_instead_of_a_file_is_not_saved_as_the_download()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot s = await h.WaitAsync(h.Add(server.Url("/html"), "movie.mkv"), x => x.State == JobState.NeedsAttention);
        Assert.Equal("html", s.Reason);
        Assert.Equal(0, s.CommittedBytes);
    }

    [Fact]
    public async Task A_wrongly_gzipped_body_is_decoded_to_the_real_bytes()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(h.Add(server.Url("/gzip"), "g.bin"), JobState.Completed);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Malicious_server_file_names_stay_inside_the_destination()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        JobSnapshot done = await h.WaitForStateAsync(h.Add(server.Url("/disposition")), JobState.Completed);
        Assert.Equal("evil.txt", Path.GetFileName(done.FinalPath));
        Assert.Equal(h.Downloads, Path.GetDirectoryName(done.FinalPath));
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        string existing = Path.Combine(h.Downloads, "keep.bin");
        await File.WriteAllTextAsync(existing, "precious");

        JobSnapshot done = await h.WaitForStateAsync(h.Add(server.Url("/file"), "keep.bin"), JobState.Completed);
        Assert.Equal("precious", await File.ReadAllTextAsync(existing));
        Assert.Equal("keep (1).bin", Path.GetFileName(done.FinalPath));
    }

    [Fact]
    public async Task Duplicate_links_and_repeated_browser_requests_do_not_create_extra_jobs()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        var first = h.Engine.Add(new AddRequest(server.Url("/slow"), RequestId: "browser-req-0001"));
        var again = h.Engine.Add(new AddRequest(server.Url("/slow"), RequestId: "browser-req-0001"));
        var other = h.Engine.Add(new AddRequest(server.Url("/slow#fragment")));
        Assert.Equal(AddStatus.Added, first.Status);
        Assert.Equal(AddStatus.AlreadySubmitted, again.Status);
        Assert.Equal(first.JobId, again.JobId);
        Assert.Equal(AddStatus.Duplicate, other.Status);
        Assert.Single(h.Engine.Snapshots());
    }

    [Fact]
    public void Private_addresses_are_refused_by_default()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-strict-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using DownloadEngine engine = DownloadEngine.Start(new EngineOptions { DataDirectory = root, DefaultFolder = Path.Combine(root, "d") });
            Assert.Equal(AddStatus.Invalid, engine.Add(new AddRequest("http://127.0.0.1:9/file")).Status);
            Assert.Equal(AddStatus.Invalid, engine.Add(new AddRequest("http://localhost/file")).Status);
            Assert.Equal(AddStatus.Invalid, engine.Add(new AddRequest("http://192.168.1.20/file")).Status);
            Assert.Empty(engine.Snapshots());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* temp directory left behind is harmless */ }
        }
    }
}

public class IntegrityTests
{
    [Fact]
    public async Task A_matching_trusted_hash_verifies_and_a_wrong_one_withholds_the_file()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();

        JobSnapshot ok = await h.WaitForStateAsync(h.Add(server.Url("/file"), "ok.bin", Payloads.Sha256(server.V1)), JobState.Completed);
        Assert.True(ok.Sha256Verified);

        Guid bad = h.Add(server.Url("/v2"), "bad.bin", Payloads.Sha256(server.V1));
        JobSnapshot suspect = await h.WaitForStateAsync(bad, JobState.Suspect);
        Assert.Equal("hash-mismatch", suspect.Reason);
        Assert.True(File.Exists(suspect.PartialPath));                       // every byte kept
        Assert.False(File.Exists(Path.Combine(h.Downloads, "bad.bin")));     // and not published
    }

    [Fact]
    public async Task Verify_without_a_trusted_hash_reports_a_digest_but_claims_no_proof()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file"), "v.bin");
        await h.WaitForStateAsync(id, JobState.Completed);
        VerifyResult result = await h.Engine.VerifyIntegrityAsync(id);
        Assert.True(result.Ran);
        Assert.Null(result.Matches);
        Assert.Equal(Payloads.Sha256(server.V1), result.Sha256);
        Assert.Contains("does not prove", result.Message);
    }
}

public class FileOperationTests
{
    [Fact]
    public async Task Removing_a_record_never_deletes_the_file_and_deleting_the_file_is_separate()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file"), "keep.bin");
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);

        RemoveResult removed = h.Engine.RemoveFromList(id);
        Assert.True(removed.Removed);
        Assert.True(File.Exists(done.FinalPath));                            // untouched

        Guid second = h.Add(server.Url("/lmonly"), "second.bin");
        JobSnapshot done2 = await h.WaitForStateAsync(second, JobState.Completed);
        DeleteResult deleted = h.Engine.DeleteDownloadedFile(second);
        Assert.True(deleted.Deleted);
        Assert.False(File.Exists(done2.FinalPath));
        Assert.True((await h.WaitAsync(second, s => s.FileMissing)).FileMissing);   // "completed but file missing", not "failed"
    }

    [Fact]
    public async Task A_replaced_file_of_a_different_size_is_not_deleted()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/file"), "swap.bin");
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        await File.WriteAllTextAsync(done.FinalPath!, "the user put something else here");

        Assert.False(h.Engine.DeleteDownloadedFile(id).Deleted);
        Assert.True(File.Exists(done.FinalPath));
    }

    [Fact]
    public async Task Removing_an_unfinished_entry_keeps_its_partial_file()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/expires-after-truncate"), "partial.bin");
        JobSnapshot s = await h.WaitForStateAsync(id, JobState.NeedsLink);
        RemoveResult removed = h.Engine.RemoveFromList(id);
        Assert.Equal(s.PartialPath, removed.RetainedPartialPath);
        Assert.True(File.Exists(s.PartialPath));
    }
}

public class RescueTests
{
    private static async Task<(Harness h, Guid id, JobSnapshot stalled)> StalledAsync(FaultServer server)
    {
        var h = new Harness();
        Guid id = h.Add(server.Url("/expires-after-truncate"), "rescue.bin");
        return (h, id, await h.WaitForStateAsync(id, JobState.NeedsLink));
    }

    [Fact]
    public async Task R0_a_new_link_with_the_same_strong_identity_reuses_saved_bytes()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var (h, id, stalled) = await StalledAsync(server);
        using (h)
        {
            long saved = stalled.CommittedBytes;
            RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.Url("/expires-after-truncate?token=fresh"));
            Assert.True(result.Accepted, result.Message);
            Assert.Equal(RescueMode.R0, result.Mode);

            JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
            Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
            Assert.Contains(server.RangeHeaders, r => r.StartsWith("/expires-after-truncate") && r.Contains($"bytes={saved}-"));   // continued, did not restart
        }
    }

    [Fact]
    public async Task R4_a_different_file_is_rejected_and_the_partial_is_left_exactly_as_it_was()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var (h, id, stalled) = await StalledAsync(server);
        using (h)
        {
            byte[] before = await File.ReadAllBytesAsync(stalled.PartialPath);
            RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.Url("/v2"));

            Assert.False(result.Accepted);
            Assert.Equal(RescueMode.R4, result.Mode);
            JobSnapshot after = h.Engine.Snapshot(id)!;
            Assert.Equal(JobState.NeedsLink, after.State);
            Assert.Equal(stalled.SourceUrl, after.SourceUrl);
            Assert.Equal(stalled.CommittedBytes, after.CommittedBytes);
            Assert.Equal(before, await File.ReadAllBytesAsync(stalled.PartialPath));
        }
    }

    [Fact]
    public async Task R1_unvalidated_source_with_a_trusted_hash_succeeds_when_the_bytes_really_match()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var (h, id, _) = await StalledAsync(server);
        using (h)
        {
            RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.Url("/noetag"), new RescueInput(Payloads.Sha256(server.V1)));
            Assert.Equal(RescueMode.R1, result.Mode);
            JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
            Assert.True(done.Sha256Verified);
            Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        }
    }

    [Fact]
    public async Task R1_a_hash_mismatch_ends_as_suspect_with_everything_preserved()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var (h, id, _) = await StalledAsync(server);
        using (h)
        {
            // /v2 differs only in its last bytes, so headers alone cannot tell; the trusted hash can.
            RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.Url("/v2"), new RescueInput(Payloads.Sha256(server.V1)));
            Assert.Equal(RescueMode.R1, result.Mode);
            JobSnapshot suspect = await h.WaitForStateAsync(id, JobState.Suspect);
            Assert.Equal("hash-mismatch", suspect.Reason);
            Assert.True(File.Exists(suspect.PartialPath));
            Assert.DoesNotContain(h.Files(), f => !f.EndsWith(".vpart") && !f.EndsWith(".vmeta"));
        }
    }

    [Fact]
    public async Task R1b_a_corrupted_stored_piece_is_found_and_only_that_piece_is_fetched_again()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var (h, id, stalled) = await StalledAsync(server);
        using (h)
        {
            const int pieceLength = 256 * 1024;
            var hashes = Enumerable.Range(0, (server.V1.Length + pieceLength - 1) / pieceLength)
                .Select(i => Payloads.Sha256(server.V1[(i * pieceLength)..Math.Min(server.V1.Length, (i + 1) * pieceLength)])).ToArray();
            string map = JsonSerializer.Serialize(new { pieceLength, totalLength = server.V1.Length, sha256 = hashes });

            // Damage one byte inside a stored piece, as a bad sector or an overeager tool might.
            using (var file = new FileStream(stalled.PartialPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                file.Position = pieceLength + 10;
                file.WriteByte((byte)(server.V1[pieceLength + 10] ^ 0xFF));
            }

            RescueResult result = await h.Engine.ReplaceLinkAsync(id, server.Url("/file?token=2"), new RescueInput(PieceMapJson: map));
            Assert.Equal(RescueMode.R1b, result.Mode);
            Assert.True(result.PiecesToRefetch >= 1);

            JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 40);
            Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));    // repaired, byte for byte
        }
    }
}

public class CrashRecoveryTests
{
    private sealed class CrashAt(string point, int occurrence = 1)
    {
        private int _seen;
        public void Check(string name)
        {
            if (name == point && Interlocked.Increment(ref _seen) == occurrence) throw new FailpointCrash(name);
        }
    }

    [Theory]
    [InlineData("checkpoint.beforeFlush")]
    [InlineData("checkpoint.afterFlush")]     // flushed but never committed: the database must not claim those bytes
    [InlineData("checkpoint.afterCommit")]    // committed but the manifest/in-memory state never advanced
    public async Task A_crash_at_every_checkpoint_boundary_still_ends_in_a_byte_exact_file(string point)
    {
        await using var server = new FaultServer(3 * 1024 * 1024); await server.StartAsync();
        var crash = new CrashAt(point, occurrence: 3);
        using var h = new Harness(crash.Check);
        Guid id = h.Add(server.Url("/slow"), "crash.bin");

        await Task.Delay(1500);                          // let the simulated process die somewhere mid-transfer
        h.Restart();                                     // "reboot": new engine, same database and files

        JobSnapshot restored = h.Engine.Snapshot(id)!;
        Assert.NotEqual(JobState.Completed, restored.State);                 // a crash must never turn into a bogus completion
        Assert.True(restored.CommittedBytes < server.V1.Length);
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 60);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Committed_bytes_never_exceed_what_the_partial_file_contains_after_a_crash()
    {
        await using var server = new FaultServer(3 * 1024 * 1024); await server.StartAsync();
        var crash = new CrashAt("checkpoint.afterFlush", occurrence: 4);
        using var h = new Harness(crash.Check);
        Guid id = h.Add(server.Url("/slow"), "invariant.bin");
        await Task.Delay(1500);

        h.Engine.Dispose();
        using var store = JobStore.Open(Path.Combine(h.Data, "vesper.db"));
        DownloadJob job = store.LoadAll().Single(j => j.Id == id);
        long fileLength = new FileInfo(job.PartialPath).Length;
        Assert.True(job.Extents.End <= fileLength);
        byte[] onDisk = await File.ReadAllBytesAsync(job.PartialPath);
        foreach (ByteRange r in job.Extents.Ranges)
            Assert.True(onDisk.AsSpan((int)r.Start, (int)r.Length).SequenceEqual(server.V1.AsSpan((int)r.Start, (int)r.Length)), "committed bytes must be correct bytes");
    }

    [Theory]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task A_crash_while_publishing_neither_loses_nor_duplicates_the_file(string point)
    {
        await using var server = new FaultServer(); await server.StartAsync();
        var crash = new CrashAt(point);
        using var h = new Harness(crash.Check);
        Guid id = h.Add(server.Url("/file"), "final.bin");
        await Task.Delay(1500);
        h.Restart();

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed, 30);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        Assert.Single(h.Files(), f => f.EndsWith(".bin"));             // exactly one published copy
        Assert.DoesNotContain(h.Files(), f => f.EndsWith(".vpart"));
    }

    [Fact]
    public async Task A_full_disk_stops_conservatively_and_keeps_the_data()
    {
        await using var server = new FaultServer(3 * 1024 * 1024); await server.StartAsync();
        int writes = 0;
        using var h = new Harness(name =>
        {
            if (name == "write.afterWrite" && Interlocked.Increment(ref writes) == 30)
                throw new LocalStorageException("disk full", new IOException("full", unchecked((int)0x80070070)));
        });
        Guid id = h.Add(server.Url("/slow"), "full.bin");
        JobSnapshot s = await h.WaitAsync(id, x => x.State == JobState.NeedsAttention);
        Assert.Equal("disk-full", s.Reason);
        Assert.True(File.Exists(s.PartialPath));
    }

    [Fact]
    public async Task A_lost_database_is_rebuilt_from_recovery_manifests_as_needs_link()
    {
        await using var server = new FaultServer(); await server.StartAsync();
        using var h = new Harness();
        Guid id = h.Add(server.Url("/expires-after-truncate"), "lost.bin");
        JobSnapshot stalled = await h.WaitForStateAsync(id, JobState.NeedsLink);
        h.Engine.Dispose();

        File.WriteAllText(Path.Combine(h.Data, "vesper.db"), "garbage" + new string('z', 4096));
        foreach (string leftover in Directory.GetFiles(h.Data, "vesper.db-*")) File.Delete(leftover);
        h.Restart();

        Assert.True(h.Engine.RecoveredFromCorruptDatabase);
        JobSnapshot recovered = h.Engine.Snapshot(id)!;
        Assert.Equal(JobState.NeedsLink, recovered.State);
        Assert.True(recovered.CommittedBytes > 0 && recovered.CommittedBytes <= stalled.CommittedBytes);
        Assert.True(File.Exists(stalled.PartialPath));
    }
}
