using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class ConnectionLimitTests
{
    [Fact]
    public async Task Download_reserves_its_host_slot_before_opening_the_first_response()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        byte[] expected = Payloads.Make(300 * 1024, 12);
        int requests = 0;
        app.MapGet("/file.bin", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes 0-{expected.Length - 1}/{expected.Length}";
            context.Response.Headers.ETag = "\"file\"";
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = expected.Length;
            await context.Response.Body.WriteAsync(expected, context.RequestAborted);
        });
        await app.StartAsync();

        using var harness = new Harness(tweak: options => options.MaxConnectionsPerHost = 1);
        var uri = new Uri(app.Urls.Single() + "/file.bin");
        SemaphoreSlim slot = harness.Engine.HostGate(uri.Host);
        await slot.WaitAsync();
        Guid id;
        try
        {
            id = harness.Add(uri.AbsoluteUri);
            await harness.WaitAsync(id, job => File.Exists(job.PartialPath), seconds: 5);
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref requests));
        }
        finally { slot.Release(); }

        JobSnapshot job = await harness.WaitForStateAsync(id, JobState.Completed, seconds: 10);
        Assert.Equal(1, requests);
        Assert.Equal(expected, await File.ReadAllBytesAsync(job.FinalPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol_experiment_preserves_exact_bytes_and_measures_multiplexing(bool h2)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listener => listener.Protocols = h2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
        await using var app = builder.Build();
        byte[] expected = Payloads.Make(256 * 1024, 1234);
        var connectionIds = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        int active = 0, peak = 0;
        var guard = new object();
        app.MapGet("/part", async (HttpContext context) =>
        {
            connectionIds.TryAdd(context.Connection.Id, 0);
            lock (guard) peak = Math.Max(peak, ++active);
            try
            {
                await Task.Delay(100, context.RequestAborted);
                context.Response.ContentLength = expected.Length;
                await context.Response.Body.WriteAsync(expected, context.RequestAborted);
            }
            finally { lock (guard) active--; }
        });
        await app.StartAsync();
        using var http = new HttpTransport(new EngineOptions
        {
            DataDirectory = Path.GetTempPath(), AllowPrivateNetworks = true, Proxy = ProxyMode.Direct,
            MaxConnectionsPerHost = 4, Http2Experiment = h2
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var uri = new Uri(app.Urls.Single() + "/part");
        async Task Fetch()
        {
            using var response = await http.SendAsync(uri, new RequestSpec(), timeout.Token);
            Assert.Equal(h2 ? HttpVersion.Version20 : HttpVersion.Version11, response.Version);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        await Fetch();
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Fetch()));
        Console.WriteLine($"HTTP_PROTOCOL h2={h2} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} connections={connectionIds.Count} peak={peak} bytes={8 * expected.Length}");
        Assert.InRange(peak, 2, 4);
        if (h2) Assert.Single(connectionIds);
        else Assert.InRange(connectionIds.Count, 1, 4);
    }

    [Fact]
    public async Task Protocol_experiment_shares_one_budget_across_http1_origins_on_same_host()
    {
        await using var first = new FaultServer(1024);
        await using var second = new FaultServer(1024);
        await first.StartAsync();
        await second.StartAsync();
        using var http = new HttpTransport(new EngineOptions
        {
            DataDirectory = Path.GetTempPath(), AllowPrivateNetworks = true, Proxy = ProxyMode.Direct,
            MaxConnectionsPerHost = 1, EnableAdaptiveHttp2 = true
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var held = await http.SendAsync(new Uri(first.Url("/file")), new RequestSpec(), stop.Token);
        Assert.Equal(HttpVersion.Version11, held.Version);
        var queued = http.SendAsync(new Uri(second.Url("/file")), new RequestSpec(), stop.Token);
        try
        {
            await Task.Delay(100, stop.Token);
            Assert.False(queued.IsCompleted);
            Assert.Equal(0, second.Hits("/file"));
        }
        finally { held.Dispose(); }
        using var response = await queued;
        Assert.Equal(second.V1, await response.Content.ReadAsByteArrayAsync(stop.Token));
        Assert.Equal(1, second.Hits("/file"));
    }

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
