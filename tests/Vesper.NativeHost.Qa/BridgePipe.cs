using System.IO.Pipes;

namespace Vesper.NativeHost;

// The production helper source is linked unchanged. Only pipe selection is
// isolated, and a missing/invalid QA pipe cannot fall back to the user's pipe.
internal static class BridgePipe
{
    public static string NameForCurrentUser()
    {
        string? name = Environment.GetEnvironmentVariable("VESPER_QA_PIPE");
        if (name is null || !name.StartsWith("vesper-qa-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["vesper-qa-".Length..], "N", out _))
            throw new InvalidOperationException("A private QA pipe is required.");
        return name;
    }

    public static Task<NamedPipeClientStream> ConnectAsync(string pipe, TimeSpan timeout, CancellationToken ct) =>
        Vesper.Engine.BridgePipe.ConnectAsync(pipe, timeout, ct);

    public static Task<string> ExchangeAsync(Stream pipe, string json, TimeSpan replyTimeout, CancellationToken ct) =>
        Vesper.Engine.BridgePipe.ExchangeAsync(pipe, json, replyTimeout, ct);

    public static void AllowDesktopForeground(NamedPipeClientStream client) =>
        Vesper.Engine.BridgePipe.AllowDesktopForeground(client);

    public static Task<string> RequestAsync(string pipe, string json, TimeSpan connectTimeout,
        TimeSpan replyTimeout, CancellationToken ct, bool bringReviewForward = false) =>
        Vesper.Engine.BridgePipe.RequestAsync(pipe, json, connectTimeout, replyTimeout, ct, bringReviewForward);
}
