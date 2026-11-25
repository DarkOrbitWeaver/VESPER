using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>Bounded, ordered clear fMP4 transfer through the existing durable session, limiters and publish path.</summary>
internal sealed class DashTransfer(DownloadEngine engine, JobSession session, HttpTransport http,
    RequestCredentials? refreshCredentials = null, bool useStoredCredentials = true)
{
    internal const long MaxObjectBytes = 64L * 1024 * 1024;
    private const int MaxInitBytes = 8 * 1024 * 1024;
    private DownloadJob Job => session.Job;
    private sealed record Cursor(int Version, string PlanFingerprint, int Next, long Offset, int InitLength,
        string PrefixHash, Mp4Progress Progress);

    internal async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!DashDownloadPlan.TryParse(Job.MediaPlanJson, engine.Options.AllowPrivateNetworks, out var plan, out var error))
            return RunOutcome.NeedsAttention("dash-plan", error);
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out var folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");
        IReadOnlyList<DashFragment> fragments = plan.Fragments;
        if (plan.ClipRange is { } clipRange)
        {
            double[] durations = [.. plan.Fragments.Select(fragment => fragment.DurationSeconds ?? double.NaN)];
            double?[] starts = [.. plan.Fragments.Select(fragment => fragment.StartSeconds)];
            if (!clipRange.TrySelect(durations, starts, out int first, out int count, out double actualStart, out double actualEnd))
                return RunOutcome.NeedsAttention("clip-range", "The requested DASH time range is outside the available segment timeline.");
            fragments = plan.Fragments.Skip(first).Take(count).ToArray();
            string range = MediaTimeRange.Describe(actualStart, actualEnd);
            engine.UpdateJob(Job, job =>
            {
                string quality = job.MediaQuality ?? "DASH";
                int priorRange = quality.IndexOf(" · Segment-aligned ", StringComparison.Ordinal);
                if (priorRange >= 0) quality = quality[..priorRange];
                job.MediaQuality = quality + " · " + range;
            });
        }
        string fingerprint = plan.Fingerprint;
        Cursor? cursor = null;
        if (Job.MediaState is not null)
        {
            try { cursor = JsonSerializer.Deserialize<Cursor>(Job.MediaState, new JsonSerializerOptions { MaxDepth = 12 }); }
            catch (JsonException) { return Stop("The saved fragment cursor is invalid."); }
        }
        PartialFile file = session.OpenFile();
        long offset = Job.Extents.ContiguousFromZero();
        if ((cursor is null && offset != 0) || Job.Extents.TotalBytes != offset || file.Length < offset)
            return Stop("The saved fragment cursor does not match committed file bytes.");
        if (cursor is null && file.Length > 0) file.Truncate(0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FragmentedMp4 mp4;
        int next = 0, initLength;
        try
        {
            if (cursor is not null)
            {
                if (cursor.Version != 1 || cursor.PlanFingerprint != fingerprint || cursor.Next < 1 || cursor.Next > fragments.Count
                    || cursor.Offset != offset || cursor.InitLength is < 8 or > MaxInitBytes || cursor.InitLength >= offset
                    || cursor.Progress is null) return Stop("The saved plan or representation changed. Restart is required.");
                // One sequential pass on resume, not random spot checks. No corrupt prefix is trusted.
                MediaPartialIntegrity.HashPrefix(file, offset, hash, ct);
                if (!Convert.ToHexString(hash.GetCurrentHash()).Equals(cursor.PrefixHash, StringComparison.Ordinal))
                    return Stop("The committed partial file failed SHA-256 validation. It was not changed.");
                if (file.Length > offset) file.Truncate(offset);
                byte[] init = MediaPartialIntegrity.ReadInitialization(file, cursor.InitLength, ct);
                mp4 = FragmentedMp4.Open(init);
                mp4.Restore(cursor.Progress);
                next = cursor.Next; initLength = cursor.InitLength;
            }
            else
            {
                using SegmentBufferLease initLease = await engine.AcquireSegmentBufferAsync(
                    SegmentBufferLimiter.Reservation(MaxInitBytes, plan.Initialization.Length), ct).ConfigureAwait(false);
                ReadOnlyMemory<byte> init = await FetchAsync(plan.Initialization, MaxInitBytes, initLease, ct).ConfigureAwait(false);
                mp4 = FragmentedMp4.Open(init.Span);
                initLength = init.Length;
                if ((plan.RequireAudio && !mp4.HasAudio) || (plan.RequireVideo && !mp4.HasVideo))
                    return RunOutcome.NeedsAttention("missing-track", "Initialization is missing the selected audio/video track. Nothing was published.");
                EnsureSpace(init.Length);
                await LimitedWriteAsync(init, 0, ct).ConfigureAwait(false);
                hash.AppendData(init.Span);
                offset = init.Length;
            }
            session.SetPhase(JobState.Downloading);
            using var prefetch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = new Queue<Task<(ReadOnlyMemory<byte> Data, SegmentBufferLease Lease)>>();
            int fetchNext = next;
            DateTime lastMessage = DateTime.MinValue;
            void Fill()
            {
                while (pending.Count < 3 && fetchNext < fragments.Count)
                    pending.Enqueue(FetchBufferedAsync(fragments[fetchNext++], MaxObjectBytes, prefetch.Token));
            }
            try
            {
            for (int i = next; i < fragments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Fill();
                Task<(ReadOnlyMemory<byte> Data, SegmentBufferLease Lease)> fetchTask = pending.Dequeue();
                var fetched = await fetchTask.ConfigureAwait(false);
                fetchTask = null!;
                ReadOnlyMemory<byte> fragment = fetched.Data;
                SegmentBufferLease lease = fetched.Lease;
                fetched = default;
                using IDisposable segmentBuffer = lease;
                Fill();
                mp4.ValidateFragment(fragment.Span);
                EnsureSpace(fragment.Length);
                await LimitedWriteAsync(fragment, offset, ct).ConfigureAwait(false);
                hash.AppendData(fragment.Span);
                long start = i == 0 ? 0 : offset;
                offset = checked(offset + fragment.Length);
                var saved = new Cursor(1, fingerprint, i + 1, offset, initLength,
                    Convert.ToHexString(hash.GetCurrentHash()), mp4.Snapshot());
                session.Report(new ByteRange(start, offset), JsonSerializer.Serialize(saved));
                await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastMessage >= TimeSpan.FromSeconds(1))
                {
                    lastMessage = DateTime.UtcNow;
                    engine.UpdateJob(Job, j => j.Message = $"Fragment {i + 1} of {fragments.Count}");
                }
            }
            }
            finally
            {
                prefetch.Cancel();
                foreach (Task<(ReadOnlyMemory<byte> Data, SegmentBufferLease Lease)> fetch in pending)
                {
                    try
                    {
                        var fetched = await fetch.ConfigureAwait(false);
                        fetched.Lease.Dispose();
                        fetched = default;
                    }
                    catch (Exception) { }
                }
                pending.Clear();
            }
            mp4.Finish(plan.RequireAudio, plan.RequireVideo);
        }
        catch (MediaContainerException ex)
        {
            return RunOutcome.NeedsAttention("invalid-container", ex.Message + " No output was published; saved data was kept.");
        }
        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        engine.UpdateJob(Job, j => { j.TotalBytes = offset; j.Message = ""; });
        ct.ThrowIfCancellationRequested();
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    // A rotated fragment map may only replace the saved map after proving the exact committed prefix.
    internal async Task<(bool Valid, string? CursorJson, string Error)> VerifyRefreshAsync(
        DashDownloadPlan current, DashDownloadPlan replacement, CancellationToken ct)
    {
        long offset = Job.Extents.TotalBytes;
        if (Job.MediaState is null)
            return offset == 0 ? (true, null, "") : (false, null, "The saved DASH data has no verified cursor.");
        Cursor? cursor;
        try { cursor = JsonSerializer.Deserialize<Cursor>(Job.MediaState, new JsonSerializerOptions { MaxDepth = 12 }); }
        catch (JsonException) { return (false, null, "The saved DASH cursor is invalid."); }
        IReadOnlyList<DashFragment> fragments = replacement.Fragments;
        if (replacement.ClipRange is { } range)
        {
            if (!range.TrySelect([.. fragments.Select(fragment => fragment.DurationSeconds ?? double.NaN)],
                [.. fragments.Select(fragment => fragment.StartSeconds)], out int first, out int count, out _, out _))
                return (false, null, "The refreshed DASH timeline cannot reproduce the selected clip.");
            fragments = fragments.Skip(first).Take(count).ToArray();
        }
        if (cursor is null || cursor.Version != 1 || cursor.PlanFingerprint != current.Fingerprint
            || cursor.Next < 1 || cursor.Next > fragments.Count || cursor.Offset != offset
            || Job.Extents.ContiguousFromZero() != offset || cursor.InitLength is < 8 or > MaxInitBytes
            || cursor.InitLength >= offset || cursor.Progress is null || cursor.PrefixHash is not { Length: 64 })
            return (false, null, "The saved DASH cursor does not match the committed bytes.");
        using (PartialFile file = session.OpenFile(create: false))
        using (var saved = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (file.Length < offset) return (false, null, "The saved DASH file is shorter than its cursor.");
            MediaPartialIntegrity.HashPrefix(file, offset, saved, ct);
            if (Convert.ToHexString(saved.GetCurrentHash()) != cursor.PrefixHash)
                return (false, null, "The saved DASH prefix failed SHA-256 validation.");
        }

        using var fresh = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long fetchedBytes = 0;
        async Task<bool> AppendAsync(DashFragment part, long limit, int? exactLength = null)
        {
            var fetched = await FetchBufferedAsync(part, limit, ct).ConfigureAwait(false);
            using (fetched.Lease)
            {
                if (exactLength is int length && fetched.Data.Length != length
                    || fetched.Data.Length > offset - fetchedBytes) return false;
                fresh.AppendData(fetched.Data.Span);
                fetchedBytes += fetched.Data.Length;
                return true;
            }
        }

        if (!await AppendAsync(replacement.Initialization, MaxInitBytes, cursor.InitLength).ConfigureAwait(false))
            return (false, null, "The refreshed DASH initialization differs from the saved prefix.");
        for (int i = 0; i < cursor.Next; i++)
            if (!await AppendAsync(fragments[i], MaxObjectBytes).ConfigureAwait(false))
                return (false, null, "The refreshed DASH fragments differ from the saved prefix.");
        if (fetchedBytes != offset || Convert.ToHexString(fresh.GetCurrentHash()) != cursor.PrefixHash)
            return (false, null, "The refreshed DASH prefix differs from the saved bytes.");
        return (true, JsonSerializer.Serialize(cursor with { PlanFingerprint = replacement.Fingerprint }), "");
    }

    // Import a contiguous prefix from another acquisition route without rewriting any saved byte.
    // Only a complete initialization and one or more complete fragments can form a DASH cursor.
    internal async Task<(bool Valid, string? CursorJson, string Error)> ImportPrefixAsync(
        DashDownloadPlan plan, CancellationToken ct)
    {
        long retained = Job.Extents.TotalBytes;
        if (retained == 0) return (true, null, "");
        if (plan.ClipRange is not null || Job.Extents.ContiguousFromZero() != retained)
            return (false, null, "The existing data is not a complete contiguous DASH prefix.");
        using PartialFile file = session.OpenFile(create: false);
        if (file.Length < retained) return (false, null, "The saved partial file is shorter than its recorded progress.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long position = 0;
        bool Match(ReadOnlyMemory<byte> data)
        {
            if (data.Length == 0 || data.Length > retained - position) return false;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int done = 0;
                while (done < data.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = file.Read(buffer.AsSpan(0, Math.Min(buffer.Length, data.Length - done)), position + done);
                    if (n <= 0 || !buffer.AsSpan(0, n).SequenceEqual(data.Span.Slice(done, n))) return false;
                    done += n;
                }
                hash.AppendData(data.Span);
                position += data.Length;
                return true;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        FragmentedMp4 mp4;
        int initLength;
        var init = await FetchBufferedAsync(plan.Initialization, MaxInitBytes, ct).ConfigureAwait(false);
        using (init.Lease)
        {
            if (!Match(init.Data)) return (false, null, "The DASH initialization does not match the saved bytes.");
            mp4 = FragmentedMp4.Open(init.Data.Span);
            if (plan.RequireAudio && !mp4.HasAudio || plan.RequireVideo && !mp4.HasVideo)
                return (false, null, "The DASH initialization lacks the selected tracks.");
            initLength = init.Data.Length;
        }
        init = default;
        int next = 0;
        while (position < retained && next < plan.Fragments.Count)
        {
            var fragment = await FetchBufferedAsync(plan.Fragments[next], MaxObjectBytes, ct).ConfigureAwait(false);
            using (fragment.Lease)
            {
                if (!Match(fragment.Data)) return (false, null, "The existing bytes differ or end inside a DASH fragment.");
                mp4.ValidateFragment(fragment.Data.Span);
            }
            next++;
        }
        if (position != retained || next == 0)
            return (false, null, "The existing bytes do not end on a complete DASH fragment.");
        var cursor = new Cursor(1, plan.Fingerprint, next, retained, initLength,
            Convert.ToHexString(hash.GetCurrentHash()), mp4.Snapshot());
        return (true, JsonSerializer.Serialize(cursor), "");
    }

    private async Task LimitedWriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        await session.WriteAsync(data, offset, report: false, ct).ConfigureAwait(false);
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Job.PartialPath);
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space. Saved data was kept."));
    }

    private async Task<ReadOnlyMemory<byte>> FetchAsync(DashFragment part, long limit, SegmentBufferLease lease, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(part.Url, out var uri, out _)) throw new RunnerStop(Stop("Invalid fragment URL."));
        long bodyLimit = part.Length ?? limit;
        long? end = part.Offset is long start && part.Length is long size ? checked(start + size - 1) : null;
        session.AddConnection();
        try
        {
            RequestCredentials? credentials = useStoredCredentials ? engine.CredentialsFor(Job.Id) : refreshCredentials;
            using var response = await http.SendAsync(uri, new RequestSpec(part.Offset, end, Referer: Job.Referer,
                UserAgent: Job.UserAgent, Secrets: credentials?.Headers,
                CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl), ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is not (200 or 206)) throw new RunnerStop(status switch
            {
                401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", "The DASH address expired or is unavailable. Saved fragments were kept; do not replace them with another representation."),
                408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The fragment server answered HTTP {status}.",
                    HttpFacts.RetryAfter(response, DateTimeOffset.UtcNow)),
                _ => RunOutcome.NeedsAttention("http-status", $"The fragment server answered HTTP {status}.")
            });
            if (!HttpFacts.IsIdentityEncoded(response) || HttpFacts.IsHtml(response))
                throw new RunnerStop(Stop("The fragment server sent compressed data or an HTML page."));
            if (part.Offset is long requested)
            {
                var check = HttpFacts.CheckRangeResponse(response, requested, end);
                if (!check.IsValid || check.EndInclusive != end)
                    throw new RunnerStop(Stop("The server returned a different byte range than requested."));
            }
            else if (status != 200) throw new RunnerStop(Stop("The server returned an unsolicited partial fragment."));
            long? declared = response.Content.Headers.ContentLength;
            if (declared > bodyLimit) throw new RunnerStop(Stop("The fragment exceeds the supported object-size limit."));
            if (declared is long known) lease.Resize(SegmentBufferLimiter.ContentLengthReservation(limit, known));
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(engine.Options.StallTimeout);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                using var body = new MemoryStream(declared is > 0 ? (int)declared.Value : 64 * 1024);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        stall.CancelAfter(Timeout.InfiniteTimeSpan);
                        await session.Limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                        await engine.GlobalLimiter.AcquireAsync(n, ct).ConfigureAwait(false);
                        if (body.Length + n > bodyLimit) throw new RunnerStop(Stop("The fragment exceeds the supported object-size limit."));
                        body.Write(buffer, 0, n);
                        stall.CancelAfter(engine.Options.StallTimeout);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                if ((declared is long expected && body.Length != expected) || (part.Length is long rangeLength && body.Length != rangeLength))
                    throw new TransientNetworkException("The DASH object ended before its declared size.");
                if (body.Length == 0) throw new RunnerStop(Stop("The fragment was empty."));
                ReadOnlyMemory<byte> data = body.GetBuffer().AsMemory(0, checked((int)body.Length));
                lease.Resize(SegmentBufferLimiter.CapacityReservation(body.Capacity));
                return data;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TransientNetworkException("The DASH object stalled."); }
        }
        finally { session.RemoveConnection(); }
    }

    private async Task<(ReadOnlyMemory<byte> Data, SegmentBufferLease Lease)> FetchBufferedAsync(
        DashFragment part, long limit, CancellationToken ct)
    {
        SegmentBufferLease lease = await engine.AcquireSegmentBufferAsync(
            SegmentBufferLimiter.Reservation(limit, part.Length), ct).ConfigureAwait(false);
        try
        {
            ReadOnlyMemory<byte> data = await FetchAsync(part, limit, lease, ct).ConfigureAwait(false);
            return (data, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static RunOutcome Stop(string reason) => RunOutcome.NeedsAttention("dash-integrity", reason + " Saved data was kept.");
}
