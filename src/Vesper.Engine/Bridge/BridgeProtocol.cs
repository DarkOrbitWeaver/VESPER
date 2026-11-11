using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vesper.Engine;

public sealed record BridgeItem(string Url, string? FileName, string Kind, string? Referer, string? UserAgent, string? Sha256, string? Cookie, string? Authorization);

public sealed record BridgeMediaHint(string Url, string? Kind, string? MimeType);

public sealed record BridgeRequest(string Id, string Type, string Mode, string? Url, IReadOnlyList<BridgeItem> Items,
    IReadOnlyList<BridgeMediaHint>? Candidates = null, string? PageTitle = null);

public sealed record BridgeResult(string Url, string Status, string? JobId, string Message);

public sealed record BridgeResponse(int V, string Id, bool Ok, string Code, string Message,
    IReadOnlyList<BridgeResult>? Results = null, object? Data = null, string? AppVersion = null)
{
    public static BridgeResponse Fail(string id, string code, string message) => new(1, id, false, code, message);
    public static BridgeResponse Success(string id, string message = "", IReadOnlyList<BridgeResult>? results = null, object? data = null, string? version = null)
        => new(1, id, true, "ok", message, results, data, version);
}

/// <summary>
/// Chrome native-messaging framing and strict validation of everything the browser may ask for.
/// The browser can request: hello, probe (describe a link), submit (add links). It can NOT choose a destination
/// folder, a file path, a program to run or any desktop action: such fields are never read.
/// </summary>
public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxItems = 50;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- framing: 4-byte little-endian length, then UTF-8 JSON -------------------------------------------------------

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        int got = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;                                       // clean end of input
        if (got < 4) throw new InvalidDataException("Truncated message header.");

        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException($"Message length {length} is not allowed.");

        var body = new byte[length];
        if (await ReadExactAsync(stream, body, ct).ConfigureAwait(false) < length) throw new InvalidDataException("Truncated message body.");
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxMessageBytes) throw new InvalidDataException("Response too large.");
        await stream.WriteAsync(BitConverter.GetBytes(body.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ---- validation ---------------------------------------------------------------------------------------------------

    public static bool TryParse(string json, out BridgeRequest request, out string error)
    {
        request = null!;
        error = "";
        if (json.Length > MaxMessageBytes) { error = "Message too large."; return false; }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "Message must be an object."; return false; }
            if (!root.TryGetProperty("v", out JsonElement v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != Version)
            { error = "Unsupported protocol version."; return false; }

            string id = Str(root, "id") ?? "";
            if (id.Length is < 8 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            { error = "Invalid request id."; return false; }

            string type = Str(root, "type") ?? "";
            if (type is not ("hello" or "probe" or "submit" or "media-picker")) { error = "Unknown request type."; return false; }

            string mode = Str(root, "mode") ?? "ask";
            if (mode is not ("ask" or "download" or "queue")) { error = "Unknown mode."; return false; }

            var items = new List<BridgeItem>();
            var hints = new List<BridgeMediaHint>();
            string? probeUrl = null;
            if (type is "probe" or "media-picker")
            {
                probeUrl = Str(root, "url");
                if (!UrlPolicy.TryParse(probeUrl, out _, out error)) return false;
                if (type == "media-picker" && root.TryGetProperty("candidates", out JsonElement candidates))
                {
                    if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 25)
                    { error = "At most 25 media hints are accepted."; return false; }
                    foreach (JsonElement hint in candidates.EnumerateArray())
                    {
                        if (hint.ValueKind != JsonValueKind.Object)
                        { error = "Each media hint must be an object."; return false; }
                        if (!UrlPolicy.TryParse(Str(hint, "url"), out Uri hintUrl, out error)) return false;
                        string? kind = Str(hint, "kind");
                        if (kind is not (null or "direct" or "hls" or "dash"))
                        { error = "Invalid media-hint type."; return false; }
                        string? mime = Str(hint, "mimeType");
                        if (mime is { Length: > 100 } || mime is not null && mime.Any(char.IsControl))
                        { error = "Invalid media-hint MIME type."; return false; }
                        hints.Add(new BridgeMediaHint(hintUrl.AbsoluteUri, kind, mime));
                    }
                }
            }
            else if (type == "submit")
            {
                if (!root.TryGetProperty("items", out JsonElement array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > MaxItems)
                { error = $"Provide between 1 and {MaxItems} items."; return false; }
                bool authorized = root.TryGetProperty("authorized", out JsonElement a) && a.ValueKind == JsonValueKind.True;

                foreach (JsonElement element in array.EnumerateArray())
                {
                    if (!TryItem(element, authorized, out BridgeItem? item, out error)) return false;
                    items.Add(item!);
                }
            }
            request = new BridgeRequest(id, type, mode, probeUrl, items,
                hints, type == "media-picker" ? (Str(root, "pageTitle") is { Length: <= 300 } title ? title : null) : null);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "Malformed message.";
            return false;
        }
    }

    private static bool TryItem(JsonElement e, bool authorized, out BridgeItem? item, out string error)
    {
        item = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "Each item must be an object."; return false; }
        if (!UrlPolicy.TryParse(Str(e, "url"), out Uri uri, out error)) return false;

        string kind = Str(e, "kind") ?? "http";
        if (kind is not ("http" or "hls")) { error = "Unsupported item kind."; return false; }

        string? referer = Header(e, "referer", 2048), agent = Header(e, "userAgent", 512);
        string? sha = Str(e, "sha256");
        if (sha is not null && !DownloadEngine.TryNormalizeSha256(sha, out sha)) { error = "Invalid SHA-256."; return false; }

        // Credentials are accepted only with explicit user consent flagged by the extension, and stay in memory.
        string? cookie = authorized ? Header(e, "cookie", 8192) : null;
        string? authorization = authorized ? Header(e, "authorization", 4096) : null;
        if ((cookie ?? authorization) is null && authorized && (e.TryGetProperty("cookie", out _) || e.TryGetProperty("authorization", out _)))
        { error = "Invalid credential header."; return false; }

        item = new BridgeItem(uri.AbsoluteUri, Str(e, "fileName") is { Length: > 0 } n ? FileNames.Sanitize(n) : null, kind, referer, agent, sha, cookie, authorization);
        error = "";
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>A header value: bounded, and never containing line breaks (header injection).</summary>
    private static string? Header(JsonElement e, string name, int max)
    {
        string? value = Str(e, name);
        return value is { Length: > 0 } && value.Length <= max && !value.Any(c => c is '\r' or '\n' or '\0') ? value : null;
    }
}
