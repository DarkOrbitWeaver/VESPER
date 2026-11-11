using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// A TRUSTED map of per-piece SHA-256 digests. "Trusted" means it came from the user or from a source the
/// user vouched for, never from the server being downloaded from. It enables Rescue mode R1b:
/// verify stored pieces locally and fetch only the missing or invalid ones.
/// </summary>
public sealed class PieceMap
{
    public long PieceLength { get; }
    public long TotalLength { get; }
    public IReadOnlyList<string> Sha256 { get; }

    public PieceMap(long pieceLength, long totalLength, IReadOnlyList<string> sha256)
    {
        PieceLength = pieceLength;
        TotalLength = totalLength;
        Sha256 = sha256;
    }

    public int PieceCount => Sha256.Count;

    public ByteRange RangeOf(int index) =>
        new(index * PieceLength, Math.Min(TotalLength, (index + 1) * PieceLength));

    public static bool TryParse(string? json, out PieceMap map, out string error)
    {
        map = null!;
        error = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json ?? "");
            JsonElement root = doc.RootElement;
            long pieceLength = root.GetProperty("pieceLength").GetInt64();
            long total = root.GetProperty("totalLength").GetInt64();
            var hashes = root.GetProperty("sha256").EnumerateArray().Select(e => (e.GetString() ?? "").ToLowerInvariant()).ToList();

            if (pieceLength < 16 * 1024 || pieceLength > 1L << 30) { error = "Piece length must be between 16 KiB and 1 GiB."; return false; }
            if (total <= 0) { error = "Total length must be positive."; return false; }
            long expected = (total + pieceLength - 1) / pieceLength;
            if (hashes.Count != expected) { error = $"Expected {expected} piece hashes, found {hashes.Count}."; return false; }
            if (hashes.Any(h => h.Length != 64 || !h.All(Uri.IsHexDigit))) { error = "Piece hashes must be 64-character SHA-256 hex values."; return false; }

            map = new PieceMap(pieceLength, total, hashes);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = "Not a valid piece-hash file.";
            return false;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new { pieceLength = PieceLength, totalLength = TotalLength, sha256 = Sha256 });

    public bool Matches(int index, ReadOnlySpan<byte> digest) =>
        Convert.ToHexString(digest).Equals(Sha256[index], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads stored pieces from disk and returns only those that are completely present AND hash correctly.
    /// Partially stored pieces are dropped: R1b keeps whole verified pieces only.
    /// </summary>
    internal ExtentSet VerifyStored(PartialFile file, ExtentSet stored, CancellationToken ct, out int invalidPieces)
    {
        var verified = new ExtentSet();
        invalidPieces = 0;
        var buffer = new byte[256 * 1024];
        for (int i = 0; i < PieceCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            ByteRange range = RangeOf(i);
            if (!stored.Covers(range.Start, range.End)) continue;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = range.Start;
            while (offset < range.End)
            {
                int want = (int)Math.Min(buffer.Length, range.End - offset);
                int read = file.Read(buffer.AsSpan(0, want), offset);
                if (read <= 0) break;
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            if (offset == range.End && Matches(i, hash.GetHashAndReset())) verified.Add(range);
            else invalidPieces++;
        }
        return verified;
    }
}
