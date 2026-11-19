using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class ConcurrentMediaRefreshTests
{
    [Fact]
    public async Task Only_one_same_snapshot_credentialed_refresh_commits()
    {
        byte[] trackBytes = Payloads.Make(2048, 7);
        const string firstCookie = "session=first-refresh-secret";
        const string secondCookie = "session=second-refresh-secret";
        await using var server = new RefreshProbeServer(trackBytes, new Dictionary<string, string>
        {
            ["/fresh/first"] = firstCookie,
            ["/fresh/second"] = secondCookie
        });
        await server.StartAsync();

        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        MediaMergePlan savedPlan = Plan(server.Url("/saved/audio"), trackBytes.Length);
        AddResult added = harness.Engine.Add(new AddRequest(savedPlan.Tracks[0].Url,
            FileName: "concurrent-refresh.m4a", StartNow: false, Kind: JobKind.Merge,
            MediaProvider: "fixture", MediaId: "same-media", MediaFormatId: "audio-format",
            MediaPlanJson: savedPlan.ToJson()));
        Assert.True(added.Ok, added.Message);

        byte[] partial = Payloads.Make(67, 9);
        DownloadJob job = FindJob(harness.Engine, added.JobId);
        await File.WriteAllBytesAsync(job.PartialPath, partial);
        harness.OwnFixturePartial(added.JobId);
        var extents = new ExtentSet();
        extents.Add(new ByteRange(0, partial.LongLength));
        harness.Engine.UpdateJob(job, current => current.Extents = extents);
        string savedExtents = job.Extents.Serialize();

        MediaMergePlan firstPlan = Plan(server.Url("/fresh/first"), trackBytes.Length);
        MediaMergePlan secondPlan = Plan(server.Url("/fresh/second"), trackBytes.Length);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        // Both calls capture the saved plan before the fixture releases either probe.
        Task<RescueResult> firstRefresh = harness.Engine.RefreshMergePlanWithCredentialsAsync(added.JobId,
            firstPlan.ToJson(), expiresAt, new Dictionary<string, string> { ["Cookie"] = firstCookie });
        Task<RescueResult> secondRefresh = harness.Engine.RefreshMergePlanWithCredentialsAsync(added.JobId,
            secondPlan.ToJson(), expiresAt, new Dictionary<string, string> { ["Cookie"] = secondCookie });
        Task<RescueResult[]> resultsTask = Task.WhenAll(firstRefresh, secondRefresh);

        try
        {
            await server.BothProbesStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            server.ReleaseProbes();
        }

        RescueResult[] results = await resultsTask;
        Assert.Single(results, result => result.Accepted);
        RescueResult refused = Assert.Single(results, result => !result.Accepted);
        Assert.Contains("changed while its refreshed tracks were being checked", refused.Message,
            StringComparison.OrdinalIgnoreCase);

        bool firstWon = results[0].Accepted;
        MediaMergePlan winningPlan = firstWon ? firstPlan : secondPlan;
        string winningCookie = firstWon ? firstCookie : secondCookie;
        RequestCredentials? installedCredentials = harness.Engine.CredentialsFor(added.JobId);
        Assert.NotNull(installedCredentials);
        Assert.Equal(winningCookie, installedCredentials.Headers["Cookie"]);

        long committedBytes = harness.Engine.Snapshot(added.JobId)!.CommittedBytes;
        harness.Engine.Dispose();
        string partialPath;
        string persistedState;
        using (var store = JobStore.Open(Path.Combine(harness.Data, "vesper.db")))
        {
            DownloadJob persisted = Assert.Single(store.LoadAll());
            Assert.Equal(winningPlan.ToJson(), persisted.MediaPlanJson);
            Assert.Equal(winningPlan.Tracks[0].Url, persisted.SourceUrl);
            Assert.Equal(savedExtents, persisted.Extents.Serialize());
            partialPath = persisted.PartialPath;
            persistedState = JsonSerializer.Serialize(persisted);
        }

        Assert.Equal(partial, await File.ReadAllBytesAsync(partialPath));
        Assert.Equal(partial.LongLength, committedBytes);
        Assert.DoesNotContain(firstCookie, persistedState, StringComparison.Ordinal);
        Assert.DoesNotContain(secondCookie, persistedState, StringComparison.Ordinal);
        foreach (string path in Directory.GetFiles(harness.Data, "*", SearchOption.AllDirectories))
        {
            string fileText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(firstCookie, fileText, StringComparison.Ordinal);
            Assert.DoesNotContain(secondCookie, fileText, StringComparison.Ordinal);
        }
        Assert.Equal(2, server.Probes.Count);
        Assert.Contains(server.Probes, probe => probe.Path == "/fresh/first" && probe.Cookie == firstCookie);
        Assert.Contains(server.Probes, probe => probe.Path == "/fresh/second" && probe.Cookie == secondCookie);
        Assert.All(server.Probes, probe => Assert.Equal("bytes=0-511", probe.Range));
    }

    private static MediaMergePlan Plan(string url, long size) => new()
    {
        Provider = "fixture",
        MediaId = "same-media",
        OutputContainer = "m4a",
        Tracks = [new MergeTrack("audio", "audio-format", url, size, "m4a", "aac", "audio-bytes-v1")]
    };

    private static DownloadJob FindJob(DownloadEngine engine, Guid id)
    {
        FieldInfo field = typeof(DownloadEngine).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var jobs = (Dictionary<Guid, DownloadJob>)field.GetValue(engine)!;
        return jobs[id];
    }

    private sealed class RefreshProbeServer : IAsyncDisposable
    {
        private const int ProbeLength = 512;
        private readonly byte[] _bytes;
        private readonly IReadOnlyDictionary<string, string> _cookiesByPath;
        private readonly WebApplication _app;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _probeCount;

        public string BaseUrl { get; private set; } = "";
        public TaskCompletionSource BothProbesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ProbeRequest> Probes { get; } = new();

        public RefreshProbeServer(byte[] bytes, IReadOnlyDictionary<string, string> cookiesByPath)
        {
            _bytes = bytes;
            _cookiesByPath = cookiesByPath;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.Run(HandleAsync);
        }

        public async Task StartAsync()
        {
            await _app.StartAsync();
            BaseUrl = _app.Urls.First().TrimEnd('/');
        }

        public string Url(string path) => BaseUrl + path;

        public void ReleaseProbes() => _release.TrySetResult();

        private async Task HandleAsync(HttpContext context)
        {
            string path = context.Request.Path.Value ?? "/";
            if (!_cookiesByPath.TryGetValue(path, out string? expectedCookie))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string? cookie = context.Request.Headers["Cookie"].FirstOrDefault();
            string? range = context.Request.Headers["Range"].FirstOrDefault();
            Probes.Enqueue(new ProbeRequest(path, cookie, range));
            if (Interlocked.Increment(ref _probeCount) == 2) BothProbesStarted.TrySetResult();

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
            if (cookie != expectedCookie)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (range != $"bytes=0-{ProbeLength - 1}")
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
            context.Response.Headers.ContentRange = $"bytes 0-{ProbeLength - 1}/{_bytes.Length}";
            context.Response.ContentLength = ProbeLength;
            await context.Response.Body.WriteAsync(_bytes.AsMemory(0, ProbeLength), context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseProbes();
            await _app.DisposeAsync();
        }
    }

    private sealed record ProbeRequest(string Path, string? Cookie, string? Range);
}
