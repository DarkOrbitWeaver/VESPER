using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class HttpResourceIdentityTests
{
    [Fact]
    public void Resource_identity_keeps_full_query_but_ignores_fragment()
    {
        Uri signed = new("https://CDN.example:443/folder/file.bin?token=first#part");
        Uri refreshed = new("https://cdn.example/folder/file.bin?token=second");

        string identity = HttpTransport.ResourceIdentity(signed);

        Assert.NotEqual(identity, HttpTransport.ResourceIdentity(refreshed));
        Assert.Equal(identity, HttpTransport.ResourceIdentity(new Uri("https://cdn.example/folder/file.bin?token=first")));
        Assert.NotEqual(identity, HttpTransport.ResourceIdentity(new Uri("https://other.example/folder/file.bin")));
        Assert.NotEqual(identity, HttpTransport.ResourceIdentity(new Uri("https://cdn.example:444/folder/file.bin")));
        Assert.NotEqual(identity, HttpTransport.ResourceIdentity(new Uri("https://cdn.example/folder/other.bin")));
        Assert.NotEqual(identity, HttpTransport.ResourceIdentity(new Uri("http://cdn.example/folder/file.bin")));
        Assert.True(HttpTransport.IsResourceIdentity(identity));
        Assert.DoesNotContain("token", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first", identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schema_migration_preserves_jobs_and_adds_nullable_identity_fields()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-http-identity-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "vesper.db");
        Uri resource = new("https://cdn.example/f/file.bin?token=private");
        ExtentSet.TryParse("0-64", out ExtentSet extents);
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            SourceUrl = "https://source.example/file",
            FinalResourceIdentity = HttpTransport.ResourceIdentity(resource),
            PublishedFileIdentity = "volume:file-id",
            FileName = "file.bin",
            DestinationFolder = root,
            PartialPath = Path.Combine(root, "file.bin.vpart"),
            TotalBytes = 100,
            Extents = extents,
            State = JobState.Paused,
            Identity = IdentityLevel.StrongEtag,
            ETag = "\"same\""
        };

        try
        {
            using (JobStore store = JobStore.Open(database))
            {
                store.Save(job);
                DownloadJob roundTrip = Assert.Single(store.LoadAll());
                Assert.Equal(job.FinalResourceIdentity, roundTrip.FinalResourceIdentity);
                Assert.Equal(job.PublishedFileIdentity, roundTrip.PublishedFileIdentity);
            }

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE jobs DROP COLUMN pending_conversion_identity; " +
                    $"PRAGMA user_version = {JobStore.SchemaVersion - 1};";
                command.ExecuteNonQuery();
            }

            using JobStore migrated = JobStore.Open(database);
            DownloadJob restored = Assert.Single(migrated.LoadAll());
            Assert.Equal(job.FinalResourceIdentity, restored.FinalResourceIdentity);
            Assert.Equal(job.PublishedFileIdentity, restored.PublishedFileIdentity);
            Assert.Null(restored.PendingConversionIdentity);
            Assert.Equal(job.Id, restored.Id);
            Assert.Equal(job.SourceUrl, restored.SourceUrl);
            Assert.Equal(job.Extents.Serialize(), restored.Extents.Serialize());
            Assert.Equal(job.ETag, restored.ETag);

            restored.PendingConversionIdentity = "pending-stage-identity";
            migrated.Save(restored);
            Assert.Equal("pending-stage-identity", Assert.Single(migrated.LoadAll()).PendingConversionIdentity);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Resume_refuses_a_changed_redirect_target_before_writing_more_bytes()
    {
        byte[] original = Payloads.Make(4 * 1024 * 1024, 41);
        byte[] changed = Payloads.Make(original.Length, 77);
        await using var source = new HttpIdentityFixtureServer();
        await using var firstCdn = new HttpIdentityFixtureServer(original, slow: true);
        await using var changedCdn = new HttpIdentityFixtureServer(changed);
        await source.StartAsync();
        await firstCdn.StartAsync();
        await changedCdn.StartAsync();
        source.RedirectTarget = firstCdn.Url("/file?signature=first-secret");

        using var harness = new Harness(tweak: options => options.MaxConnectionsPerJob = 1);
        Guid id = harness.Add(source.Url("/entry?ticket=source-secret"), "redirected.bin");
        await harness.WaitAsync(id, job => job.State == JobState.Downloading && job.ProgressBytes > 160_000);
        harness.Engine.Pause(id);
        JobSnapshot paused = await harness.WaitForStateAsync(id, JobState.Paused);
        byte[] before = await File.ReadAllBytesAsync(paused.PartialPath);
        Assert.True(paused.CommittedBytes > 0 && paused.CommittedBytes < original.Length);

        string? storedIdentity = ReadResourceIdentity(Path.Combine(harness.Data, "vesper.db"), id);
        Assert.Equal(HttpTransport.ResourceIdentity(new Uri(firstCdn.Url("/file?signature=first-secret"))), storedIdentity);
        Assert.DoesNotContain("signature", storedIdentity!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first-secret", storedIdentity!, StringComparison.OrdinalIgnoreCase);

        source.RedirectTarget = changedCdn.Url("/file?signature=changed-secret");
        harness.Restart();
        harness.Engine.Resume(id);
        JobSnapshot refused = await harness.WaitForStateAsync(id, JobState.NeedsAttention);

        Assert.Equal("redirect", refused.Reason);
        Assert.Contains("different file location", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("\"shared\"", firstCdn.EntityTag);
        Assert.Equal(firstCdn.EntityTag, changedCdn.EntityTag);
        Assert.Equal(original.Length, changedCdn.Data.Length);
        Assert.Contains(changedCdn.RangeRequests, request => request.IfRange == changedCdn.EntityTag);
        Assert.Equal(paused.CommittedBytes, refused.CommittedBytes);
        Assert.Equal(before, await File.ReadAllBytesAsync(paused.PartialPath));

        bool restartedFromScratch = harness.Engine.RestartFromScratch(id);
        JobSnapshot? afterRestartAttempt = harness.Engine.Snapshot(id);
        Assert.True(restartedFromScratch,
            $"RestartFromScratch failed with {afterRestartAttempt?.State}/{afterRestartAttempt?.Reason}: {afterRestartAttempt?.Message}");
        JobSnapshot restarted = await harness.WaitForStateAsync(id, JobState.Completed, 30);
        Assert.Equal(changed, await File.ReadAllBytesAsync(restarted.FinalPath!));
    }

    [Fact]
    public async Task Parallel_ranges_refuse_a_redirect_change_during_a_fresh_download()
    {
        byte[] original = Payloads.Make(9 * 1024 * 1024, 41);
        byte[] changed = Payloads.Make(original.Length, 77);
        await using var source = new HttpIdentityFixtureServer();
        await using var firstCdn = new HttpIdentityFixtureServer(original, slow: true, holdFirstResponse: true);
        await using var changedCdn = new HttpIdentityFixtureServer(changed);
        await source.StartAsync();
        await firstCdn.StartAsync();
        await changedCdn.StartAsync();
        source.RedirectTarget = firstCdn.Url("/file?signature=first-secret");

        using var harness = new Harness(tweak: options => options.MaxConnectionsPerJob = 4);
        Guid id = harness.Add(source.Url("/entry?ticket=source-secret"), "parallel-redirect.bin");
        await firstCdn.FirstFileResponseStarted.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.WaitAsync(id, job => job.State == JobState.Downloading);
        source.RedirectTarget = changedCdn.Url("/file?signature=changed-secret");
        firstCdn.ReleaseFirstFileResponse();

        JobSnapshot refused = await harness.WaitForStateAsync(id, JobState.NeedsAttention, 20);

        Assert.Equal("redirect", refused.Reason);
        (string? Range, string? IfRange) changedRequest = Assert.Single(changedCdn.RangeRequests);
        Assert.Equal(changedCdn.EntityTag, changedRequest.IfRange);
        Assert.Equal(original.Length, changedCdn.Data.Length);

        string[] bounds = changedRequest.Range![6..].Split('-', 2);
        int start = int.Parse(bounds[0]);
        int end = int.Parse(bounds[1]);
        byte[] partial = await File.ReadAllBytesAsync(refused.PartialPath);
        Assert.InRange(start, 1, partial.Length - 1);
        Assert.True(partial.AsSpan(start, end - start + 1).IndexOfAnyExcept((byte)0) < 0,
            "Bytes from the changed redirect were written before its identity was checked.");
    }

    [Fact]
    public async Task A_zero_commit_restart_can_capture_a_new_redirect_target()
    {
        byte[] bytes = Payloads.Make(256 * 1024, 63);
        await using var source = new HttpIdentityFixtureServer();
        await using var oldCdn = new HttpIdentityFixtureServer(bytes);
        await using var newCdn = new HttpIdentityFixtureServer(bytes);
        await source.StartAsync();
        await oldCdn.StartAsync();
        await newCdn.StartAsync();
        source.RedirectTarget = newCdn.Url("/file?signature=current");

        string root = Path.Combine(Path.GetTempPath(), "vesper-zero-identity-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(data);
        Guid id = Guid.NewGuid();
        string partial = Path.Combine(downloads, "zero-commit.bin.vpart");
        using (JobStore store = JobStore.Open(Path.Combine(data, "vesper.db")))
        {
            store.Save(new DownloadJob
            {
                Id = id,
                Kind = JobKind.Http,
                SourceUrl = source.Url("/entry"),
                FinalResourceIdentity = HttpTransport.ResourceIdentity(new Uri(oldCdn.Url("/file?signature=stale"))),
                FileName = "zero-commit.bin",
                DestinationFolder = downloads,
                PartialPath = partial,
                TotalBytes = bytes.Length,
                State = JobState.Paused,
                Identity = IdentityLevel.StrongEtag,
                ETag = newCdn.EntityTag
            });
        }

        try
        {
            using DownloadEngine engine = DownloadEngine.Start(Options(data, downloads));
            engine.Resume(id);

            JobSnapshot completed = await WaitForStateAsync(engine, id, JobState.Completed, TimeSpan.FromSeconds(30));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.FinalPath!));
            Assert.Equal(HttpTransport.ResourceIdentity(new Uri(newCdn.Url("/file?signature=current"))),
                ReadResourceIdentity(Path.Combine(data, "vesper.db"), id));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rescue_accepts_a_new_starting_url_that_reaches_the_saved_final_resource()
    {
        byte[] bytes = Payloads.Make(1024 * 1024, 28);
        await using var oldSource = new HttpIdentityFixtureServer();
        await using var newSource = new HttpIdentityFixtureServer();
        await using var cdn = new HttpIdentityFixtureServer(bytes, slow: true);
        await oldSource.StartAsync();
        await newSource.StartAsync();
        await cdn.StartAsync();
        oldSource.RedirectTarget = cdn.Url("/file?signature=old");
        newSource.RedirectTarget = cdn.Url("/file?signature=old");

        using var harness = new Harness(tweak: options => options.MaxConnectionsPerJob = 1);
        Guid id = harness.Add(oldSource.Url("/entry?ticket=old"), "same-final-resource.bin");
        await harness.WaitAsync(id, job => job.State == JobState.Downloading && job.ProgressBytes > 96_000);
        harness.Engine.Pause(id);
        JobSnapshot paused = await harness.WaitForStateAsync(id, JobState.Paused);

        RescueResult rescued = await harness.Engine.ReplaceLinkAsync(id, newSource.Url("/entry?ticket=new"));

        Assert.True(rescued.Accepted, rescued.Message);
        Assert.Equal(RescueMode.R0, rescued.Mode);
        Assert.Equal(paused.CommittedBytes, rescued.RetainedBytes);
        JobSnapshot completed = await harness.WaitForStateAsync(id, JobState.Completed, 30);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.FinalPath!));
    }

    [Fact]
    public async Task Persisted_length_only_browser_proof_is_stopped_before_body_bytes_are_saved()
    {
        byte[] bytes = Payloads.Make(64 * 1024, 53);
        await using var server = new HttpIdentityFixtureServer(bytes);
        await server.StartAsync();

        string root = Path.Combine(Path.GetTempPath(), "vesper-legacy-browser-proof-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(data);
        Guid id = Guid.NewGuid();
        string partial = Path.Combine(downloads, "legacy-browser.bin.vpart");
        using (JobStore store = JobStore.Open(Path.Combine(data, "vesper.db")))
        {
            store.Save(new DownloadJob
            {
                Id = id,
                Kind = JobKind.Http,
                SourceUrl = server.Url("/file"),
                FileName = "legacy-browser.bin",
                DestinationFolder = downloads,
                PartialPath = partial,
                State = JobState.Paused,
                BrowserProof = new BrowserTransferProof(bytes.Length, null, "application/octet-stream", AwaitingConfirmation: false)
            });
        }

        try
        {
            using DownloadEngine engine = DownloadEngine.Start(Options(data, downloads));
            engine.Resume(id);
            JobSnapshot stopped = await WaitForStateAsync(engine, id, JobState.Cancelled);

            Assert.Equal("browser-fallback", stopped.Reason);
            Assert.Equal(0, stopped.CommittedBytes);
            Assert.Equal(0, stopped.ProgressBytes);
            Assert.Null(stopped.FinalPath);
            Assert.True(File.Exists(partial));
            Assert.Equal(0, new FileInfo(partial).Length);
            Assert.Equal(1, server.Hits);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Trusted_hash_is_required_to_rescue_bytes_across_a_changed_redirect_target()
    {
        byte[] original = Payloads.Make(4 * 1024 * 1024, 41);
        byte[] changed = Payloads.Make(original.Length, 77);
        await using var source = new HttpIdentityFixtureServer();
        await using var firstCdn = new HttpIdentityFixtureServer(original, slow: true);
        await using var changedCdn = new HttpIdentityFixtureServer(changed);
        await source.StartAsync();
        await firstCdn.StartAsync();
        await changedCdn.StartAsync();
        source.RedirectTarget = firstCdn.Url("/file?signature=first-secret");

        using var harness = new Harness(tweak: options => options.MaxConnectionsPerJob = 1);
        string sourceUrl = source.Url("/entry?ticket=source-secret");
        AddResult added = harness.Engine.Add(new AddRequest(sourceUrl, FileName: "trusted-redirect.bin",
            ExpectedSha256: Payloads.Sha256(original)));
        Assert.True(added.Ok, added.Message);
        await harness.WaitAsync(added.JobId, job => job.State == JobState.Downloading && job.ProgressBytes > 160_000);
        harness.Engine.Pause(added.JobId);
        JobSnapshot paused = await harness.WaitForStateAsync(added.JobId, JobState.Paused);
        Assert.True(paused.CommittedBytes > 0 && paused.CommittedBytes < original.Length);

        source.RedirectTarget = changedCdn.Url("/file?signature=changed-secret");
        harness.Restart();
        harness.Engine.Resume(added.JobId);
        JobSnapshot refused = await harness.WaitForStateAsync(added.JobId, JobState.NeedsAttention);
        Assert.Equal("redirect", refused.Reason);
        Assert.Equal(paused.CommittedBytes, refused.CommittedBytes);

        RescueResult rescued = await harness.Engine.ReplaceLinkAsync(added.JobId, sourceUrl);
        Assert.True(rescued.Accepted, rescued.Message);
        Assert.Equal(RescueMode.R1, rescued.Mode);
        Assert.Equal("\"shared\"", firstCdn.EntityTag);
        Assert.Equal(firstCdn.EntityTag, changedCdn.EntityTag);
        Assert.Equal(original.Length, changedCdn.Data.Length);

        JobSnapshot suspect = await harness.WaitForStateAsync(added.JobId, JobState.Suspect, 30);
        Assert.Equal("hash-mismatch", suspect.Reason);
        Assert.Null(suspect.FinalPath);
        Assert.True(File.Exists(suspect.PartialPath));
    }

    [Fact]
    public async Task Legacy_partial_is_kept_without_network_and_can_be_recovered_with_trusted_pieces()
    {
        byte[] bytes = Payloads.Make(1024 * 1024, 91);
        await using var source = new HttpIdentityFixtureServer();
        await using var cdn = new HttpIdentityFixtureServer(bytes);
        await source.StartAsync();
        await cdn.StartAsync();
        source.RedirectTarget = cdn.Url("/file");

        string root = Path.Combine(Path.GetTempPath(), "vesper-legacy-identity-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(data);
        string partial = Path.Combine(downloads, "legacy.bin.vpart");
        byte[] prefix = bytes[..(64 * 1024)];
        await File.WriteAllBytesAsync(partial, prefix);
        string? partialIdentity;
        using (SafeFileHandle handle = File.OpenHandle(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            partialIdentity = PathSafety.TemporaryFileIdentity(handle);
        Assert.NotNull(partialIdentity);
        ExtentSet.TryParse($"0-{prefix.Length}", out ExtentSet extents);
        Guid id = Guid.NewGuid();
        string sourceUrl = source.Url("/entry");
        using (JobStore store = JobStore.Open(Path.Combine(data, "vesper.db")))
        {
            store.Save(new DownloadJob
            {
                Id = id,
                Kind = JobKind.Http,
                SourceUrl = sourceUrl,
                FileName = "legacy.bin",
                NameLocked = true,
                DestinationFolder = downloads,
                PartialPath = partial,
                PartialIdentity = partialIdentity,
                TotalBytes = bytes.Length,
                Extents = extents,
                State = JobState.Paused,
                Identity = IdentityLevel.StrongEtag,
                ETag = cdn.EntityTag
            });
        }

        try
        {
            using DownloadEngine engine = DownloadEngine.Start(Options(data, downloads));
            engine.Resume(id);
            JobSnapshot blocked = await WaitForStateAsync(engine, id, JobState.NeedsAttention);
            Assert.Equal("redirect-identity-missing", blocked.Reason);
            Assert.Equal(prefix, await File.ReadAllBytesAsync(partial));
            Assert.Equal(0, source.Hits);
            Assert.Equal(0, cdn.Hits);

            const int pieceLength = 16 * 1024;
            string pieceMap = PieceMapFor(bytes, pieceLength);
            RescueResult rescued = await engine.ReplaceLinkAsync(id, cdn.Url("/file?signature=refresh-token"),
                new RescueInput(PieceMapJson: pieceMap));
            Assert.True(rescued.Accepted, rescued.Message);
            Assert.Equal(RescueMode.R1b, rescued.Mode);

            JobSnapshot completed = await WaitForStateAsync(engine, id, JobState.Completed, TimeSpan.FromSeconds(30));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.FinalPath!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static EngineOptions Options(string data, string downloads) => new()
    {
        DataDirectory = data,
        DefaultFolder = downloads,
        AllowPrivateNetworks = true,
        MaxConnectionsPerJob = 1,
        Retry = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(40), MaxDelay = TimeSpan.FromMilliseconds(400), MaxAttempts = 4 },
        CheckpointBytes = 64 * 1024,
        CheckpointInterval = TimeSpan.FromMilliseconds(100)
    };

    private static string PieceMapFor(byte[] bytes, int pieceLength)
    {
        string[] hashes = Enumerable.Range(0, (bytes.Length + pieceLength - 1) / pieceLength)
            .Select(index => Convert.ToHexString(SHA256.HashData(bytes.AsSpan(index * pieceLength,
                Math.Min(pieceLength, bytes.Length - index * pieceLength)))))
            .ToArray();
        return new PieceMap(pieceLength, bytes.Length, hashes).ToJson();
    }

    private static string? ReadResourceIdentity(string database, Guid id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT final_resource_identity FROM jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    private static async Task<JobSnapshot> WaitForStateAsync(DownloadEngine engine, Guid id, JobState state,
        TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            JobSnapshot? job = engine.Snapshot(id);
            if (job?.State == state) return job;
            await Task.Delay(25);
        }

        JobSnapshot? last = engine.Snapshot(id);
        throw new TimeoutException($"Timed out. Last state: {last?.State} {last?.Reason} {last?.Message}");
    }

    private sealed class HttpIdentityFixtureServer : IAsyncDisposable
    {
        private readonly bool _slow;
        private readonly bool _holdFirstResponse;
        private WebApplication? _app;
        private string? _redirectTarget;
        private int _hits;
        private int _fileResponses;

        public byte[] Data { get; }
        public string EntityTag { get; } = "\"shared\"";
        public string BaseUrl { get; private set; } = "";
        public int Hits => Volatile.Read(ref _hits);
        public Task FirstFileResponseStarted => _firstFileResponseStarted.Task;
        public ConcurrentQueue<(string? Range, string? IfRange)> RangeRequests { get; } = new();
        private readonly TaskCompletionSource _firstFileResponseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstFileResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? RedirectTarget
        {
            get => Volatile.Read(ref _redirectTarget);
            set => Volatile.Write(ref _redirectTarget, value);
        }

        public HttpIdentityFixtureServer() : this([], false) { }

        public HttpIdentityFixtureServer(byte[] data) : this(data, false) { }

        public HttpIdentityFixtureServer(byte[] data, bool slow = false, bool holdFirstResponse = false)
        {
            Data = data;
            _slow = slow;
            _holdFirstResponse = holdFirstResponse;
        }

        public void ReleaseFirstFileResponse() => _releaseFirstFileResponse.TrySetResult();

        private async Task HandleAsync(HttpContext context)
        {
            Interlocked.Increment(ref _hits);
            if (context.Request.Path == "/entry" && RedirectTarget is { } target)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = target;
                return;
            }

            if (Data.Length == 0 || context.Request.Path != "/file")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            long start = 0;
            long end = Data.Length - 1;
            bool partial = false;
            if (context.Request.Headers.TryGetValue("Range", out var header))
            {
                string range = header.ToString();
                string? ifRange = context.Request.Headers["If-Range"].FirstOrDefault();
                RangeRequests.Enqueue((range, ifRange));
                if (TryParseRange(range, out long requestedStart, out long requestedEnd)
                    && (ifRange is null || ifRange == EntityTag))
                {
                    start = requestedStart;
                    end = requestedEnd < 0 ? Data.Length - 1 : Math.Min(requestedEnd, Data.Length - 1);
                    partial = start < Data.Length && end >= start;
                }
            }

            HttpResponse response = context.Response;
            if (partial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{end}/{Data.Length}";
            }
            response.Headers.ETag = EntityTag;
            response.Headers.AcceptRanges = "bytes";
            response.ContentType = "application/octet-stream";
            long length = end - start + 1;
            response.ContentLength = length;
            bool firstResponse = Interlocked.Increment(ref _fileResponses) == 1;
            long sent = 0;
            if (_holdFirstResponse && firstResponse)
            {
                await response.StartAsync(context.RequestAborted);
                int firstChunk = (int)Math.Min(16 * 1024, length);
                await response.Body.WriteAsync(Data.AsMemory((int)start, firstChunk), context.RequestAborted);
                await response.Body.FlushAsync(context.RequestAborted);
                sent = firstChunk;
                _firstFileResponseStarted.TrySetResult();
                await _releaseFirstFileResponse.Task.WaitAsync(context.RequestAborted);
            }
            else
            {
                _firstFileResponseStarted.TrySetResult();
            }
            while (sent < length)
            {
                int count = (int)Math.Min(16 * 1024, length - sent);
                await response.Body.WriteAsync(Data.AsMemory((int)(start + sent), count), context.RequestAborted);
                await response.Body.FlushAsync(context.RequestAborted);
                sent += count;
                if (_slow) await Task.Delay(4, context.RequestAborted);
            }
        }

        public async Task StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            WebApplication app = builder.Build();
            app.Run(HandleAsync);
            _app = app;
            await app.StartAsync();
            BaseUrl = app.Urls.Single().TrimEnd('/');
        }

        public string Url(string path) => BaseUrl + path;

        private static bool TryParseRange(string text, out long start, out long end)
        {
            start = 0;
            end = -1;
            if (!text.StartsWith("bytes=", StringComparison.Ordinal)) return false;
            string[] parts = text[6..].Split('-');
            return parts.Length == 2 && long.TryParse(parts[0], out start)
                && (parts[1].Length == 0 || long.TryParse(parts[1], out end));
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not { } app) return;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
