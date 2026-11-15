using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Vesper.Engine;

/// <summary>Reads and validates the response headers the integrity rules depend on.</summary>
public static class HttpFacts
{
    public static string? StrongEtag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } tag ? tag.Tag : null;

    public static bool HasOnlyWeakEtag(HttpResponseMessage response) => response.Headers.ETag is { IsWeak: true };

    public static DateTimeOffset? LastModified(HttpResponseMessage response) => response.Content.Headers.LastModified;

    /// <summary>True if the body is the stored representation. Anything else makes byte ranges ambiguous.</summary>
    public static bool IsIdentityEncoded(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase));

    public static bool HasCompleteBodyFraming(HttpResponseMessage response) =>
        response.Content.Headers.ContentLength is >= 0
        || response.Headers.TransferEncodingChunked == true
        || response.Version.Major >= 2;

    public static string? ContentType(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType;

    /// <summary>True when the server says it honours byte ranges, so a length can be confirmed after the fact.</summary>
    public static bool AcceptsRanges(HttpResponseMessage response) =>
        response.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));

    public static bool IsHtml(HttpResponseMessage response) =>
        ContentType(response) is "text/html" or "application/xhtml+xml";

    public static bool IsHtmlFileName(string? fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant()
        is ".html" or ".htm" or ".xhtml" or ".php" or ".asp" or ".aspx";

    public static bool LooksLikeHtml(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) prefix = prefix[3..];
        string text = Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n');
        for (int i = 0; i < 4 && text.StartsWith("<!--", StringComparison.Ordinal); i++)
        {
            int end = text.IndexOf("-->", StringComparison.Ordinal);
            if (end < 0) return false;
            text = text[(end + 3)..].TrimStart();
        }
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<meta", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAttachment(HttpResponseMessage response) =>
        response.Content.Headers.ContentDisposition?.DispositionType?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Delay requested by 429/503 responses, capped so a hostile value cannot park a job forever.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null) return null;
        TimeSpan? delay = header.Delta ?? (header.Date - now);
        if (delay is null) return null;
        return TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, 3600));
    }

    /// <summary>
    /// Validates a 206 response against the range that was requested.
    /// The start must match exactly: a server that answers with a different start would corrupt the file.
    /// </summary>
    public static RangeCheck CheckRangeResponse(HttpResponseMessage response, long requestedStart, long? requestedEndInclusive)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent) return RangeCheck.Fail("not-206", "The server did not return a partial response.");
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (range is null || range.From is null || range.To is null) return RangeCheck.Fail("no-content-range", "The partial response had no valid Content-Range.");
        if (!string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)) return RangeCheck.Fail("bad-unit", "Unsupported range unit.");
        if (range.From.Value != requestedStart) return RangeCheck.Fail("wrong-start", $"The server returned bytes from {range.From} instead of {requestedStart}.");
        if (range.To.Value < range.From.Value) return RangeCheck.Fail("bad-range", "Invalid Content-Range.");
        if (requestedEndInclusive is long end && range.To.Value > end) return RangeCheck.Fail("too-long", "The server returned more bytes than requested.");
        if (range.Length is long total && range.To.Value >= total) return RangeCheck.Fail("bad-total", "Content-Range extends past the stated file size.");
        long length = range.To.Value - range.From.Value + 1;
        if (response.Content.Headers.ContentLength is long declared && declared != length)
            return RangeCheck.Fail("length-mismatch", "Content-Length disagrees with Content-Range.");
        return RangeCheck.Ok(range.From.Value, range.To.Value, range.Length);
    }

    public static string? SuggestedFileName(HttpResponseMessage response)
    {
        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim('"', ' ');
    }
}

public readonly record struct RangeCheck(bool IsValid, string Code, string Message, long Start, long EndInclusive, long? Total)
{
    public static RangeCheck Ok(long start, long end, long? total) => new(true, "", "", start, end, total);
    public static RangeCheck Fail(string code, string message) => new(false, code, message, 0, 0, null);
}

/// <summary>Shared by discovery and transfers using one configured network route.</summary>
public sealed class OriginCooldown
{
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _latestUtcTicks;

    public void Remember(Uri url, DateTimeOffset until)
    {
        lock (_lock)
        {
            string origin = url.GetLeftPart(UriPartial.Authority);
            if (_until.TryGetValue(origin, out var existing) && existing >= until) return;
            if (_until.Count >= 256) _until.Remove(_until.MinBy(entry => entry.Value).Key);
            _until[origin] = until;
            if (until.UtcTicks > _latestUtcTicks) Volatile.Write(ref _latestUtcTicks, until.UtcTicks);
        }
    }

    public DateTimeOffset? RetryAt(Uri url)
    {
        long latest = Volatile.Read(ref _latestUtcTicks);
        if (latest == 0) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (latest <= now.UtcTicks) return null;
        lock (_lock)
            return _until.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var until) && until > now ? until : null;
    }

    internal bool WasRecentlyThrottled(Uri url)
    {
        long latest = Volatile.Read(ref _latestUtcTicks);
        if (latest == 0) return false;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        if (latest <= cutoff.UtcTicks) return false;
        lock (_lock)
            return _until.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var until) && until > cutoff;
    }
}
