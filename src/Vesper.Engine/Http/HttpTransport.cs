using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Vesper.Engine;

/// <summary>One request's variable parts. Secrets are never persisted and never follow a cross-origin redirect.</summary>
public sealed record RequestSpec(
    long? RangeStart = null,
    long? RangeEndInclusive = null,
    string? IfRangeEtag = null,
    DateTimeOffset? IfRangeDate = null,
    string? Referer = null,
    string? UserAgent = null,
    IReadOnlyDictionary<string, string>? Secrets = null);

public sealed class RedirectException(string message) : Exception(message);

/// <summary>A network failure worth retrying (stall, timeout, reset).</summary>
public sealed class TransientNetworkException(string message, Exception? inner = null) : Exception(message, inner);

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
    private readonly bool _validateProxyDns;

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

        switch (options.Proxy)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                handler.ConnectCallback = ConnectValidatedAsync;   // only possible when we connect to the target ourselves
                break;
            case ProxyMode.System:
                handler.UseProxy = true;
                break;
            case ProxyMode.Custom when Uri.TryCreate(options.ProxyAddress, UriKind.Absolute, out Uri? proxy):
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
                break;
        }
        _validateProxyDns = handler.UseProxy && !options.AllowPrivateNetworks;

        // HttpClient.Timeout would also cut healthy multi-hour transfers. Stalls are handled per read.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Follows redirects and returns the final response with headers read, body unread.</summary>
    public async Task<HttpResponseMessage> SendAsync(Uri url, RequestSpec spec, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri current = url;

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
            if (_validateProxyDns)
            {
                using var dns = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dns.CancelAfter(_options.ConnectTimeout);
                IPAddress[] addresses;
                try { addresses = await Dns.GetHostAddressesAsync(current.IdnHost, dns.Token).ConfigureAwait(false); }
                catch (SocketException ex) { throw new TransientNetworkException("Could not verify the proxy destination in DNS.", ex); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TransientNetworkException("DNS verification timed out."); }
                if (addresses.Length == 0 || addresses.Any(address => !UrlPolicy.IsPublicAddress(address)))
                    throw new BlockedAddressException(current.IdnHost);
            }

            using HttpRequestMessage request = BuildRequest(current, spec, UrlPolicy.IsSameOrigin(url, current));
            HttpResponseMessage response = await SendOnceAsync(request, ct).ConfigureAwait(false);
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

    private static HttpRequestMessage BuildRequest(Uri url, RequestSpec spec, bool sameOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(spec.UserAgent) ? DefaultUserAgent : spec.UserAgent);
        // A page URL can carry tokens. Never forward its full path/query to a
        // different origin after a redirect, even if the original request supplied it.
        if (sameOrigin && Uri.TryCreate(spec.Referer, UriKind.Absolute, out Uri? referer)
            && referer.Scheme is "http" or "https")
            request.Headers.Referrer = referer;

        if (spec.RangeStart is long start)
            request.Headers.Range = new RangeHeaderValue(start, spec.RangeEndInclusive);
        if (spec.RangeStart is not null)
        {
            if (spec.IfRangeEtag is not null && EntityTagHeaderValue.TryParse(spec.IfRangeEtag, out EntityTagHeaderValue? tag))
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            else if (spec.IfRangeDate is DateTimeOffset date)
                request.Headers.IfRange = new RangeConditionHeaderValue(date);
        }

        if (sameOrigin && spec.Secrets is not null)
        {
            foreach ((string name, string value) in spec.Secrets)
                if (name is "Cookie" or "Authorization") request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Resolves and connects ourselves so that the address actually used is the address that was checked,
    /// closing the DNS-rebinding gap between validation and connection.
    /// </summary>
    private async ValueTask<Stream> ConnectValidatedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        Exception? last = null;

        foreach (IPAddress address in addresses)
        {
            if (!_options.AllowPrivateNetworks && !UrlPolicy.IsPublicAddress(address))
            {
                last = new BlockedAddressException(host);
                continue;
            }
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    public void Dispose() => _client.Dispose();
}
