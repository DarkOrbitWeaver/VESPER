using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Vesper.Shared;

public static class SetupExitProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const string RequestType = "setup-exit";

    public static string PipeNameForCurrentUser() => WindowsObjectNames.NameForCurrentUser("vesper");

    public static async Task<bool> RequestExitAsync(TimeSpan connectTimeout, TimeSpan replyTimeout, CancellationToken ct)
        => await RequestExitAsync(PipeNameForCurrentUser(), connectTimeout, replyTimeout, ct).ConfigureAwait(false);

    public static async Task<bool> RequestExitAsync(string pipeName, TimeSpan connectTimeout, TimeSpan replyTimeout, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(connectTimeout);
            try { await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("VESPER did not answer the setup shutdown request.");
            }
        }

        using var reply = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reply.CancelAfter(replyTimeout);
        await WriteFrameAsync(pipe, CreateRequest(id), reply.Token).ConfigureAwait(false);
        string response = await ReadFrameAsync(pipe, reply.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("VESPER closed the setup shutdown request without answering.");
        if (!TryReadResponse(response, id, out bool accepted))
            throw new InvalidDataException("VESPER returned an invalid setup shutdown response.");
        return accepted;
    }

    public static bool TryReadRequest(string json, out string id)
    {
        id = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("v", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != Version || !root.TryGetProperty("id", out JsonElement requestId)
                || requestId.ValueKind != JsonValueKind.String || !IsRequestId(requestId.GetString())
                || !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != RequestType)
                return false;
            id = requestId.GetString()!;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static string CreateRequest(string id) => JsonSerializer.Serialize(new { v = Version, id, type = RequestType });

    public static string CreateResponse(string id, bool accepted) => JsonSerializer.Serialize(new { v = Version, id, accepted });

    public static bool TryReadResponse(string json, string expectedId, out bool accepted)
    {
        accepted = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("v", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != Version || !root.TryGetProperty("id", out JsonElement requestId)
                || requestId.ValueKind != JsonValueKind.String || requestId.GetString() != expectedId
                || !root.TryGetProperty("accepted", out JsonElement decision)
                || decision.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            accepted = decision.GetBoolean();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct) => ReadFrameCoreAsync(stream, ct);

    internal static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Message size is not allowed.");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadFrameCoreAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[sizeof(int)];
        int headerBytes = await ReadExactAsync(stream, header, allowCleanEnd: true, ct).ConfigureAwait(false);
        if (headerBytes == 0) return null;
        if (headerBytes != header.Length) throw new InvalidDataException("Truncated message header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Message size is not allowed.");
        byte[] body = new byte[length];
        if (await ReadExactAsync(stream, body, allowCleanEnd: false, ct).ConfigureAwait(false) != body.Length)
            throw new InvalidDataException("Truncated message body.");
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, bool allowCleanEnd, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEnd && total == 0) return 0;
                return total;
            }
            total += read;
        }
        return total;
    }

    private static bool IsRequestId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
