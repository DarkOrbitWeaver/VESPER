using System.Globalization;
using System.Net.Http.Headers;

namespace Vesper.Engine;

/// <summary>
/// Validator proofs for staging verified browser downloads:
/// <list type="bullet">
/// <item><c>etag</c>: Strong HTTP ETag + matching representation.</item>
/// <item><c>last-modified</c>: Strong RFC 9110 Last-Modified (>= 60s age) + exact Content-Length.</item>
/// <item><c>attachment-length</c>: Explicit attachment or binary file >= 256 KiB with Accept-Ranges: bytes and exact Content-Length.</item>
/// </list>
/// </summary>
public sealed record BrowserTransferProof(long? Length, string? ETag, string? ContentType, bool AwaitingConfirmation = true,
    string? LastModified = null, string? FileName = null, bool? Attachment = null, string? Basis = null, bool Adopted = false,
    string? AdoptionTransferId = null, bool AdoptionReleased = false, bool? AcceptsRanges = null)
{
    internal const long MinimumMetadataLength = 256 * 1024;

    public static IReadOnlyList<string> AcceptedBases { get; } = ["etag", "last-modified", "attachment-length", "github-archive"];

    internal bool UsesAttachmentLength => Basis == "attachment-length";

    internal bool UsesMetadata => Basis is "metadata" or "attachment-length";

    internal bool UsesLastModified => Basis == "last-modified";
    internal bool UsesGitHubArchive => Basis == "github-archive";

    internal bool MatchesGitHubArchive(Uri url)
    {
        if (url.Scheme != "https" || url.IdnHost != "codeload.github.com" || !url.IsDefaultPort
            || url.Query.Length > 0 || url.Fragment.Length > 0 || url.UserInfo.Length > 0) return false;
        string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts.Take(2).All(name => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))) return false;
        return parts[2] switch
        {
            "zip" => ContentType is "application/zip" or "application/x-zip-compressed",
            "tar.gz" => ContentType is "application/gzip" or "application/x-gzip",
            _ => false
        };
    }

    internal DateTimeOffset? LastModifiedValue =>
        LastModified is { Length: > 0 and <= 64 }
        && DateTimeOffset.TryParseExact(LastModified, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value : null;

    internal static bool IsGenericType(string? type) => type is null or "application/octet-stream" or "binary/octet-stream"
        or "application/force-download" or "application/download" or "application/x-download" or "application/unknown";

    internal bool IsValid
    {
        get
        {
            if (Length is < 0) return false;
            bool strong = ETag is { Length: > 0 and <= 256 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)
                && !tag.IsWeak && tag.Tag != "*" && tag.ToString() == etag;
            if (ETag is not null && Basis is null or "etag" && !strong) return false;
            if (ContentType is not null && !(ContentType.Length <= 100 && MediaTypeHeaderValue.TryParse(ContentType, out var type)
                && type.Parameters.Count == 0 && type.MediaType == ContentType)) return false;
            if (LastModified is not null && LastModifiedValue is null) return false;
            if (FileName is not null && (FileName.Length is 0 or > 255 || FileName.Any(c => c is < ' ' or > '~' or '/' or '\\')))
                return false;
            return Basis switch
            {
                null or "etag" => strong,
                "last-modified" => ETag is null && Length is > 0 && LastModifiedValue is not null,
                "attachment-length" => Length >= MinimumMetadataLength
                    && AcceptsRanges == true && Attachment == true && ETag is null && LastModified is null
                    && ContentType is not (null or "text/html" or "application/xhtml+xml"),
                "github-archive" => Length != 0 && ETag is null && LastModified is null && Attachment == true
                    && ContentType is ("application/zip" or "application/x-zip-compressed" or "application/gzip" or "application/x-gzip"),
                _ => false
            };
        }
    }
}

/// <summary>A finished file written by Chrome. VESPER only records the existing file.</summary>
public sealed record BrowserCompletedFile(string Profile, string DownloadId, string Path, long Length,
    IReadOnlyList<string> Hosts);

/// <summary>Display and control information for a live Chrome download, without its URL, path or credentials.</summary>
public sealed record BrowserActiveFile(string Id, string Name, long Received, long? Length, DateTime StartedUtc,
    bool Paused, bool CanResume, IReadOnlyList<string>? Hosts = null, bool Managed = false);

public sealed record BrowserActivity(string Profile, IReadOnlyList<BrowserActiveFile> Files);
