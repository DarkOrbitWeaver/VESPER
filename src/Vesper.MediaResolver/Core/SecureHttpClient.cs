using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;

namespace Vesper.MediaResolver.Core;

public sealed record HttpTextResponse(
    Uri FinalUrl,
    int StatusCode,
    string Content,
    string? ContentType,
    IReadOnlyList<string> Cookies,
    bool IsText = true)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>Returns this response or throws a typed <see cref="MediaResolverException"/> for non-2xx statuses.</summary>
    public HttpTextResponse EnsureSuccess() =>
        IsSuccess ? this : throw new MediaResolverException(MediaErrors.FromHttpStatus(StatusCode, FinalUrl));
}

public sealed record ProbeResult(Uri FinalUrl, int StatusCode, string? ContentType, long? ContentLength, bool AcceptsRanges, string? FileName)
{
    public bool IsSuccess => StatusCode is 200 or 206;
}

/// <summary>
/// The only component that touches the network. It enforces:
/// http/https only, no credentials in URLs, manual redirect following with per-hop validation (loops, downgrades, private hosts),
/// connect-time DNS filtering (defeats DNS rebinding), per-host credential scoping, response size caps, timeouts and a concurrency bound.
/// The underlying HttpClient is created once and shared; idle connections are recycled by the handler (no timers of our own).
/// </summary>
public sealed class SecureHttpClient : IDisposable
{
    private readonly ResolverOptions _o;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate;
    private readonly bool _validateProxyDns;
    private int _requests;

    public int RequestCount => Volatile.Read(ref _requests);

    public SecureHttpClient(ResolverOptions options, HttpMessageHandler? handlerOverride = null)
    {
        _o = options;
        _validateProxyDns = handlerOverride is null && (options.UseSystemProxy || options.ProxyAddress is not null) && !options.AllowPrivateNetworks;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
        _client = new HttpClient(handlerOverride ?? CreateHandler(options), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan // per-request timeouts are applied through linked tokens
        };
    }

