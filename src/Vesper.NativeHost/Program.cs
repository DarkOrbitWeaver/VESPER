using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.NativeHost;

/// <summary>
/// Chrome Native Messaging host. Relays validated messages over the per-user pipe in three shapes:
/// <list type="bullet">
/// <item>one-shot: chrome.runtime.sendNativeMessage starts this process for a single request;</item>
/// <item>session: a "session-open" handshake on a connectNative port, after which one process serves many
/// concurrent request/reply commands (one short pipe connection each) until Chrome closes the port;</item>
/// <item>dedicated ports: a browser-body copy (pipelined) or a managed-download watch.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string AppFileName = "VESPER.exe";
    private const int MaxInFlight = 16;
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestLimit = TimeSpan.FromMinutes(6);
    private static readonly object LaunchGate = new();
    private static DateTime _launchedUtc = DateTime.MinValue;

    private static async Task<int> Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        var replies = new ReplyWriter(output);
        string? first;
        try
        {
            using var wait = new CancellationTokenSource(RequestLimit);
            first = await BridgeProtocol.ReadFrameAsync(input, wait.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            await replies.TryWriteAsync(Serialize(BridgeResponse.Fail("unknown000", "error", "The browser message could not be processed.")));
            return 1;
        }
        if (first is null) return 0;
        if (TryReadSessionOpen(first, out string sessionId)) return await RunSessionAsync(input, replies, sessionId);
        return await RunClassicAsync(input, replies, first);
    }

    // multiplexed request session

    private static async Task<int> RunSessionAsync(Stream input, ReplyWriter replies, string sessionId)
    {
        // Answered locally: the handshake must not wake the desktop app.
        await replies.WriteAsync(Serialize(BridgeResponse.Success(sessionId, data: new { mux = true, maxInFlight = MaxInFlight })),
            CancellationToken.None);
        using var slots = new SemaphoreSlim(MaxInFlight);
        var running = new HashSet<Task>();
        try
        {
            while (true)
            {
                // No read timeout: the extension closes an idle session, which ends this loop with EOF.
                string? json = await BridgeProtocol.ReadFrameAsync(input, CancellationToken.None);
                if (json is null) break;
                await slots.WaitAsync();
                Task work = ServeSessionRequestAsync(json, replies);
                lock (running) running.Add(work);
                _ = work.ContinueWith(done =>
                {
                    lock (running) running.Remove(done);
                    slots.Release();
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Chrome closed the port or sent an invalid frame. In-flight replies have nowhere to go.
        }
        Task[] remaining;
        lock (running) remaining = [.. running];
        await Task.WhenAny(Task.WhenAll(remaining), Task.Delay(TimeSpan.FromSeconds(2)));
        return 0;
    }

    private static async Task ServeSessionRequestAsync(string json, ReplyWriter replies)
    {
        string reply;
        try
        {
            using var limit = new CancellationTokenSource(RequestLimit);
            if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out string error))
                reply = Serialize(BridgeResponse.Fail(TryReadId(json) ?? "unknown000", "invalid", error));
            else if (request.BrowserBody is { Session: true } || request.Type == "browser-activity"
                     || request.Type == "browser-managed" && request.Operation == "watch")
                reply = Serialize(BridgeResponse.Fail(request.Id, "invalid", "This command needs its own browser connection."));
            else
                reply = await HandleAsync(request, json, limit.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            reply = Serialize(BridgeResponse.Fail(TryReadId(json) ?? "unknown000", "error", "The desktop connection failed while answering."));
        }
        await replies.TryWriteAsync(reply);
    }

    // one-shot and dedicated ports

    private static async Task<int> RunClassicAsync(Stream input, ReplyWriter replies, string json)
    {
        string id = "unknown000";
        try
        {
            while (true)
            {
                using var overall = new CancellationTokenSource(RequestLimit);
                string reply;
                bool persistent = false;
                if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out string error))
                {
                    reply = Serialize(BridgeResponse.Fail(id, "invalid", error));
                }
                else
                {
                    id = request.Id;
                    if (request.BrowserBody is { Session: true, Operation: "begin" })
                        return await RunBodySessionAsync(input, replies, request, json);
                    if (request.BrowserBody is { Session: true })
                        reply = Serialize(BridgeResponse.Fail(id, "invalid", "Start a browser copy session first."));
                    else
                    {
                        // connectNative holds a port only for a live Chrome-download watch. Other commands stay one-shot.
                        persistent = request.Type == "browser-activity"
                            || request.Type == "browser-managed" && request.Operation == "watch";
                        reply = await HandleAsync(request, json, overall.Token);
                    }
                }
                await replies.WriteAsync(reply, overall.Token);
                if (!persistent) return 0;
                string? next = await BridgeProtocol.ReadFrameAsync(input, overall.Token);
                if (next is null) return 0;
                json = next;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            await replies.TryWriteAsync(Serialize(BridgeResponse.Fail(id, "error", "The browser message could not be processed.")));
            return 1;
        }
    }

    // A body copy holds one pipe. "begin" is a normal exchange; after it, chunk frames are pipelined:
    // the desktop handles them strictly in order and answers each exactly once, so replies are relayed
    // as they arrive while the next chunks are already queued. Memory is bounded by the extension's window.
    private static async Task<int> RunBodySessionAsync(Stream input, ReplyWriter replies, BridgeRequest begin, string json)
    {
        string transferId = begin.BrowserBody!.TransferId;
        NamedPipeClientStream? pipe;
        using (var opening = new CancellationTokenSource(RequestLimit))
        {
            try { pipe = await OpenBodyPipeAsync(opening.Token); }
            catch (OperationCanceledException) { pipe = null; }
        }
        if (pipe is null)
        {
            await replies.TryWriteAsync(Serialize(BridgeResponse.Fail(begin.Id, "app-unavailable", "The browser copy session is unavailable.")));
            return 0;
        }
        using var lifetime = new CancellationTokenSource();
        Task<bool>? relay = null;
        try
        {
            await using (pipe)
            {
                string opened = await BridgePipe.ExchangeAsync(pipe, json, TimeSpan.FromMinutes(2), lifetime.Token);
                await replies.WriteAsync(opened, lifetime.Token);
                if (!IsOk(opened)) return 0;
                relay = RelayRepliesAsync(pipe, replies, lifetime.Token);
                while (true)
                {
                    Task<string?> read = BridgeProtocol.ReadFrameAsync(input, lifetime.Token);
                    if (await Task.WhenAny(read, relay) == relay) return await relay ? 0 : 1;   // the desktop ended the session
                    string? frame = await read;
                    if (frame is null) return 0;   // Chrome closed the port: disposing the pipe aborts unpublished bytes
                    if (!BridgeProtocol.TryParse(frame, out BridgeRequest request, out _)
                        || request.BrowserBody is not { Session: true } body || body.TransferId != transferId
                        || body.Operation is not ("append" or "finish" or "abort"))
                    {
                        await replies.TryWriteAsync(Serialize(BridgeResponse.Fail(TryReadId(frame) ?? "invalid000", "invalid",
                            "Browser copy session changed.")));
                        return 1;
                    }
                    if (body.Operation == "finish") BridgePipe.AllowDesktopForeground(pipe);
                    await BridgeProtocol.WriteFrameAsync(pipe, frame, lifetime.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            await replies.TryWriteAsync(Serialize(BridgeResponse.Fail(begin.Id, "error", "The browser copy connection failed.")));
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            if (relay is not null)
            {
                try { await relay; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static async Task<bool> RelayRepliesAsync(Stream pipe, ReplyWriter replies, CancellationToken ct)
    {
        while (true)
        {
            string? frame = await BridgeProtocol.ReadFrameAsync(pipe, ct);
            if (frame is null) return true;
            await replies.WriteAsync(frame, ct);
        }
    }

    private static async Task<NamedPipeClientStream?> OpenBodyPipeAsync(CancellationToken ct)
    {
        string pipe = BridgePipe.NameForCurrentUser();
        async Task<NamedPipeClientStream?> TryConnect()
        {
            try { return await BridgePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(500), ct); }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
        }
        NamedPipeClientStream? connected = await TryConnect();
        if (connected is not null || !TryStartApp()) return connected;
        DateTime deadline = DateTime.UtcNow + LaunchWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, ct);
            connected = await TryConnect();
            if (connected is not null) return connected;
        }
        return null;
    }

    private static async Task<string> HandleAsync(BridgeRequest request, string rawJson, CancellationToken ct)
    {
        string pipe = BridgePipe.NameForCurrentUser();
        bool bringReviewForward = request.Type is "batch-review" or "media-picker" or "submit" or "media-select"
            || request.Type == "browser-managed" && request.Operation == "connect"
            || request.Type == "browser-transfer" && request.Operation == "accept"
            || request.Type == "browser-body" && request.BrowserBody?.Operation == "finish";

        string? answer = await TryRequestAsync(pipe, rawJson, ct, bringReviewForward);
        if (answer is not null) return answer;

        // A browser download, a parked-transfer question (so a crashed desktop can recover its job) or a format
        // request can wake the desktop. Watches, managed completions and body follow-ups never start it.
        if (request is { Type: "browser-activity", Operation: "watch" }
            || request is { Type: "browser-managed", Operation: "watch" or "complete" }
            || request is { Type: "browser-body", BrowserBody.Operation: not "begin" }
            || request.Type == "browser-diagnostics"
            || !TryStartApp())
            return Serialize(BridgeResponse.Fail(request.Id, "app-unavailable", "The desktop connection is unavailable."));

        DateTime deadline = DateTime.UtcNow + LaunchWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, ct);
            answer = await TryRequestAsync(pipe, rawJson, ct, bringReviewForward);
            if (answer is not null) return answer;
        }
        return Serialize(BridgeResponse.Fail(request.Id, "app-unavailable", "VESPER started but did not answer in time. Try again in a moment."));
    }

    private static async Task<string?> TryRequestAsync(string pipe, string json, CancellationToken ct, bool bringReviewForward)
    {
        try { return await BridgePipe.RequestAsync(pipe, json, TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(5), ct, bringReviewForward); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
    }

    // Concurrent session requests share one launch: the others wait for the same desktop instance.
    private static bool TryStartApp()
    {
        lock (LaunchGate)
        {
            if (DateTime.UtcNow - _launchedUtc < LaunchWait) return true;
            string? path = FindApp();
            if (path is null) return false;
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(path, "--background")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path)!
                });
                if (process is null) return false;
                _launchedUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
    }

    private static string? FindApp()
    {
        string installed = Path.Combine(AppContext.BaseDirectory, AppFileName);
        if (File.Exists(installed)) return installed;

        // The F5 helper lives under src/Vesper.NativeHost/bin/{Debug,Release}.
        // Resolve only the matching desktop build in that same checkout.
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        if (framework.Name == "win-x64" && framework.Parent is { } parent) framework = parent;
        var config = framework.Parent;
        var bin = config?.Parent;
        var project = bin?.Parent;
        var source = project?.Parent;
        var root = source?.Parent;
        if (framework.Name != "net10.0" || config?.Name is not ("Debug" or "Release") || bin?.Name != "bin"
            || project?.Name != "Vesper.NativeHost" || source?.Name != "src" || root is null
            || !File.Exists(Path.Combine(root.FullName, "VESPER.csproj")))
            return null;
        string output = Path.Combine(root.FullName, "bin", config.Name, "net10.0-windows");
        string app = Path.Combine(output, AppFileName);
        if (File.Exists(app)) return app;
        app = Path.Combine(output, "win-x64", AppFileName);
        return File.Exists(app) ? app : null;
    }

    // small helpers

    private static bool TryReadSessionOpen(string json, out string id)
    {
        id = "";
        if (json.Length > 512) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "session-open"
                || !root.TryGetProperty("v", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != BridgeProtocol.Version)
                return false;
            string? candidate = root.TryGetProperty("id", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (!IsRequestId(candidate)) return false;
            id = candidate!;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? TryReadId(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out JsonElement value) && value.ValueKind == JsonValueKind.String
                && IsRequestId(value.GetString()) ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsOk(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    // Mirrors BridgeProtocol.IsRequestId, which is internal to the engine assembly.
    private static bool IsRequestId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Serialize(BridgeResponse response) => JsonSerializer.Serialize(response, BridgeProtocol.Json);

    /// <summary>Frames from concurrent requests must never interleave on Chrome's stdin pipe.</summary>
    private sealed class ReplyWriter(Stream output)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(string json, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try { await BridgeProtocol.WriteFrameAsync(output, json, ct); }
            finally { _gate.Release(); }
        }

        public async Task TryWriteAsync(string json)
        {
            try { await WriteAsync(json, CancellationToken.None); }
            catch (IOException) { /* Chrome already closed the port. */ }
        }
    }
}
