using System.Text;

namespace Vesper.MediaResolver.Security;

/// <summary>
/// Produces log/display-safe URL strings: query values are always dropped (signed URLs, tokens),
/// and long opaque path segments are shortened.
/// </summary>
public static class UrlRedactor
{
    public static string Redact(Uri? uri)
    {
        if (uri is null) return "(null)";
        if (!uri.IsAbsoluteUri) return "(relative-url)";

        var sb = new StringBuilder(96);
        sb.Append(uri.Scheme).Append("://").Append(uri.Host);
        if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);

        foreach (var seg in uri.AbsolutePath.Split('/'))
        {
            if (seg.Length == 0) { sb.Append('/'); continue; }
            sb.Append(seg.Length > 32 ? seg[..8] + "…" : seg);
            sb.Append('/');
        }
        if (sb.Length > 0 && sb[^1] == '/' && !uri.AbsolutePath.EndsWith('/')) sb.Length--;

        string q = uri.Query;
        if (q.Length > 1)
        {
            sb.Append('?');
            bool firstKey = true;
            foreach (var pair in q[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!firstKey) sb.Append('&');
                firstKey = false;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair[..eq];
                sb.Append(key.Length > 24 ? key[..24] : key).Append("=…");
            }
        }
        return sb.ToString();
    }

    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? Redact(u) : "(invalid-url)";
}
