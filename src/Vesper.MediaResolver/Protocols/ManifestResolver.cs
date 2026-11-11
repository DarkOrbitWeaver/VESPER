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
            "application/vnd.apple.mpegurl, application/x-mpegURL, application/dash+xml, */*").ConfigureAwait(false)).EnsureSuccess();
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
        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Content is neither an HLS playlist nor a DASH manifest"));
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

        // One extra request: look at a single media playlist to learn container, protection, liveness and duration.
        HlsMediaPlaylist? probe = null;
        try
        {
            var first = master.Variants[0].Url;
            var pr = (await _http.GetStringAsync(first, ctx, ct, _o.MaxManifestBytes).ConfigureAwait(false)).EnsureSuccess();
            probe = HlsParser.Parse(pr.Content, pr.FinalUrl, _o.MaxSegmentsPerRepresentation * 2).Media;
            if (probe is not null) GuardMedia(probe);
        }
        catch (MediaResolverException ex) when (ex.Error.Code is MediaErrorCode.NetworkFailure or MediaErrorCode.VideoUnavailable)
        {
            _o.Diagnostics?.Invoke("HLS probe playlist unavailable; continuing without container/duration metadata");
        }

        return HlsRepresentationBuilder.FromMaster(master, url, ctx, probe);
    }

    private static void GuardMedia(HlsMediaPlaylist p)
    {
        if (p.Protection == HlsProtection.Drm)
            throw new MediaResolverException(MediaErrors.Drm("HLS playlist uses a DRM key format"));
        if (p.Protection == HlsProtection.SampleAes)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("HLS SAMPLE-AES encryption is not supported"));
        if (p.IsLive)
            throw new MediaResolverException(MediaErrors.Live("HLS playlist has no EXT-X-ENDLIST"));
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
