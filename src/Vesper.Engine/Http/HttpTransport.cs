using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Vesper.Shared;

namespace Vesper.Engine;

/// <summary>One request's variable parts. Secrets are never persisted and stay within the consented source origin.</summary>
public sealed record RequestSpec(
    long? RangeStart = null,
    long? RangeEndInclusive = null,
    string? IfRangeEtag = null,
    DateTimeOffset? IfRangeDate = null,
    string? Referer = null,
    string? UserAgent = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    string? CredentialOrigin = null,
    string? ExpectedResourceIdentity = null,
    IReadOnlyDictionary<string, string>? HostCookies = null,
    string? OriginHeader = null);

internal sealed record RequestCredentials(
    Uri Origin,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string>? HostCookies = null,
    string? OriginHeader = null);

public sealed class RedirectException(string message) : Exception(message);

/// <summary>A network failure worth retrying (stall, timeout, reset).</summary>
public sealed class TransientNetworkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The configured proxy cannot be used. This is a settings problem, not a network problem, so it is never retried.</summary>
public sealed class ProxyConfigurationException(string message) : Exception(message);

/// <summary>
/// The only place that talks to the network. Wraps HttpClient/SocketsHttpHandler with:
/// manual redirects (loop detection, no https→http, re-validation per hop),
/// no automatic decompression, and connect-time rejection of private addresses.
/// </summary>
internal sealed class HttpTransport : IDisposable
{
    private const int MaxRedirects = 10;
    private const string DefaultUserAgent = "VESPER/1.0 (personal download manager)";

    private readonly EngineOptions _options;
    private readonly HttpClient _client;
    private readonly OriginAwareProxy? _originAwareProxy;
    private readonly bool _validateProxyDns;
    private readonly string? _proxyError;
    private readonly StreamAdmission _streams = new();
    // Protocol failures are scoped to the full origin and expire. Reuse the
    // bounded cooldown structure; this is independent of server Retry-After.
    private readonly OriginCooldown _http2Cooldown = new();

    public HttpTransport(EngineOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,   // stored bytes must stay stored bytes
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(25),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(8),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(75),
            MaxConnectionsPerServer = Math.Max(options.MaxConnectionsPerHost, 1)
        };

