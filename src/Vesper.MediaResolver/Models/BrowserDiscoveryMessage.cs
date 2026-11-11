namespace Vesper.MediaResolver.Models;

/// <summary>
/// Stable browser-to-native interchange format, schema "vesper.discovery/1".
/// JSON property names are camelCase. See docs/BROWSER_MESSAGE.md.
/// The message is untrusted input: it is parsed and validated by <c>BrowserMessageParser</c> / <c>BrowserMessageValidator</c>.
/// </summary>
public sealed class BrowserDiscoveryMessage
{
    public const string CurrentSchema = "vesper.discovery/1";

    public required string Schema { get; init; }
    public required string PageUrl { get; init; }
    public string? Title { get; init; }
    public string? VideoId { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public List<BrowserMediaCandidate> Candidates { get; init; } = new();
    public List<BrowserSubtitle> Subtitles { get; init; } = new();
    public List<BrowserAudioTrack> AudioTracks { get; init; } = new();
    public BrowserAuthorization? Authorization { get; init; }
}

public sealed class BrowserMediaCandidate
{
    public required string Url { get; init; }
    /// <summary>"direct", "hls", "dash" or "unknown" (sniffed by the resolver).</summary>
    public string? Kind { get; init; }
    public string? MimeType { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? Bitrate { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Label { get; init; }
    /// <summary>"video-element", "network", "metadata" - informational only.</summary>
    public string? Source { get; init; }
}

public sealed class BrowserSubtitle
{
    public required string Url { get; init; }
    public string? Language { get; init; }
    public string? Label { get; init; }
    public string? Format { get; init; }
}

public sealed class BrowserAudioTrack
{
    public string? Language { get; init; }
    public string? Label { get; init; }
    public string? Url { get; init; }
}

/// <summary>
/// Explicit, user-approved credentials for ONE origin. Nothing here is used unless <see cref="ConsentGranted"/> is true
/// and <see cref="Origin"/> equals the page origin.
/// </summary>
public sealed class BrowserAuthorization
{
    public required string Origin { get; init; }
    public bool ConsentGranted { get; init; }
    public string? CookieHeader { get; init; }
    /// <summary>Only Authorization and Accept-Language are honoured.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>Extra hosts for credentials; each must equal or be a subdomain of the page host.</summary>
    public List<string>? AllowedHosts { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public override string ToString() =>
        $"BrowserAuthorization(Origin={Origin}, Consent={ConsentGranted}, Cookie={(CookieHeader is null ? "none" : "[redacted]")}, Headers={Headers?.Count ?? 0})";
}
