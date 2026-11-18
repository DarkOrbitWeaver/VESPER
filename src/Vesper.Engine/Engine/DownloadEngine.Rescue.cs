using System.Net.Http;

namespace Vesper.Engine;

/// <summary>What one request to the replacement link revealed. Built from a single ranged GET; the body is never read.</summary>
public sealed record ProbeFacts(int Status, bool RangeOk, long? TotalBytes, string? StrongEtag, DateTimeOffset? LastModified, bool IdentityEncoding);

public sealed record RescueDecision(RescueMode Mode, bool Accepted, string Message);

public sealed record RescueInput(string? Sha256 = null, string? PieceMapJson = null);

public sealed record RescueResult(bool Accepted, RescueMode Mode, string Message, long RetainedBytes, int PiecesToRefetch = 0);

/// <summary>
/// Decides whether saved bytes may be reused with a replacement link. Pure and deterministic.
///   R1b  trusted piece hashes → verify stored pieces locally; refetch only missing/invalid ones
///   R0   original strong ETag → same resource AND matching strong ETag
///        original Last-Modified identity → same resource, size and modification time
///   R1   trusted full SHA-256 → reuse candidates only with mandatory final verification
///   R4   anything else → reject; saved data stays untouched
/// Random spot checks never establish source identity.
/// </summary>
public static class RescueEvaluator
{
    public static RescueDecision Evaluate(DownloadJob job, Uri newUrl, ProbeFacts probe, string? trustedSha256, PieceMap? pieces)
    {
        if (!probe.RangeOk)
        {
            if (probe.Status == 401)
                return Reject("The refreshed link requires sign-in. Open the source in your authorized browser and refresh it; saved data was kept.");
            if (probe.Status == 403)
                return Reject("The server refused the refreshed link (HTTP 403). It may be expired, private or require sign-in; refresh it in your authorized browser. Saved data was kept.");
            if (probe.Status == 407)
                return Reject("The configured proxy requires authentication (HTTP 407). Fix the proxy settings; saved data was kept.");
            return Reject(probe.Status == 200
                ? "The new link does not support resuming, so the saved data cannot be continued from it."
                : $"The new link did not give a usable answer (HTTP {probe.Status}).");
        }
        if (!probe.IdentityEncoding) return Reject("The new link compresses its data, so saved bytes cannot be combined with it safely.");
        if (probe.TotalBytes is not long total) return Reject("The new link does not say how large the file is.");
        if (job.TotalBytes is long known && known != total)
            return Reject($"The new link serves a different size ({Formatting.Bytes(total)}; this download is {Formatting.Bytes(known)}).");

        if (pieces is not null)
        {
            return pieces.TotalLength == total
                ? Accept(RescueMode.R1b, "Trusted piece checksums are available. VESPER will verify the saved pieces and download only what is missing or damaged.")
                : Reject("The piece-checksum list describes a different file size than the new link serves.");
        }

        Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? old);
        // An ETag only identifies a representation within the scope of its resource.
        // A different host or path may reuse the same ETag for unrelated bytes.
        bool sameResource = old is not null && UrlPolicy.IsSameOrigin(old, newUrl)
            && UrlPolicy.IsSameResource(old, newUrl);
        string differs = "";
        if (job.Identity == IdentityLevel.StrongEtag)
        {
            if (sameResource && probe.StrongEtag is not null && probe.StrongEtag == job.ETag)
                return Accept(RescueMode.R0, "The replacement link identifies the same resource with a matching strong ETag. Saved data will be reused.");
            if (probe.StrongEtag is not null && probe.StrongEtag != job.ETag)
                differs = " The new link reports a different ETag.";
        }

        bool sameTime = job.LastModified is not null && probe.LastModified is not null
            && job.LastModified.Value.ToUnixTimeSeconds() == probe.LastModified.Value.ToUnixTimeSeconds();
        if (job.Identity == IdentityLevel.LastModified && sameResource && sameTime)
            return Accept(RescueMode.R0, "Same address, size and modification time. Saved data will be reused.");

