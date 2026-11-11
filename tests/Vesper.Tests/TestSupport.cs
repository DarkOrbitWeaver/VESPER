using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vesper.Engine;

namespace Vesper.Tests;

internal static class Payloads
{
    /// <summary>Deterministic pseudo-random bytes: the known-good fixture every final file is compared with.</summary>
    public static byte[] Make(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Same size and same leading bytes, different tail: a "changed source" that a header check can miss.</summary>
    public static byte[] WithChangedTail(byte[] original, int tail = 100)
    {
        byte[] copy = [.. original];
        for (int i = copy.Length - tail; i < copy.Length; i++) copy[i] ^= 0x5A;
        return copy;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>
/// A deterministic local HTTP server whose misbehaviour is selected by path. Each instance keeps its own
/// request counters, so a test controls exactly which request fails.
/// </summary>
internal sealed class FaultServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _hits = new();
    private static readonly DateTimeOffset Modified = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public byte[] V1 { get; }
    public byte[] V2 { get; }
    public string BaseUrl { get; private set; } = "";
    public int Hits(string path) => _hits.GetValueOrDefault(path);
    public ConcurrentQueue<string> RangeHeaders { get; } = new();

    public FaultServer(int size = 2 * 1024 * 1024)
    {
        V1 = Payloads.Make(size, 1);
        V2 = Payloads.WithChangedTail(V1);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Run(Handle);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        BaseUrl = _app.Urls.First().TrimEnd('/');
    }

    public string Url(string path) => BaseUrl + path;

    private async Task Handle(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? "/";
        int hit = _hits.AddOrUpdate(path, 1, (_, n) => n + 1);
        if (ctx.Request.Headers.TryGetValue("Range", out var range)) RangeHeaders.Enqueue(path + " " + range);
        HttpResponse res = ctx.Response;
        if (path.StartsWith("/hls/")) { await HandleHls(ctx, path); return; }

        switch (path)
        {
            case "/file": await Serve(ctx, V1, "\"p1\"", Modified); break;
            case "/v2": await Serve(ctx, V2, "\"p2\"", Modified); break;
            case "/noetag": await Serve(ctx, V1, null, null); break;
            case "/lmonly": await Serve(ctx, V1, null, Modified); break;
            case "/norange": await Serve(ctx, V1, "\"p1\"", Modified, ranges: false); break;
            case "/truncate-once": await Serve(ctx, V1, "\"p1\"", Modified, truncate: hit == 1 ? 0.6 : null); break;
            case "/norange-truncate-once": await Serve(ctx, V1, "\"p1\"", Modified, ranges: false, truncate: hit == 1 ? 0.6 : null); break;
            case "/expires-after-truncate":
                if (ctx.Request.Query["token"] == "fresh") await Serve(ctx, V1, "\"p1\"", Modified);
                else if (hit == 1) await Serve(ctx, V1, "\"p1\"", Modified, truncate: 0.6);
                else res.StatusCode = 403;
                break;
            case "/changes-after-truncate":
                if (hit == 1) await Serve(ctx, V1, "\"p1\"", Modified, truncate: 0.6);
                else await Serve(ctx, V2, "\"p2\"", Modified);
                break;
            case "/flaky503":
                if (hit <= 2) { res.StatusCode = 503; res.Headers["Retry-After"] = "1"; }
                else await Serve(ctx, V1, "\"p1\"", Modified);
                break;
            case "/loop-a": res.StatusCode = 302; res.Headers.Location = "/loop-b"; break;
            case "/loop-b": res.StatusCode = 302; res.Headers.Location = "/loop-a"; break;
            case "/redirect": res.StatusCode = 302; res.Headers.Location = "/file"; break;
            case "/empty": // a zero-byte file whose server rejects any Range request
                if (ctx.Request.Headers.ContainsKey("Range")) res.StatusCode = 416;
                else { res.ContentLength = 0; res.ContentType = "application/octet-stream"; }
                break;
            case "/gone": res.StatusCode = 410; break;
            case "/notfound": res.StatusCode = 404; break;
            case "/html":
                res.ContentType = "text/html";
                await res.WriteAsync("<html>Please sign in to the hotel Wi-Fi</html>");
                break;
            case "/gzip":
                res.Headers.ContentEncoding = "gzip";
                using (var packed = new MemoryStream())
                {
                    await using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) await gz.WriteAsync(V1);
                    res.ContentLength = packed.Length;
                    await res.Body.WriteAsync(packed.ToArray());
                }
                break;
            case "/chunked": // no Content-Length: the server streams until it is done
                res.ContentType = "application/octet-stream";
                for (int offset = 0; offset < V1.Length; offset += 32 * 1024)
                {
                    await res.Body.WriteAsync(V1.AsMemory(offset, Math.Min(32 * 1024, V1.Length - offset)));
                    await res.Body.FlushAsync();
                }
                break;
            case "/disposition":
                res.Headers.ContentDisposition = "attachment; filename=\"..\\..\\evil.txt\"";
                await Serve(ctx, V1, "\"p1\"", Modified);
                break;
            case "/badrange": // honest about the first block, then answers every later range from the wrong offset
                await Serve(ctx, V1, "\"p1\"", Modified, wrongStartAfterZero: true);
                break;
            case "/slow": await Serve(ctx, V1, "\"p1\"", Modified, delayPerChunkMs: 15); break;
            default: res.StatusCode = 404; break;
        }
    }

    // ---- HLS fixtures: five 100 kB segments; the expected output is their concatenation --------------------------------

    public const int SegmentCount = 5;
    public static byte[] HlsSegment(int index) => Payloads.Make(100_000, 100 + index);
    public static byte[] HlsExpected() => Enumerable.Range(0, SegmentCount).SelectMany(HlsSegment).ToArray();
    public static readonly byte[] HlsKey = Payloads.Make(16, 77);
    public static readonly byte[] HlsIv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private async Task HandleHls(HttpContext ctx, string path)
    {
        HttpResponse res = ctx.Response;
        async Task Text(string body)
        {
            res.ContentType = "application/vnd.apple.mpegurl";
            await res.WriteAsync(body);
        }
        string Playlist(string header, bool endList = true)
        {
            var lines = new List<string> { "#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:4", "#EXT-X-MEDIA-SEQUENCE:0" };
            if (header.Length > 0) lines.Add(header);
            for (int i = 0; i < SegmentCount; i++) { lines.Add("#EXTINF:4.0,"); lines.Add($"s{i}.ts"); }
            if (endList) lines.Add("#EXT-X-ENDLIST");
            return string.Join('\n', lines) + "\n";
        }

        switch (path)
        {
            case "/hls/master.m3u8":
                await Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360\nlow/index.m3u8\n" +
                           "#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080\nplain/index.m3u8\n");
                return;
            case "/hls/plain/index.m3u8": await Text(Playlist("")); return;
            case "/hls/range-bad/index.m3u8":
                await Text("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\n#EXT-X-BYTERANGE:100@50\ns0.ts\n#EXT-X-ENDLIST\n");
                return;
            case "/hls/range-bad/s0.ts":
                // A server claiming to return a byte range from the wrong offset.
                res.StatusCode = 206;
                res.Headers.ContentRange = "bytes 0-99/100000";
                res.ContentLength = 100;
                await res.Body.WriteAsync(HlsSegment(0).AsMemory(0, 100));
                return;

            case "/hls/low/index.m3u8": await Text("#EXTM3U\n#EXTINF:4.0,\nWRONG-QUALITY.ts\n#EXT-X-ENDLIST\n"); return;
            case "/hls/enc/index.m3u8": await Text(Playlist("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x" + Convert.ToHexString(HlsIv))); return;
            case "/hls/drm/index.m3u8": await Text(Playlist("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")); return;
            case "/hls/live/index.m3u8": await Text(Playlist("", endList: false)); return;
            case "/hls/enc/key.bin": res.ContentType = "application/octet-stream"; await res.Body.WriteAsync(HlsKey); return;
        }

        string[] parts = path.Split('/');                       // /hls/<kind>/s<i>.ts
        if (parts.Length == 4 && parts[3].StartsWith('s') && int.TryParse(parts[3][1..^3], out int index) && index < SegmentCount)
        {
            byte[] data = HlsSegment(index);
            if (parts[2] == "enc")
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = HlsKey;
                data = aes.EncryptCbc(data, HlsIv, System.Security.Cryptography.PaddingMode.PKCS7);
            }
            res.ContentType = "video/mp2t";
            res.ContentLength = data.Length;
            await res.Body.WriteAsync(data);
            return;
        }
        res.StatusCode = 404;
    }

    private static async Task Serve(HttpContext ctx, byte[] data, string? etag, DateTimeOffset? modified,
        bool ranges = true, double? truncate = null, bool wrongStartAfterZero = false, int delayPerChunkMs = 0)
    {
        HttpResponse res = ctx.Response;
        long start = 0, end = data.Length - 1;
        bool partial = false;

        if (ranges && ctx.Request.Headers.TryGetValue("Range", out var header) && TryParseRange(header.ToString(), data.Length, out long s, out long e))
        {
            string? ifRange = ctx.Request.Headers["If-Range"].FirstOrDefault();
            bool ifRangeOk = ifRange is null || ifRange == etag || (modified is not null && ifRange == modified.Value.ToString("R"));
            if (ifRangeOk) { start = s; end = e; partial = true; }
        }

        long length = end - start + 1;
        if (partial)
        {
            res.StatusCode = 206;
            long reportedStart = wrongStartAfterZero && start > 0 ? start - 1 : start;
            res.Headers.ContentRange = $"bytes {reportedStart}-{end}/{data.Length}";
        }
        if (etag is not null) res.Headers.ETag = etag;
        if (modified is not null) res.Headers.LastModified = modified.Value.ToString("R");
        if (ranges) res.Headers.AcceptRanges = "bytes";
        res.ContentType = "application/octet-stream";
        res.ContentLength = length;

        long toSend = truncate is { } fraction ? (long)(length * fraction) : length;
        long sent = 0;
        while (sent < toSend)
        {
            int n = (int)Math.Min(16 * 1024, toSend - sent);
            await res.Body.WriteAsync(data.AsMemory((int)(start + sent), n));
            await res.Body.FlushAsync();
            sent += n;
            if (delayPerChunkMs > 0) await Task.Delay(delayPerChunkMs);
        }
        if (truncate is not null) ctx.Abort();   // the connection dies mid-body
    }

    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = end = 0;
        if (!header.StartsWith("bytes=")) return false;
        string[] parts = header[6..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out start) || start >= total) return false;
        end = parts[1].Length == 0 ? total - 1 : Math.Min(long.Parse(parts[1]), total - 1);
        return end >= start;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>An engine over a throw-away directory, tuned so tests run in seconds. Restart() simulates process death and relaunch.</summary>
