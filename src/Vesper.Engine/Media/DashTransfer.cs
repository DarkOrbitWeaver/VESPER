using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>Serial, bounded clear fMP4 transfer through the existing durable session, limiters and publish path.</summary>
internal sealed class DashTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FragmentedMp4 mp4;
        int next = 0, initLength;
        try
        {
            if (cursor is not null)
            {
                if (cursor.Version != 1 || cursor.PlanFingerprint != fingerprint || cursor.Next < 1 || cursor.Next > plan.Fragments.Count
                    || cursor.Offset != offset || cursor.InitLength is < 8 or > MaxInitBytes || cursor.InitLength >= offset
                    || cursor.Progress is null) return Stop("The saved plan or representation changed. Restart is required.");
                // One sequential pass on resume, not random spot checks. No corrupt prefix is trusted.
                MediaPartialIntegrity.HashPrefix(file, offset, hash, ct);
                if (!Convert.ToHexString(hash.GetCurrentHash()).Equals(cursor.PrefixHash, StringComparison.Ordinal))
                    return Stop("The committed partial file failed SHA-256 validation. It was not changed.");
                byte[] init = MediaPartialIntegrity.ReadInitialization(file, cursor.InitLength, ct);
                mp4 = FragmentedMp4.Open(init);
                mp4.Restore(cursor.Progress);
                next = cursor.Next; initLength = cursor.InitLength;
            }
            else
            {
                ReadOnlyMemory<byte> init = await FetchAsync(plan.Initialization, MaxInitBytes, ct).ConfigureAwait(false);
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
            for (int i = next; i < plan.Fragments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> fragment = await FetchAsync(plan.Fragments[i], MaxObjectBytes, ct).ConfigureAwait(false);
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
                engine.UpdateJob(Job, j => j.Message = $"Fragment {i + 1} of {plan.Fragments.Count}");
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

    private async Task LimitedWriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        await session.Limiter.AcquireAsync(data.Length, ct).ConfigureAwait(false);
        await engine.GlobalLimiter.AcquireAsync(data.Length, ct).ConfigureAwait(false);
        await session.WriteAsync(data, offset, report: false, ct).ConfigureAwait(false);
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Job.PartialPath);
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "There is not enough free disk space. Saved data was kept."));
    }

    private async Task<ReadOnlyMemory<byte>> FetchAsync(DashFragment part, long limit, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(part.Url, out var uri, out _)) throw new RunnerStop(Stop("Invalid fragment URL."));
        long? end = part.Offset is long start && part.Length is long size ? checked(start + size - 1) : null;
        session.AddConnection();
        try
        {
            using var response = await http.SendAsync(uri, new RequestSpec(part.Offset, end, Referer: Job.Referer,
                UserAgent: Job.UserAgent), ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is not (200 or 206)) throw new RunnerStop(status switch
            {
                401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", "The DASH address expired or is unavailable. Saved fragments were kept; do not replace them with another representation."),
                408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The fragment server answered HTTP {status}."),
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
            if (declared > limit) throw new RunnerStop(Stop("The fragment exceeds the supported object-size limit."));
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
                        if (body.Length + n > limit) throw new RunnerStop(Stop("The fragment exceeds the supported object-size limit."));
                        body.Write(buffer, 0, n);
                        stall.CancelAfter(engine.Options.StallTimeout);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                if ((declared is long expected && body.Length != expected) || (part.Length is long rangeLength && body.Length != rangeLength))
                    throw new TransientNetworkException("The DASH object ended before its declared size.");
                if (body.Length == 0) throw new RunnerStop(Stop("The fragment was empty."));
                // MemoryStream disposal does not invalidate its managed buffer. Avoid an extra full-object copy.
                return body.GetBuffer().AsMemory(0, checked((int)body.Length));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TransientNetworkException("The DASH object stalled."); }
        }
        finally { session.RemoveConnection(); }
    }

    private static RunOutcome Stop(string reason) => RunOutcome.NeedsAttention("dash-integrity", reason + " Saved data was kept.");
}
