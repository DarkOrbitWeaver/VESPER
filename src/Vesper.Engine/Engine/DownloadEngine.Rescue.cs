using System.Net.Http;

namespace Vesper.Engine;

/// <summary>What one request to the replacement link revealed. Built from a single ranged GET; the body is never read.</summary>
public sealed record ProbeFacts(int Status, bool RangeOk, long? TotalBytes, string? StrongEtag, DateTimeOffset? LastModified, bool IdentityEncoding);

public sealed record RescueDecision(RescueMode Mode, bool Accepted, string Message);

public sealed record RescueInput(string? Sha256 = null, string? PieceMapJson = null);

public sealed record RescueResult(bool Accepted, RescueMode Mode, string Message, long RetainedBytes, int PiecesToRefetch = 0);

/// <summary>
/// Decides whether saved bytes may be reused with a replacement link. Pure and deterministic.
///   R1b  trusted piece hashes  → verify stored pieces locally, fetch only missing/invalid ones
///   R0   strong identity       → strong ETag equal, or same resource + same size + same Last-Modified
///   R1   trusted full digest   → candidate reuse, final verification is mandatory
///   R4   anything else         → reject; saved data stays untouched
/// Random spot checks are never accepted as proof of identity.
/// </summary>
public static class RescueEvaluator
{
    public static RescueDecision Evaluate(DownloadJob job, Uri newUrl, ProbeFacts probe, string? trustedSha256, PieceMap? pieces)
    {
        if (!probe.RangeOk)
        {
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
    public async Task<RescueResult> ReplaceLinkAsync(Guid id, string newUrl, RescueInput? input = null, CancellationToken ct = default)
    {
        DownloadJob? job;
        Task? stopping = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job)) return Rejected("That entry no longer exists.");
            if (job.State == JobState.Completed) return Rejected("This download is already complete.");
            if (job.Kind != JobKind.Http)
                return Rejected("A streaming manifest is not a replacement file URL. Resolve the source into a new plan; the existing fragments remain untouched.");
            if (_sessions.TryGetValue(id, out JobSession? running)) { running.Intent = StopIntent.Pause; running.Cts.Cancel(); stopping = running.RunTask; }
        }
        if (stopping is not null) await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(15), ct)).ConfigureAwait(false);
        lock (_lock)
            if (_sessions.ContainsKey(id)) return Rejected("The download is still stopping. Try again in a moment.");

        if (!UrlPolicy.TryParse(newUrl, out Uri uri, out string urlError)) return Rejected(urlError);
        if (MediaPageLinks.TryGetSite(uri, out string mediaSite))
            return Rejected($"This is a {mediaSite} video page, not a replacement file link. No saved data was changed.");
        try { UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks); }
        catch (BlockedAddressException ex) { return Rejected(ex.Message); }

        string? sha = job.ExpectedSha256;
        if (!string.IsNullOrWhiteSpace(input?.Sha256))
        {
            if (!TryNormalizeSha256(input.Sha256, out string normalized)) return Rejected("A SHA-256 checksum has 64 hexadecimal characters.");
            sha = normalized;
        }
        PieceMap? pieces = null;
        string? pieceJson = input?.PieceMapJson ?? job.PieceMapJson;
        if (pieceJson is not null && !PieceMap.TryParse(pieceJson, out pieces, out string pieceError)) return Rejected(pieceError);

        long retained = job.Extents.TotalBytes;
        if (retained == 0)
        {
            ApplyRescue(job, uri, new RescueDecision(RescueMode.None, true, ""), null, null, null, null);
            return new RescueResult(true, RescueMode.None, "Link replaced. Nothing had been saved yet, so nothing needed validating.", 0);
        }

        long offset = job.Extents.Gaps(job.TotalBytes ?? long.MaxValue).Select(g => g.Start).DefaultIfEmpty(0).First();
        ProbeFacts probe;
        try { probe = await ProbeAsync(job, uri, offset, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException && (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException))
        {
            return Rejected("The new link could not be reached: " + ErrorClassifier.Describe(ex));
        }

        RescueDecision decision = RescueEvaluator.Evaluate(job, uri, probe, sha, pieces);
        Log(id, decision.Accepted ? "rescue-accepted" : "rescue-rejected", $"{decision.Mode} {UrlPolicy.Redact(uri.AbsoluteUri)}");
        if (!decision.Accepted) return new RescueResult(false, decision.Mode, decision.Message, retained);

        ExtentSet? verified = null;
        int refetch = 0;
        if (decision.Mode == RescueMode.R1b)
        {
            try
            {
                // Local reads only: stored pieces are checked against the trusted hashes; invalid ones leave the verified set.
                using PartialFile file = PartialFile.Open(job.PartialPath);
                verified = pieces!.VerifyStored(file, job.Extents, ct, out int invalid);
                refetch = Enumerable.Range(0, pieces.PieceCount).Count(i => !verified.Covers(pieces.RangeOf(i).Start, pieces.RangeOf(i).End));
                if (invalid > 0) Log(id, "rescue-pieces", $"{invalid} stored piece(s) failed verification.");
            }
            catch (LocalStorageException ex) { return Rejected("The saved data could not be read for verification: " + ex.Message); }
        }

        ApplyRescue(job, uri, decision, probe, sha, pieces, verified);
        long keep = verified?.TotalBytes ?? retained;
        string message = decision.Message + (decision.Mode == RescueMode.R1b ? $" {Formatting.Bytes(keep)} of verified data kept." : "");
        return new RescueResult(true, decision.Mode, message, keep, refetch);
    }

    private async Task<ProbeFacts> ProbeAsync(DownloadJob job, Uri uri, long offset, CancellationToken ct)
    {
        RequestCredentials? credentials = CredentialsFor(job.Id);
        var spec = new RequestSpec(offset, null, Referer: job.Referer, UserAgent: job.UserAgent,
            Secrets: credentials?.Headers, CredentialOrigin: credentials?.Origin.AbsoluteUri ?? job.SourceUrl);
        using HttpResponseMessage response = await _http.SendAsync(uri, spec, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        RangeCheck check = HttpFacts.CheckRangeResponse(response, offset, null);
        long? total = check.IsValid ? check.Total : response.Content.Headers.ContentLength;
        return new ProbeFacts(status, check.IsValid, total, HttpFacts.StrongEtag(response), HttpFacts.LastModified(response), HttpFacts.IsIdentityEncoded(response));
    }

    private void ApplyRescue(DownloadJob job, Uri uri, RescueDecision decision, ProbeFacts? probe, string? sha, PieceMap? pieces, ExtentSet? verified)
    {
        lock (_lock)
        {
            string[] previous = job.PreviousUrls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            job.PreviousUrls = string.Join('\n', previous.Append(job.SourceUrl).TakeLast(10));
            ChangeSourceUrlLocked(job, uri.AbsoluteUri);
            job.RescueMode = decision.Mode;

            if (decision.Mode is RescueMode.R1 or RescueMode.R1b && probe is not null)
            {
                // Identity cannot be proven by headers here; the trusted hash / piece list carries the proof instead.
                job.ETag = probe.StrongEtag;
                job.LastModified = probe.LastModified;
                job.Identity = probe.StrongEtag is not null ? IdentityLevel.StrongEtag : probe.LastModified is not null ? IdentityLevel.LastModified : IdentityLevel.None;
                job.UnverifiedResumeApproved = true;
                job.AcceptsRanges = true;
                job.ExpectedSha256 = sha ?? job.ExpectedSha256;
                job.RescueCandidate = decision.Mode == RescueMode.R1;
                job.Sha256Verified = false;
            }
            if (decision.Mode == RescueMode.R1b && pieces is not null && verified is not null)
            {
                job.PieceMapJson = pieces.ToJson();
                job.TotalBytes = pieces.TotalLength;
                job.Extents = verified;
            }
            if (decision.Mode == RescueMode.None)
            {
                (job.ETag, job.LastModified, job.Identity, job.AcceptsRanges) = (null, null, IdentityLevel.None, false);
            }

            job.State = JobState.Queued;
            AddDuplicateCandidateLocked(job);
            job.Reason = "";
            job.Message = "";
            job.RetryCount = 0;
            job.NextRetryUtc = null;
            SaveLocked(job);
            Changed(job);
        }
        ScheduleNext();
    }

    private static RescueResult Rejected(string message) => new(false, RescueMode.R4, message, 0);
}
