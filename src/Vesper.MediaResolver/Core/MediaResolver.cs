using System.Text.Json;
using System.Xml;
using Vesper.Intelligence;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Rules;
using Vesper.MediaResolver.Security;

namespace Vesper.MediaResolver.Core;

/// <summary>
/// Default <see cref="IMediaResolver"/>. Construction allocates almost nothing: the HTTP client, cache and adapters are
/// created on first use. Nothing is scheduled between calls (no timers, threads or background scanning).
/// </summary>
public sealed class DefaultMediaResolver : IMediaResolver, IDisposable
{
    private readonly ResolverOptions _options;
    private readonly HttpMessageHandler? _handler;
    private readonly IReadOnlyList<AdapterDescriptor> _descriptors;
    private readonly RulePackStore _rulePacks;
    private readonly OnlineLearner _routeLearner = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, IMediaSourceAdapter> _adapters = new(StringComparer.Ordinal);
    private ResolverServices? _services;
    private MetadataCache? _cache;
    private bool _disposed;

    public DefaultMediaResolver(ResolverOptions? options = null, HttpMessageHandler? handler = null, IEnumerable<AdapterDescriptor>? adapters = null)
    {
        _options = options ?? new ResolverOptions();
        _handler = handler;
        _rulePacks = new RulePackStore(_options.RulePackDirectory, _options.RulePackPublicKeySpki);
        _descriptors = [DeclarativeExtractorAdapter.CreateDescriptor(_rulePacks), .. (adapters ?? AdapterRegistry.Default)];
    }

    public RulePackStore RulePacks => _rulePacks;

    public IReadOnlyList<AdapterDescriptor> Adapters => _descriptors;

    /// <summary>Total network requests issued so far (0 until the first resolve).</summary>
    public int NetworkRequestCount => _services?.Http.RequestCount ?? 0;

    public void ClearCache() => _cache?.Clear();

