using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class ConnectionLimitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Shared_transport_respects_small_per_host_limits(int limit)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        int active = 0, peak = 0;
        var gate = new object();
        app.MapGet("/part", async (HttpContext context) =>
        {
            lock (gate) peak = Math.Max(peak, ++active);
            try { await Task.Delay(50, context.RequestAborted); }
            finally { lock (gate) active--; }
            await context.Response.WriteAsync("part");
        });
        await app.StartAsync();
        using var http = new HttpTransport(new EngineOptions
            { DataDirectory = Path.GetTempPath(), AllowPrivateNetworks = true, Proxy = ProxyMode.Direct, MaxConnectionsPerHost = limit });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var uri = new Uri(app.Urls.Single() + "/part");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await http.SendAsync(uri, new RequestSpec(), timeout.Token);
            Assert.Equal("part", await response.Content.ReadAsStringAsync(timeout.Token));
        }));
        Assert.InRange(peak, 1, limit);
    }
}