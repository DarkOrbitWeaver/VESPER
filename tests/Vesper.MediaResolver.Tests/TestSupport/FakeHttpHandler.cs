using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Vesper.MediaResolver.Tests.TestSupport;

/// <summary>In-memory HTTP server for offline tests. Unknown URLs answer 404. Records every request (including headers).</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Route(int Status, string Body, string ContentType, IReadOnlyDictionary<string, string>? Headers = null,
        string? Redirect = null, IReadOnlyDictionary<string, string>? ContentHeaders = null, bool IgnoreRange = false);

    public sealed record Recorded(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body);

    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public List<Recorded> Requests { get; } = new();
    public TimeSpan Delay { get; set; }

    public int Count(string urlContains) => Requests.Count(r => r.Url.AbsoluteUri.Contains(urlContains, StringComparison.Ordinal));

    public FakeHttpHandler Add(string url, string body, string contentType = "text/html", int status = 200,
        Dictionary<string, string>? headers = null, Dictionary<string, string>? contentHeaders = null, bool ignoreRange = false)
    {
        _routes[new Uri(url).AbsoluteUri] = new Route(status, body, contentType, headers, ContentHeaders: contentHeaders, IgnoreRange: ignoreRange);
        return this;
    }

    public FakeHttpHandler AddIgnoringRange(string url, string body, string contentType = "video/mp4") =>
        Add(url, body, contentType, ignoreRange: true);

    public FakeHttpHandler AddFile(string url, string fixture, string contentType = "application/json") =>
        Add(url, Fixture.Load(fixture), contentType);

    public FakeHttpHandler AddMedia(string url, string contentType = "video/mp4", int length = 1000) =>
        Add(url, new string('x', length), contentType);

    public FakeHttpHandler AddRedirect(string from, string to, int status = 302)
    {
        _routes[new Uri(from).AbsoluteUri] = new Route(status, "", "text/plain", null, to);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_lock)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));
        }
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

        if (!_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var r))
            r = new Route(404, "not found", "text/plain");

        var resp = new HttpResponseMessage((HttpStatusCode)r.Status) { RequestMessage = request };
        if (r.Redirect is not null)
        {
            resp.Headers.Location = new Uri(r.Redirect);
            resp.Content = new StringContent("");
            return resp;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(r.Body);
        if (request.Method == HttpMethod.Head)
        {
            resp.Content = new ByteArrayContent([]);
            resp.Content.Headers.ContentLength = bytes.Length;
        }
        else if (request.Headers.Range is not null && r.Status == 200 && !r.IgnoreRange)
        {
            var range = request.Headers.Range.Ranges.SingleOrDefault();
            long start = range?.From ?? 0;
            long end = range?.To ?? bytes.Length - 1;
            if (start < 0 || start >= bytes.Length || end < start)
            {
                resp.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
                resp.Content = new ByteArrayContent([]);
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(bytes.Length);
            }
            else
            {
                int first = checked((int)start);
                int count = checked((int)(Math.Min(end, bytes.Length - 1) - start + 1));
                resp.StatusCode = HttpStatusCode.PartialContent;
                resp.Content = new ByteArrayContent(bytes.AsSpan(first, count).ToArray());
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + count - 1, bytes.Length);
            }
        }
        else
        {
            resp.Content = new ByteArrayContent(bytes);
        }
        resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(r.ContentType);
        if (r.ContentHeaders is not null)
            foreach (var kv in r.ContentHeaders)
            {
                resp.Content.Headers.Remove(kv.Key);
                if (kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && kv.Value.Length == 0) continue;
                resp.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        if (r.Headers is not null)
            foreach (var kv in r.Headers) resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        return resp;
    }
}