internal sealed class Harness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vesper-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public string Downloads => Path.Combine(Root, "downloads");
    public string Data => Path.Combine(Root, "data");
    public DownloadEngine Engine { get; private set; }

    public Harness(Action<string>? failpoint = null, Action<EngineOptions>? tweak = null)
    {
        Directory.CreateDirectory(Downloads);
        Engine = Start(failpoint, tweak);
    }

    private DownloadEngine Start(Action<string>? failpoint, Action<EngineOptions>? tweak)
    {
        var options = new EngineOptions
        {
            DataDirectory = Data,
            AllowPrivateNetworks = true,                 // the fault server is on loopback
            Retry = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(40), MaxDelay = TimeSpan.FromMilliseconds(400), MaxAttempts = 6 },
            CheckpointBytes = 64 * 1024,
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            MinSegmentedSize = 256 * 1024,
            MinSplitBytes = 128 * 1024,
            RampInterval = TimeSpan.FromMilliseconds(150),
            StallTimeout = TimeSpan.FromSeconds(5),
            Failpoint = failpoint,
            DefaultFolder = Downloads
        };
        tweak?.Invoke(options);
        return DownloadEngine.Start(options);
    }

    public void Restart(Action<string>? failpoint = null)
    {
        Engine.Dispose();
        Engine = Start(failpoint, null);
    }

    public Guid Add(string url, string? name = null, string? sha256 = null, string? pieces = null, bool startNow = true)
    {
        AddResult result = Engine.Add(new AddRequest(url, FileName: name, ExpectedSha256: sha256, PieceMapJson: pieces, StartNow: startNow));
        Assert.True(result.Ok, result.Message);
        return result.JobId;
    }

    public async Task<JobSnapshot> WaitAsync(Guid id, Func<JobSnapshot, bool> done, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            JobSnapshot? s = Engine.Snapshot(id);
            if (s is not null && done(s)) return s;
            await Task.Delay(25);
        }
        JobSnapshot? last = Engine.Snapshot(id);
        throw new TimeoutException($"Timed out. Last state: {last?.State} {last?.Reason} {last?.Message}");
    }

    public Task<JobSnapshot> WaitForStateAsync(Guid id, JobState state, int seconds = 30) => WaitAsync(id, s => s.State == state, seconds);

    public string[] Files() => Directory.GetFiles(Downloads);

    public void Dispose()
    {
        Engine.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a temp directory left behind is harmless */ }
    }
}
