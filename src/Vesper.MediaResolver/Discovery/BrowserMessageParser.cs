using System.Text.Json;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Discovery;

/// <summary>Parses the native-messaging payload (schema "vesper.discovery/1"). Size- and depth-bounded.</summary>
public static class BrowserMessageParser
{
    public const int MaxMessageChars = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        AllowTrailingCommas = false
    };

    public static BrowserDiscoveryMessage Parse(string json)
    {
        if (!TryParse(json, out var message, out var error)) throw new MediaResolverException(error!);
        return message!;
    }

    public static bool TryParse(string? json, out BrowserDiscoveryMessage? message, out MediaError? error)
    {
        message = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = MediaErrors.Invalid("Empty browser message"); return false; }
        if (json.Length > MaxMessageChars) { error = MediaErrors.TooLarge("Browser message exceeds the size limit"); return false; }
        try
        {
            message = JsonSerializer.Deserialize<BrowserDiscoveryMessage>(json, Options);
            if (message is null) error = MediaErrors.Invalid("Browser message is null");
        }
        catch (JsonException ex)
        {
            error = MediaErrors.Invalid($"Browser message is not valid: {ex.Message}");
        }
        return message is not null;
    }
}
