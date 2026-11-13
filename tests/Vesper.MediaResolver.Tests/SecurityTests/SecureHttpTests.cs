using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public class SecureHttpTests
{
    private static SecureHttpClient Client(FakeHttpHandler h, ResolverOptions? o = null) => new(o ?? new ResolverOptions(), h);

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

    [Fact]
    public async Task Cookie_IsNotForwardedToAnotherHostAfterRedirect_ButIsToSubdomains()
    {
        var h = new FakeHttpHandler()
            .AddRedirect("https://example.com/start", "https://cdn.other-site.net/file")
            .Add("https://cdn.other-site.net/file", "ok", "text/plain")
            .AddRedirect("https://example.com/start2", "https://media.example.com/file")
            .Add("https://media.example.com/file", "ok", "text/plain");
        var ctx = new MediaRequestContext
        {
            Referer = "https://example.com/watch?id=secret", CookieHeader = "sid=1",
            CredentialOrigin = new Uri("https://example.com/"), CredentialHosts = ["example.com"]
        };
        var c = Client(h);

        await c.GetStringAsync(new Uri("https://example.com/start"), ctx, default);
        Assert.True(h.Requests[0].Headers.ContainsKey("Cookie"));
        Assert.False(h.Requests[1].Headers.ContainsKey("Cookie"));
        Assert.Equal("https://example.com/", h.Requests[1].Headers["Referer"]);
        Assert.DoesNotContain("secret", h.Requests[1].Headers["Referer"]);

        await c.GetStringAsync(new Uri("https://example.com/start2"), ctx, default);
        Assert.True(h.Requests[3].Headers.ContainsKey("Cookie"));        // subdomain of the credential host
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
}
