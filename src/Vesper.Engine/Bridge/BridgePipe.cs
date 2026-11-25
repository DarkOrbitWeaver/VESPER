using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Vesper.Shared;

namespace Vesper.Engine;

public static class BridgePipe
{
    /// <summary>Per-user pipe name, so another Windows account on the same PC can neither reach nor squat it.</summary>
    public static string NameForCurrentUser() => SetupExitProtocol.PipeNameForCurrentUser();

    public static string NameForCurrentUser(string purpose) => WindowsObjectNames.NameForCurrentUser(purpose);

    internal static string NameForIdentity(string purpose, string identity) => WindowsObjectNames.NameForIdentity(purpose, identity);

    /// <summary>One request, one response, then close. Used by the native-messaging host.</summary>
    public static async Task<string> RequestAsync(string pipeName, string json, TimeSpan connectTimeout, TimeSpan replyTimeout,
        CancellationToken ct, bool bringReviewForward = false)
    {
        await using var client = await ConnectAsync(pipeName, connectTimeout, ct).ConfigureAwait(false);
        if (bringReviewForward && OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(client.SafePipeHandle, out uint processId))
            _ = AllowSetForegroundWindow(processId);
        return await ExchangeAsync(client, json, replyTimeout, ct).ConfigureAwait(false);
    }

    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(timeout);
            try { await client.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("VESPER is not accepting connections."); }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    // The caller serializes exchanges; the body session never pipelines frames.
    public static async Task<string> ExchangeAsync(Stream client, string json, TimeSpan replyTimeout, CancellationToken ct)
    {
        using var reply = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reply.CancelAfter(replyTimeout);
        await BridgeProtocol.WriteFrameAsync(client, json, reply.Token).ConfigureAwait(false);
        return await BridgeProtocol.ReadFrameAsync(client, reply.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("VESPER closed the connection without answering.");
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}

/// <summary>
/// Listens on the per-user pipe and hands validated requests to a handler. Connections are short-lived;
/// the server holds no state between requests, and there is no polling.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<BridgeRequest, CancellationToken, Task<BridgeResponse>> _handler;
    private readonly Func<string, CancellationToken, Task<string?>>? _setupExitHandler;
    private readonly Action? _setupExitResponseWritten;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _loop;
    private Task? _stopping;

    public BridgeServer(string pipeName, Func<BridgeRequest, CancellationToken, Task<BridgeResponse>> handler,
        Func<string, CancellationToken, Task<string?>>? setupExitHandler = null,
        Action? setupExitResponseWritten = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _setupExitHandler = setupExitHandler;
        _setupExitResponseWritten = setupExitResponseWritten;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping is not null, this);
            _loop ??= Task.Run(AcceptLoopAsync);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);   // only the same Windows user may connect
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);                   // transient: another instance is being torn down
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            lock (_gate)
            {
                Task connection = Task.Run(() => ServeAsync(pipe));
                _connections.Add(connection);
                _ = connection.ContinueWith(task =>
                {
                    _ = task.Exception;
                    lock (_gate) _connections.Remove(task);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            string id = "unknown000";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));       // "ask" mode waits for the user

                string? json = await BridgeProtocol.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (json is null) return;

                if (SetupExitProtocol.TryReadRequest(json, out string setupRequestId))
                {
                    id = setupRequestId;
                    using var setupRequest = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    Task<bool> clientClosed = WaitForSetupClientCloseAsync(pipe, setupRequest.Token);
                    Task<string?> setupResponse = _setupExitHandler is null
                        ? Task.FromResult<string?>(SetupExitProtocol.CreateResponse(id, accepted: false))
                        : _setupExitHandler(json, setupRequest.Token);
                    Task finished = await Task.WhenAny(setupResponse, clientClosed).ConfigureAwait(false);
                    if (finished == clientClosed && await clientClosed.ConfigureAwait(false))
                    {
                        setupRequest.Cancel();
                        try { _ = await setupResponse.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        return;
                    }

                    string responseJson = await setupResponse.ConfigureAwait(false)
                        ?? SetupExitProtocol.CreateResponse(id, accepted: false);
                    setupRequest.Cancel();
                    try { await clientClosed.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    bool accepted = SetupExitProtocol.TryReadResponse(responseJson, id, out bool decision) && decision;
                    await SetupExitProtocol.WriteFrameAsync(pipe, responseJson, timeout.Token).ConfigureAwait(false);
                    if (accepted) _setupExitResponseWritten?.Invoke();
                    return;
                }

                BridgeResponse response;
                if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out string error))
                {
                    response = BridgeResponse.Fail(id, "invalid", error);
                }
                else
                {
                    id = request.Id;
                    if (request.BrowserBody is { Session: true } body)
                    {
                        if (body.Operation != "begin")
                        {
                            await BridgeProtocol.WriteFrameAsync(pipe,
                                JsonSerializer.Serialize(BridgeResponse.Fail(id, "invalid", "Start a browser body session first."), BridgeProtocol.Json),
                                timeout.Token).ConfigureAwait(false);
                            return;
                        }
                        await ServeBodySessionAsync(pipe, request, timeout.Token).ConfigureAwait(false);
                        return;
                    }
                    try { response = await _handler(request, timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { response = BridgeResponse.Fail(id, "cancelled", "The request timed out."); }
                }
                await BridgeProtocol.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, BridgeProtocol.Json), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // The peer went away or sent garbage. Nothing to recover; the connection is simply dropped.
            }
        }
    }

    // A session holds one transfer ID and accepts only sequential body commands.
    // Disconnect cancels unpublished bytes immediately; an accepted job is immutable.
    private async Task ServeBodySessionAsync(Stream pipe, BridgeRequest begin, CancellationToken firstToken)
    {
        string transferId = begin.BrowserBody!.TransferId;
        bool opened = false, ended = false;
        try
        {
            BridgeResponse initial = await _handler(begin, firstToken).ConfigureAwait(false);
            opened = initial.Ok;
            await BridgeProtocol.WriteFrameAsync(pipe, JsonSerializer.Serialize(initial, BridgeProtocol.Json), firstToken).ConfigureAwait(false);
            if (!opened) return;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                string? json = await BridgeProtocol.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (json is null) return;
                if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out _)
                    || request.BrowserBody is not { Session: true } body
                    || body.TransferId != transferId || body.Operation is not ("append" or "finish" or "abort"))
                {
                    await BridgeProtocol.WriteFrameAsync(pipe,
                        JsonSerializer.Serialize(BridgeResponse.Fail("invalid000", "invalid", "Browser body session changed."), BridgeProtocol.Json),
                        timeout.Token).ConfigureAwait(false);
                    return;
                }
                if (body.Operation == "finish") timeout.CancelAfter(TimeSpan.FromMinutes(10));
                BridgeResponse response = await _handler(request, timeout.Token).ConfigureAwait(false);
                ended = response.Ok && body.Operation is "finish" or "abort";
                await BridgeProtocol.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, BridgeProtocol.Json), timeout.Token).ConfigureAwait(false);
                if (ended || !response.Ok) return;
            }
        }
        finally
        {
            if (opened && !ended)
            {
                // This may race a successful finish whose reply was lost. The engine
                // treats abort of an already accepted ID as a read-only receipt.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _handler(begin with { BrowserBody = new BrowserBodyRequest("abort", transferId) }, cleanup.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static async Task<bool> WaitForSetupClientCloseAsync(Stream pipe, CancellationToken ct)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (await pipe.ReadAsync(buffer, ct).ConfigureAwait(false) != 0) { }
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_gate) return _stopping ??= Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        _stop.Cancel();
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        finally
        {
            Task[] connections;
            lock (_gate) connections = [.. _connections];
            try { await Task.WhenAll(connections).ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) { System.Diagnostics.Trace.WriteLine(ex.Message, "Browser bridge shutdown"); }
    }
}
