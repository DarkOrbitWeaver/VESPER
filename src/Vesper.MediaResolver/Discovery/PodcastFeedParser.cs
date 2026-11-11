using System.Xml;
using System.Xml.Linq;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Discovery;

/// <summary>RSS 2.0 / Atom feeds with enclosures. Entries carry only the enclosure URL: each one is verified (probed) when resolved.</summary>
internal static class PodcastFeedParser
{
    public static bool LooksLikeFeed(string? contentType, string body)
    {
        var head = body.AsSpan(0, Math.Min(body.Length, 2048));
        return head.IndexOf("<rss", StringComparison.OrdinalIgnoreCase) >= 0
            || head.IndexOf("<feed", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static MediaCollection? Parse(string xml, Uri feedUrl, int maxItems)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 };
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null) return null;

        string title = "";
        string? channelImage = null;
        var entries = new List<CollectionEntry>();
        int total = 0;

        if (root.Name.LocalName == "rss")
        {
            var channel = Kid(root, "channel");
            if (channel is null) return null;
            title = Kid(channel, "title")?.Value.Trim() ?? "";
            channelImage = Kid(channel, "image")?.Attribute("href")?.Value ?? Kid(Kid(channel, "image"), "url")?.Value;

            foreach (var item in channel.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var enc = Kid(item, "enclosure");
                if (enc is null) continue;
                total++;
                if (entries.Count >= maxItems) continue;
                AddEntry(entries, feedUrl, enc.Attribute("url")?.Value, Kid(item, "guid")?.Value, Kid(item, "title")?.Value,
                    Kid(item, "duration")?.Value, Kid(item, "image")?.Attribute("href")?.Value ?? channelImage);
            }
        }
        else if (root.Name.LocalName == "feed")
        {
            title = Kid(root, "title")?.Value.Trim() ?? "";
            foreach (var entry in root.Elements().Where(e => e.Name.LocalName == "entry"))
            {
                var link = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && e.Attribute("rel")?.Value == "enclosure");
                if (link is null) continue;
                total++;
                if (entries.Count >= maxItems) continue;
                AddEntry(entries, feedUrl, link.Attribute("href")?.Value, Kid(entry, "id")?.Value, Kid(entry, "title")?.Value, null, null);
            }
        }
        else return null;

        if (entries.Count == 0) return null;
        return new MediaCollection
        {
            SourceUrl = feedUrl,
            Provider = "podcast-feed",
            CollectionId = null,
            Title = title,
            Kind = CollectionKind.Feed,
            TotalCount = total,
            Entries = entries,
            IsComplete = total <= maxItems
        };
    }

    private static void AddEntry(List<CollectionEntry> list, Uri feedUrl, string? url, string? guid, string? title, string? duration, string? image)
    {
        var u = UrlUtil.Resolve(feedUrl, url);
        if (u is null) return;
        list.Add(new CollectionEntry
        {
            MediaId = TextUtil.Clip(string.IsNullOrWhiteSpace(guid) ? u.AbsoluteUri : guid.Trim(), 300),
            Url = u,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Duration = TextUtil.ParseDuration(duration),
            Thumbnail = UrlUtil.Resolve(feedUrl, image),
            Index = list.Count
        });
    }

    private static XElement? Kid(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
}
