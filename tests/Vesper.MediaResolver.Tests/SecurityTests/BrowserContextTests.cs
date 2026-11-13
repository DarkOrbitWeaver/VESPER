using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public class BrowserContextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static BrowserDiscoveryMessage Message(BrowserAuthorization? auth = null, params BrowserMediaCandidate[] candidates) => new()
    {
        Schema = BrowserDiscoveryMessage.CurrentSchema,
        PageUrl = "https://example.com/watch/42",
        Title = "Page title",
        Candidates = candidates.ToList(),
        Authorization = auth
    };

    private static ValidatedBrowserContext Validate(BrowserDiscoveryMessage m) =>
        BrowserMessageValidator.Validate(m, Now, new ResolverOptions());

    private static MediaErrorCode CodeOf(BrowserDiscoveryMessage m) =>
        Assert.Throws<MediaResolverException>(() => Validate(m)).Error.Code;

    [Fact]
    public void MinimalMessage_GivesRefererOnly_NoCredentials()
    {
        var v = Validate(Message());
        Assert.Equal("https://example.com/watch/42", v.Context.Referer);
        Assert.Null(v.Context.CookieHeader);
        Assert.False(v.Context.HasCredentials);
        Assert.Equal("Page title", v.Title);
    }

    [Fact]
    public void Authorization_WithoutConsent_IsIgnored()
    {
        var v = Validate(Message(new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = false, CookieHeader = "sid=1" }));
        Assert.Null(v.Context.CookieHeader);
        Assert.Contains(v.Warnings, w => w.Contains("consent"));
    }

    [Fact]
    public void Authorization_WithConsent_IsScopedToPageHost()
    {
        var v = Validate(Message(new BrowserAuthorization
        {
            Origin = "https://example.com", ConsentGranted = true, CookieHeader = "sid=1",
            Headers = new() { ["Authorization"] = "Bearer t", ["X-Evil"] = "1", ["Accept-Language"] = "de" },
            AllowedHosts = ["media.example.com", "cdn.evil.net", "example.com.evil.net"]
        }));

        Assert.Equal("sid=1", v.Context.CookieHeader);
        Assert.Equal("Bearer t", v.Context.SensitiveHeaders["Authorization"]);
        Assert.False(v.Context.SensitiveHeaders.ContainsKey("X-Evil"));
        Assert.Equal("de", v.Context.AcceptLanguage);
        Assert.True(new[] { "example.com", "media.example.com" }.SequenceEqual(v.Context.CredentialHosts.OrderBy(x => x)));
        Assert.Equal(new Uri("https://example.com/"), v.Context.CredentialOrigin);
        Assert.Contains(v.Warnings, w => w.Contains("X-Evil"));
        Assert.Contains(v.Warnings, w => w.Contains("cdn.evil.net"));
    }

    [Fact]
    public void CrossOriginReferrerIsReducedAndDowngradesGetNoReferrer()
    {
        var context = Validate(Message()).Context;

        Assert.Equal("https://example.com/watch/42", context.HeadersFor(new Uri("https://example.com/media"))
            .Single(header => header.Key == "Referer").Value);
        Assert.Equal("https://example.com/", context.HeadersFor(new Uri("https://cdn.example.net/media"))
            .Single(header => header.Key == "Referer").Value);
        Assert.DoesNotContain(context.HeadersFor(new Uri("http://example.com/media")), header => header.Key == "Referer");
    }

    [Fact]
    public void BrowserCredentialsStayOnTheIssuingSchemeAndPort()
    {
        var context = Validate(Message(new BrowserAuthorization
        {
            Origin = "https://example.com", ConsentGranted = true, CookieHeader = "sid=1",
            AllowedHosts = ["media.example.com"]
        })).Context;

        Assert.Contains(context.HeadersFor(new Uri("https://media.example.com/video")), header => header.Key == "Cookie");
        Assert.DoesNotContain(context.HeadersFor(new Uri("http://example.com/video")), header => header.Key == "Cookie");
        Assert.DoesNotContain(context.HeadersFor(new Uri("https://example.com:8443/video")), header => header.Key == "Cookie");
        Assert.DoesNotContain(context.HeadersFor(new Uri("https://cdn.example.net/video")), header => header.Key == "Cookie");
    }

    [Fact]
    public void Authorization_ForAnotherOrigin_RejectsTheWholeMessage() =>
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy,
            CodeOf(Message(new BrowserAuthorization { Origin = "https://evil.example.net", ConsentGranted = true, CookieHeader = "sid=1" })));

    [Fact]
    public void HeaderInjection_IsRejected()
    {
        Assert.Equal(MediaErrorCode.InvalidInput,
            CodeOf(Message(new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = true, CookieHeader = "a=b\r\nX-Injected: 1" })));
        Assert.Equal(MediaErrorCode.InvalidInput,
            CodeOf(Message(new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = true, Headers = new() { ["Authorization"] = "x\nEvil: 1" } })));
    }

    [Fact]
    public void ExpiredAuthorization_IsRejected() =>
        Assert.Equal(MediaErrorCode.SourceExpired,
            CodeOf(Message(new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = true, ExpiresAt = Now.AddMinutes(-1) })));

    [Fact]
    public void BadSchemaOrPageUrl_AreRejected()
    {
        Assert.Equal(MediaErrorCode.InvalidInput, CodeOf(new BrowserDiscoveryMessage { Schema = "other/9", PageUrl = "https://example.com/" }));
        Assert.Equal(MediaErrorCode.InvalidInput, CodeOf(new BrowserDiscoveryMessage { Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = "file:///C:/x" }));
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, CodeOf(new BrowserDiscoveryMessage { Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = "http://192.168.0.10/x" }));
    }

    [Fact]
    public void Candidates_AreFilteredClassifiedAndBounded()
    {
        var m = Message(null,
            new BrowserMediaCandidate { Url = "https://cdn.example.net/a.m3u8" },
            new BrowserMediaCandidate { Url = "https://cdn.example.net/b", Kind = "dash" },
            new BrowserMediaCandidate { Url = "file:///C:/Windows/win.ini" },
            new BrowserMediaCandidate { Url = "http://169.254.169.254/latest" },
            new BrowserMediaCandidate { Url = "https://cdn.example.net/c.mp4", Width = 99999, Height = 720, Label = "x\ny" });
        var v = Validate(m);

        Assert.Equal(3, v.Candidates.Count);
        Assert.Equal(CandidateKind.Hls, v.Candidates[0].Kind);
        Assert.Equal(CandidateKind.Dash, v.Candidates[1].Kind);
        Assert.Equal(CandidateKind.Direct, v.Candidates[2].Kind);
        Assert.Null(v.Candidates[2].Width);                    // absurd dimension dropped
        Assert.Equal("xy", v.Candidates[2].Label);             // control characters stripped
        Assert.Equal(2, v.Warnings.Count(w => w.Contains("Dropped candidate")));

        var many = Message(null, Enumerable.Range(0, 200).Select(i => new BrowserMediaCandidate { Url = $"https://cdn.example.net/{i}.mp4" }).ToArray());
        Assert.Equal(64, Validate(many).Candidates.Count);
    }

    [Fact]
    public void Parser_AcceptsDocumentedShape_AndRejectsGarbage()
    {
        const string json = """
        {"schema":"vesper.discovery/1","pageUrl":"https://example.com/watch/42","title":"T",
         "candidates":[{"url":"https://cdn.example.net/a.m3u8","kind":"hls","height":720}],
         "authorization":{"origin":"https://example.com","consentGranted":true,"cookieHeader":"sid=1"}}
        """;
        Assert.True(BrowserMessageParser.TryParse(json, out var msg, out _));
        Assert.Equal("hls", msg!.Candidates[0].Kind);
        Assert.True(msg.Authorization!.ConsentGranted);

        Assert.False(BrowserMessageParser.TryParse("{not json", out _, out var e1));
        Assert.Equal(MediaErrorCode.InvalidInput, e1!.Code);
        Assert.False(BrowserMessageParser.TryParse("{\"schema\":\"vesper.discovery/1\"}", out _, out var e2));   // pageUrl is required
        Assert.Equal(MediaErrorCode.InvalidInput, e2!.Code);
        Assert.False(BrowserMessageParser.TryParse(new string('x', BrowserMessageParser.MaxMessageChars + 1), out _, out var e3));
        Assert.Equal(MediaErrorCode.ResponseTooLarge, e3!.Code);
        Assert.False(BrowserMessageParser.TryParse("", out _, out _));
    }

    [Fact]
    public async Task EndToEnd_CookieReachesOnlyThePageHost()
    {
        var h = new FakeHttpHandler()
            .AddMedia("https://example.com/own.mp4", "video/mp4", 2000)
            .AddMedia("https://cdn.other-site.net/third.mp4", "video/mp4", 3000);
        using var resolver = T.Resolver(h);

        var msg = Message(new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = true, CookieHeader = "sid=SECRET" },
            new BrowserMediaCandidate { Url = "https://example.com/own.mp4", Height = 480 },
            new BrowserMediaCandidate { Url = "https://cdn.other-site.net/third.mp4", Height = 720 });

        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = new Uri(msg.PageUrl), Browser = msg }, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        Assert.All(h.Requests.Where(r => r.Url.Host == "example.com"), r => Assert.True(r.Headers.ContainsKey("Cookie")));
        Assert.All(h.Requests.Where(r => r.Url.Host == "cdn.other-site.net"), r => Assert.False(r.Headers.ContainsKey("Cookie")));
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.Equal(2, result.Item!.Representations.Count);
        Assert.Equal("Page title", result.Item.Title);
    }
}
