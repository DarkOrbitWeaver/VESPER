using System.Security.Cryptography;
using System.Text;

namespace Vesper.MediaResolver.Models;

/// <summary>Cross-checks player-observed URL digests against parsed media segments.
/// A match is a membership observation, never proof of byte identity or authority to download.</summary>
public static class ManifestPlaybackEvidence
{
    public const int MaxWitnesses = 12;
    private const int MaxInspectedAddresses = 32_768;

    public static int CountMatches(IEnumerable<MediaRepresentation> representations,
        IReadOnlyList<string>? witnesses, CancellationToken ct = default)
    {
        if (witnesses is null || witnesses.Count is 0 or > MaxWitnesses) return 0;
        var remaining = new HashSet<string>(witnesses.Where(IsFingerprint), StringComparer.OrdinalIgnoreCase);
        if (remaining.Count == 0) return 0;
        int initial = remaining.Count;
        int inspected = 0;
        foreach (var representation in representations)
        {
            // Initialization segments, manifests and encryption keys can be shared
            // by different assets. Only actual media segment addresses count.
            if (representation.Protocol is not (StreamProtocol.Hls or StreamProtocol.Dash)) continue;
            IEnumerable<Uri> addresses = (representation.Segments?.Segments.Select(segment => segment.Url)
                ?? Enumerable.Empty<Uri>()).Concat(representation.PlaybackEvidenceUrls);
            foreach (Uri address in addresses)
            {
                if (++inspected > MaxInspectedAddresses) return initial - remaining.Count;
                ct.ThrowIfCancellationRequested();
                if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https")
                    || address.UserInfo.Length != 0 || address.AbsoluteUri.Length > 4096) continue;
                string exact = address.AbsoluteUri.Split('#', 2)[0];
                string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exact)));
                remaining.Remove(digest);
                if (remaining.Count == 0) return initial;
            }
        }
        return initial - remaining.Count;
    }

    private static bool IsFingerprint(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
