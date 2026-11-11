using System.Text.Json;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols;
using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Shared plumbing for site adapters: bounded JSON/page fetching and manifest merging. No site knowledge lives here.</summary>
internal abstract class AdapterBase : IMediaSourceAdapter
{
    protected ResolverServices Svc { get; }
    protected SecureHttpClient Http => Svc.Http;
    protected ResolverOptions Options => Svc.Options;

    protected AdapterBase(ResolverServices svc) => Svc = svc;

    public abstract bool CanHandle(Uri url);

    public abstract Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct);

    protected async Task<JsonDocument> GetJsonAsync(Uri url, MediaRequestContext? ctx, CancellationToken ct)
    {
        var r = (await Http.GetStringAsync(url, ctx, ct, Options.MaxJsonBytes, "application/json, text/plain;q=0.8, */*;q=0.5").ConfigureAwait(false)).EnsureSuccess();
        return JsonExt.TryParse(r.Content)
            ?? throw new MediaResolverException(MediaErrors.SourceChanged($"{UrlRedactor.Redact(url)} did not return valid JSON"));
    }

    protected async Task<HttpTextResponse> GetPageAsync(Uri url, MediaRequestContext? ctx, CancellationToken ct) =>
        (await Http.GetStringAsync(url, ctx, ct, Options.MaxPageBytes, "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5").ConfigureAwait(false)).EnsureSuccess();

    /// <summary>Resolves a manifest and appends its representations (ids prefixed to avoid clashes with progressive ones).</summary>
    protected async Task<bool> TryAddManifestAsync(
        Uri manifestUrl, MediaRequestContext ctx, string idPrefix, List<MediaRepresentation> reps,
        List<AudioTrack> audio, List<SubtitleTrack> subs, List<MediaError> problems, CancellationToken ct, Action<TimeSpan?>? duration = null)
    {
        try
        {
            var m = await Svc.Manifests.ResolveAsync(manifestUrl, ctx, ct).ConfigureAwait(false);
            reps.AddRange(m.Representations.Select(r => r with { Id = idPrefix + r.Id }));
            audio.AddRange(m.AudioTracks);
            subs.AddRange(m.Subtitles);
            duration?.Invoke(m.Duration);
            return true;
        }
        catch (MediaResolverException ex)
        {
            problems.Add(ex.Error);
            return false;
        }
    }

    /// <summary>The most specific reason all manifests failed, preferring DRM/live/unsupported over generic network noise.</summary>
    protected static MediaError PickProblem(List<MediaError> problems, string fallbackDiagnostic) =>
        problems.FirstOrDefault(p => p.Code is MediaErrorCode.DrmProtected or MediaErrorCode.LiveStreamNotSupported or MediaErrorCode.UnsupportedManifest)
        ?? problems.FirstOrDefault()
        ?? MediaErrors.NoMedia(fallbackDiagnostic, browserMayHelp: true);

    protected static MediaDiscoveryResult Fail(MediaError e) => MediaDiscoveryResult.Failure(e);

    protected static Uri? U(string? s) => UrlUtil.TryAbsolute(s);

    protected static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
