using System.Text.Json;
using System.Text.Json.Serialization;
using Vesper.Shared;

namespace Vesper.Engine;

public sealed record BridgeItem(string Url, string? FileName, string Kind, string? Referer, string? UserAgent, string? Sha256,
    string? Cookie, string? Authorization, string? OriginalUrl = null);

public sealed record BridgeMediaHint(string Url, string? Kind, string? MimeType, bool MutedLoop = false);

public sealed record BridgeRequest(string Id, string Type, string Mode, string? Url, IReadOnlyList<BridgeItem> Items,
    IReadOnlyList<BridgeMediaHint>? Candidates = null, string? PageTitle = null, bool ConfirmTransfer = false,
    Guid? MediaSession = null, int? FormatChoice = null, BrowserTransferProof? BrowserProof = null,
    string? TransferId = null, string? Operation = null, BrowserActivity? Activity = null,
    MediaTimeRange? Clip = null, BridgeItem? MediaAuth = null, string? AudioTrackId = null,
    IReadOnlyList<string>? SubtitleLanguages = null, bool IncludeAutoSubtitles = false,
    bool EmbedSubtitles = false);

/// <param name="TransferReason">Why a Chrome handoff was not ready ("auth", "queued", "slow", ...). The extension turns it into plain words.</param>
public sealed record BridgeResult(string Url, string Status, string? JobId, string Message, bool? TransferReady = null,
    string? TransferReason = null);

public sealed record BridgeResponse(int V, string Id, bool Ok, string Code, string Message,
    IReadOnlyList<BridgeResult>? Results = null, object? Data = null, string? AppVersion = null)
{
    public static BridgeResponse Fail(string id, string code, string message) => new(1, id, false, code, message);
    public static BridgeResponse Success(string id, string message = "", IReadOnlyList<BridgeResult>? results = null, object? data = null, string? version = null)
        => new(1, id, true, "ok", message, results, data, version);
}

/// <summary>
/// Chrome native-messaging framing and strict validation of everything the browser may ask for.
/// Handles browser connection, discovery, and download requests. The desktop resolves
/// format selections from its own session and controls all destination paths.
/// </summary>
public static class BridgeProtocol
{
    public const int Version = SetupExitProtocol.Version;
    public const int MaxMessageBytes = SetupExitProtocol.MaxMessageBytes;
    public const int MaxItems = 50;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- framing: 4-byte little-endian length, then UTF-8 JSON -------------------------------------------------------

