using System.Text.Json;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>One explicit, bounded update check. Never scheduled and never downloads executable content.</summary>
internal static class ReleaseCheck
{
    internal sealed record Release(string Tag, string Url);

    internal static async Task<Release> LatestAsync(ResolverOptions network)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new SecureHttpClient(network);
        var response = await http.GetStringAsync(new Uri("https://api.github.com/repos/DarkOrbitWeaver/VESPER/releases/latest"),
            new MediaRequestContext { UserAgent = "VESPER-ManualUpdateCheck/1.0" }, deadline.Token,
            maxBytes: 256 * 1024, accept: "application/vnd.github+json");
        if (response.StatusCode == 404) throw new IOException("No public release has been published yet.");
        response.EnsureSuccess();
        return ParseRelease(response.Content);
    }

    internal static Release ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("html_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String)
            throw new IOException("GitHub returned incomplete release information. Nothing was opened.");
        string? tag = tagValue.GetString();
        string? address = urlValue.GetString();
        if (tag is not { Length: > 0 and <= 100 } || tag.Any(char.IsControl)
            || !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com"
            || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort
            || !url.AbsolutePath.StartsWith("/DarkOrbitWeaver/VESPER/releases/", StringComparison.Ordinal))
            throw new IOException("GitHub returned an unexpected release address. Nothing was opened.");
        return new Release(tag, url.AbsoluteUri);
    }
}
