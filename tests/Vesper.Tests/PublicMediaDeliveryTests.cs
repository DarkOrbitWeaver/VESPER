using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vesper.Engine;
using Xunit.Abstractions;

namespace Vesper.Tests;

// Explicit opt-in: public, documented, small media samples. An independent
// HttpClient obtains the reference bytes; the engine must publish the same
// bytes, not merely report a successful job. No cookies, competitor software,
// installed settings, private profile, or production runtime dependency.
public sealed class PublicMediaDeliveryTests(ITestOutputHelper output)
{
    public sealed class LiveMediaTheoryAttribute : TheoryAttribute
    {
        public LiveMediaTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("VESPER_PUBLIC_MEDIA_DELIVERY") != "1")
                Skip = "Set VESPER_PUBLIC_MEDIA_DELIVERY=1 to run public media delivery checks.";
        }
    }

    [LiveMediaTheory]
    [InlineData("https://mdn.github.io/shared-assets/videos/flower.mp4", "flower.mp4")]
    [InlineData("https://mdn.github.io/shared-assets/videos/flower.webm", "flower.webm")]
    public async Task Published_public_media_matches_independent_reference_and_contains_video(string address, string name)
    {
        const int limit = 4 * 1024 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(response.Content.Headers.ContentLength ?? 0, 1, limit);
        using var reference = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                Assert.True(reference.Length + read <= limit);
                reference.Write(buffer, 0, read);
            }
        }
        Assert.Equal(response.Content.Headers.ContentLength, reference.Length);
        byte[] bytes = reference.ToArray();
        string sha = Convert.ToHexString(SHA256.HashData(bytes));

        string root = Path.Combine(Path.GetTempPath(), "vesper-public-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                DefaultFolder = Path.Combine(root, "files"),
                AllowPrivateNetworks = false, MaxConnectionsPerHost = 1, MaxConnectionsPerJob = 1,
                Retry = new RetryOptions { MaxAttempts = 1 }
            });
            var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.JobChanged += job =>
            {
                if (job.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink)
                    done.TrySetResult(job);
            };
            long start = Stopwatch.GetTimestamp();
            var added = engine.Add(new AddRequest(address, FileName:name, ExpectedSha256:sha,
                OriginalPageUrl:address, MediaProvider:"generic", MediaId:"public-mdn-" + name));
            Assert.True(added.Ok, added.Message);
            var result = await done.Task.WaitAsync(ct);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.True(result.State == JobState.Completed, $"{result.State}: {result.Reason} {result.Message}");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.FinalPath!, ct));
            using var file = File.OpenRead(result.FinalPath!);
            var tracks = MediaMerger.Demux(file, file.Length, ct);
            Assert.Contains(tracks, track => track.Kind == TrackKind.Video && track.Samples.Count > 0);
            Assert.All(tracks, track => Assert.True(track.DurationSeconds > 0));
            output.WriteLine(JsonSerializer.Serialize(new { name, bytes = bytes.Length, sha256 = sha, elapsed,
                tracks = tracks.Select(t => new { kind = t.Kind.ToString(), samples = t.Samples.Count, seconds = t.DurationSeconds }),
                independentlyCompared = true }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive:true); }
    }
}
