using System.Globalization;

namespace Vesper.MediaResolver.Utilities;

/// <summary>Reads well-known expiry parameters from signed URLs. Returns null when nothing defensible is present.</summary>
public static class ExpiryParser
{
    private static readonly string[] UnixKeys = ["expires", "expire", "exp", "deadline", "Expires"];

    public static DateTimeOffset? FromUrl(Uri url)
    {
        if (string.IsNullOrEmpty(url.Query)) return null;

        foreach (var key in UnixKeys)
        {
            if (long.TryParse(UrlUtil.QueryValue(url, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)
                && secs is > 1_000_000_000 and < 4_100_000_000)
                return DateTimeOffset.FromUnixTimeSeconds(secs);
        }

        // AWS-style presigned URLs: X-Amz-Date + X-Amz-Expires (seconds).
        string? amzDate = UrlUtil.QueryValue(url, "X-Amz-Date");
        if (amzDate is not null
            && long.TryParse(UrlUtil.QueryValue(url, "X-Amz-Expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttl)
            && ttl is > 0 and < 31_536_000
            && DateTimeOffset.TryParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            return start.AddSeconds(ttl);

        return null;
    }
}
