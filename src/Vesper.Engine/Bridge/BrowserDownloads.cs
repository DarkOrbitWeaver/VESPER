using System.Net.Http.Headers;

namespace Vesper.Engine;

/// <summary>Response facts observed by Chrome. The pending flag is owned by the engine.</summary>
public sealed record BrowserTransferProof(long? Length, string? ETag, string? ContentType, bool AwaitingConfirmation = true)
{
    internal bool IsValid => Length is not < 0
        && ETag is { Length: > 0 and <= 256 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)
        && !tag.IsWeak && tag.Tag != "*" && tag.ToString() == etag
        && (ContentType is null || ContentType.Length <= 100 && MediaTypeHeaderValue.TryParse(ContentType, out var type)
            && type.Parameters.Count == 0 && type.MediaType == ContentType);
}

/// <summary>Display and control information for a live Chrome download, without its URL, path or credentials.</summary>
public sealed record BrowserActiveFile(string Id, string Name, long Received, long? Length, DateTime StartedUtc,
    bool Paused, bool CanResume);

public sealed record BrowserActivity(string Profile, IReadOnlyList<BrowserActiveFile> Files);
