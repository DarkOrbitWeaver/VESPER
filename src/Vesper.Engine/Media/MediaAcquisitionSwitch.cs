using System.Buffers;

namespace Vesper.Engine;

/// <summary>The replacement is a new delivery route for the exact selected media representation.</summary>
public sealed record MediaAcquisitionReplacement(ResolvedMediaSource Source, JobKind Kind,
    string FileName, string Quality, string? PlanJson = null, long? ExactSize = null,
    string? Referer = null, string? UserAgent = null);

public sealed partial class DownloadEngine
{
    private sealed record HttpPrefixProof(long Total, string ETag, string ResourceIdentity, string? ContentType);

    public static bool IsRecoverableDeliveryFailure(JobState state, string reason) =>
        state == JobState.NeedsAttention && reason is ("range-ignored" or "range-rejected" or
            "invalid-response" or "invalid-range" or "unexpected-range" or "not-playlist" or
            "media-validation" or "invalid-container");

    private bool MatchesDeliverySwitchLocked(DownloadJob job, string expectedSource) =>
        MatchesAutomaticRefreshLocked(job, expectedSource)
        || IsRecoverableDeliveryFailure(job.State, job.Reason)
            && job.SourceUrl == expectedSource && !_sessions.ContainsKey(job.Id)
            && job.BrowserProof is null;

