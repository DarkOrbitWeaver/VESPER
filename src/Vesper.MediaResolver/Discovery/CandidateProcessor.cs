using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols;
using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

internal sealed class CandidateOutcome
{
    public List<MediaRepresentation> Representations { get; } = new();
    public List<AudioTrack> AudioTracks { get; } = new();
    public List<SubtitleTrack> Subtitles { get; } = new();
    public TimeSpan? Duration { get; set; }
    public List<MediaError> Problems { get; } = new();
}

/// <summary>
/// Turns unverified candidate URLs (from page markup or from the browser) into verified representations.
/// A direct URL only becomes a representation after a real HEAD/ranged-GET probe returns a success status and a
/// media content type. Manifests go through <see cref="ManifestResolver"/>. Work is capped per page.
/// </summary>
internal sealed class CandidateProcessor
{
    private readonly SecureHttpClient _http;
    private readonly ManifestResolver _manifests;
    private readonly ResolverOptions _o;

    // Enough for a web-optimised MP4's track table. At most this many files are opened per page.
    private const int HeaderBytes = 256 * 1024, MaxHeaderReads = 3;

    public CandidateProcessor(SecureHttpClient http, ManifestResolver manifests, ResolverOptions options)
    {
        _http = http;
        _manifests = manifests;
        _o = options;
    }