    private static SocketsHttpHandler CreateHandler(ResolverOptions o)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = o.UseSystemProxy || o.ProxyAddress is not null,
            Proxy = o.ProxyAddress is { } proxy ? new WebProxy(proxy) : null
        };
        if (!h.UseProxy && !o.AllowPrivateNetworks)
            h.ConnectCallback = ConnectFilteredAsync;
        return h;
    }

    /// <summary>Resolves DNS ourselves and refuses to connect to non-public addresses (every answer is checked).</summary>
    private static async ValueTask<Stream> ConnectFilteredAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        string host = ctx.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowed = addresses.Where(IpAddressClassifier.IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new MediaResolverException(MediaErrors.Blocked($"Host '{host}' resolves only to non-public addresses"));

        Exception? last = null;
        foreach (var ip in allowed)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    // ---- public API -------------------------------------------------------------------------------------------

    public async Task<HttpTextResponse> GetStringAsync(
        Uri url, MediaRequestContext? ctx, CancellationToken ct, int? maxBytes = null, string? accept = null)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, ctx, ct, req =>
        {
            if (accept is not null) req.Headers.TryAddWithoutValidation("Accept", accept);
            if (ctx?.AcceptLanguage is null) req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        }).ConfigureAwait(false);

        Uri final = resp.RequestMessage?.RequestUri ?? url;
        string? ctype = resp.Content.Headers.ContentType?.MediaType;
        int status = (int)resp.StatusCode;
        var cookies = resp.Headers.TryGetValues("Set-Cookie", out var raw)
            ? raw.Select(NameValuePart).Where(c => c.Length > 0).ToList()
            : new List<string>();

        if (status < 400 && ctype is not null && !IsTextual(ctype))
            return new HttpTextResponse(final, status, "", ctype, cookies, IsText: false);

        int limit = status >= 400 ? _o.MaxErrorBodyBytes : maxBytes ?? _o.MaxPageBytes;
        string body = await ReadLimitedAsync(resp, limit, ct).ConfigureAwait(false);
        return new HttpTextResponse(final, status, body, ctype, cookies);
    }

    /// <summary>
    /// Fetches a bounded JPEG/PNG/GIF thumbnail using the SAME validated HTTP path as discovery.
    /// Never follows unchecked redirects, forwards cookies, or exposes a generic binary fetch to page code.
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(Uri url, CancellationToken ct, int maxBytes = 768 * 1024)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_o.RequestTimeout);
        ct = timeout.Token;
        using var response = await SendAsync(HttpMethod.Get, url, null, ct, req =>
            req.Headers.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/gif")).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("image/jpeg" or "image/png" or "image/gif")) return null;
        if (response.Content.Headers.ContentLength is long length && length > maxBytes) return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > maxBytes) return null;
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    /// <summary>
    /// POSTs a small JSON body to an API endpoint through the same validated path as GET (URL policy, connect-time
    /// DNS filtering, per-hop redirect checks, timeouts, response caps). Credentials follow the context's host scoping.
    /// </summary>
    public async Task<HttpTextResponse> PostJsonAsync(Uri url, string json, MediaRequestContext? ctx,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct, int? maxBytes = null)
    {
        if (json.Length > 64 * 1024) throw new MediaResolverException(MediaErrors.TooLarge("JSON request body exceeds 64 KiB"));
        using var resp = await SendAsync(HttpMethod.Post, url, ctx, ct, req =>
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (headers is not null)
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (ctx?.AcceptLanguage is null) req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        }).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        int limit = status >= 400 ? _o.MaxErrorBodyBytes : maxBytes ?? _o.MaxJsonBytes;
        string body = await ReadLimitedAsync(resp, limit, ct).ConfigureAwait(false);
        return new HttpTextResponse(resp.RequestMessage?.RequestUri ?? url, status, body, resp.Content.Headers.ContentType?.MediaType, []);
    }

    /// <summary>
    /// Requests exactly one byte at <paramref name="offset"/> and returns the HTTP status. Used to prove that a media URL
    /// serves data beyond its first bytes (some clients' URLs are cut off after ~1 MiB without a proof-of-origin token).
    /// </summary>
    public async Task<int> ProbeRangeAsync(Uri url, MediaRequestContext? ctx, long offset, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, ctx, ct,
            req => req.Headers.Range = new RangeHeaderValue(offset, offset)).ConfigureAwait(false);
        return (int)resp.StatusCode;
    }

    /// <summary>HEAD first; if the server refuses, a 1-byte ranged GET. The body is never downloaded.</summary>
    public async Task<ProbeResult> ProbeAsync(Uri url, MediaRequestContext? ctx, CancellationToken ct)
    {
        try
        {
            using var head = await SendAsync(HttpMethod.Head, url, ctx, ct, null).ConfigureAwait(false);
            var r = ToProbe(head, url);
            if (r.IsSuccess && r.ContentType is not null) return r;
        }
        catch (MediaResolverException ex) when (ex.Error.Code == MediaErrorCode.NetworkFailure) { /* fall back to ranged GET */ }

        using var get = await SendAsync(HttpMethod.Get, url, ctx, ct,
            req => req.Headers.Range = new RangeHeaderValue(0, 0)).ConfigureAwait(false);
        return ToProbe(get, url);
    }

    private static ProbeResult ToProbe(HttpResponseMessage resp, Uri fallbackUrl)
    {
        var h = resp.Content.Headers;
        long? length = null;
        if (h.ContentRange?.Length is { } total) length = total;
        else if (resp.StatusCode == HttpStatusCode.OK) length = h.ContentLength;

        bool ranges = resp.StatusCode == HttpStatusCode.PartialContent ||
                      resp.Headers.AcceptRanges.Contains("bytes");
        return new ProbeResult(resp.RequestMessage?.RequestUri ?? fallbackUrl, (int)resp.StatusCode,
            h.ContentType?.MediaType, length, ranges, h.ContentDisposition?.FileName?.Trim('"'));
    }

    // ---- internals --------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri url, MediaRequestContext? ctx, CancellationToken ct, Action<HttpRequestMessage>? configure)
    {
        ctx ??= MediaRequestContext.Empty;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool leased = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_o.RequestTimeout);

            for (int hop = 0; ; hop++)
            {
                UrlValidator.EnsureAllowed(url, _o);
                if (!visited.Add(url.AbsoluteUri))
                    throw new MediaResolverException(MediaErrors.Network("Redirect loop detected", retry: false));
                if (_o.OriginRetryAt?.Invoke(url) is { } deferred && deferred > _o.TimeProvider.GetUtcNow())
                    throw new MediaResolverException(MediaErrors.RateLimited("This origin is cooling down") with { RetryAt = deferred });
                if (_validateProxyDns)
                    await ValidateProxyDestinationAsync(url, cts.Token).ConfigureAwait(false);

                using var req = new HttpRequestMessage(method, url);
                req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent ?? _o.UserAgent);
                foreach (var kv in ctx.HeadersFor(url))
                {
                    if (kv.Key == "User-Agent") { req.Headers.Remove("User-Agent"); }
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
                configure?.Invoke(req);

                Interlocked.Increment(ref _requests);
                DiscoveryTrace.Current.Value?.RequestStarted();
                _o.Diagnostics?.Invoke($"{method} {UrlRedactor.Redact(url)}");

                HttpResponseMessage resp;
                try
                {
                    resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new MediaResolverException(MediaErrors.Network($"Timeout contacting {UrlRedactor.Redact(url)}"));
                }
                catch (HttpRequestException ex)
                {
                    for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
                        if (inner is MediaResolverException mre) throw mre;
                    throw new MediaResolverException(MediaErrors.Network($"{ex.GetType().Name} contacting {UrlRedactor.Redact(url)}"));
                }

                var status = resp.StatusCode;
                if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable && resp.Headers.RetryAfter is not null)
                {
                    DateTimeOffset now = _o.TimeProvider.GetUtcNow();
                    TimeSpan delay = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromSeconds(30);
                    DateTimeOffset until = now.AddTicks(Math.Clamp(delay.Ticks, 0, TimeSpan.TicksPerHour));
                    _o.OriginThrottled?.Invoke(url, until);
                    string origin = url.GetLeftPart(UriPartial.Authority);
                    _o.Diagnostics?.Invoke($"HTTP {(int)status} from {origin}. Retry at {until:O}.");
                    resp.Dispose();
                    throw new MediaResolverException(MediaErrors.RateLimited($"HTTP {(int)status} from {origin}") with { RetryAt = until });
                }
                if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                    && resp.Headers.Location is { } location)
                {
                    resp.Dispose();
                    if (hop >= _o.MaxRedirects)
                        throw new MediaResolverException(MediaErrors.Network("Too many redirects", retry: false));
                    var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
                        throw new MediaResolverException(MediaErrors.Blocked("Redirect downgrades https to http"));
                    url = next; // the next loop iteration re-validates it and re-filters credentials for the new host
                    continue;
                }
                resp.Content = new LeasedContent(resp.Content, _gate);
                leased = true;
                return resp;
            }
        }
        finally
        {
            if (!leased) _gate.Release();
        }
    }

    private static async Task ValidateProxyDestinationAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(address => !IpAddressClassifier.IsPublic(address)))
                throw new MediaResolverException(MediaErrors.Blocked("The proxy destination must resolve to public addresses."));
        }
        catch (SocketException)
        {
            throw new MediaResolverException(MediaErrors.Network("Could not verify the proxy destination in DNS."));
        }
    }

    private async Task<string> ReadLimitedAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        long? declared = resp.Content.Headers.ContentLength;
        if (declared > maxBytes)
            throw new MediaResolverException(MediaErrors.TooLarge($"Content-Length {declared} exceeds limit {maxBytes}"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_o.RequestTimeout * 2);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var ms = new MemoryStream(declared is > 0 ? (int)Math.Min(declared.Value, maxBytes) : 16 * 1024);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > maxBytes)
                    throw new MediaResolverException(MediaErrors.TooLarge($"Body exceeds limit {maxBytes}"));
                ms.Write(buffer, 0, read);
            }
            string s = GetEncoding(resp.Content.Headers.ContentType?.CharSet).GetString(ms.GetBuffer(), 0, (int)ms.Length);
            return s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MediaResolverException(MediaErrors.Network("Timed out reading response body"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MediaResolverException(MediaErrors.Network($"Body read failed"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(charset.Trim('"', '\'', ' ')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Contains("dash", StringComparison.OrdinalIgnoreCase);

    private static string NameValuePart(string setCookie)
    {
        int semi = setCookie.IndexOf(';');
        string nv = (semi < 0 ? setCookie : setCookie[..semi]).Trim();
        return nv.Contains('=') && nv.Length < 4096 ? nv : "";
    }

    // The response owns its concurrency permit until its body is consumed/disposed, not merely until headers arrive.
    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private SemaphoreSlim? _lease;
        internal LeasedContent(HttpContent inner, SemaphoreSlim lease)
        {
            _inner = inner;
            _lease = lease;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => _inner.ReadAsStreamAsync(ct);
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) _inner.Dispose(); }
            finally { Interlocked.Exchange(ref _lease, null)?.Release(); base.Dispose(disposing); }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        // Do not dispose the managed semaphore: a closing response may still return its lease.
    }
}
