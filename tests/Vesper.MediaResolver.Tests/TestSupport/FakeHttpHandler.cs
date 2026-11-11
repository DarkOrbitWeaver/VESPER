using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Vesper.MediaResolver.Tests.TestSupport;

/// <summary>In-memory HTTP server for offline tests. Unknown URLs answer 404. Records every request (including headers).</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Route(int Status, string Body, string ContentType, IReadOnlyDictionary<string, string>? Headers = null, string? Redirect = null);

    public sealed record Recorded(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers);

    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public List<Recorded> Requests { get; } = new();
    public TimeSpan Delay { get; set; }

    public int Count(string urlContains) => Requests.Count(r => r.Url.AbsoluteUri.Contains(urlContains, StringComparison.Ordinal));

    public FakeHttpHandler Add(string url, string body, string contentType = "text/html", int status = 200, Dictionary<string, string>? headers = null)
    {
        _routes[new Uri(url).AbsoluteUri] = new Route(status, body, contentType, headers);
        return this;
    }

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
        lock (_lock)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
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
        else if (request.Headers.Range is not null && r.Status == 200)
        {
            resp.StatusCode = HttpStatusCode.PartialContent;
            resp.Content = new ByteArrayContent(bytes.Take(1).ToArray());
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, bytes.Length);
        }
        else
        {
            resp.Content = new ByteArrayContent(bytes);
        }
        resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(r.ContentType);
        if (r.Headers is not null)
            foreach (var kv in r.Headers) resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        return resp;
    }
}
