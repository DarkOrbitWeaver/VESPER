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

    public CandidateProcessor(SecureHttpClient http, ManifestResolver manifests, ResolverOptions options)
    {
        _http = http;
        _manifests = manifests;
        _o = options;
    }

    public async Task<CandidateOutcome> ProcessAsync(IEnumerable<MediaCandidate> candidates, MediaRequestContext ctx, CancellationToken ct)
    {
        var outcome = new CandidateOutcome();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int manifestCount = 0, directIndex = 0;

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
                outcome.Representations.Add(BuildDirect(c, probe, ctx, directIndex++));
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

    private static MediaRepresentation BuildDirect(MediaCandidate c, ProbeResult p, MediaRequestContext ctx, int index)
    {
        string ctype = p.ContentType?.ToLowerInvariant() ?? "";
        string? ext = ExtensionOf(p, c);
        string? container = CodecInfo.ContainerFromMime(ctype) ?? ext;

        bool audio = ctype.StartsWith("audio/")
            || (!ctype.StartsWith("video/") && ext is not null && MediaCandidate.AudioExtensions.Contains(ext))
            || (!ctype.StartsWith("video/") && c.MimeType?.StartsWith("audio/") == true);

        return new MediaRepresentation
        {
            Id = $"direct-{index}",
            Url = c.Url,
            Protocol = StreamProtocol.Http,
            Content = audio ? StreamContent.AudioOnly : StreamContent.VideoAndAudio,
            // A bare video file carries no codec metadata here, so audio presence is honestly marked unverified.
            AudioUnverified = !audio,
            Container = container,
            Width = c.Width,
            Height = c.Height,
            BitrateKbps = c.Bitrate is { } b ? (int)Math.Min(int.MaxValue, b / 1000) : null,
            FileSize = p.ContentLength,
            ExpiresAt = ExpiryParser.FromUrl(c.Url),
            Label = c.Label,
            Context = ctx
        };
    }
}
