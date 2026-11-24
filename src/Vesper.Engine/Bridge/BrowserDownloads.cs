using System.Globalization;
using System.Net.Http.Headers;

namespace Vesper.Engine;

/// <summary>
/// What Chrome's own response said about a download VESPER may take over (Chrome stays paused meanwhile).
/// <para>Basis "etag" (or null, for records saved by older versions): a strong validator the server must honour.</para>
/// <para>Basis "last-modified": the server sent no ETag at all, but a Last-Modified date that RFC 9110 8.8.2.2 lets a
/// client treat as strong (we require a conservative 60-second separation in the native response), together with an exact length. It is
/// the validator Chrome itself would resume on and the identity the engine already trusts for its own resumes.</para>
/// <para>Basis "metadata": the server sent no validator, so VESPER's response must reproduce the exact length, the
/// media type and every other identifying fact Chrome saw (disposition, file name, Last-Modified). This proves a
/// consistent server replay, not byte identity with Chrome's copy; Chrome's paused copy remains the fallback.</para>
/// <para>Adopted: Chrome's transfer was the fresh address of a download that was waiting for a new link. That download
/// continues its saved bytes under its own validators; the proof only ties the parked Chrome transfer to it.</para>
/// </summary>
public sealed record BrowserTransferProof(long? Length, string? ETag, string? ContentType, bool AwaitingConfirmation = true,
    string? LastModified = null, string? FileName = null, bool? Attachment = null, string? Basis = null, bool Adopted = false,
    string? AdoptionTransferId = null, bool AdoptionReleased = false)
{
    internal const long MinimumMetadataLength = 256 * 1024;

    /// <summary>The proofs this build accepts, announced to the extension so it never parks Chrome for one that would be refused.</summary>
    public static IReadOnlyList<string> AcceptedBases { get; } = ["etag", "last-modified"];

    internal bool UsesMetadata => Basis == "metadata";

    internal bool UsesLastModified => Basis == "last-modified";

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
            if (ETag is not null && !strong) return false;
            if (ContentType is not null && !(ContentType.Length <= 100 && MediaTypeHeaderValue.TryParse(ContentType, out var type)
                && type.Parameters.Count == 0 && type.MediaType == ContentType)) return false;
            if (LastModified is not null && LastModifiedValue is null) return false;
            if (FileName is not null && (FileName.Length is 0 or > 255 || FileName.Any(c => c is < ' ' or > '~' or '/' or '\\')))
                return false;
            return Basis switch
            {
                null or "etag" => strong,
                "last-modified" => ETag is null && Length is > 0 && LastModifiedValue is not null,
                "metadata" => false, // Metadata cannot prove that Chrome and VESPER received the same representation.
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
