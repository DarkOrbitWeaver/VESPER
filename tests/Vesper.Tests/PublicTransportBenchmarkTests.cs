using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class PublicTransportBenchmarkTests
{
    public sealed class PublicTransportFactAttribute : FactAttribute
    {
        public PublicTransportFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_PUBLIC_TRANSPORT_BENCHMARK") != "1")
                Skip = "Set VESPER_PUBLIC_TRANSPORT_BENCHMARK=1 for a bounded public HTTPS comparison.";
        }
    }

    [PublicTransportFact]
    [Trait("Category", "PublicTransportBenchmark")]
    public async Task Public_https_protocol_comparison_checks_identical_objects()
    {
        // Stable public text resources, not competitor binaries, accounts or
        // signed URLs. At most four GETs per origin; no retries on throttling.
        string[] addresses =
        [
            "https://www.rfc-editor.org/rfc/rfc9112.txt",
            "https://www.gnu.org/licenses/gpl-3.0.txt",
            "https://www.iana.org/assignments/http-status-codes/http-status-codes-1.csv"
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        CancellationToken ct = deadline.Token;
        var cooldown = new OriginCooldown();
        EngineOptions options(bool h2) => new()
        {
            DataDirectory = Path.GetTempPath(), EnableAdaptiveHttp2 = h2, Proxy = ProxyMode.Direct,
            MaxConnectionsPerHost = 2, ConnectTimeout = TimeSpan.FromSeconds(7), HeaderTimeout = TimeSpan.FromSeconds(10)
        };
        int verified = 0;
        foreach (string address in addresses)
        {
            var uri = new Uri(address);
            byte[]? baseline = null;
            try
            {
                using var h1 = new HttpTransport(options(false));
                using var h2 = new HttpTransport(options(true));
                for (int trial = 0; trial < 4; trial++)
                {
                    bool adaptive = trial % 2 != 0;
                    if (cooldown.RetryAt(uri) is not null) break;
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(12));
                    var watch = Stopwatch.StartNew();
                    using var response = await (adaptive ? h2 : h1).SendAsync(uri, new RequestSpec(), requestDeadline.Token);
                    double headersMs = watch.Elapsed.TotalMilliseconds;
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        cooldown.Remember(uri, DateTimeOffset.UtcNow.AddHours(1));
                        Console.WriteLine($"PUBLIC_TRANSPORT_UNAVAILABLE {uri.Host} status={(int)response.StatusCode}");
                        break;
                    }
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.True(HttpFacts.IsIdentityEncoded(response));
                    Assert.InRange(response.Content.Headers.ContentLength ?? 0, 0, 512 * 1024);
                    using var body = new MemoryStream();
                    await using var stream = await response.Content.ReadAsStreamAsync(requestDeadline.Token);
                    byte[] buffer = new byte[16 * 1024];
                    int n;
                    while ((n = await stream.ReadAsync(buffer, requestDeadline.Token)) > 0)
                    {
                        Assert.True(body.Length + n <= 512 * 1024, "Public fixture exceeded its byte budget.");
                        body.Write(buffer, 0, n);
                    }
                    byte[] bytes = body.ToArray();
                    Assert.NotEmpty(bytes);
                    if (response.Content.Headers.ContentLength is long length) Assert.Equal(length, bytes.Length);
                    baseline ??= bytes;
                    Assert.Equal(baseline, bytes);
                    Console.WriteLine("PUBLIC_TRANSPORT " + JsonSerializer.Serialize(new
                    {
                        origin = uri.Host, trial, adaptive, protocol = response.Version.ToString(),
                        headersMs, totalMs = watch.Elapsed.TotalMilliseconds, bytes = bytes.Length,
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)), sameBytes = true
                    }));
                    verified++;
                    await Task.Delay(150, ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TransientNetworkException or OperationCanceledException)
            {
                Console.WriteLine($"PUBLIC_TRANSPORT_UNAVAILABLE {uri.Host} {ex.GetType().Name}");
                if (ct.IsCancellationRequested) break;
            }
        }
        // Report coverage explicitly rather than claiming a successful live
        // comparison when no origin could be reached.
        Assert.True(verified >= 4, $"Only {verified} public objects received; insufficient comparison coverage.");
    }
}
