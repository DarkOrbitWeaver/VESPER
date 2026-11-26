using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols.Dash;
using Vesper.MediaResolver.Protocols.Hls;

namespace Vesper.MediaResolver.Protocols;

/// <summary>
/// Fetches and interprets an HLS or DASH manifest. DRM, SAMPLE-AES, live streams, multi-period DASH and malformed
/// manifests surface as typed <see cref="MediaResolverException"/>s: they never produce a "successful" result.
/// </summary>
public sealed class ManifestResolver
{
    private readonly SecureHttpClient _http;
    private readonly ResolverOptions _o;

    public ManifestResolver(SecureHttpClient http, ResolverOptions options)
    {
        _http = http;
        _o = options;
    }

    public async Task<ManifestResult> ResolveAsync(Uri url, MediaRequestContext ctx, CancellationToken ct)
    {
        var r = (await _http.GetStringAsync(url, ctx, ct, _o.MaxManifestBytes,
            "application/vnd.apple.mpegurl, application/x-mpegURL, application/dash+xml, */*", allowBinaryManifest: true).ConfigureAwait(false)).EnsureSuccess();
        if (!r.IsText)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Manifest URL returned non-text content ({r.ContentType})"));
        return await ResolveTextAsync(r.Content, r.FinalUrl, ctx, ct).ConfigureAwait(false);
    }

    public async Task<ManifestResult> ResolveTextAsync(string text, Uri url, MediaRequestContext ctx, CancellationToken ct)
    {
        if (LooksLikeHls(text))
            return await ResolveHlsAsync(text, url, ctx, ct).ConfigureAwait(false);
        if (text.Contains("<MPD", StringComparison.Ordinal))
            return ResolveDash(text, url, ctx);
        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Content is neither an HLS playlist nor a DASH manifest")
            with { UserMessage = "The site did not send a readable HLS or DASH playlist." });
    }

    private static bool LooksLikeHls(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 256)).TrimStart('\uFEFF').TrimStart();
        return head.StartsWith("#EXTM3U", StringComparison.Ordinal);
    }

    private async Task<ManifestResult> ResolveHlsAsync(string text, Uri url, MediaRequestContext ctx, CancellationToken ct)
    {
        var parsed = HlsParser.Parse(text, url, _o.MaxSegmentsPerRepresentation * 2);

        if (parsed.Media is { } media)
        {
            GuardMedia(media);
            return HlsRepresentationBuilder.FromMedia(media, url, ctx);
        }

        var master = parsed.Master!;
        if (master.SessionProtection == HlsProtection.Drm)
            throw new MediaResolverException(MediaErrors.Drm("HLS master playlist declares a DRM session key"));
        if (master.SessionProtection == HlsProtection.SampleAes)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS SAMPLE-AES encryption is not supported"));
        if (master.Variants.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS master playlist has no variants"));

        // Probe one variant and, if needed, its default audio rendition.
        HlsMediaPlaylist? probe = null;
        HlsMediaPlaylist? audioProbe = null;
        Uri? probedVariant = null;
        Uri? probedAudio = null;
        try
        {
            var variant = SelectProbeVariant(master);
            probe = await ProbeHlsAsync(variant.Url, ctx, ct).ConfigureAwait(false);
            if (probe is not null)
            {
                probedVariant = variant.Url;
                Uri? audio = master.Renditions.Where(r => r.Type == "AUDIO" && r.GroupId == variant.AudioGroup && r.Url is not null)
                    .OrderByDescending(r => r.IsDefault).FirstOrDefault()?.Url;
                if (audio is not null)
                {
                    audioProbe = await ProbeHlsAsync(audio, ctx, ct).ConfigureAwait(false);
                    if (audioProbe is not null) probedAudio = audio;
                }
            }
        }
        catch (MediaResolverException ex) when (ex.Error.Code is MediaErrorCode.NetworkFailure or MediaErrorCode.VideoUnavailable)
        {
            _o.Diagnostics?.Invoke("HLS probe playlist unavailable");
        }

        return HlsRepresentationBuilder.FromMaster(master, url, ctx, probe, probedVariant, audioProbe, probedAudio);
    }

    private async Task<HlsMediaPlaylist?> ProbeHlsAsync(Uri url, MediaRequestContext ctx, CancellationToken ct)
    {
        var response = (await _http.GetStringAsync(url, ctx, ct, _o.MaxManifestBytes, allowBinaryManifest: true)
            .ConfigureAwait(false)).EnsureSuccess();
        var media = HlsParser.Parse(response.Content, response.FinalUrl, _o.MaxSegmentsPerRepresentation * 2).Media;
        if (media is not null) GuardMedia(media);
        return media;
    }

    private static HlsVariant SelectProbeVariant(HlsMasterPlaylist master)
    {
        // Keep the liveness probe aligned with the engine's default muxed variant.
        var externalAudioGroups = master.Renditions
            .Where(r => r.Type == "AUDIO" && r.Url is not null)
            .Select(r => r.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        var usable = master.Variants.Where(variant =>
        {
            bool audioOnly = variant.Height is null && variant.Codecs is { } codecs
                && !codecs.Contains("avc", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("hev", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("vp0", StringComparison.OrdinalIgnoreCase)
                && !codecs.Contains("av01", StringComparison.OrdinalIgnoreCase);
            return variant.AudioGroup is null || !externalAudioGroups.Contains(variant.AudioGroup) || audioOnly;
        }).ToList();

        return usable.Count > 0
            ? usable.MaxBy(variant => variant.Bandwidth)!
            : master.Variants[0];
    }

    private static void GuardMedia(HlsMediaPlaylist p)
    {
        if (p.Protection == HlsProtection.Drm)
            throw new MediaResolverException(MediaErrors.Drm("HLS playlist uses a DRM key format"));
        if (p.Protection == HlsProtection.SampleAes)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS SAMPLE-AES encryption is not supported"));
        if (p.IsLive)
            throw new MediaResolverException(MediaErrors.Live("HLS playlist has no EXT-X-ENDLIST"));
        if (p.HasGaps)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS playlists with missing segments (EXT-X-GAP) are not supported"));
        if (p.IsIFrameOnly)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS I-frame-only playlists are not supported"));
    }

    private ManifestResult ResolveDash(string text, Uri url, MediaRequestContext ctx)
    {
        var m = DashParser.Parse(text, url, _o.MaxSegmentsPerRepresentation);
        if (m.IsProtected)
            throw new MediaResolverException(MediaErrors.Drm($"DASH ContentProtection present: {string.Join(", ", m.ProtectionSchemes)}"));
        if (m.IsDynamic)
            throw new MediaResolverException(MediaErrors.Live("DASH manifest is dynamic (live)"));
        if (m.Representations.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest(
                "DASH manifest contains no usable representations" + (m.Warnings.Count > 0 ? $" ({m.Warnings[0]})" : "")));
        return DashRepresentationBuilder.Build(m, url, ctx);
    }
}
