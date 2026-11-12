using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VESPER;

namespace Vesper.Setup;

internal sealed record PayloadFile(string Path, long Size, string Sha256);
internal sealed record PayloadManifest(string Version, PayloadFile[] Files);
internal readonly record struct InstallProgress(string Activity, long Verified = 0, long Total = 0);

internal sealed class InstallPayload(Stream archive, PayloadManifest manifest)
{
    internal const string SetupName = "VESPER-Setup.exe";
    internal const string Marker = ".vesper-install";
    internal string Version => manifest.Version;

    internal static Stream Resource(string name) => typeof(InstallPayload).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException("This setup build has no installation package. Build it with Build-Release.ps1 -Setup.");

    internal static PayloadManifest ReadManifest()
    {
        using var stream = Resource("Vesper.Setup.Payload.json");
        return JsonSerializer.Deserialize<PayloadManifest>(stream)
            ?? throw new InvalidDataException("The package manifest is missing.");
    }

    internal void Extract(string stage, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var expected = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (!ValidPath(file.Path) || file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)
                || !expected.TryAdd(file.Path, file))
                throw new InvalidDataException("The package has an invalid or duplicate file entry.");
            total = checked(total + file.Size);
        }
        if (!expected.ContainsKey("VESPER.exe") || !expected.ContainsKey("vesper-host.exe")
            || !expected.ContainsKey("extension/manifest.json"))
            throw new InvalidDataException("The package is missing VESPER, the browser helper, or the extension.");

        long verified = 0;
        progress?.Report(new("Verifying installation files…", verified, total));
        long lastUpdate = Stopwatch.GetTimestamp();
        var buffer = new byte[128 * 1024];
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            int mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!expected.Remove(entry.FullName, out var file) || entry.FullName != file.Path
                || entry.Length != file.Size || mode is not (0 or 0x8000)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The package contains an unexpected file.");

            string destination = Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = file.Size;
            int length;
            while ((length = input.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                remaining -= length;
                if (remaining < 0) throw new InvalidDataException("An installation file is larger than expected.");
                output.Write(buffer, 0, length);
                hash.AppendData(buffer, 0, length);
            }
            if (remaining != 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The package failed verification for {file.Path}.");
            verified += file.Size;
            if (verified == total || Stopwatch.GetElapsedTime(lastUpdate).TotalMilliseconds >= 50)
            {
                progress?.Report(new("Verifying installation files…", verified, total));
                lastUpdate = Stopwatch.GetTimestamp();
            }
        }
        if (expected.Count != 0) throw new InvalidDataException("The package is incomplete.");
        VerifyExtension(stage);
    }

    private static bool ValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')
            || path.Equals(SetupName, StringComparison.OrdinalIgnoreCase) || path.Equals(Marker, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
            string stem = part.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "0123456789¹²³".Contains(stem[3]))
                return false;
        }
        return true;
    }

    private static void VerifyExtension(string stage)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(stage, "extension", "manifest.json")));
        if (!json.RootElement.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The extension manifest has no pinned key.");
        var digest = SHA256.HashData(Convert.FromBase64String(key.GetString()!));
        string id = string.Concat(Convert.ToHexString(digest.AsSpan(0, 16)).Select(c => (char)('a' + (c <= '9' ? c - '0' : c - 'A' + 10))));
        if (id != NativeHostRegistration.ExtensionId)
            throw new InvalidDataException("The packaged extension and browser helper use different IDs.");
    }
}