    public static Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct) => SetupExitProtocol.ReadFrameAsync(stream, ct);

    public static Task WriteFrameAsync(Stream stream, string json, CancellationToken ct) => SetupExitProtocol.WriteFrameAsync(stream, json, ct);

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
            if (!IsRequestId(id))
            { error = "Invalid request id."; return false; }

            string type = Str(root, "type") ?? "";
            if (type is not ("hello" or "probe" or "submit" or "batch-review" or "media-picker" or "media-options" or "media-select"
                or "browser-submit" or "browser-transfer" or "browser-activity"))
            { error = "Unknown request type."; return false; }

            string mode = Str(root, "mode") ?? "ask";
            if (mode is not ("ask" or "download" or "queue")) { error = "Unknown mode."; return false; }

            var items = new List<BridgeItem>();
            var hints = new List<BridgeMediaHint>();
            string? probeUrl = null;
            Guid? mediaSession = null;
            int? formatChoice = null;
            MediaTimeRange? clip = null;
            string? audioTrack = null;
            List<string>? subtitleLanguages = null;
            bool autoSubtitles = false;
            bool embedSubtitles = false;
            BridgeItem? mediaAuth = null;
            BrowserTransferProof? browserProof = null;
            BrowserActivity? activity = null;
            string? transferId = null, operation = null;
            if (type is "probe" or "media-picker" or "media-options")
            {
                probeUrl = Str(root, "url");
                if (!UrlPolicy.TryParse(probeUrl, out _, out error)) return false;
                if (type is ("media-picker" or "media-options") && root.TryGetProperty("mediaAuth", out var auth))
                {
                    bool authorized = root.TryGetProperty("authorized", out var consent) && consent.ValueKind == JsonValueKind.True;
                    if (!authorized || !TryItem(auth, authorized, false, out mediaAuth, out error)
                        || mediaAuth is null || mediaAuth.Url != probeUrl || mediaAuth.Kind != "http")
                    { error = "Invalid media authorization context."; return false; }
                }
                if (type is ("media-picker" or "media-options") && root.TryGetProperty("candidates", out JsonElement candidates))
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
                        bool mutedLoop = hint.TryGetProperty("mutedLoop", out var muted) && muted.ValueKind == JsonValueKind.True;
                        hints.Add(new BridgeMediaHint(hintUrl.AbsoluteUri, kind, mime, mutedLoop));
                    }
                }
            }
            else if (type == "media-select")
            {
                if (mode == "ask" || !Guid.TryParseExact(Str(root, "mediaSession"), "N", out Guid session)
                    || !root.TryGetProperty("formatChoice", out var choice) || choice.ValueKind != JsonValueKind.Number
                    || !choice.TryGetInt32(out int index) || index is < 0 or >= 64)
                { error = "Invalid media selection."; return false; }
                audioTrack = Str(root, "audioTrack");
                if (audioTrack is { Length: > 256 } || audioTrack?.Any(char.IsControl) == true)
                { error = "Invalid audio track."; return false; }
                if (root.TryGetProperty("subtitles", out var subtitles))
                {
                    if (subtitles.ValueKind != JsonValueKind.Array || subtitles.GetArrayLength() > 25)
                    { error = "Invalid subtitle selection."; return false; }
                    subtitleLanguages = [];
                    foreach (var language in subtitles.EnumerateArray())
                    {
                        if (language.ValueKind != JsonValueKind.String || language.GetString() is not { Length: > 0 and <= 35 } tag
                            || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                        { error = "Invalid subtitle language."; return false; }
                        if (!subtitleLanguages.Contains(tag)) subtitleLanguages.Add(tag);
                    }
                }
                autoSubtitles = root.TryGetProperty("includeAutoSubtitles", out var auto) && auto.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("embedSubtitles", out var embed))
                {
                    if (embed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    { error = "Invalid subtitle embedding preference."; return false; }
                    embedSubtitles = embed.ValueKind == JsonValueKind.True;
                }
                if (embedSubtitles && subtitleLanguages is not { Count: > 0 })
                { error = "Choose at least one subtitle language before embedding."; return false; }
                mediaSession = session;
                formatChoice = index;
                // "Download part": optional start/end in seconds, checked again by the planner.
                if (root.TryGetProperty("range", out JsonElement range) && range.ValueKind != JsonValueKind.Null)
                {
                    if (range.ValueKind != JsonValueKind.Object
                        || !range.TryGetProperty("start", out JsonElement startValue) || startValue.ValueKind != JsonValueKind.Number
                        || !range.TryGetProperty("end", out JsonElement endValue) || endValue.ValueKind != JsonValueKind.Number
                        || !startValue.TryGetDouble(out double start) || !endValue.TryGetDouble(out double end)
                        || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > 31_536_000)
                    { error = "Invalid time range."; return false; }
                    clip = new MediaTimeRange(start, end);
                }
            }
            else if (type is "submit" or "batch-review" or "browser-submit")
            {
                if (!root.TryGetProperty("items", out JsonElement array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > MaxItems)
                { error = $"Provide between 1 and {MaxItems} items."; return false; }
                bool authorized = root.TryGetProperty("authorized", out JsonElement a) && a.ValueKind == JsonValueKind.True;

                foreach (JsonElement element in array.EnumerateArray())
                {
                    if (!TryItem(element, authorized, type == "browser-submit", out BridgeItem? item, out error)) return false;
                    items.Add(item!);
                }
                if (type == "browser-submit")
                {
                    if (items is not [{ Kind: "http" }] || !root.TryGetProperty("browserProof", out JsonElement proof)
                        || proof.ValueKind != JsonValueKind.Object
                        || !TryLength(proof, "length", out long? length))
                    { error = "Invalid browser download proof."; return false; }
                    browserProof = new BrowserTransferProof(length, Str(proof, "etag"), Str(proof, "contentType"));
                    if (!browserProof.IsValid)
                    { error = "Invalid browser download proof."; return false; }
                }
            }
            else if (type == "browser-transfer")
            {
                transferId = Str(root, "transferId");
                operation = Str(root, "operation");
                if (!IsRequestId(transferId) || operation is not ("accept" or "abandon" or "status" or "watch"))
                { error = "Invalid browser transfer."; return false; }
            }
            else if (type == "browser-activity")
            {
                operation = Str(root, "operation");
                if (operation is not ("connect" or "watch") || !TryBrowserActivity(root, out activity))
                { error = "Invalid Chrome download activity."; return false; }
            }
            bool confirmTransfer = type == "browser-submit" || type == "submit" && items.Count == 1
                && root.TryGetProperty("confirmTransfer", out JsonElement confirm) && confirm.ValueKind == JsonValueKind.True;
            string? pageTitle = type is ("media-picker" or "media-options") && Str(root, "pageTitle") is { Length: <= 300 } title ? title : null;
            request = new BridgeRequest(id, type, mode, probeUrl, items, hints, pageTitle,
                confirmTransfer, mediaSession, formatChoice, browserProof, transferId, operation, activity, clip, mediaAuth,
                audioTrack, subtitleLanguages, autoSubtitles, embedSubtitles);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "Malformed message.";
            return false;
        }
    }

    private static bool TryItem(JsonElement e, bool authorized, bool browserSubmit, out BridgeItem? item, out string error)
    {
        item = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "Each item must be an object."; return false; }
        if (!UrlPolicy.TryParse(Str(e, "url"), out Uri uri, out error)) return false;

        string? originalUrl = null;
        if (browserSubmit && e.TryGetProperty("originalUrl", out JsonElement original))
        {
            if (original.ValueKind != JsonValueKind.String
                || !UrlPolicy.TryParse(original.GetString(), out Uri originalUri, out error)) return false;
            originalUrl = originalUri.AbsoluteUri;
        }

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

        item = new BridgeItem(uri.AbsoluteUri, Str(e, "fileName") is { Length: > 0 } n ? FileNames.Sanitize(n) : null,
            kind, referer, agent, sha, cookie, authorization, originalUrl);
        error = "";
        return true;
    }

    private static bool TryBrowserActivity(JsonElement root, out BrowserActivity? activity)
    {
        activity = null;
        string? profile = Str(root, "profile");
        if (profile is not { Length: >= 8 and <= 36 } || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || !root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32)
            return false;
        var files = new List<BrowserActiveFile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) return false;
            string? id = Str(entry, "id"), name = Str(entry, "name");
            if (!IsRequestId(id) || !id!.StartsWith("c-" + profile + "-", StringComparison.Ordinal) || !ids.Add(id)
                || name is not { Length: > 0 and <= 255 } || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':']) >= 0
                || !TryLength(entry, "received", out var received) || received is null
                || !TryLength(entry, "length", out var length)
                || !TryLength(entry, "start", out var start) || start is not (>= 946684800000L and <= 253402300799999L)) return false;
            bool paused = entry.TryGetProperty("paused", out var pause) && pause.ValueKind == JsonValueKind.True;
            bool canResume = entry.TryGetProperty("canResume", out var resume) && resume.ValueKind == JsonValueKind.True;
            files.Add(new(id, name, received.Value, length, DateTimeOffset.FromUnixTimeMilliseconds(start.Value).UtcDateTime, paused, canResume));
        }
        activity = new(profile, files);
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static bool IsRequestId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool TryLength(JsonElement element, string name, out long? length)
    {
        length = null;
        if (!element.TryGetProperty(name, out JsonElement value)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long bytes) || bytes < 0) return false;
        length = bytes;
        return true;
    }

    /// <summary>A header value: bounded, and never containing line breaks (header injection).</summary>
    private static string? Header(JsonElement e, string name, int max)
    {
        string? value = Str(e, name);
        return value is { Length: > 0 } && value.Length <= max && !value.Any(c => c is '\r' or '\n' or '\0') ? value : null;
    }
}
