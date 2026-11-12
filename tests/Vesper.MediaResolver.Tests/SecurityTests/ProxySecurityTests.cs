using System.Net;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.Tests.Shared;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public sealed class ProxySecurityTests
{
    private static readonly Uri Destination = new("http://downloads.example.test:8123/file?token=sample");

    [Fact]
    public async Task ProxyReceivesPinnedAddressAndResolverReturnsOriginalUrl()
    {
        await using var proxy = new RecordingProxy();
        using var client = Client(proxy.Address, [IPAddress.Parse("8.8.8.8")]);

        HttpTextResponse response = await client.GetStringAsync(Destination, null, default);
        string request = await proxy.Headers;

        Assert.Equal("ok", response.Content);
        Assert.Equal(Destination, response.FinalUrl);
        Assert.Contains("GET http://8.8.8.8:8123/file?token=sample HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains("Host: downloads.example.test:8123", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpsProxyConnectPinsAddressAndUsesOriginalTlsHostWhenAvailable()
    {
        await using var proxy = new RecordingProxy();
        using var client = Client(proxy.Address, [IPAddress.Parse("8.8.8.8")]);
        Uri destination = new("https://downloads.example.test/file");

        await Assert.ThrowsAsync<MediaResolverException>(() =>
            client.GetStringAsync(destination, null, default).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("CONNECT 8.8.8.8:443 HTTP/1.1", await proxy.Headers.WaitAsync(TimeSpan.FromSeconds(5)), StringComparison.Ordinal);
        string hello = await proxy.ClientHello.WaitAsync(TimeSpan.FromSeconds(5));
        if (hello.Length > 0) Assert.Contains("downloads.example.test", hello, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyPrivateDnsAnswerBlocksTheProxyRequest()
    {
        await using var proxy = new RecordingProxy();
        using var client = Client(proxy.Address, [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]);

        MediaResolverException error = await Assert.ThrowsAsync<MediaResolverException>(() =>
            client.GetStringAsync(Destination, null, default));

        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, error.Error.Code);
        Assert.Equal(0, proxy.Accepted);
    }

    private static SecureHttpClient Client(Uri proxy, IPAddress[] addresses) => new(new ResolverOptions
    {
        ProxyAddress = proxy,
        RequestTimeout = TimeSpan.FromSeconds(2),
        ResolveHost = (_, _) => Task.FromResult(addresses)
    });
}
