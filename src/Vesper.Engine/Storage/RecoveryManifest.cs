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
        string Extents, string? ETag, string? LastModified, int Identity, string Destination);

    private sealed record Envelope(string Check, string Body);

    public static string PathFor(string partialPath) => partialPath + Extension;

    public static void Write(DownloadJob job)
    {
        if (job.PartialPath.Length == 0) return;
        var body = new Body(1, job.Id, job.Generation, UrlPolicy.Redact(job.SourceUrl), job.FileName, job.TotalBytes,
            job.Extents.Serialize(), job.ETag, job.LastModified?.ToString("O"), (int)job.Identity, job.DestinationFolder);
        string bodyJson = JsonSerializer.Serialize(body);
        string json = JsonSerializer.Serialize(new Envelope(Hash(bodyJson), bodyJson));

        string path = PathFor(job.PartialPath);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
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
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(manifestPath));
            if (envelope is null || Hash(envelope.Body) != envelope.Check) { problem = "checksum mismatch"; return false; }
            Body? body = JsonSerializer.Deserialize<Body>(envelope.Body);
            if (body is null || body.V != 1) { problem = "unsupported version"; return false; }
            if (!ExtentSet.TryParse(body.Extents, out ExtentSet extents)) { problem = "unreadable extents"; return false; }

            string partial = manifestPath[..^Extension.Length];
            long length = File.Exists(partial) ? new FileInfo(partial).Length : -1;
            if (length < 0) { problem = "partial file missing"; return false; }
            extents.ClampTo(length);   // never claim bytes the file cannot contain

            job = new DownloadJob
            {
                Id = body.JobId, Kind = JobKind.Http, SourceUrl = body.Url, FileName = body.FileName, NameLocked = true,
                DestinationFolder = body.Destination, PartialPath = partial, TotalBytes = body.Total, Extents = extents,
                State = JobState.NeedsLink, Reason = "restored",
                Message = "Restored from a recovery file. Provide the download link again to continue.",
                ETag = body.ETag, Identity = (IdentityLevel)body.Identity, AcceptsRanges = true, Generation = body.Generation,
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

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