    private ResolverServices Services
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_services is null)
                {
                    _services = new ResolverServices(_options, _handler) { EmbeddedResolver = ResolveEmbeddedAsync };
                    if (_options.CacheTtl > TimeSpan.Zero) _cache = new MetadataCache(_options.CacheMaxEntries);
                }
                return _services;
            }
        }
    }

    private IMediaSourceAdapter GetAdapter(AdapterDescriptor d, ResolverServices svc)
    {
        lock (_gate)
        {
            if (!_adapters.TryGetValue(d.Id, out var a))
                _adapters[d.Id] = a = d.Factory(svc);
            return a;
        }
    }

    public async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Per-call scratch state must not survive request reuse or race another caller using the same request object.
        request = new MediaDiscoveryRequest { Url = request.Url, Browser = request.Browser, Options = request.Options, Progress = request.Progress };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.DiscoveryTimeout);
        var trace = new DiscoveryTrace { Parent = DiscoveryTrace.Current.Value };
        DiscoveryTrace.Current.Value = trace;
        MediaDiscoveryResult result;
        try
        {
            result = await ResolveGuardedAsync(request, deadline.Token, cancellationToken).ConfigureAwait(false);
            if (result.Item is { } item)
                request.Publish(new DiscoveryProgress(item.Title, item.Thumbnail, item.Representations.Count));
            else if (result.Collection is { } collection)
                request.Publish(new DiscoveryProgress(collection.Title, collection.Entries.FirstOrDefault()?.Thumbnail));
        }
        finally { DiscoveryTrace.Current.Value = trace.Parent; }
        try { _options.Measurement?.Invoke(trace.Finish(result)); }
        catch (Exception) { /* Diagnostics are optional and must not affect downloading. */ }
        return result;
    }

    private async Task<MediaDiscoveryResult> ResolveGuardedAsync(MediaDiscoveryRequest request,
        CancellationToken deadlineToken, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveCoreAsync(request, deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return MediaDiscoveryResult.Failure(MediaErrors.Network("The whole discovery deadline was exceeded."));
        }
        catch (MediaResolverException ex)
        {
            return MediaDiscoveryResult.Failure(ex.Error);
        }
        catch (HttpRequestException ex)
        {
            return MediaDiscoveryResult.Failure(MediaErrors.Network(ex.GetType().Name));
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException or InvalidOperationException or ArgumentException)
        {
            return MediaDiscoveryResult.Failure(MediaErrors.SourceChanged($"{ex.GetType().Name}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return MediaDiscoveryResult.Failure(MediaErrors.Internal(ex));
        }
    }

    private async Task<MediaDiscoveryResult> ResolveCoreAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var svc = Services;
        UrlValidator.EnsureAllowed(request.Url, _options);

        if (request.Browser is { } message)
            request.ValidatedBrowser = BrowserMessageValidator.Validate(message, svc.Now, _options);

        if (request.Options.ExactBrowserAssetGroup)
        {
            if (request.Browser is not { Candidates.Count: >= 2 and <= 5 } raw
                || request.ValidatedBrowser is not { Candidates.Count: >= 2 and <= 5 } validated
                || validated.Candidates.Count != raw.Candidates.Count
                || !BrowserAssetIdentity.CanGroup(request.Url, validated.Candidates.Select(c => c.Url)))
                return MediaDiscoveryResult.Failure(MediaErrors.Invalid("The observed sources do not share a supported media asset identity."));
            // No page lookup, unrelated adapter fallback or embed traversal.
            var group = await GetAdapter(GenericAdapter.Descriptor, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            if (group.Item is { } item)
            {
                string? asset = BrowserAssetIdentity.XVideo(validated.Candidates[0].Url);
                // A manifest may point to another asset or delivery namespace.
                // Do not silently fold those tracks into this group.
                bool Matches(Uri url) => BrowserAssetIdentity.XVideo(url) == asset;
                var reps = item.Representations.Where(rep => Matches(rep.Url)
                    && (rep.ManifestUrl is null || Matches(rep.ManifestUrl))
                    && (rep.Segments is null || (rep.Segments.InitializationUrl is null || Matches(rep.Segments.InitializationUrl))
                        && rep.Segments.Segments.All(segment => Matches(segment.Url)))).ToArray();
                if (reps.Length != item.Representations.Count)
                    return MediaDiscoveryResult.Failure(MediaErrors.Invalid("A grouped source referred to a different asset. Check its sources separately."));
            }
            return ResultFinalizer.Finalize(group, _options, svc.Now);
        }

        if (request.Options.ExactBrowserSource)
        {
            if (request.Browser is not { Candidates.Count: 1 } || request.ValidatedBrowser is not { Candidates.Count: 1 })
                return MediaDiscoveryResult.Failure(MediaErrors.Invalid("Checking a media source requires exactly one valid candidate."));
            // A failed check must never offer a different video from the page or its adapter.
            var checkedSource = await GetAdapter(GenericAdapter.Descriptor, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            return ResultFinalizer.Finalize(checkedSource, _options, svc.Now);
        }

        string cacheKey = $"{request.Url.AbsoluteUri}|{request.Options.CollectionIntent}|{request.Options.PageToken}|{request.Options.IncludeSubtitles}|{request.Options.MaxCollectionItems}|{request.Options.ResolveEmbeds}|{request.Options.EmbedDepth}";
        bool cacheable = _cache is not null && request.Browser is null;
        if (cacheable && _cache!.TryGet(cacheKey, svc.Now, out var hit))
        {
            if (DiscoveryTrace.Current.Value is { } trace) trace.CacheHit = true;
            return hit;
        }

        // Browser candidates are already observed playback: do not refetch an inaccessible watch page first.
        MediaDiscoveryResult? browserResult = null;
        if (request.ValidatedBrowser is { Candidates.Count: > 0 })
        {
            browserResult = await GetAdapter(GenericAdapter.Descriptor, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            if (browserResult.IsSuccess) return ResultFinalizer.Finalize(browserResult, _options, svc.Now);
        }

        MediaDiscoveryResult? result = null;
        var matches = _descriptors.Where(descriptor => descriptor.Matches(request.Url)).ToArray();
        bool hasAlternative = matches.Length >= 2 && matches[0].Id == "declarative-pack";
        if (hasAlternative)
        {
            string? preferred = _routeLearner.SelectBestRoute(request.Url.IdnHost, [matches[1].Id, matches[0].Id]);
            if (preferred == matches[1].Id) (matches[0], matches[1]) = (matches[1], matches[0]);
        }
        for (int index = 0; index < matches.Length; index++)
        {
            AdapterDescriptor descriptor = matches[index];
            var answer = await GetAdapter(descriptor, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            bool fallback = answer.Error?.Code == MediaErrorCode.NotThisSite
                || answer.Error is { Code: MediaErrorCode.NoDownloadableMedia, BrowserMayHelp: true }
                || answer.Error?.Code == MediaErrorCode.UnsupportedSite;
            if (hasAlternative && index < 2 && (answer.IsSuccess || fallback))
                _routeLearner.RecordRouteOutcome(request.Url.IdnHost, descriptor.Id, answer.IsSuccess);
            if (answer.Error?.Code == MediaErrorCode.NotThisSite) continue;
            if (answer.IsSuccess || result is null) result = answer;
            if (!hasAlternative || index != 0 || !fallback) break;
        }

        if (result is null || (!result.IsSuccess && ShouldTryGeneric(result, request)))
        {
            var generic = browserResult ?? await GetAdapter(GenericAdapter.Descriptor, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            // Keep a specific adapter's precise error when the generic scan finds nothing either.
            if (result is null || generic.IsSuccess) result = generic;
        }

        var final = ResultFinalizer.Finalize(result, _options, svc.Now);
        if (cacheable && final.IsSuccess) _cache!.Set(cacheKey, final, svc.Now, _options.CacheTtl);
        return final;
    }

    private static bool ShouldTryGeneric(MediaDiscoveryResult failed, MediaDiscoveryRequest request)
    {
        if (failed.Error?.Code == MediaErrorCode.SourceChanged)
            return request.ValidatedBrowser is { Candidates.Count: > 0 };
        if (request.ValidatedBrowser is { Candidates.Count: > 0 }) return true;
        if (failed.Error is { Code: MediaErrorCode.NoDownloadableMedia, BrowserMayHelp: false }) return false;
        return failed.Error?.Code is MediaErrorCode.NoDownloadableMedia
            or MediaErrorCode.UnsupportedSite
            or MediaErrorCode.BrowserContextRequired;
    }

    /// <summary>Follows any legitimate player iframe one level deep while filtering ad and tracker domains.</summary>
    private Task<MediaDiscoveryResult> ResolveEmbeddedAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        if (PlayerConfigDiscovery.IsAdOrTrackingHost(request.Url.Host))
            return Task.FromResult(MediaDiscoveryResult.Failure(MediaErrors.NotThisSite("Ignored ad/tracker iframe")));
        return ResolveAsync(request, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _services?.Dispose();
            _cache?.Clear();
        }
    }
}
