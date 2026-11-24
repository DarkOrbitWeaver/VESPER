namespace Vesper.MediaResolver.Models;

/// <summary>
/// Conservative delivery-family evidence. A known provider's media namespace
/// identifies one asset, not necessarily the post the user selected. Unknown
/// hosts and paths stay separate. No signed query is modified or reused.
/// </summary>
public static class BrowserAssetIdentity
{
    public static string? XVideo(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || url.Host != "video.twimg.com"
            || !url.IsDefaultPort || url.UserInfo.Length != 0) return null;
        string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[0] is not ("ext_tw_video" or "amplify_video" or "tweet_video")
            || parts[1].Length is < 5 or > 24 || !parts[1].All(char.IsAsciiDigit)) return null;
        if (parts.Any(p => p.Contains('%'))) return null;
        return "x:" + parts[0] + ":" + parts[1];
    }

    public static bool IsXPost(Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != "https" || !page.IsDefaultPort
            || page.UserInfo.Length != 0
            || page.Host is not ("x.com" or "www.x.com" or "twitter.com" or "www.twitter.com" or "mobile.twitter.com")) return false;
        string[] p = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int status = p.Length > 2 && p[0] == "i" && p[1] == "web" ? 2 : 1;
        return p.Length > status + 1 && p[status] == "status"
            && p[status + 1].Length is >= 5 and <= 24 && p[status + 1].All(char.IsAsciiDigit);
    }

    public static bool CanGroup(Uri page, IEnumerable<Uri> urls)
    {
        if (!IsXPost(page)) return false;
        string? key = null;
        int count = 0;
        foreach (Uri url in urls)
        {
            string? candidate = XVideo(url);
            if (++count > 5 || candidate is null || key is not null && candidate != key) return false;
            key = candidate;
        }
        return count >= 2;
    }
}
