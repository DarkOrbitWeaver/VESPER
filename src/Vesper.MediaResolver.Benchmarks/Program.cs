global using StreamContent = Vesper.MediaResolver.Models.StreamContent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

// Developer-only executable. No benchmark runs automatically and no website identity is spoofed.
// JSONL is printed to stdout; only origin/path are reported, never signed query strings or credentials.
#if !BASELINE
if (Value("--validate-ts") is { } transportPath)
{
    var inspector = new TransportStreamValidator();
    using var file = File.OpenRead(transportPath);
    var bytes = new byte[188 * 512];
    while (true)
    {
        int count = 0;
        while (count < bytes.Length)
        { int read = file.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
        if (count == 0) break;
        inspector.Validate(bytes.AsSpan(0, count));
    }
    inspector.Finish(requireAudio: true, requireVideo: true);
    Console.WriteLine("{\"transportValidation\":\"passed\"}");
    return;
}
#endif
int iterations = int.TryParse(Value("--iterations"), out int n) ? Math.Clamp(n, 1, 100) : 5;
string? fixture = Value("--fixture");
string url = Value("--url") ?? "https://www.youtube.com/watch?v=EK3tCffac5k";
bool warm = args.Contains("--warm"), proxy = args.Contains("--system-proxy"), poster = args.Contains("--thumbnail");
string? destination = Value("--download");
int cancelMs = int.TryParse(Value("--cancel-ms"), out int c) ? Math.Clamp(c, 1, 60_000) : 0;
DefaultMediaResolver? shared = warm ? NewResolver() : null;
try
{
    for (int run = 0; run < iterations; run++)
    {
        var resolver = shared ?? NewResolver();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes();
        var clock = Stopwatch.StartNew();
        double? firstMetadata = null, firstRepresentation = null, thumbnailBytesReady = null;
        Task? thumbnailTask = null;
        using var lifetime = new CancellationTokenSource();
        if (cancelMs > 0) lifetime.CancelAfter(cancelMs);
        using var thumbnails = new SecureHttpClient(new ResolverOptions { UseSystemProxy = proxy, MaxConcurrentRequests = 1 });
        int before = resolver.NetworkRequestCount;
        MediaDiscoveryResult? result = null;
        string? cancelled = null;
        try
        {
            var request = new MediaDiscoveryRequest
            {
                Url = new(url),
                Browser = fixture == "browser" ? new BrowserDiscoveryMessage
                {
                    Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = url, Title = "Authorized fixture playback",
                    Candidates = [new BrowserMediaCandidate { Url = "https://cdn.example.com/fixture.mp4", MimeType = "video/mp4" }]
                } : null,
#if !BASELINE
                Progress = new Observer(update =>
                {
                    if (update.Title is not null || update.Thumbnail is not null) firstMetadata ??= clock.Elapsed.TotalMilliseconds;
                    if (update.RepresentationCount > 0) firstRepresentation ??= clock.Elapsed.TotalMilliseconds;
                    if (poster && update.Thumbnail is { } thumbnail && thumbnailTask is null)
                        thumbnailTask = LoadPosterAsync(thumbnail);
                }),
#endif
                Options = new DiscoveryOptions { CollectionIntent = args.Contains("--collection") ? CollectionIntent.CollectionOnly : CollectionIntent.ItemOnly }
            };
            result = await resolver.ResolveAsync(request, lifetime.Token);
        }
        catch (OperationCanceledException) { cancelled = "cancelled"; }
        double enumerationMs = clock.Elapsed.TotalMilliseconds;
        if (thumbnailTask is not null) await thumbnailTask;
        var process = Process.GetCurrentProcess();
        int? compatible = null;
#if !BASELINE
        compatible = result?.Item is { } item ? VESPER.MediaTransferBridge.PlayableOnly(item).Representations.Count : 0;
#endif
        Console.WriteLine(JsonSerializer.Serialize(new
        {
#if BASELINE
            revision = "b0c5ac14877dc729280b5eb210123f23da6ae067",
#else
            revision = "working-branch",
#endif
            run, mode = warm ? run == 0 ? "cold" : "warm" : "cold", fixture,
            source = new Uri(url).GetLeftPart(UriPartial.Path),
            status = cancelled ?? result?.Status.ToString(), error = result?.Error?.Code.ToString(),
            firstMetadataMs = firstMetadata, thumbnailBytesReadyMs = thumbnailBytesReady,
            firstSourceRepresentationMs = firstRepresentation, enumerationMs,
            networkRequests = resolver.NetworkRequestCount - before, thumbnailRequests = thumbnails.RequestCount,
            representations = result?.Item?.Representations.Count ?? 0, compatibleSingleStreamFormats = compatible,
            collectionEntries = result?.Collection?.Entries.Count, diagnostic = result?.Error?.Diagnostic,
            cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            processAllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            processPeakWorkingSetBytes = process.PeakWorkingSet64
        }));
#if !BASELINE
        if (destination is not null && result?.Item is { } media)
            await DownloadAsync(media, destination, lifetime.Token);
        if (args.Contains("--collection") && result?.Collection is { } collection
            && int.TryParse(Value("--queue-items"), out int selected) && selected > 0)
            await QueueSelectedAsync(resolver, collection, Math.Min(selected, 5), Value("--queue-folder") ?? Path.Combine(Path.GetTempPath(), "vesper-queue-smoke"), lifetime.Token);
#endif
        if (!warm) resolver.Dispose();

        async Task LoadPosterAsync(Uri thumbnail)
        {
            try { if (await thumbnails.GetThumbnailAsync(thumbnail, lifetime.Token) is not null) thumbnailBytesReady = clock.Elapsed.TotalMilliseconds; }
            catch (Exception) when (!lifetime.IsCancellationRequested) { /* optional image; reported as null */ }
            catch (OperationCanceledException) { }
        }
    }
}
finally { shared?.Dispose(); }

string? Value(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
DefaultMediaResolver NewResolver() => new(new ResolverOptions { UseSystemProxy = proxy }, fixture is null ? null : new FixtureHandler(fixture));

#if !BASELINE
static async Task QueueSelectedAsync(DefaultMediaResolver resolver, MediaCollection collection, int selected, string folder, CancellationToken ct)
{
    var batch = await new Vesper.MediaResolver.Playlists.BatchResolver(resolver).ResolveAsync(new()
    {
        Entries = collection.Entries.Take(selected).Select(e => new Vesper.MediaResolver.Playlists.BatchEntry(e.Url, e.MediaId, e.Resolved)).ToArray(),
        Preferences = Vesper.MediaResolver.Formats.FormatPreferences.BestQuality with { AllowMuxing = false },
        UserAuthorizedBatch = true, MaxConcurrency = 4, MaxItems = selected
    }, null, ct);
    Directory.CreateDirectory(folder);
    using var engine = DownloadEngine.Start(new EngineOptions
    {
        DataDirectory = Path.Combine(folder, "state-" + Guid.NewGuid().ToString("N")), DefaultFolder = Path.GetFullPath(folder),
        // A closed window keeps these explicitly selected jobs queued without transferring their bodies during this smoke check.
        Schedule = new SchedulePolicy(true, TimeOnly.FromDateTime(DateTime.Now.AddHours(1)), TimeOnly.FromDateTime(DateTime.Now.AddHours(2)))
    });
    int accepted = 0;
    foreach (var outcome in batch.Outcomes)
    {
        if (outcome.Item is not { } item) continue;
        var prepared = VESPER.MediaTransferBridge.Prepare(item, Vesper.MediaResolver.Formats.FormatPreferences.BestQuality with { AllowMuxing = false });
        if (prepared.Choice is not { } choice) continue;
        var result = engine.Add(new AddRequest(choice.Url, FileName: choice.FileName, StartNow: false,
            Kind: choice.Kind, Referer: choice.Referer, UserAgent: choice.UserAgent, MediaProvider: choice.Provider,
            MediaId: choice.MediaId, MediaFormatId: choice.FormatId, MediaPlanJson: choice.MediaPlanJson));
        if (result.Ok) accepted++;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { collection = collection.Provider, title = collection.Title,
        enumerated = collection.Entries.Count, explicitlySelected = selected, accepted,
        states = engine.Snapshots().Select(j => j.State.ToString()).ToArray(), bodiesDownloaded = false }));
}

static async Task DownloadAsync(MediaItem item, string destination, CancellationToken ct)
{
    var choices = VESPER.MediaTransferBridge.PlayableOnly(item).Representations;
    var selected = choices.OrderByDescending(r => r.HasVideo).ThenByDescending(r => r.Height ?? 0).ThenByDescending(r => r.BitrateKbps ?? 0).FirstOrDefault();
    if (selected is null) { Console.WriteLine("{\"transfer\":\"unsupported\"}"); return; }
    var prepared = VESPER.MediaTransferBridge.Prepare(item,
        Vesper.MediaResolver.Formats.FormatPreferences.BestQuality with { AllowMuxing = false }, selected);
    if (prepared.Choice is not { } choice) { Console.WriteLine(JsonSerializer.Serialize(new { transfer = "unavailable", reason = prepared.Error })); return; }
    string root = Path.GetFullPath(destination); Directory.CreateDirectory(root);
    // Isolated state, never the user's normal VESPER database. Downloaded files always use the engine's collision protection.
    string state = Path.Combine(root, ".vesper-benchmark-" + Guid.NewGuid().ToString("N"));
    using var engine = DownloadEngine.Start(new EngineOptions { DataDirectory = state, DefaultFolder = root, MaxConcurrentJobs = 1 });
    var added = engine.Add(new AddRequest(choice.Url, FileName: choice.FileName, Referer: choice.Referer, UserAgent: choice.UserAgent,
        Kind: choice.Kind, OriginalPageUrl: choice.OriginalPageUrl, MediaProvider: choice.Provider, MediaId: choice.MediaId,
        MediaFormatId: choice.FormatId, MediaPlanJson: choice.MediaPlanJson));
    if (!added.Ok) { Console.WriteLine(JsonSerializer.Serialize(new { transfer = "rejected", reason = added.Message })); return; }
    var watch = Stopwatch.StartNew();
    try
    {
        while (watch.Elapsed < TimeSpan.FromMinutes(5))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = engine.Snapshot(added.JobId)!;
            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink or JobState.Suspect)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { transfer = snapshot.State.ToString(), reason = snapshot.Reason,
                    file = snapshot.FinalPath, message = snapshot.Message, bytes = snapshot.CommittedBytes, elapsedMs = watch.Elapsed.TotalMilliseconds }));
                return;
            }
            await Task.Delay(50, ct);
        }
        engine.Pause(added.JobId); Console.WriteLine("{\"transfer\":\"deadline\",\"partialKept\":true}");
    }
    catch (OperationCanceledException) { engine.Pause(added.JobId); Console.WriteLine("{\"transfer\":\"cancelled\",\"partialKept\":true}"); }
}

sealed class Observer(Action<DiscoveryProgress> action) : IProgress<DiscoveryProgress>
{ public void Report(DiscoveryProgress update) => action(update); }
#endif

sealed class FixtureHandler(string fixture) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(40, ct); // deterministic per-request transport delay; not a real-network speed claim
        string body = fixture == "browser"
            ? "<script>var ytInitialPlayerResponse={\"playabilityStatus\":{\"status\":\"OK\"},\"videoDetails\":{\"videoId\":\"EK3tCffac5k\",\"title\":\"Fixture\"},\"streamingData\":{\"formats\":[{\"signatureCipher\":\"s=not-executed\"}]}};</script>"
            : "<html><title>Playback unavailable</title></html>";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        if (request.RequestUri!.Host == "cdn.example.com")
        {
            response.Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : Encoding.ASCII.GetBytes("fixture"));
            response.Content.Headers.ContentType = new("video/mp4"); response.Content.Headers.ContentLength = 7;
        }
        else response.Content = new StringContent(body, Encoding.UTF8, "text/html");
        return response;
    }
}
