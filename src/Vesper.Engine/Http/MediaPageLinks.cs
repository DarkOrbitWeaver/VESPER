namespace Vesper.Engine;

/// <summary>
/// Recognizes page addresses that are not themselves media files. This does not
/// discover streams or grant access to content. VESPER's optional native resolver
/// inspects recognized media pages only when the user requests discovery.
/// </summary>
public static class MediaPageLinks
{
    public static bool TryGetSite(Uri uri, out string site)
    {
        site = "";
        string host = uri.IdnHost.ToLowerInvariant();
        string path = uri.AbsolutePath.TrimEnd('/');

        static bool Domain(string hostName, string domain) =>
            hostName == domain || hostName.EndsWith("." + domain, StringComparison.Ordinal);

        if (Domain(host, "youtu.be") && path.Length > 1
            || Domain(host, "youtube.com") &&
               (path.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/playlist", StringComparison.OrdinalIgnoreCase)))
        {
            site = "YouTube";
            return true;
        }
        if (Domain(host, "tiktok.com") &&
            (path.Contains("/video/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase)))
        {
            site = "TikTok";
            return true;
        }
        if (Domain(host, "instagram.com") &&
            (path.StartsWith("/reel/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/p/", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/tv/", StringComparison.OrdinalIgnoreCase)))
        {
            site = "Instagram";
            return true;
        }
        if (Domain(host, "vimeo.com") && path.Length > 1
            && path.AsSpan(1).IndexOf('/') < 0
            && long.TryParse(path.AsSpan(1), out _))
        {
            site = "Vimeo";
            return true;
        }
        if (Domain(host, "dailymotion.com") && path.StartsWith("/video/", StringComparison.OrdinalIgnoreCase)
            || Domain(host, "dai.ly") && path.Length > 1)
        {
            site = "Dailymotion";
            return true;
        }
        if (Domain(host, "facebook.com") && (path.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/reel/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/videos/", StringComparison.OrdinalIgnoreCase)))
        {
            site = "Facebook";
            return true;
        }
        if ((Domain(host, "x.com") || Domain(host, "twitter.com"))
            && path.Contains("/status/", StringComparison.OrdinalIgnoreCase))
        {
            site = "X";
            return true;
        }
        if (Domain(host, "reddit.com") && (path.Contains("/comments/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/r/", StringComparison.OrdinalIgnoreCase) && path.Contains("/s/", StringComparison.OrdinalIgnoreCase)))
        {
            site = "Reddit";
            return true;
        }
        if (Domain(host, "bilibili.com") && path.StartsWith("/video/", StringComparison.OrdinalIgnoreCase))
        {
            site = "Bilibili";
            return true;
        }
        if (Domain(host, "twitch.tv") && (path.Contains("/clip/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/videos/", StringComparison.OrdinalIgnoreCase))
            || Domain(host, "clips.twitch.tv") && path.Length > 1)
        {
            site = "Twitch";
            return true;
        }
        if (Domain(host, "archive.org") && path.StartsWith("/details/", StringComparison.OrdinalIgnoreCase))
        {
            site = "Internet Archive";
            return true;
        }
        if (Domain(host, "bandcamp.com") && (path.StartsWith("/track/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/album/", StringComparison.OrdinalIgnoreCase)))
        {
            site = "Bandcamp";
            return true;
        }
        return false;
    }
}
