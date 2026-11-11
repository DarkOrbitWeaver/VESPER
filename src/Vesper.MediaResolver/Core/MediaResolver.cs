using System.Text.Json;
using System.Xml;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Extractors;
using Vesper.MediaResolver.Models;
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
    private readonly object _gate = new();
    private readonly Dictionary<string, IMediaSourceAdapter> _adapters = new(StringComparer.Ordinal);
    private ResolverServices? _services;
    private MetadataCache? _cache;
    private bool _disposed;

    public DefaultMediaResolver(ResolverOptions? options = null, HttpMessageHandler? handler = null, IEnumerable<AdapterDescriptor>? adapters = null)
    {
        _options = options ?? new ResolverOptions();
        _handler = handler;
        _descriptors = (adapters ?? AdapterRegistry.Default).ToList();
    }

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
        foreach (var d in _descriptors)
        {
            if (!d.Matches(request.Url)) continue;
            var r = await GetAdapter(d, svc).ResolveAsync(request, ct).ConfigureAwait(false);
            if (r.Error?.Code == MediaErrorCode.NotThisSite) continue;
            result = r;
            break;
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
        if (request.ValidatedBrowser is { Candidates.Count: > 0 }) return true;
        if (failed.Error is { Code: MediaErrorCode.NoDownloadableMedia, BrowserMayHelp: false }) return false;
        return failed.Error?.Code is MediaErrorCode.NoDownloadableMedia or MediaErrorCode.SourceChanged or MediaErrorCode.UnsupportedSite;
    }

    /// <summary>Only players that a dedicated adapter understands are followed; arbitrary iframes (ads, widgets) are ignored.</summary>
    private Task<MediaDiscoveryResult> ResolveEmbeddedAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        if (!_descriptors.Any(d => d.Matches(request.Url)))
            return Task.FromResult(MediaDiscoveryResult.Failure(MediaErrors.NotThisSite("No adapter for embedded URL")));
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
