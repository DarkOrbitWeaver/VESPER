using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public class SecureHttpTests
{
    private static SecureHttpClient Client(HttpMessageHandler h, ResolverOptions? o = null) => new(o ?? new ResolverOptions(), h);

    private static async Task<MediaError> Error(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<MediaResolverException>(action);
        return ex.Error;
    }

    [Fact]
    public async Task RedirectToPrivateAddress_IsBlockedBeforeAnyRequestIsMade()
    {
        var h = new FakeHttpHandler().AddRedirect("https://a.example.com/x", "https://127.0.0.1/admin");
        var e = await Error(() => Client(h).GetStringAsync(new Uri("https://a.example.com/x"), null, default));
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, e.Code);
        Assert.DoesNotContain(h.Requests, r => r.Url.Host == "127.0.0.1");
    }

    [Fact]
    public async Task RedirectToLocalFileScheme_IsBlocked()
    {
        var h = new FakeHttpHandler().AddRedirect("https://a.example.com/x", "file:///C:/Windows/win.ini");
        var e = await Error(() => Client(h).GetStringAsync(new Uri("https://a.example.com/x"), null, default));
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, e.Code);
    }

    [Fact]
    public async Task HttpsToHttpDowngrade_IsBlocked()
    {
        var h = new FakeHttpHandler().AddRedirect("https://a.example.com/x", "http://b.example.com/y");
        var e = await Error(() => Client(h).GetStringAsync(new Uri("https://a.example.com/x"), null, default));
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, e.Code);
    }

    [Fact]
    public async Task RedirectLoop_IsDetected()
    {
        var h = new FakeHttpHandler()
            .AddRedirect("https://a.example.com/1", "https://a.example.com/2")
            .AddRedirect("https://a.example.com/2", "https://a.example.com/1");
        var e = await Error(() => Client(h).GetStringAsync(new Uri("https://a.example.com/1"), null, default));
        Assert.Equal(MediaErrorCode.NetworkFailure, e.Code);
        Assert.Contains("loop", e.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LongRedirectChain_IsCapped()
    {
        var h = new FakeHttpHandler();
        for (int i = 0; i < 12; i++) h.AddRedirect($"https://a.example.com/{i}", $"https://a.example.com/{i + 1}");
        var e = await Error(() => Client(h).GetStringAsync(new Uri("https://a.example.com/0"), null, default));
        Assert.Equal(MediaErrorCode.NetworkFailure, e.Code);
        Assert.True(h.Requests.Count <= 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CookieFollowsOnlyExplicitlyAllowedRedirectHosts(bool allowSubdomain)
    {
        var h = new FakeHttpHandler()
            .AddRedirect("https://example.com/start", "https://cdn.other-site.net/file")
            .Add("https://cdn.other-site.net/file", "ok", "text/plain")
            .AddRedirect("https://example.com/start2", "https://media.example.com/file")
            .Add("https://media.example.com/file", "ok", "text/plain");
        var ctx = new MediaRequestContext
        {
            Referer = "https://example.com/watch?id=secret", CookieHeader = "sid=1",
            CredentialOrigin = new Uri("https://example.com/"),
            CredentialHosts = allowSubdomain ? ["example.com", "media.example.com"] : ["example.com"]
        };
        var c = Client(h);

        await c.GetStringAsync(new Uri("https://example.com/start"), ctx, default);
        Assert.True(h.Requests[0].Headers.ContainsKey("Cookie"));
        Assert.False(h.Requests[1].Headers.ContainsKey("Cookie"));
        Assert.Equal("https://example.com/", h.Requests[1].Headers["Referer"]);
        Assert.DoesNotContain("secret", h.Requests[1].Headers["Referer"]);

        await c.GetStringAsync(new Uri("https://example.com/start2"), ctx, default);
        Assert.Equal(allowSubdomain, h.Requests[3].Headers.ContainsKey("Cookie"));
        Assert.Equal("https://example.com/", h.Requests[3].Headers["Referer"]);
    }

    [Fact]
    public async Task CredentialsAreNotForwardedAcrossSchemeOrPort()
    {
        var h = new FakeHttpHandler()
            .Add("http://example.com/video", "ok", "text/plain")
            .Add("https://example.com:8443/video", "ok", "text/plain");
        var context = new MediaRequestContext
        {
            Referer = "https://example.com/watch?token=secret", CookieHeader = "sid=1",
            SensitiveHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            CredentialOrigin = new Uri("https://example.com/"), CredentialHosts = ["example.com"]
        };
        var client = Client(h);

        await client.GetStringAsync(new Uri("http://example.com/video"), context, default);
        await client.GetStringAsync(new Uri("https://example.com:8443/video"), context, default);

        Assert.DoesNotContain(h.Requests[0].Headers.Keys, key => key is "Cookie" or "Authorization" or "Referer");
        Assert.DoesNotContain(h.Requests[1].Headers.Keys, key => key is "Cookie" or "Authorization");
        Assert.Equal("https://example.com/", h.Requests[1].Headers["Referer"]);
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedByDeclaredLength()
    {
        var h = new FakeHttpHandler().Add("https://a.example.com/big", new string('x', 5000), "text/html");
        var o = new ResolverOptions { MaxPageBytes = 1000 };
        var e = await Error(() => Client(h, o).GetStringAsync(new Uri("https://a.example.com/big"), null, default));
        Assert.Equal(MediaErrorCode.ResponseTooLarge, e.Code);
    }

    [Fact]
    public async Task BinaryContent_IsNeverReadAsText()
    {
        var h = new FakeHttpHandler().AddMedia("https://a.example.com/v.mp4", "video/mp4", 50_000);
        var r = await Client(h).GetStringAsync(new Uri("https://a.example.com/v.mp4"), null, default);
        Assert.False(r.IsText);
        Assert.Equal("", r.Content);
    }

    [Theory]
    [InlineData(404, MediaErrorCode.VideoUnavailable)]
    [InlineData(410, MediaErrorCode.VideoUnavailable)]
    [InlineData(401, MediaErrorCode.LoginRequired)]
    [InlineData(403, MediaErrorCode.AuthorizationRequired)]
    [InlineData(429, MediaErrorCode.RateLimited)]
    [InlineData(451, MediaErrorCode.RegionRestricted)]
    [InlineData(503, MediaErrorCode.NetworkFailure)]
    public async Task HttpStatuses_MapToTypedErrors(int status, MediaErrorCode code)
    {
        var h = new FakeHttpHandler().Add("https://a.example.com/x", "denied", "text/plain", status);
        var e = await Error(async () => (await Client(h).GetStringAsync(new Uri("https://a.example.com/x"), null, default)).EnsureSuccess());
        Assert.Equal(code, e.Code);
        Assert.Equal(status is 429 or 503, e.IsRetryable);
    }

    [Theory]
    [InlineData(401, MediaErrorCode.LoginRequired)]
    [InlineData(403, MediaErrorCode.AuthorizationRequired)]
    [InlineData(404, MediaErrorCode.VideoUnavailable)]
    [InlineData(429, MediaErrorCode.RateLimited)]
    [InlineData(451, MediaErrorCode.RegionRestricted)]
    [InlineData(503, MediaErrorCode.NetworkFailure)]
    public async Task OversizedErrorBody_DoesNotHideHttpStatus(int status, MediaErrorCode expected)
    {
        var url = new Uri("https://a.example.com/error");
        foreach (bool post in new[] { false, true })
        {
            var h = new FakeHttpHandler().Add(url.AbsoluteUri, new string('x', 5000), "text/html", status);
            using var client = Client(h, new ResolverOptions { MaxErrorBodyBytes = 1000 });
            var error = await Error(async () =>
            {
                var response = post
                    ? await client.PostJsonAsync(url, "{}", null, null, default)
                    : await client.GetStringAsync(url, null, default);
                Assert.Equal(status, response.StatusCode);
                Assert.Empty(response.Content);
                response.EnsureSuccess();
            });
            Assert.Equal(expected, error.Code);
        }
    }

    [Fact]
    public async Task Timeout_BecomesRetryableNetworkError_NotRawCancellation()
    {
        var h = new FakeHttpHandler { Delay = TimeSpan.FromSeconds(5) }.Add("https://a.example.com/slow", "x");
        var o = new ResolverOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        var e = await Error(() => Client(h, o).GetStringAsync(new Uri("https://a.example.com/slow"), null, default));
        Assert.Equal(MediaErrorCode.NetworkFailure, e.Code);
        Assert.True(e.IsRetryable);
    }

    [Fact]
    public async Task UserCancellation_PropagatesAsCancellation()
    {
        var h = new FakeHttpHandler { Delay = TimeSpan.FromSeconds(5) }.Add("https://a.example.com/slow", "x");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(h).GetStringAsync(new Uri("https://a.example.com/slow"), null, cts.Token));
    }

    [Fact]
    public async Task Probe_ReportsTypeAndLength_AndRejectsMissingFiles()
    {
        var h = new FakeHttpHandler().AddMedia("https://a.example.com/v.mp4", "video/mp4", 4321);
        var c = Client(h);

        var ok = await c.ProbeAsync(new Uri("https://a.example.com/v.mp4"), null, default);
        Assert.True(ok.IsSuccess);
        Assert.Equal("video/mp4", ok.ContentType);
        Assert.Equal(4321, ok.ContentLength);

        var missing = await c.ProbeAsync(new Uri("https://a.example.com/none.mp4"), null, default);
        Assert.False(missing.IsSuccess);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Probe_does_not_follow_a_refused_head_with_a_get(int status)
    {
        var url = new Uri("https://a.example.com/v.mp4");
        var handler = new FakeHttpHandler().Add(url.AbsoluteUri, "denied", "text/plain", status);
        using var client = Client(handler);

        if (status == 429)
        {
            MediaError error = await Error(async () => { await client.ProbeAsync(url, null, default); });
            Assert.Equal(MediaErrorCode.RateLimited, error.Code);
        }
        else Assert.Equal(status, (await client.ProbeAsync(url, null, default)).StatusCode);
        Assert.Equal(HttpMethod.Head, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task ExactRangeProbe_RequiresMatchingContentRangeAndOneReadableByte()
    {
        const string url = "https://a.example.com/v.mp4";
        var validHandler = new FakeHttpHandler().AddMedia(url, "video/mp4", 64);
        var valid = await Client(validHandler).ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.True(valid.IsValid, valid.Detail);
        Assert.Equal(206, valid.StatusCode);
        Assert.Contains("bytes 63-63/64", valid.Detail);
        Assert.Equal("bytes=63-63", validHandler.Requests.Single().Headers["Range"]);

        var ignoredHandler = new FakeHttpHandler().AddIgnoringRange(url, new string('x', 64));
        var ignored = await Client(ignoredHandler).ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.False(ignored.IsValid);
        Assert.Equal(200, ignored.StatusCode);

        var wrongRangeHandler = new FakeHttpHandler().Add(url, "x", "video/mp4", status: 206,
            contentHeaders: new Dictionary<string, string> { ["Content-Range"] = "bytes 0-0/64" });
        var wrongRange = await Client(wrongRangeHandler).ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.False(wrongRange.IsValid);
        Assert.Contains("Content-Range", wrongRange.Detail);

        var missingRangeHandler = new FakeHttpHandler().Add(url, "x", "video/mp4", status: 206);
        var missingRange = await Client(missingRangeHandler).ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.False(missingRange.IsValid);

        var oversizedBodyHandler = new FakeHttpHandler().Add(url, "xx", "video/mp4", status: 206,
            contentHeaders: new Dictionary<string, string> { ["Content-Range"] = "bytes 63-63/64" });
        var oversizedBody = await Client(oversizedBodyHandler).ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.False(oversizedBody.IsValid);
        Assert.Contains("Content-Length was 2", oversizedBody.Detail);

        var chunkedValid = await Client(new NoLengthRangeHandler("bytes 63-63/64", "x"))
            .ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.True(chunkedValid.IsValid, chunkedValid.Detail);
        var chunkedExtra = await Client(new NoLengthRangeHandler("bytes 63-63/64", "xy"))
            .ProbeExactRangeAsync(new Uri(url), null, 63, default);
        Assert.False(chunkedExtra.IsValid);
    }

    private sealed class NoLengthRangeHandler(string contentRange, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new NoLengthContent(contentRange, Encoding.ASCII.GetBytes(body))
            });
    }

    private sealed class NoLengthContent : HttpContent
    {
        private readonly byte[] _body;

        internal NoLengthContent(string contentRange, byte[] body)
        {
            _body = body;
            Headers.ContentRange = ContentRangeHeaderValue.Parse(contentRange);
            Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_body).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_body, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
