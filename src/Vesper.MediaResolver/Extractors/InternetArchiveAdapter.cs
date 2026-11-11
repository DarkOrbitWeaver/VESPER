using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Extractors;

/// <summary>Internet Archive items via the documented metadata API. Multi-track audio items become collections.</summary>
internal sealed class InternetArchiveAdapter : AdapterBase
{
    public static readonly AdapterDescriptor Descriptor = new(
        "internet-archive", "Internet Archive", Matches, svc => new InternetArchiveAdapter(svc),
        new AdapterCapabilities(
            Domains: ["archive.org"],
            UrlPatterns: ["archive.org/details/{id}", "archive.org/details/{id}/{file}", "archive.org/embed/{id}"],
            SingleItem: true, Playlist: true, AudioOnly: true, Subtitles: true,
            Authentication: "None (public items); lending-library items are not downloadable",
            KnownUnsupported: ["collections/search pages", "borrowable (lending) items", "texts/images"],
            Notes: "Reads archive.org/metadata/{id}; files are plain HTTPS downloads."));

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "webm", "ogv", "mkv", "mov", "avi", "mpg", "mpeg" };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { "mp3", "ogg", "oga", "flac", "m4a", "wav", "opus" };

    public InternetArchiveAdapter(ResolverServices svc) : base(svc) { }

    private static bool Matches(Uri u) => UrlUtil.HostIs(u, "archive.org") && Parse(u) is not null;

    public override bool CanHandle(Uri url) => Matches(url);

    private static (string Id, string? File)? Parse(Uri u)
    {
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segs.Length < 2 || segs[0] is not ("details" or "embed")) return null;
        string id = segs[1];
        if (id.Length == 0 || id.Length > 120 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return null;
        return (id, segs.Length > 2 ? string.Join('/', segs.Skip(2)) : null);
    }

    private sealed record IaFile(string Name, string Stem, string Ext, string? Title, long? Size, TimeSpan? Length, int? Height, int? Width, int? Bitrate);

    public override async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken ct)
    {
        var parsed = Parse(request.Url);
        if (parsed is null) return Fail(MediaErrors.NotThisSite("Not an Internet Archive item URL"));
        var (id, wantedFile) = parsed.Value;

        using var doc = await GetJsonAsync(new Uri($"https://archive.org/metadata/{Uri.EscapeDataString(id)}"), null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var files = root.P("files");
        if (files is null || root.P("is_dark").Bool() == true)
            return Fail(MediaErrors.Unavailable("Internet Archive item does not exist, is dark or has no files"));

        string title = root.P("metadata").P("title").Str() ?? id;
        string? description = root.P("metadata").P("description").Str();
        string? creator = root.P("metadata").P("creator").Str();
        string mediaType = root.P("metadata").P("mediatype").Str() ?? "";
        var thumb = new Uri($"https://archive.org/services/img/{Uri.EscapeDataString(id)}");
        request.Publish(new DiscoveryProgress(title, thumb));

        var video = new List<IaFile>();
        var audio = new List<IaFile>();
        var subs = new List<SubtitleTrack>();

        foreach (var f in files.Items())
        {
            string? name = f.P("name").Str();
            if (string.IsNullOrEmpty(name) || f.P("source").Str() == "metadata") continue;
            string ext = Path.GetExtension(name).TrimStart('.');
            string groupName = f.P("original").Str() is { } original && AudioExt.Contains(Path.GetExtension(original).TrimStart('.'))
                ? original : name;
            string groupExt = Path.GetExtension(groupName);
            string stem = groupName[..^(groupExt.Length)];
            var file = new IaFile(name, stem, ext, f.P("title").Str(), f.P("size").Long(), TextUtil.ParseDuration(f.P("length").Str()),
                f.P("height").Int(), f.P("width").Int(), f.P("bitrate").Int());

            if (VideoExt.Contains(ext)) video.Add(file);
            else if (AudioExt.Contains(ext)) audio.Add(file);
            else if (ext is "vtt" or "srt" && request.Options.IncludeSubtitles)
            {
                var parts = stem.Split('.');
                string lang = parts.Length > 1 && parts[^1].Length is 2 or 3 ? parts[^1] : "und";
                subs.Add(new SubtitleTrack(DownloadUrl(id, name), TextUtil.NormalizeLang(lang), null, ext));
            }
        }

        bool preferVideo = video.Count > 0 && (mediaType is "movies" or "video" || audio.Count == 0);
        if (preferVideo)
        {
            var reps = video.Select((f, i) => ToRep(id, f, i, audio: false)).ToList();
            return MediaDiscoveryResult.FromItem(new MediaItem
            {
                SourceUrl = request.Url, Provider = "internet-archive", MediaId = id, Title = title, Description = description,
                Uploader = creator, Duration = video.Select(v => v.Length).FirstOrDefault(l => l is not null), Thumbnail = thumb,
                Representations = reps, Subtitles = subs
            });
        }
        if (audio.Count == 0)
            return Fail(MediaErrors.NoMedia("Item contains no playable video or audio files", browserMayHelp: false));

        // Group the same track delivered in several formats.
        var groups = audio.GroupBy(a => a.Stem, StringComparer.Ordinal).ToList();
        if (wantedFile is not null)
        {
            string wantedStem = wantedFile.Contains('.') ? wantedFile[..wantedFile.LastIndexOf('.')] : wantedFile;
            var match = groups.Where(g => g.Key == wantedStem || g.Any(f => f.Name == wantedFile)).ToList();
            if (match.Count > 0) groups = match;
        }

        if (groups.Count == 1 || request.Options.CollectionIntent == CollectionIntent.ItemOnly && wantedFile is not null)
        {
            var g = groups[0];
            return MediaDiscoveryResult.FromItem(TrackItem(id, g.Key, g.ToList(), title, creator, thumb, request.Url));
        }

        var entries = groups.Take(Options.MaxCollectionItems).Select((g, i) =>
        {
            var first = g.First();
            var url = new Uri($"https://archive.org/details/{Uri.EscapeDataString(id)}/{string.Join('/', first.Name.Split('/').Select(Uri.EscapeDataString))}");
            var item = TrackItem(id, g.Key, g.ToList(), first.Title ?? g.Key, creator, thumb, url);
            return new CollectionEntry
            {
                MediaId = $"{id}/{g.Key}", Url = url, Title = item.Title, Duration = first.Length, Thumbnail = thumb, Index = i, Resolved = item
            };
        }).ToList();

        return MediaDiscoveryResult.FromCollection(new MediaCollection
        {
            SourceUrl = request.Url, Provider = "internet-archive", CollectionId = id, Title = title,
            Kind = mediaType == "etree" || mediaType == "audio" ? CollectionKind.Album : CollectionKind.Playlist,
            TotalCount = groups.Count, Entries = entries, IsComplete = entries.Count == groups.Count
        });
    }

    private static MediaItem TrackItem(string id, string stem, List<IaFile> formats, string title, string? creator, Uri thumb, Uri url) => new()
    {
        SourceUrl = url,
        Provider = "internet-archive",
        MediaId = $"{id}/{stem}",
        Title = title,
        Uploader = creator,
        Duration = formats.Select(f => f.Length).FirstOrDefault(l => l is not null),
        Thumbnail = thumb,
        Kind = MediaKind.Audio,
        Representations = formats.Select((f, i) => ToRep(id, f, i, audio: true)).ToList()
    };

    private static Uri DownloadUrl(string id, string name) =>
        new($"https://archive.org/download/{Uri.EscapeDataString(id)}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}");

    private static MediaRepresentation ToRep(string id, IaFile f, int index, bool audio) => new()
    {
        Id = $"ia-{f.Ext.ToLowerInvariant()}-{index}",
        Url = DownloadUrl(id, f.Name),
        Protocol = StreamProtocol.Http,
        Content = audio ? StreamContent.AudioOnly : StreamContent.VideoAndAudio,
        AudioUnverified = !audio,
        Container = f.Ext.ToLowerInvariant() is "oga" ? "ogg" : f.Ext.ToLowerInvariant(),
        AudioCodec = audio ? Formats.CodecInfo.NormalizeAudio(f.Ext.ToLowerInvariant() is "oga" ? "vorbis" : f.Ext) : null,
        Width = f.Width,
        Height = f.Height,
        BitrateKbps = f.Bitrate,
        FileSize = f.Size
    };
}