    /// <summary>Switch compatible delivery routes, retaining only exactly verified committed data.</summary>
    public async Task<RescueResult> SwitchMediaAcquisitionAsync(Guid id, MediaAcquisitionReplacement replacement,
        string expectedExpiredSource, CancellationToken ct = default)
    {
        using var operation = EnterOperation(ct);
        ct = operation.Token;
        ArgumentNullException.ThrowIfNull(replacement);
        var source = replacement.Source;
        if (string.IsNullOrWhiteSpace(expectedExpiredSource) || !UrlPolicy.TryParse(source.Url, out Uri url, out _))
            return Rejected("The replacement source is invalid.");
        try { UrlPolicy.EnsureAllowed(url, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return Rejected(ex.Message); }
        if (!TryCreateReplacementCredentials(source, out RequestCredentials? credentials, out string credentialError))
            return Rejected(credentialError);

        DownloadJob original, snapshot;
        ExtentSet savedExtents;
        DashDownloadPlan? dash = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job)) return Rejected("That entry no longer exists.");
            if (!MatchesDeliverySwitchLocked(job, expectedExpiredSource) || _cancelling.Contains(id)
                || IsCleanupPending(job) || job.PendingFinalPath is not null || job.BrowserProof is not null
                || job.ExpectedSha256 is not null || job.PieceMapJson is not null || job.MediaMetadataJson is not null
                || job.Output != MediaOutput.Original || job.Kind == replacement.Kind
                    && job.SourceUrl == url.AbsoluteUri
                || job.Kind is not (JobKind.Http or JobKind.Dash or JobKind.Hls)
                || replacement.Kind is not (JobKind.Http or JobKind.Dash or JobKind.Hls))
                return Rejected("The download cannot safely switch acquisition routes. Saved data was kept.");
            if (string.IsNullOrWhiteSpace(job.MediaProvider) || job.MediaProvider == "generic"
                || string.IsNullOrWhiteSpace(job.MediaId) || string.IsNullOrWhiteSpace(job.MediaFormatId)
                || string.IsNullOrWhiteSpace(job.MediaQuality)
                || job.MediaProvider != source.Provider || job.MediaId != source.MediaId
                || job.MediaFormatId != source.FormatId || job.MediaQuality != replacement.Quality)
                return Rejected("The replacement does not identify the same selected media and quality.");
            string extension = Path.GetExtension(job.FileName);
            if (!extension.Equals(Path.GetExtension(replacement.FileName), StringComparison.OrdinalIgnoreCase)
                || extension.ToLowerInvariant() is not (".mp4" or ".m4a" or ".ts"))
                return Rejected("The replacement would change the selected output container.");
            if (job.Kind == JobKind.Dash && (!DashDownloadPlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out var oldDash, out _)
                || oldDash.ClipRange is not null || oldDash.Provider != source.Provider || oldDash.MediaId != source.MediaId
                || oldDash.RepresentationId != source.FormatId)
                || job.Kind == JobKind.Http && (job.MediaPlanJson is not null || job.NonResumable)
                || job.Kind == JobKind.Hls && (job.MediaPlanJson is not null || HlsTransfer.IsLiveState(job.MediaState)))
                return Rejected("The existing selection includes a clip, live recording or incompatible output plan.");
            if (replacement.Kind == JobKind.Dash)
            {
                if (!DashDownloadPlan.TryParse(replacement.PlanJson, Options.AllowPrivateNetworks, out dash, out _)
                    || dash.ClipRange is not null || dash.Provider != source.Provider || dash.MediaId != source.MediaId
                    || dash.RepresentationId != source.FormatId || extension.ToLowerInvariant() is not (".mp4" or ".m4a")
                    || dash.RequireVideo != extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    return Rejected("The replacement DASH plan does not match the selected representation.");
            }
            else if (replacement.PlanJson is not null || replacement.ExactSize is <= 0
                || replacement.Kind == JobKind.Hls && extension.ToLowerInvariant() is not (".mp4" or ".ts"))
                return Rejected("The replacement file has an incompatible output plan or size.");
            if (source.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
            if (replacement.Kind != JobKind.Http && job.Extents.TotalBytes != job.Extents.ContiguousFromZero())
                return Rejected("Saved ranges are not a contiguous prefix. No data was changed.");
            original = job;
            snapshot = job.CopyForUpdate();
            savedExtents = job.Extents;
        }

        long retained = snapshot.Extents.TotalBytes;
        HttpPrefixProof? proof = null;
        string? newCursor = null;
        // Pin the HTTP partial through installation of its new validator. No writer can
        // modify verified bytes between the comparison and the durable state change.
        FileStream? verifiedFile = null;
        try
        {
            if (retained > 0 || replacement.Kind == JobKind.Hls)
            {
                // Use the proposed request context for verification; do not modify the active credentials yet.
                snapshot.Referer = replacement.Referer;
                snapshot.UserAgent = replacement.UserAgent;
                if (dash is not null)
                {
                    using var session = new JobSession(this, snapshot);
                    var transfer = new DashTransfer(this, session, _http, credentials, useStoredCredentials: false);
                    (bool valid, newCursor, string error) = await transfer.ImportPrefixAsync(dash, ct).ConfigureAwait(false);
                    if (!valid) return Rejected(error + " Saved data was kept.");
                }
                else if (replacement.Kind == JobKind.Hls)
                {
                    using var session = new JobSession(this, snapshot);
                    var transfer = new HlsTransfer(this, session, _http, credentials, useStoredCredentials: false);
                    (bool valid, newCursor, string? error) = await transfer.ImportVodPrefixAsync(url.AbsoluteUri,
                        Path.GetExtension(snapshot.FileName), ct).ConfigureAwait(false);
                    if (!valid) return Rejected(error + " Saved data was kept.");
                }
                else
                {
                    verifiedFile = PartialFile.OpenOwnedRead(snapshot);
                    proof = await VerifyFileRangesAsync(snapshot, verifiedFile, url, credentials, replacement.ExactSize, ct).ConfigureAwait(false);
                    if (proof is null)
                    {
                        verifiedFile.Dispose();
                        return Rejected("The replacement file did not prove every committed range and a strong HTTP identity. Saved data was kept.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { verifiedFile?.Dispose(); throw; }
        catch (RunnerStop stop) { verifiedFile?.Dispose(); return Rejected(stop.Outcome.Message); }
        catch (Exception ex) when (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex)
            || ex is RedirectException or IOException or UnauthorizedAccessException or MediaContainerException)
        { verifiedFile?.Dispose(); return Rejected("The replacement delivery could not be verified: " + ErrorClassifier.Describe(ex) + ". Saved data was kept."); }
        catch { verifiedFile?.Dispose(); throw; }

        using (verifiedFile)
        {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, original)
                || _cancelling.Contains(id) || IsCleanupPending(job) || job.PendingFinalPath is not null
                || !MatchesDeliverySwitchLocked(job, expectedExpiredSource) || job.Kind != snapshot.Kind
                || job.MediaState != snapshot.MediaState || job.MediaPlanJson != snapshot.MediaPlanJson
                || job.Generation != snapshot.Generation || job.PartialIdentity != snapshot.PartialIdentity
                || job.State != snapshot.State || job.Reason != snapshot.Reason
                || !ReferenceEquals(job.Extents, savedExtents) || job.FileName != snapshot.FileName
                || source.ExpiresAt <= DateTimeOffset.UtcNow)
                return Rejected("The download changed while its alternate delivery was being checked.");
            // Never create a second job or write the old file during a switch. Only the durable cursor changes.
            DownloadJob updated = job.CopyForUpdate();
            updated.PreviousUrls = AppendPreviousUrl(updated.PreviousUrls, job.SourceUrl);
            updated.Kind = replacement.Kind;
            updated.SourceUrl = url.AbsoluteUri;
            updated.MediaPlanJson = dash?.ToJson();
            updated.MediaState = newCursor;
            updated.MediaExpiresAt = source.ExpiresAt;
            updated.Referer = replacement.Referer;
            updated.UserAgent = replacement.UserAgent;
            updated.FinalResourceIdentity = proof?.ResourceIdentity;
            updated.ETag = proof?.ETag;
            updated.Identity = proof is null ? IdentityLevel.None : IdentityLevel.StrongEtag;
            updated.LastModified = null;
            updated.TotalBytes = proof?.Total;
            updated.ContentType = proof?.ContentType;
            updated.AcceptsRanges = proof is not null;
            updated.NonResumable = false;
            updated.UnverifiedResumeApproved = false;
            updated.RescueCandidate = false;
            updated.RescueMode = retained > 0 ? RescueMode.R0 : RescueMode.None;
            updated.Sha256Verified = false;
            updated.Generation++;
            (updated.State, updated.Reason, updated.Message, updated.RetryCount) =
                (JobState.Queued, "", "Alternate delivery selected; saved data preserved.", 0);
            updated.NextRetryUtc = null;
            ct.ThrowIfCancellationRequested();
            if (!SaveRefreshedJobLocked(job, updated))
                return Rejected("The replacement source is already owned by another download. Saved data was kept.");
            if (credentials is not null) _credentials[id] = credentials;
            else _credentials.Remove(id);
            // The manifest is auxiliary; the SQLite transaction is authoritative.
            if (retained > 0) WriteManifest(updated, verifiedFile?.SafeFileHandle);
            // Release before leaving the scheduler lock; another queued job may wake the scheduler.
            verifiedFile?.Dispose();
            Log(id, "acquisition-switch", $"{snapshot.Kind} -> {replacement.Kind}; retained {retained} bytes");
        }
        }
        FlushOutbox();
        ScheduleNext();
        return new RescueResult(true, retained > 0 ? RescueMode.R0 : RescueMode.None,
            "Alternate delivery selected without discarding committed data.", retained);
    }

    private async Task<HttpPrefixProof?> VerifyFileRangesAsync(DownloadJob job, FileStream local, Uri url,
        RequestCredentials? credentials, long? exactSize, CancellationToken ct)
    {
        if (local.Length < job.Extents.End) return null;
        long? expectedTotal = job.Kind == JobKind.Http ? job.TotalBytes : null;
        if (expectedTotal is long originalSize && exactSize is long plannedSize && originalSize != plannedSize)
            return null;
        HttpPrefixProof? proof = null;
        Uri? verifiedFinalUrl = null;
        byte[] saved = ArrayPool<byte>.Shared.Rent(64 * 1024), incoming = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (ByteRange extent in job.Extents.Ranges)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await _http.SendAsync(url, new RequestSpec(extent.Start, extent.End - 1,
                    IfRangeEtag: proof?.ETag, Referer: job.Referer, UserAgent: job.UserAgent, Secrets: credentials?.Headers,
                    CredentialOrigin: credentials?.Origin.AbsoluteUri, ExpectedResourceIdentity: proof?.ResourceIdentity), ct).ConfigureAwait(false);
                RangeCheck range = HttpFacts.CheckRangeResponse(response, extent.Start, extent.End - 1);
                string? etag = HttpFacts.StrongEtag(response);
                if (!range.IsValid || range.EndInclusive != extent.End - 1 || range.Total is not long total
                    || total < job.Extents.End || exactSize is long size && total != size
                    || expectedTotal is long expected && total != expected || etag is null || !HttpFacts.IsIdentityEncoded(response)
                    || HttpFacts.IsHtml(response) || response.RequestMessage?.RequestUri is not { } finalUri)
                    return null;
                string resource = HttpTransport.ResourceIdentity(finalUri);
                // An ETag is scoped to the actual HTTP representation. During this proof, even a
                // query-only redirect change is not enough to establish equivalence across ranges.
                if (proof is not null && (proof.Total != total || proof.ETag != etag || proof.ResourceIdentity != resource
                    || verifiedFinalUrl != finalUri))
                    return null;
                verifiedFinalUrl ??= finalUri;
                proof ??= new HttpPrefixProof(total, etag, resource, HttpFacts.ContentType(response));
                local.Position = extent.Start;
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(Options.StallTimeout);
                await using var remote = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                long remaining = extent.Length;
                while (remaining > 0)
                {
                    int count = (int)Math.Min(remaining, Math.Min(saved.Length, incoming.Length));
                    await local.ReadExactlyAsync(saved.AsMemory(0, count), ct).ConfigureAwait(false);
                    int done = 0;
                    while (done < count)
                    {
                        int n = await remote.ReadAsync(incoming.AsMemory(done, count - done), stall.Token).ConfigureAwait(false);
                        if (n == 0) return null;
                        done += n;
                        stall.CancelAfter(Options.StallTimeout);
                    }
                    if (!saved.AsSpan(0, count).SequenceEqual(incoming.AsSpan(0, count))) return null;
                    remaining -= count;
                }
                if (await remote.ReadAsync(incoming.AsMemory(0, 1), stall.Token).ConfigureAwait(false) != 0) return null;
            }
            return proof;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally
        {
            ArrayPool<byte>.Shared.Return(saved);
            ArrayPool<byte>.Shared.Return(incoming);
        }
    }
}
