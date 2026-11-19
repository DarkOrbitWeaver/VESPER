using System.IO.Pipes;
using System.Text.Json;
using Vesper.Shared;

namespace Vesper.Engine;

public static class BridgePipe
{
    /// <summary>Per-user pipe name, so another Windows account on the same PC can neither reach nor squat it.</summary>
    public static string NameForCurrentUser() => SetupExitProtocol.PipeNameForCurrentUser();

    public static string NameForCurrentUser(string purpose) => WindowsObjectNames.NameForCurrentUser(purpose);

    internal static string NameForIdentity(string purpose, string identity) => WindowsObjectNames.NameForIdentity(purpose, identity);

    /// <summary>One request, one response, then close. Used by the native-messaging host.</summary>
    public static async Task<string> RequestAsync(string pipeName, string json, TimeSpan connectTimeout, TimeSpan replyTimeout, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(connectTimeout);
            try { await client.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("VESPER is not accepting connections.");   // lets callers tell "not running" from "no answer"
            }
        }
        using var reply = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reply.CancelAfter(replyTimeout);
        await BridgeProtocol.WriteFrameAsync(client, json, reply.Token).ConfigureAwait(false);
        return await BridgeProtocol.ReadFrameAsync(client, reply.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("VESPER closed the connection without answering.");
    }
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
    private Task? _loop;

    public BridgeServer(string pipeName, Func<BridgeRequest, CancellationToken, Task<BridgeResponse>> handler,
        Func<string, CancellationToken, Task<string?>>? setupExitHandler = null,
        Action? setupExitResponseWritten = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _setupExitHandler = setupExitHandler;
        _setupExitResponseWritten = setupExitResponseWritten;
    }

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

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
            _ = Task.Run(() => ServeAsync(pipe));
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

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop ended by cancellation */ }
        _stop.Dispose();
    }
}
