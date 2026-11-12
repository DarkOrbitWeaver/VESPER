using System.Net;
using Vesper.Engine;
using Vesper.Shared;
using Vesper.Tests.Shared;

namespace Vesper.Tests;

public sealed class ProxySecurityTests
{
    private static readonly Uri Destination = new("http://downloads.example.test:8123/file?token=sample");

    [Fact]
    public async Task ProxyReceivesPinnedAddressAndOriginalHostAndResponseUrl()
    {
        await using var proxy = new RecordingProxy();
        using var transport = new HttpTransport(Options(proxy.Address, [IPAddress.Parse("8.8.8.8")]));

        using var response = await transport.SendAsync(Destination, new RequestSpec(), default);
        string request = await proxy.Headers;

        Assert.Contains("GET http://8.8.8.8:8123/file?token=sample HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains("Host: downloads.example.test:8123", request, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Destination, response.RequestMessage?.RequestUri);
    }

    [Fact]
    public async Task HttpsProxyConnectPinsAddressAndUsesOriginalTlsHostWhenAvailable()
    {
        await using var proxy = new RecordingProxy();
        using var transport = new HttpTransport(Options(proxy.Address, [IPAddress.Parse("8.8.8.8")]));
        Uri destination = new("https://downloads.example.test/file");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            transport.SendAsync(destination, new RequestSpec(), default).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("CONNECT 8.8.8.8:443 HTTP/1.1", await proxy.Headers.WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.Ordinal);
        string hello = await proxy.ClientHello.WaitAsync(TimeSpan.FromSeconds(5));
        if (hello.Length > 0) Assert.Contains("downloads.example.test", hello, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyPrivateDnsAnswerBlocksTheProxyRequest()
    {
        await using var proxy = new RecordingProxy();
        using var transport = new HttpTransport(Options(proxy.Address,
            [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]));

        await Assert.ThrowsAsync<BlockedAddressException>(() => transport.SendAsync(Destination, new RequestSpec(), default));

        Assert.Equal(0, proxy.Accepted);
    }

    [Fact]
    public void ProxyBypassChecksUseTheOriginalUriAfterPinning()
    {
        Uri proxyAddress = new("http://proxy.example.test:8080");
        var inner = new RecordingWebProxy(proxyAddress);
        var proxy = new OriginAwareProxy(inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, Destination);
        proxy.PinRequest(request, Destination, IPAddress.Parse("8.8.8.8"));
        Uri pinned = request.RequestUri!;

        Assert.Equal("8.8.8.8", pinned.Host);
        Assert.Equal(Destination.Authority, request.Headers.Host);
        Assert.True(proxy.IsBypassed(pinned));
        Assert.Equal(Destination, inner.LastDestination);
        Assert.Equal(proxyAddress, proxy.GetProxy(pinned));
        Assert.Equal(Destination, inner.LastDestination);
    }

    private static EngineOptions Options(Uri proxy, IPAddress[] addresses) => new()
    {
        DataDirectory = Path.GetTempPath(),
        Proxy = ProxyMode.Custom,
        ProxyAddress = proxy.AbsoluteUri,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        HeaderTimeout = TimeSpan.FromSeconds(2),
        ResolveHost = (_, _) => Task.FromResult(addresses)
    };

    private sealed class RecordingWebProxy(Uri proxyAddress) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        internal Uri? LastDestination { get; private set; }

        public Uri GetProxy(Uri destination)
        {
            LastDestination = destination;
            return proxyAddress;
        }

        public bool IsBypassed(Uri host)
        {
            LastDestination = host;
            return host.Host == Destination.Host;
        }
    }
}
