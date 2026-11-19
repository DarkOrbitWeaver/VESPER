using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public sealed class SecureHttpPostRedirectTests
{
    [Fact]
    public async Task CrossOriginRedirect_DoesNotForwardPostBodyOrHeaders()
    {
        const string source = "https://api.example.com/player";
        const string target = "https://other.example.net/collect";
        var handler = new FakeHttpHandler()
            .AddRedirect(source, target)
            .Add(target, "accepted", "text/plain");
        using var client = new SecureHttpClient(new ResolverOptions(), handler);

        var error = await Assert.ThrowsAsync<MediaResolverException>(() => client.PostJsonAsync(
            new Uri(source), "{\"visitor\":\"private-value\"}",
            new MediaRequestContext { UserAgent = "test-agent" },
            new Dictionary<string, string> { ["X-Visitor"] = "private-value" },
            CancellationToken.None));

        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, error.Error.Code);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("private-value", handler.Requests[0].Body);
        Assert.DoesNotContain(handler.Requests, request => request.Url.AbsoluteUri == target);
    }

    [Fact]
    public async Task SameOriginPostRedirect_StillFollowsWithBody()
    {
        const string source = "https://api.example.com/start";
        const string target = "https://api.example.com/finish";
        const string body = "{\"videoId\":\"fixture\"}";
        var handler = new FakeHttpHandler()
            .AddRedirect(source, target, status: 307)
            .Add(target, "{}", "application/json");
        using var client = new SecureHttpClient(new ResolverOptions(), handler);

        var response = await client.PostJsonAsync(new Uri(source), body, null, null, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(body, handler.Requests[0].Body);
        Assert.Equal(body, handler.Requests[1].Body);
    }
}
