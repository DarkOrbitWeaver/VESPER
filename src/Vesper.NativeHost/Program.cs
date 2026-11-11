using System.Diagnostics;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.NativeHost;

/// <summary>
/// Chrome Native Messaging host. Chrome starts it for one message, it relays that message to the running
/// VESPER over the per-user pipe, writes the answer to stdout and exits. It holds no state and does no
/// downloading. Everything it forwards has been validated first, and it never writes anything but a reply to stdout.
/// </summary>
internal static class Program
{
    private const string AppFileName = "VESPER.exe";
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(12);

    private static async Task<int> Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        string id = "unknown000";
        try
        {
            string? json = await BridgeProtocol.ReadFrameAsync(input, overall.Token);
            if (json is null) return 0;

            string reply;
            if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out string error))
            {
                reply = Serialize(BridgeResponse.Fail(id, "invalid", error));
            }
            else
            {
                id = request.Id;
                reply = await HandleAsync(request, json, overall.Token);
            }
            await BridgeProtocol.WriteFrameAsync(output, reply, overall.Token);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            try { await BridgeProtocol.WriteFrameAsync(output, Serialize(BridgeResponse.Fail(id, "error", "The browser message could not be processed.")), CancellationToken.None); }
            catch (IOException) { /* Chrome already closed the pipe */ }
            return 1;
        }
    }

    private static async Task<string> HandleAsync(BridgeRequest request, string rawJson, CancellationToken ct)
    {
        string pipe = BridgePipe.NameForCurrentUser();

        if (request.Type == "hello")
        {
            bool running = await TryRequestAsync(pipe, rawJson, ct) is not null;
            return Serialize(BridgeResponse.Success(request.Id, running ? "VESPER is running." : "VESPER is not running.",
                data: new { hostVersion = "1.0.0", appRunning = running }));
        }

        string? answer = await TryRequestAsync(pipe, rawJson, ct);
        if (answer is not null) return answer;

        // The desktop app is not listening. A user-initiated submit or probe may start it; nothing else may.
        if (!TryStartApp()) return Serialize(BridgeResponse.Fail(request.Id, "app-unavailable", "VESPER is not running and could not be started. Open VESPER and try again."));

        DateTime deadline = DateTime.UtcNow + LaunchWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, ct);
            answer = await TryRequestAsync(pipe, rawJson, ct);
            if (answer is not null) return answer;
        }
        return Serialize(BridgeResponse.Fail(request.Id, "app-unavailable", "VESPER started but did not answer in time. Try again in a moment."));
    }

    private static async Task<string?> TryRequestAsync(string pipe, string json, CancellationToken ct)
    {
        try { return await BridgePipe.RequestAsync(pipe, json, TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(5), ct); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Starts VESPER from the fixed path next to this host. No user- or browser-supplied value reaches the command line.</summary>
    private static bool TryStartApp()
    {
        string path = Path.Combine(AppContext.BaseDirectory, AppFileName);
        if (!File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path, "--background") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static string Serialize(BridgeResponse response) => JsonSerializer.Serialize(response, BridgeProtocol.Json);
}
