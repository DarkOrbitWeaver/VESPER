using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Engine;

public static class BridgePipe
{
    /// <summary>Per-user pipe name, so another Windows account on the same PC can neither reach nor squat it.</summary>
    public static string NameForCurrentUser()
    {
        string identity = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        return "vesper-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    }

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
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public BridgeServer(string pipeName, Func<BridgeRequest, CancellationToken, Task<BridgeResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
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

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop ended by cancellation */ }
        _stop.Dispose();
    }
}