        if (trustedSha256 is not null)
            return Accept(RescueMode.R1, "The source cannot be proven identical, but a trusted SHA-256 exists. VESPER will continue and verify the finished file; a mismatch will be flagged and nothing will be published.");

        return new RescueDecision(RescueMode.R4, false,
            "VESPER cannot prove the new link serves the same file." + differs + " Your saved data was kept untouched. You can add a trusted SHA-256, or download the file again from this link as a separate copy.");
    }

    private static RescueDecision Reject(string message) => new(RescueMode.R4, false, message + " Your saved data was kept untouched.");
    private static RescueDecision Accept(RescueMode mode, string message) => new(mode, true, message);
}

public sealed partial class DownloadEngine
{
    /// <summary>
    /// Replace Link / Rescue. One ranged request to the new link is evaluated BEFORE anything changes.
    /// If it is not accepted, the job (including its partial data) is left exactly as it was.
    /// </summary>
    public Task<RescueResult> ReplaceLinkAsync(Guid id, string newUrl, RescueInput? input = null, CancellationToken ct = default) =>
        ReplaceLinkCoreAsync(id, newUrl, input, ct, null, null);

    private async Task<RescueResult> ReplaceLinkCoreAsync(Guid id, string newUrl, RescueInput? input, CancellationToken ct,
        RequestCredentials? replacementCredentials, DateTimeOffset? mediaExpiresAt = null)
    {
        DownloadJob? job;
        DownloadJob snapshot;
        Task? stopping = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job)) return Rejected("That entry no longer exists.");
            if (_cancelling.Contains(id)) return Rejected("The download is being cancelled.");
            if (job.Reason == "restart-cleanup")
                return Rejected("Restart from scratch must finish before this link can be replaced.");
            if (IsCleanupPending(job) || job.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its link can be replaced.");
            if (job.State == JobState.Completed) return Rejected("This download is already complete.");
            if (job.Kind != JobKind.Http)
                return Rejected("A streaming manifest is not a replacement file URL. Resolve the source into a new plan; the existing fragments remain untouched.");
            if (_sessions.TryGetValue(id, out JobSession? running))
            {
                if (running.Intent is not (StopIntent.None or StopIntent.Window or StopIntent.Pause))
                    return Rejected("The download is already stopping. Try again in a moment.");
                running.Intent = StopIntent.Pause;
                running.Cts.Cancel();
                stopping = running.RunTask;
            }
        }
        if (stopping is not null) await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(15), ct)).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? current) || !ReferenceEquals(current, job))
                return Rejected("The download changed while it was stopping. Try again.");
            if (_cancelling.Contains(id)) return Rejected("The download is being cancelled.");
            if (IsCleanupPending(current) || current.PendingFinalPath is not null)
                return Rejected("The download must finish its pending cleanup before its link can be replaced.");
            if (_sessions.ContainsKey(id)) return Rejected("The download is still stopping. Try again in a moment.");
            if (current.State == JobState.Completed) return Rejected("This download is already complete.");
            snapshot = current.CopyForUiMutation();
        }
        ct.ThrowIfCancellationRequested();

        if (!UrlPolicy.TryParse(newUrl, out Uri uri, out string urlError)) return Rejected(urlError);
        if (MediaPageLinks.TryGetSite(uri, out string mediaSite))
            return Rejected($"This is a {mediaSite} video page, not a replacement file link. No saved data was changed.");
        try { UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return Rejected(ex.Message); }

        string? sha = snapshot.ExpectedSha256;
        if (!string.IsNullOrWhiteSpace(input?.Sha256))
        {
            if (!TryNormalizeSha256(input.Sha256, out string normalized)) return Rejected("A SHA-256 checksum has 64 hexadecimal characters.");
            sha = normalized;
        }
        PieceMap? pieces = null;
        string? pieceJson = input?.PieceMapJson ?? snapshot.PieceMapJson;
        if (pieceJson is not null && !PieceMap.TryParse(pieceJson, out pieces, out string pieceError)) return Rejected(pieceError);

        long retained = snapshot.Extents.TotalBytes;
        if (retained == 0)
        {
            try
            {
                if (!ApplyRescue(job, snapshot, uri, new RescueDecision(RescueMode.None, true, ""), null, null, null, null,
                        replacementCredentials, mediaExpiresAt))
                    return Rejected("This download changed while checking its replacement link. Try again.");
            }
            catch (LocalStorageException ex)
            {
                return Rejected("The replacement link could not be saved; the previous link and saved data were kept. " + ex.Message);
            }
            return new RescueResult(true, RescueMode.None, "Link replaced. Nothing had been saved yet, so nothing needed validating.", 0);
        }

        long offset = snapshot.Extents.Gaps(snapshot.TotalBytes ?? long.MaxValue).Select(g => g.Start).DefaultIfEmpty(0).First();
        ProbeFacts probe;
        try { probe = await ProbeAsync(snapshot, uri, offset, ct, replacementCredentials).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException && (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException))
        {
            return Rejected("The new link could not be reached: " + ErrorClassifier.Describe(ex));
        }

        RescueDecision decision = RescueEvaluator.Evaluate(snapshot, uri, probe, sha, pieces);
        if (!decision.Accepted)
        {
            Log(id, "rescue-rejected", $"{decision.Mode} {UrlPolicy.Redact(uri.AbsoluteUri)}");
            return new RescueResult(false, decision.Mode, decision.Message, retained);
        }

        ExtentSet? verified = null;
        int refetch = 0;
        if (decision.Mode == RescueMode.R1b)
        {
            try
            {
                // Local reads only: stored pieces are checked against the trusted hashes; invalid ones leave the verified set.
                using PartialFile file = PartialFile.OpenOwned(snapshot);
                verified = pieces!.VerifyStored(file, snapshot.Extents, ct, out int invalid);
                refetch = Enumerable.Range(0, pieces.PieceCount).Count(i => !verified.Covers(pieces.RangeOf(i).Start, pieces.RangeOf(i).End));
                if (invalid > 0) Log(id, "rescue-pieces", $"{invalid} stored piece(s) failed verification.");
            }
            catch (LocalStorageException ex) { return Rejected("The saved data could not be read for verification: " + ex.Message); }
        }

        ct.ThrowIfCancellationRequested();
        Failpoint("rescue.beforeCommit");
        try
        {
            if (!ApplyRescue(job, snapshot, uri, decision, probe, sha, pieces, verified, replacementCredentials, mediaExpiresAt))
                return new RescueResult(false, RescueMode.R4,
                    "This download changed while checking its replacement link. No replacement was applied.", retained);
        }
        catch (LocalStorageException ex)
        {
            return new RescueResult(false, RescueMode.R4,
                "The replacement link could not be saved; the previous link and saved data were kept. " + ex.Message, retained);
        }
        Log(id, "rescue-accepted", $"{decision.Mode} {UrlPolicy.Redact(uri.AbsoluteUri)}");
        long keep = verified?.TotalBytes ?? retained;
        string message = decision.Message + (decision.Mode == RescueMode.R1b ? $" {Formatting.Bytes(keep)} of verified data kept." : "");
        return new RescueResult(true, decision.Mode, message, keep, refetch);
    }

    private async Task<ProbeFacts> ProbeAsync(DownloadJob job, Uri uri, long offset, CancellationToken ct,
        RequestCredentials? replacementCredentials = null)
    {
        RequestCredentials? credentials = replacementCredentials ?? CredentialsFor(job.Id);
        var spec = new RequestSpec(offset, null, Referer: job.Referer, UserAgent: job.UserAgent,
            Secrets: credentials?.Headers, CredentialOrigin: credentials?.Origin.AbsoluteUri ?? job.SourceUrl);
        using HttpResponseMessage response = await _http.SendAsync(uri, spec, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        RangeCheck check = HttpFacts.CheckRangeResponse(response, offset, null);
        long? total = check.IsValid ? check.Total : response.Content.Headers.ContentLength;
        return new ProbeFacts(status, check.IsValid, total, HttpFacts.StrongEtag(response), HttpFacts.LastModified(response), HttpFacts.IsIdentityEncoded(response));
    }

    private bool ApplyRescue(DownloadJob job, DownloadJob snapshot, Uri uri, RescueDecision decision, ProbeFacts? probe, string? sha, PieceMap? pieces,
        ExtentSet? verified, RequestCredentials? replacementCredentials = null, DateTimeOffset? mediaExpiresAt = null)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(job.Id, out DownloadJob? current) || !ReferenceEquals(current, job)
                || _sessions.ContainsKey(job.Id) || _cancelling.Contains(job.Id) || job.State == JobState.Completed
                || IsCleanupPending(job) || job.PendingFinalPath is not null || mediaExpiresAt <= DateTimeOffset.UtcNow
                || job.State != snapshot.State || job.SourceUrl != snapshot.SourceUrl
                || !ReferenceEquals(job.Extents, snapshot.Extents) || job.ExpectedSha256 != snapshot.ExpectedSha256
                || job.PieceMapJson != snapshot.PieceMapJson)
                return false;

            // The source and duplicate index become visible only AFTER SQLite accepts the new job.
            // If a database write fails, the original state, saved bytes and credentials stay intact.
            DownloadJob updated = job.CopyForUpdate();
            string[] previous = job.PreviousUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            updated.PreviousUrls = string.Join('\n', previous.Append(job.SourceUrl).TakeLast(10));
            updated.SourceUrl = uri.AbsoluteUri;
            updated.RescueMode = decision.Mode;
            if (mediaExpiresAt is not null) updated.MediaExpiresAt = mediaExpiresAt;

            if (decision.Mode is RescueMode.R1 or RescueMode.R1b && probe is not null)
            {
                updated.ETag = probe.StrongEtag;
                updated.LastModified = probe.LastModified;
                updated.Identity = probe.StrongEtag is not null ? IdentityLevel.StrongEtag
                    : probe.LastModified is not null ? IdentityLevel.LastModified : IdentityLevel.None;
                updated.UnverifiedResumeApproved = true;
                updated.AcceptsRanges = true;
                updated.ExpectedSha256 = sha ?? job.ExpectedSha256;
                updated.RescueCandidate = decision.Mode == RescueMode.R1;
                updated.Sha256Verified = false;
            }
            if (decision.Mode == RescueMode.R1b && pieces is not null && verified is not null)
            {
                updated.PieceMapJson = pieces.ToJson();
                updated.TotalBytes = pieces.TotalLength;
                updated.Extents = verified;
            }
            if (decision.Mode == RescueMode.None)
                (updated.ETag, updated.LastModified, updated.Identity, updated.AcceptsRanges) =
                    (null, null, IdentityLevel.None, false);

            updated.State = JobState.Queued;
            updated.Reason = "";
            updated.Message = "";
            updated.RetryCount = 0;
            updated.NextRetryUtc = null;
            SaveLocked(updated); // The only possible durable mutation happens before any in-memory one.

            RemoveDuplicateCandidateLocked(job, job.SourceUrl);
            _jobs[job.Id] = updated;
            AddDuplicateCandidateLocked(updated);

            if (replacementCredentials is not null) _credentials[job.Id] = replacementCredentials;
            else if (_credentials.TryGetValue(job.Id, out RequestCredentials? prior)
                && !UrlPolicy.IsSameOrigin(prior.Origin, uri)) _credentials.Remove(job.Id);
            Changed(updated);
        }
        ScheduleNext();
        return true;
    }

    private static RescueResult Rejected(string message) => new(false, RescueMode.R4, message, 0);
}
