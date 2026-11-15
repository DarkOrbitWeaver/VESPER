using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
    string? CredentialOrigin = null);

internal sealed record RequestCredentials(Uri Origin, IReadOnlyDictionary<string, string> Headers);

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

    public HttpTransport(EngineOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,   // stored bytes must stay stored bytes
            UseCookies = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = Math.Max(options.MaxConnectionsPerHost, 4)
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
            HttpResponseMessage response = await SendOnceAsync(request, ct).ConfigureAwait(false);
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
            if (!IsRedirect(response.StatusCode)) return response;

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientNetworkException("The server took too long to respond.");
        }
    }

    private static HttpRequestMessage BuildRequest(Uri url, RequestSpec spec, bool sameRequestOrigin, bool sameCredentialOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(spec.UserAgent) ? DefaultUserAgent : spec.UserAgent);
        // Page URLs can carry tokens. Keep the full referrer on the source origin only.
        if (sameRequestOrigin && Uri.TryCreate(spec.Referer, UriKind.Absolute, out Uri? referer)
            && referer.Scheme is "http" or "https")
        {
            request.Headers.Referrer = sameCredentialOrigin && UrlPolicy.IsSameOrigin(referer, url)
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

        if (sameCredentialOrigin && spec.Secrets is not null)
        {
            foreach ((string name, string value) in spec.Secrets)
                if (name is "Cookie" or "Authorization") request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

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
}
