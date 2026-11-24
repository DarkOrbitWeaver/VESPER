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
        var outcome = new CandidateOutcome();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int manifestCount = 0, directIndex = 0, headerReads = 0;

        foreach (var c in candidates)
        {
            if (!seen.Add(c.Url.AbsoluteUri)) continue;
            if (seen.Count > _o.MaxCandidatesPerPage) break;
            ct.ThrowIfCancellationRequested();

            try
            {
                if (c.Kind is CandidateKind.Hls or CandidateKind.Dash)
                {
                    if (manifestCount >= _o.MaxManifestsPerPage) continue;
                    await AddManifestAsync(c.Url, ctx, outcome, manifestCount++, ct).ConfigureAwait(false);
                    continue;
                }

                var probe = await _http.ProbeAsync(c.Url, ctx, ct).ConfigureAwait(false);
                if (!probe.IsSuccess)
                {
                    outcome.Problems.Add(MediaErrors.FromHttpStatus(probe.StatusCode, c.Url));
                    continue;
                }

                // The observed asset key must survive redirects before a direct
                // representation is allowed to join a grouped picker.
                if (exactAssetGroup && BrowserAssetIdentity.XVideo(c.Url) != BrowserAssetIdentity.XVideo(probe.FinalUrl))
                {
                    outcome.Problems.Add(MediaErrors.Invalid("A source redirected to a different media asset."));
                    continue;
                }

                string ctype = probe.ContentType?.ToLowerInvariant() ?? "";
                if (ctype.Contains("mpegurl") || ctype.Contains("dash+xml"))
                {
                    if (manifestCount < _o.MaxManifestsPerPage)
                        await AddManifestAsync(c.Url, ctx, outcome, manifestCount++, ct).ConfigureAwait(false);
                    continue;
                }

                if (!IsMedia(probe, c))
                {
                    outcome.Problems.Add(MediaErrors.NoMedia($"{UrlRedactor.Redact(c.Url)} is not media (content-type '{ctype}')", false));
                    continue;
                }
                Mp4HeaderFacts? facts = null;
                if (headerReads < MaxHeaderReads && !headerDeadline.IsCancellationRequested && IsMp4(probe, c))
                {
                    if (headerReads == 0) headerDeadline.CancelAfter(TimeSpan.FromSeconds(4));
                    headerReads++;
                    facts = await ReadHeaderAsync(c.Url, ctx, probe, headerDeadline.Token, ct).ConfigureAwait(false);
                }
                if (facts is { Encrypted: true })
                {
                    outcome.Problems.Add(MediaErrors.Drm($"{UrlRedactor.Redact(c.Url)} declares encrypted tracks"));
                    continue;
                }
                outcome.Representations.Add(BuildDirect(c, probe, ctx, directIndex++, facts));
                if (facts?.DurationSeconds is { } seconds) outcome.Duration ??= TimeSpan.FromSeconds(seconds);
            }
            catch (MediaResolverException ex)
            {
                outcome.Problems.Add(ex.Error);
            }
        }
        return outcome;
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
    private async Task<Mp4HeaderFacts?> ReadHeaderAsync(Uri url, MediaRequestContext ctx, ProbeResult probe,
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

    private static MediaRepresentation BuildDirect(MediaCandidate c, ProbeResult p, MediaRequestContext ctx, int index, Mp4HeaderFacts? facts = null)
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
            StandaloneVideo = facts is { HasVideo: true, HasAudio: false },
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