        IWebProxy? proxy = null;
        switch (options.Proxy)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                handler.ConnectCallback = ConnectValidatedAsync;   // only possible when we connect to the target ourselves
                break;
            case ProxyMode.System:
                handler.UseProxy = true;
                proxy = HttpClient.DefaultProxy;
                break;
            case ProxyMode.Custom when IsUsableProxy(options.ProxyAddress, out Uri? customProxy):
                proxy = new WebProxy(customProxy);
                handler.UseProxy = true;
                break;
            case ProxyMode.Custom:
                // A mistyped proxy must not quietly become the system proxy or a direct connection.
                _proxyError = "The custom proxy address is invalid. Fix it in Settings, Network.";
                handler.UseProxy = false;
                break;
        }
        if (handler.UseProxy)
        {
            _originAwareProxy = new OriginAwareProxy(proxy ?? HttpClient.DefaultProxy);
            handler.Proxy = _originAwareProxy;
        }
        _validateProxyDns = handler.UseProxy && !options.AllowPrivateNetworks;

        // HttpClient.Timeout would also cut healthy multi-hour transfers. Stalls are handled per read.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsUsableProxy(string? address, out Uri? proxy)
    {
        proxy = null;
        return !string.IsNullOrWhiteSpace(address)
            && Uri.TryCreate(address.Trim(), UriKind.Absolute, out proxy)
            && proxy.Scheme is "http" or "https" && !string.IsNullOrEmpty(proxy.Host);
    }

    /// <summary>Follows redirects and returns the final response with headers read, body unread.</summary>
    public async Task<HttpResponseMessage> SendAsync(Uri url, RequestSpec spec, CancellationToken ct)
    {
        if (_proxyError is not null) throw new ProxyConfigurationException(_proxyError);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri current = url;
        Uri? credentialOrigin = null;
        if (Uri.TryCreate(spec.CredentialOrigin, UriKind.Absolute, out Uri? parsedOrigin)
            && parsedOrigin.Scheme is "http" or "https")
            credentialOrigin = parsedOrigin;

        bool initialRequestWithinCredentialOrigin = credentialOrigin is not null
            && UrlPolicy.IsSameOrigin(credentialOrigin, url);

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!visited.Add(current.AbsoluteUri)) throw new RedirectException("The server redirected in a loop.");
            UrlPolicy.EnsureAllowed(current, _options.AllowPrivateNetworks);
            if (_options.Cooldown.RetryAt(current) is { } retryAt)
            {
                var deferred = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                deferred.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
                return deferred;
            }
            IPAddress? proxyAddress = null;
            if (_validateProxyDns)
            {
                using var dns = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dns.CancelAfter(_options.ConnectTimeout);
                try
                {
                    proxyAddress = await ResolveProxyDestinationAsync(current, dns.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TransientNetworkException("DNS verification timed out.");
                }
            }

            bool sameRequestOrigin = UrlPolicy.IsSameOrigin(url, current);
            bool sameCredentialOrigin = initialRequestWithinCredentialOrigin
                && UrlPolicy.IsSameOrigin(credentialOrigin!, current);
            using HttpRequestMessage request = BuildRequest(current, spec, sameRequestOrigin, sameCredentialOrigin);
            if (proxyAddress is not null)
                _originAwareProxy!.PinRequest(request, current, proxyAddress);
            // When protocol experimentation is enabled, keep one shared host
            // budget across both protocol pools, including HTTP/1 fallback and
            // HTTP/2 streams that were already in flight. The ordinary H1-only
            // path continues to use its existing pooled socket limit.
            bool sharedProtocolBudget = _options.EnableAdaptiveHttp2 || _options.Http2Experiment;
            IDisposable? lease = sharedProtocolBudget
                ? await _streams.AcquireAsync(current, _options.MaxConnectionsPerHost, ct).ConfigureAwait(false)
                : null;
            HttpResponseMessage response;
            try
            {
                response = await SendOnceAsync(request, ct).ConfigureAwait(false);
                if (lease is not null)
                {
                    response.Content = new AdmittedContent(response.Content, lease);
                    lease = null;
                }
            }
            finally { lease?.Dispose(); }
            request.RequestUri = current;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                TimeSpan? delay = HttpFacts.RetryAfter(response, now);
                if (delay is null && response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    delay = TimeSpan.FromSeconds(30);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(delay.Value);
                }
                if (delay is { } wait) _options.Cooldown.Remember(current, now + wait);
            }
            if (!IsRedirect(response.StatusCode))
            {
                if (spec.ExpectedResourceIdentity is { Length: > 0 } expected
                    && (!IsCurrentResourceIdentity(expected)
                        || !string.Equals(expected, ResourceIdentity(current), StringComparison.Ordinal)))
                {
                    response.Dispose();
                    throw new RedirectException("The download link now points to a different file location. Saved data was kept.");
                }
                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new RedirectException("The server redirected without saying where.");

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!UrlPolicy.TryParse(next.AbsoluteUri, out Uri validated, out string problem))
                throw new RedirectException("The server redirected to an unsupported address: " + problem);
            if (current.Scheme == Uri.UriSchemeHttps && validated.Scheme == Uri.UriSchemeHttp)
                throw new RedirectException("Blocked a redirect from HTTPS to HTTP.");
            current = validated;
        }
        throw new RedirectException("The server redirected too many times.");
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(_options.HeaderTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
        }
        catch (HttpProtocolException) when (request.Version >= HttpVersion.Version20 && request.RequestUri is { } targetUri)
        {
            // CDN / middlebox rejected HTTP/2 stream framing. Reconsider
            // this exact origin after a bounded interval, not every request.
            _http2Cooldown.Remember(targetUri, DateTimeOffset.UtcNow.AddMinutes(30));
            using var fallbackRequest = CloneAsHttp11(request);
            return await _client.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientNetworkException("The server took too long to respond.");
        }
    }

    private static HttpRequestMessage CloneAsHttp11(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private HttpRequestMessage BuildRequest(Uri url, RequestSpec spec, bool sameRequestOrigin, bool sameCredentialOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        bool allowH2 = (_options.EnableAdaptiveHttp2 || _options.Http2Experiment)
            && _http2Cooldown.RetryAt(url) is null;
        if (allowH2 && url.Scheme == Uri.UriSchemeHttps)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        else if (allowH2 && _options.Http2Experiment && _options.AllowPrivateNetworks && url.IsLoopback)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(spec.UserAgent) ? DefaultUserAgent : spec.UserAgent);
        if (sameRequestOrigin && spec.OriginHeader is not null && url.Scheme == Uri.UriSchemeHttps)
            request.Headers.TryAddWithoutValidation("Origin", spec.OriginHeader);

        if (Uri.TryCreate(spec.Referer, UriKind.Absolute, out Uri? referer)
            && referer.Scheme is "http" or "https"
            && !(referer.Scheme == Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttps))
        {
            request.Headers.Referrer = sameRequestOrigin && sameCredentialOrigin && UrlPolicy.IsSameOrigin(referer, url)
                ? referer
                : new UriBuilder(referer) { Path = "/", Query = "", Fragment = "", UserName = "", Password = "" }.Uri;
        }

        if (spec.RangeStart is long start)
            request.Headers.Range = new RangeHeaderValue(start, spec.RangeEndInclusive);
        if (spec.RangeStart is not null)
        {
            if (spec.IfRangeEtag is not null && EntityTagHeaderValue.TryParse(spec.IfRangeEtag, out EntityTagHeaderValue? tag))
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            else if (spec.IfRangeDate is DateTimeOffset date)
                request.Headers.IfRange = new RangeConditionHeaderValue(date);
        }

        bool attachedHostCookie = url.Scheme == Uri.UriSchemeHttps
            && spec.HostCookies?.TryGetValue(url.GetLeftPart(UriPartial.Path), out _) == true;
        if (attachedHostCookie)
            request.Headers.TryAddWithoutValidation("Cookie", spec.HostCookies![url.GetLeftPart(UriPartial.Path)]);
        if (sameCredentialOrigin && spec.Secrets is not null)
        {
            foreach ((string name, string value) in spec.Secrets)
            {
                if (name == "Cookie" && attachedHostCookie) continue;
                if (name is "Cookie" or "Authorization") request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return request;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static string ResourceIdentity(Uri uri)
    {
        // Query parameters can select different files. Hash the full address without storing tokens.
        string normalized = UrlPolicy.Normalize(uri);
        return "v2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    internal static string LegacyResourceIdentity(Uri uri)
    {
        var resource = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UrlPolicy.Normalize(resource))));
    }

    internal static bool IsResourceIdentity(string? identity) =>
        identity is { Length: 64 } && identity.All(Uri.IsHexDigit) || IsCurrentResourceIdentity(identity);

    internal static bool IsCurrentResourceIdentity(string? identity) =>
        identity is { Length: 67 } && identity.StartsWith("v2:", StringComparison.Ordinal)
        && identity.AsSpan(3).ToString().All(Uri.IsHexDigit);

    private async Task<IPAddress> ResolveProxyDestinationAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = _options.ResolveHost is { } resolveHost
                ? await resolveHost(uri.IdnHost, ct).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(uri.IdnHost, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new TransientNetworkException("Could not verify the proxy destination in DNS.", ex);
        }
        if (addresses.Length == 0 || addresses.Any(address => !UrlPolicy.IsPublicAddress(address)))
            throw new BlockedAddressException(uri.IdnHost);

        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }

    /// <summary>
    /// Resolves and connects ourselves so that the address actually used is the address that was checked,
    /// closing the DNS-rebinding gap between validation and connection.
    /// </summary>
    // Addresses are tried with a short stagger (RFC 8305 Happy Eyeballs): a black-holed IPv6 route no longer
    // eats the whole connect budget while a working IPv4 route sits untried. At most two attempts in flight.
    private async ValueTask<Stream> ConnectValidatedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] resolved = _options.ResolveHost is { } resolveHost
            ? await resolveHost(host, ct).ConfigureAwait(false)
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        IPAddress[] allowed = resolved.Where(a => _options.AllowPrivateNetworks || UrlPolicy.IsPublicAddress(a)).ToArray();
        if (allowed.Length == 0) throw new BlockedAddressException(host);

        return await NetworkConnector.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    // Only active/waiting origins occupy entries. No lifetime-growing host map.
    private sealed class StreamAdmission
    {
        private sealed class Entry(int limit)
        {
            internal readonly SemaphoreSlim Gate = new(Math.Max(1, limit));
            internal int References;
        }
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        internal async Task<IDisposable> AcquireAsync(Uri uri, int limit, CancellationToken ct)
        {
            string key = uri.IdnHost; // match the engine's per-host limit across ports
            Entry entry;
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out entry!))
                    _entries.Add(key, entry = new Entry(limit));
                entry.References++;
            }
            try { await entry.Gate.WaitAsync(ct).ConfigureAwait(false); }
            catch { RemoveReference(key, entry); throw; }
            return new Lease(this, key, entry);
        }
        private void RemoveReference(string key, Entry entry)
        {
            lock (_entries)
            {
                if (--entry.References != 0) return;
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
        private sealed class Lease(StreamAdmission owner, string key, Entry entry) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                entry.Gate.Release();
                owner.RemoveReference(key, entry);
            }
        }
    }

    private sealed class AdmittedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IDisposable _lease;
        internal AdmittedContent(HttpContent inner, IDisposable lease)
        {
            _inner = inner;
            _lease = lease;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream, context);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            _inner.CopyToAsync(stream, context, ct);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => _inner.ReadAsStreamAsync(ct);
        protected override Stream CreateContentReadStream(CancellationToken ct) => _inner.ReadAsStream(ct);
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            try { base.Dispose(true); _inner.Dispose(); }
            finally { _lease.Dispose(); }
        }
    }
}
