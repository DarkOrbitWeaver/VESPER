using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Auxiliary recovery record stored next to the partial file. The database is authoritative; this
/// only lets a job be re-created if the database is lost. It is written AFTER a checkpoint committed,
/// so everything it claims was already flushed. It deliberately omits query strings (signed tokens).
/// </summary>
internal static class RecoveryManifest
{
    public const string Extension = ".vmeta";

    private sealed record Body(int V, Guid JobId, long Generation, string Url, string FileName, long? Total,
        string Extents, string? ETag, string? LastModified, int Identity, string Destination, int Kind = 0,
        string? MediaMetadataJson = null, string? MediaThumbnailUrl = null, string? PartialIdentity = null);

    private sealed record Envelope(string Check, string Body);

    public static string PathFor(string partialPath) => partialPath + Extension;

    public static void Write(DownloadJob job)
    {
        if (job.PartialPath.Length == 0) return;
        var body = new Body(1, job.Id, job.Generation, UrlPolicy.Redact(job.SourceUrl), job.FileName, job.TotalBytes,
            job.Extents.Serialize(), job.ETag, job.LastModified?.ToString("O"), (int)job.Identity, job.DestinationFolder,
            (int)job.Kind, job.MediaMetadataJson, ResourcePath(job.MediaThumbnailUrl), job.PartialIdentity);
        string bodyJson = JsonSerializer.Serialize(body);
        string json = JsonSerializer.Serialize(new Envelope(Hash(bodyJson), bodyJson));

        string path = PathFor(job.PartialPath);
        string temp = path + ".tmp";
        using (var file = PathSafety.OpenTemporaryStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096))
        using (var writer = new StreamWriter(file, Encoding.UTF8))
            writer.Write(json);
        File.Move(temp, path, overwrite: true);          // atomic replace: a reader sees the old or the new manifest
    }

    public static void Delete(string partialPath)
    {
        try { File.Delete(PathFor(partialPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* auxiliary file; harmless to leave */ }
    }

    /// <summary>Returns a job in NeedsLink state: the manifest knows the identity but not the secret link.</summary>
    public static bool TryRecover(string manifestPath, out DownloadJob job, out string problem)
    {
        job = null!;
        problem = "";
        try
        {
            if (!manifestPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            { problem = "invalid recovery file name"; return false; }
            using var manifest = PathSafety.OpenTemporaryStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            using var reader = new StreamReader(manifest, Encoding.UTF8);
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(reader.ReadToEnd());
            if (envelope is null || Hash(envelope.Body) != envelope.Check) { problem = "checksum mismatch"; return false; }
            Body? body = JsonSerializer.Deserialize<Body>(envelope.Body);
            if (body is null || body.V != 1) { problem = "unsupported version"; return false; }
            if (!ExtentSet.TryParse(body.Extents, out ExtentSet extents)) { problem = "unreadable extents"; return false; }

            string partial = manifestPath[..^Extension.Length];
            using var saved = PathSafety.OpenTemporaryHandle(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (body.PartialIdentity is not null)
                PathSafety.RequireTemporaryIdentity(saved, body.PartialIdentity);
            long length = RandomAccess.GetLength(saved);

            // The checksum is unkeyed, so a planted manifest could name another folder or file.
            // Only import one that describes the partial sitting next to it.
            string here = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
            if (!string.Equals(Path.GetFullPath(body.Destination ?? ""), here, StringComparison.OrdinalIgnoreCase))
            { problem = "the recovery file names a different folder"; return false; }
            string fileName = FileNames.Sanitize(body.FileName);
            extents.ClampTo(length);   // never claim bytes the file cannot contain
            string? thumbnailUrl = UrlPolicy.TryParse(body.MediaThumbnailUrl, out Uri thumbnail, out _)
                ? thumbnail.GetLeftPart(UriPartial.Path) : null;
            string? mediaMetadataJson = MediaMergePlan.SupportsAudioMetadataFile(body.FileName)
                && MediaMergePlan.TryParseMetadata(body.MediaMetadataJson, out MediaMergeMetadata? metadata)
                && (MediaMergePlan.HasAudioTagValues(metadata!) || thumbnailUrl is not null)
                ? JsonSerializer.Serialize(metadata) : null;

            job = new DownloadJob
            {
                Id = body.JobId, Kind = Enum.IsDefined((JobKind)body.Kind) ? (JobKind)body.Kind : JobKind.Http, SourceUrl = body.Url, FileName = fileName, NameLocked = true,
                DestinationFolder = here, PartialPath = partial, PartialIdentity = body.PartialIdentity, TotalBytes = body.Total, Extents = extents,
                State = body.Kind == 0 ? JobState.NeedsLink : JobState.NeedsAttention, Reason = "restored",
                Message = body.Kind == 0 ? "Restored from a recovery file. Provide the download link again to continue."
                    : "Streaming partial restored without a private transfer plan. Resolve a new copy; saved fragments were kept and must not be treated as an ordinary HTTP file.",
                ETag = body.ETag, Identity = (IdentityLevel)body.Identity, AcceptsRanges = true, Generation = body.Generation,
                MediaMetadataJson = mediaMetadataJson,
                MediaThumbnailUrl = thumbnailUrl,
                LastModified = DateTimeOffset.TryParse(body.LastModified, out DateTimeOffset lm) ? lm : null
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static string? ResourcePath(string? address) => UrlPolicy.TryParse(address, out Uri uri, out _)
        ? uri.GetLeftPart(UriPartial.Path) : null;

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
