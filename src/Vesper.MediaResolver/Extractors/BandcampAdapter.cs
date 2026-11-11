using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// Bandcamp tracks and albums from the page's public data-tralbum JSON. Only the streams Bandcamp itself serves
/// to anonymous listeners are returned (typically 128 kbps MP3); purchase-only or unreleased tracks are not bypassed.
/// </summary>
internal sealed class BandcampAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "bandcamp", "Bandcamp", Matches, svc => new BandcampAdapter(svc),
        new AdapterCapabilities(
            Domains: ["*.bandcamp.com"],
            UrlPatterns: ["{artist}.bandcamp.com/track/{slug}", "{artist}.bandcamp.com/album/{slug}"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: false,
            Authentication: "None; only publicly streamable tracks (no purchase bypass)",
            KnownUnsupported: ["custom-domain Bandcamp stores", "purchased high-quality downloads", "artist/label pages", "tracks without a public stream"],
            Notes: "Returns the public preview stream exposed to every visitor; downloading is only appropriate where the rights holder allows it."));

    public BandcampAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) =>
        UrlUtil.HostIs(u, "bandcamp.com") && (u.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal) || u.AbsolutePath.StartsWith("/album/", StringComparison.Ordinal));

    public override bool CanHandle(Uri url) => Matches(url);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var page = await GetPageAsync(request.Url, null, ct).ConfigureAwait(false);

        string? raw = null;
        foreach (var tag in HtmlScanner.Scan(page.Content))
        {
            if (!tag.IsClosing && tag.Attr("data-tralbum") is { } v) { raw = v; break; }
        }
        if (raw is null) return Fail(MediaErrors.SourceChanged("data-tralbum attribute not found on the Bandcamp page"));

        using var doc = JsonExt.TryParse(raw) ?? throw new MediaResolverException(MediaErrors.SourceChanged("data-tralbum is not valid JSON"));
        var tr = doc.RootElement;
        string? artist = tr.P("artist").Str();
        string albumTitle = tr.P("current").P("title").Str() ?? "";
        Uri? art = tr.P("art_id").Long() is { } artId && artId > 0 ? new Uri($"https://f4.bcbits.com/img/a{artId}_10.jpg") : null;

        var items = new List<(MediaItem Item, string? Link)>();
        foreach (var t in tr.P("trackinfo").Items())
        {
            var files = t.P("file");
            if (files is null) continue;

            var reps = new List<MediaRepresentation>();
            foreach (var f in files.Props())
            {
                var url = UrlUtil.Resolve(page.FinalUrl, f.Value.Str());
                if (url is null) continue;
                var parts = f.Name.Split('-');
                string codec = parts[0].ToLowerInvariant();
                reps.Add(new MediaRepresentation
                {
                    Id = $"bandcamp-{f.Name}",
                    Url = url,
                    Protocol = StreamProtocol.Http,
                    Content = StreamContent.AudioOnly,
                    Container = codec,
                    AudioCodec = CodecInfo.NormalizeAudio(codec),
                    BitrateKbps = parts.Length > 1 && int.TryParse(parts[1], out var k) ? k : null,
                    Context = new MediaRequestContext { Referer = page.FinalUrl.GetLeftPart(UriPartial.Authority) + "/" }
                });
            }
            if (reps.Count == 0) continue;

            string? link = t.P("title_link").Str();
            var trackUrl = UrlUtil.Resolve(page.FinalUrl, link) ?? request.Url;
            var item = new MediaItem
            {
                SourceUrl = trackUrl,
                Provider = "bandcamp",
                MediaId = t.P("track_id").Str() ?? t.P("id").Str(),
                Title = t.P("title").Str() ?? albumTitle,
                Uploader = artist,
                Duration = TextUtil.Seconds(t.P("duration").Dbl()),
                Thumbnail = art,
                Kind = MediaKind.Audio,
                Representations = reps
            };
            items.Add((item, link));
        }

        if (items.Count == 0)
            return Fail(MediaErrors.NoMedia("Bandcamp exposes no public stream for this page (purchase or login required)", browserMayHelp: false));

        bool isTrackPage = request.Url.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal);
        if (request.Options.CollectionIntent != CollectionIntent.CollectionOnly && (isTrackPage || items.Count == 1))
            return MediaDiscoveryResult.FromItem(items[0].Item with { SourceUrl = request.Url });

        var entries = items.Take(request.Options.MaxCollectionItems).Select((x, i) => new CollectionEntry
        {
            MediaId = x.Item.MediaId ?? $"track{i}",
            Url = x.Item.SourceUrl,
            Title = x.Item.Title,
            Duration = x.Item.Duration,
            Thumbnail = art,
            Index = i,
            Resolved = x.Item
        }).ToList();

        return MediaDiscoveryResult.FromCollection(new MediaCollection
        {
            SourceUrl = request.Url,
            Provider = "bandcamp",
            CollectionId = tr.P("id").Str(),
            Title = string.IsNullOrEmpty(albumTitle) ? "Bandcamp album" : albumTitle,
            Kind = CollectionKind.Album,
            TotalCount = tr.P("trackinfo").Items().Count(),
            Entries = entries,
            IsComplete = entries.Count == items.Count
        });
    }
}