    public async Task<CandidateOutcome> ProcessAsync(IEnumerable<MediaCandidate> candidates, MediaRequestContext ctx, CancellationToken ct,
        bool exactAssetGroup = false)
    {
        var ordered = candidates.DistinctBy(c => c.Url.AbsoluteUri, StringComparer.Ordinal)
            .Take(_o.MaxCandidatesPerPage).ToArray();
        var direct = new DirectProbe?[ordered.Length];
        using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerDeadline.CancelAfter(TimeSpan.FromSeconds(6));
        // Reserve header slots in input order; network completion order must not change the result.
        var headerSlots = ordered.Select((c, i) => (c, i))
            .Where(x => x.c.Kind is not (CandidateKind.Hls or CandidateKind.Dash)
                && (x.c.ObservedLength is not null || UrlUtil.Extension(x.c.Url) is null or "mp4" or "m4v" or "m4a" or "mov" or "webm"))
            .Take(MaxHeaderReads).Select(x => x.i).ToHashSet();
        await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _o.MaxConcurrentRequests), CancellationToken = ct },
            async (i, token) =>
            {
                if (ordered[i].Kind is CandidateKind.Hls or CandidateKind.Dash) return;
                direct[i] = await ProbeDirectAsync(ordered[i], ctx, exactAssetGroup,
                    headerSlots.Contains(i), headerDeadline.Token, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var outcome = new CandidateOutcome();
        var manifestSlots = Enumerable.Range(0, ordered.Length)
            .Where(i => ordered[i].Kind is CandidateKind.Hls or CandidateKind.Dash || direct[i]?.Manifest == true)
            .Take(_o.MaxManifestsPerPage).ToArray();
        var manifests = new CandidateOutcome?[ordered.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, manifestSlots.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(2, _o.MaxConcurrentRequests)), CancellationToken = ct },
            async (slot, token) =>
            {
                int i = manifestSlots[slot];
                var result = new CandidateOutcome();
                try { await AddManifestAsync(ordered[i].Url, ctx, result, slot, token).ConfigureAwait(false); }
                catch (MediaResolverException ex) { result.Problems.Add(ex.Error); }
                manifests[i] = result;
            }).ConfigureAwait(false);

        int directIndex = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (manifests[i] is { } manifest)
            {
                outcome.Representations.AddRange(manifest.Representations);
                outcome.AudioTracks.AddRange(manifest.AudioTracks);
                outcome.Subtitles.AddRange(manifest.Subtitles);
                outcome.Problems.AddRange(manifest.Problems);
                outcome.Duration ??= manifest.Duration;
            }
            else if (direct[i] is { } result)
            {
                if (result.Problem is not null) outcome.Problems.Add(result.Problem);
                else if (!result.Manifest && result.Probe is { } probe)
                {
                    outcome.Representations.Add(BuildDirect(ordered[i], probe, ctx, directIndex++, result.Facts));
                    if (result.Facts?.DurationSeconds is { } seconds) outcome.Duration ??= TimeSpan.FromSeconds(seconds);
                }
            }
        }
        return outcome;
    }

    private sealed record DirectProbe(ProbeResult? Probe, DirectMediaFacts? Facts, MediaError? Problem, bool Manifest = false);

    private async Task<DirectProbe> ProbeDirectAsync(MediaCandidate c, MediaRequestContext ctx,
        bool exactAssetGroup, bool headerSlot, CancellationToken headerToken, CancellationToken ct)
    {
        try
        {
            bool rangedWitness = c.ObservedLength is not null;
            byte[]? start = null;
            ProbeResult probe;
            if (rangedWitness)
                probe = await _http.ProbeGetAsync(c.Url, ctx, ct).ConfigureAwait(false);
            else if (headerSlot && !headerToken.IsCancellationRequested)
            {
                try { (probe, start) = await _http.ProbeWithStartAsync(c.Url, ctx, HeaderBytes, headerToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { probe = await _http.ProbeAsync(c.Url, ctx, ct).ConfigureAwait(false); }
            }
            else probe = await _http.ProbeAsync(c.Url, ctx, ct).ConfigureAwait(false);
            if (rangedWitness && (probe.StatusCode != 206 || probe.InvalidExtent || probe.FinalUrl != c.Url
                || !string.Equals(probe.ContentType, "video/mp4", StringComparison.OrdinalIgnoreCase)
                || probe.ContentLength != c.ObservedLength || probe.StrongETag != c.ObservedETag))
                return new(null, null, MediaErrors.NoMedia("The observed range no longer identifies the same complete file.", false));
            if (probe.InvalidExtent)
            {
                if (rangedWitness) return new(null, null, MediaErrors.NoMedia("The server returned an invalid byte range.", false));
                // Malformed range is not evidence of track structure. A separate
                // whole-resource HEAD can still establish an unverified direct file.
                probe = await _http.ProbeAsync(c.Url, ctx, ct).ConfigureAwait(false);
                start = null;
                if (probe.InvalidExtent) return new(null, null, MediaErrors.NoMedia("The server returned an invalid byte range.", false));
            }
            if (!probe.IsSuccess)
                return new(null, null, MediaErrors.FromHttpStatus(probe.StatusCode, c.Url));
            if (exactAssetGroup && BrowserAssetIdentity.XVideo(c.Url) != BrowserAssetIdentity.XVideo(probe.FinalUrl))
                return new(null, null, MediaErrors.Invalid("A source redirected to a different media asset."));

            string ctype = probe.ContentType?.ToLowerInvariant() ?? "";
            if (ctype.Contains("mpegurl") || ctype.Contains("dash+xml"))
                return new(null, null, null, true);
            if (!IsMedia(probe, c))
                return new(null, null, MediaErrors.NoMedia($"{UrlRedactor.Redact(c.Url)} is not media (content-type '{ctype}')", false));
            DirectMediaFacts? facts = null;
            if (IsMp4(probe, c))
            {
                if (start is not null) facts = Mp4HeaderFacts.Read(start);
                else if (rangedWitness && headerSlot && !headerToken.IsCancellationRequested)
                    facts = await ReadHeaderAsync(c.Url, ctx, probe, headerToken, ct).ConfigureAwait(false);
            }
            else if (start is not null && (CodecInfo.ContainerFromMime(ctype) ?? ExtensionOf(probe, c)) == "webm")
                facts = WebmHeaderFacts.Read(start, probe.ContentLength);
            if (facts is { Encrypted: true })
                return new(null, null, MediaErrors.Drm($"{UrlRedactor.Redact(c.Url)} declares encrypted tracks"));
            if (rangedWitness && (facts is not { HasVideo: true, HasAudio: true, DurationSeconds: > 0 }
                || facts.VideoCodec is not ("h264" or "h265" or "av1") || facts.AudioCodec is not ("aac" or "mp3")))
                return new(null, null, MediaErrors.NoMedia("The ranged source is not a verified complete audio/video file.", false));
            return new(probe, facts, null);
        }
        catch (MediaResolverException ex) { return new(null, null, ex.Error); }
    }

    private async Task AddManifestAsync(Uri url, MediaRequestContext ctx, CandidateOutcome outcome, int index, CancellationToken ct)
    {
        var m = await _manifests.ResolveAsync(url, ctx, ct).ConfigureAwait(false);
        foreach (var r in m.Representations)
            outcome.Representations.Add(index == 0 ? r : r with { Id = $"m{index}-{r.Id}" });
        outcome.AudioTracks.AddRange(m.AudioTracks);
        outcome.Subtitles.AddRange(m.Subtitles);
        outcome.Duration ??= m.Duration;
    }

    private static bool IsMp4(ProbeResult p, MediaCandidate c) =>
        (CodecInfo.ContainerFromMime(p.ContentType?.ToLowerInvariant()) ?? ExtensionOf(p, c)) is "mp4" or "m4v" or "m4a" or "mov";

    // The header is extra detail. A server that will not serve it leaves the file's details unknown, nothing more.
    private async Task<DirectMediaFacts?> ReadHeaderAsync(Uri url, MediaRequestContext ctx, ProbeResult probe,
        CancellationToken headerToken, CancellationToken ct)
    {
        try
        {
            byte[]? start = await _http.ReadStartAsync(url, ctx, probe, HeaderBytes, headerToken).ConfigureAwait(false);
            return start is null ? null : Mp4HeaderFacts.Read(start);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (MediaResolverException) { return null; }
    }

    private static bool IsMedia(ProbeResult p, MediaCandidate c)
    {
        string ctype = p.ContentType?.ToLowerInvariant() ?? "";
        if (ctype.StartsWith("video/") || ctype.StartsWith("audio/")) return true;
        if (ctype is "application/ogg" or "application/mp4") return true;
        if (ctype is "application/octet-stream" or "binary/octet-stream" or "application/force-download" or "application/x-download" or "")
        {
            string? ext = ExtensionOf(p, c);
            return ext is not null && MediaCandidate.MediaExtensions.Contains(ext);
        }
        return false;
    }

    private static string? ExtensionOf(ProbeResult p, MediaCandidate c)
    {
        string? fromName = p.FileName is null ? null : Path.GetExtension(p.FileName).TrimStart('.').ToLowerInvariant();
        return UrlUtil.Extension(p.FinalUrl) ?? UrlUtil.Extension(c.Url) ?? (string.IsNullOrEmpty(fromName) ? null : fromName);
    }

    private static MediaRepresentation BuildDirect(MediaCandidate c, ProbeResult p, MediaRequestContext ctx, int index, DirectMediaFacts? facts = null)
    {
        string ctype = p.ContentType?.ToLowerInvariant() ?? "";
        string? ext = ExtensionOf(p, c);
        string? container = CodecInfo.ContainerFromMime(ctype) ?? ext;

        // The file's own track table outranks what its address or content type suggest.
        bool audio = facts is not null ? !facts.HasVideo
            : ctype.StartsWith("audio/")
            || (!ctype.StartsWith("video/") && ext is not null && MediaCandidate.AudioExtensions.Contains(ext))
            || (!ctype.StartsWith("video/") && c.MimeType?.StartsWith("audio/") == true);

        string? resourceHash = null, contentIdentity = null;
        if (facts is not null && p.StrongETag is { } tag && p.ContentLength is > 0)
        {
            resourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(p.FinalUrl.AbsoluteUri)));
            contentIdentity = "http-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(resourceHash + "|" + tag + "|" +
                    p.ContentLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        return new MediaRepresentation
        {
            Id = $"direct-{index}",
            ResourceETag = resourceHash is not null ? p.StrongETag : null,
            BrowserRangeVerified = c.ObservedLength is not null,
            ResourceUrlSha256 = resourceHash,
            ContentIdentity = contentIdentity,
            // An unreadable track table cannot establish equivalent quality.
            // Keep its endpoint distinct instead of discarding a potential file.
            DeliveryId = facts is null ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(c.Url.AbsoluteUri))) : null,
            Url = c.Url,
            Protocol = StreamProtocol.Http,
            Content = audio ? StreamContent.AudioOnly
                : facts is { HasAudio: false } ? StreamContent.VideoOnly : StreamContent.VideoAndAudio,
            // Without a readable track table, whether a video file has sound stays honestly unverified.
            AudioUnverified = !audio && facts is null,
            StandaloneVideo = facts is { HasVideo: true, HasAudio: false, CompleteFileCandidate: true },
            MutedLoop = !audio && c.MutedLoop,
            Container = container,
            Width = facts?.Width ?? c.Width,
            Height = facts?.Height ?? c.Height,
            VideoCodec = audio ? null : facts?.VideoCodec,
            AudioCodec = facts is { HasAudio: true } ? facts.AudioCodec : null,
            BitrateKbps = c.Bitrate is { } b ? (int)Math.Min(int.MaxValue, b / 1000) : null,
            FileSize = p.ContentLength,
            ExpiresAt = ExpiryParser.FromUrl(c.Url),
            Label = c.Label,
            Context = ctx
        };
    }
}
