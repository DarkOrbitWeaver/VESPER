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

/// <summary>A finished file written by Chrome. VESPER only records the existing file.</summary>
public sealed record BrowserCompletedFile(string Profile, string DownloadId, string Path, long Length,
    IReadOnlyList<string> Hosts);

/// <summary>Display and control information for a live Chrome download, without its URL, path or credentials.</summary>
public sealed record BrowserActiveFile(string Id, string Name, long Received, long? Length, DateTime StartedUtc,
    bool Paused, bool CanResume, IReadOnlyList<string>? Hosts = null, bool Managed = false);

public sealed record BrowserActivity(string Profile, IReadOnlyList<BrowserActiveFile> Files);
