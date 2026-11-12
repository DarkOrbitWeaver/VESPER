using System.Diagnostics;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.NativeHost;

/// <summary>
/// Chrome Native Messaging host. Relays validated messages over the per-user pipe. Ordinary requests are
/// one-shot. A live Chrome download can hold a metadata-only port until that download ends.
/// </summary>
internal static class Program
{
    private const string AppFileName = "VESPER.exe";
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(12);

    private static async Task<int> Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        string id = "unknown000";
        try
        {
            while (true)
            {
                using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                string? json = await BridgeProtocol.ReadFrameAsync(input, overall.Token);
                if (json is null) return 0;
                string reply;
                bool persistent = false;
                if (!BridgeProtocol.TryParse(json, out BridgeRequest request, out string error))
                {
                    reply = Serialize(BridgeResponse.Fail(id, "invalid", error));
                }
                else
                {
                    id = request.Id;
                    persistent = request.Type == "browser-activity";
                    reply = await HandleAsync(request, json, overall.Token);
                }
                await BridgeProtocol.WriteFrameAsync(output, reply, overall.Token);
                // connectNative holds a port only for live Chrome downloads. Other commands stay one-shot.
                if (!persistent) return 0;
            }
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

        string? answer = await TryRequestAsync(pipe, rawJson, ct);
        if (answer is not null) return answer;

        // A browser download or format request can wake the desktop.
        if (request is { Type: "browser-activity", Operation: "watch" } || !TryStartApp())
            return Serialize(BridgeResponse.Fail(request.Id, "app-unavailable", "The desktop connection is unavailable."));

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

    private static bool TryStartApp()
    {
        string? path = FindApp();
        if (path is null) return false;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path, "--background")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
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

    private static string Serialize(BridgeResponse response) => JsonSerializer.Serialize(response, BridgeProtocol.Json);
}
