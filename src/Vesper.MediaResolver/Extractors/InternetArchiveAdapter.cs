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

    private sealed record IaFile(string Name, string Stem, string Ext, string? Title, long? Size, TimeSpan? Length, int? Height, int? Width, int? Bitrate,
        string? Original, string? Source);

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
            string stem = name[..^(Path.GetExtension(name).Length)];
            string? original = f.P("original").Str();
            if (original is not null && !(VideoExt.Contains(ext) && VideoExt.Contains(Path.GetExtension(original).TrimStart('.'))
                || AudioExt.Contains(ext) && AudioExt.Contains(Path.GetExtension(original).TrimStart('.'))))
                original = null;
            var file = new IaFile(name, stem, ext, f.P("title").Str(), f.P("size").Long(), TextUtil.ParseDuration(f.P("length").Str()),
                f.P("height").Int(), f.P("width").Int(), f.P("bitrate").Int(), original, f.P("source").Str());

            if (VideoExt.Contains(ext)) video.Add(file);
            else if (AudioExt.Contains(ext)) audio.Add(file);
            else if (ext is "vtt" or "srt" && request.Options.IncludeSubtitles)
            {
                var parts = stem.Split('.');
                string lang = parts.Length > 1 && parts[^1].Length is 2 or 3 ? parts[^1] : "und";
                subs.Add(new SubtitleTrack(DownloadUrl(id, name), TextUtil.NormalizeLang(lang), null, ext));
            }
        }

        var videoGroups = GroupFiles(video);
        var audioGroups = GroupFiles(audio);
        var groups = video.Count > 0 && (mediaType is "movies" or "video" || audio.Count == 0)
            ? videoGroups : audioGroups;
        if (wantedFile is not null)
        {
            // An explicit file takes precedence over the item's declared media type.
            var all = videoGroups.Concat(audioGroups).ToList();
            var matches = all.Where(g => g.Any(f => f.Name == wantedFile)).ToList();
            if (matches.Count == 0)
            {
                string wantedStem = wantedFile;
                matches = all.Where(g => g.Key == wantedStem || g.Any(f => f.Stem == wantedStem)).ToList();
            }
            if (matches.Count != 1)
                return Fail(MediaErrors.NoMedia("The requested Archive file is missing or ambiguous.", browserMayHelp: false));
            groups = matches;
        }
        if (groups.Count == 0)
            return Fail(MediaErrors.NoMedia("Item contains no playable video or audio files", browserMayHelp: false));
        if (groups.Count == 1)
        {
            var group = groups[0];
            return MediaDiscoveryResult.FromItem(FileItem(id, group, title, description, creator, thumb, request.Url, subs));
        }
        if (request.Options.CollectionIntent == CollectionIntent.ItemOnly)
            return Fail(MediaErrors.NoMedia("This Archive item contains several different files. Select an individual file.", browserMayHelp: false));

        var entries = groups.Take(Options.MaxCollectionItems).Select((group, i) =>
        {
            IaFile first = group.First();
            var url = new Uri($"https://archive.org/details/{Uri.EscapeDataString(id)}/{string.Join('/', first.Name.Split('/').Select(Uri.EscapeDataString))}");
            var item = FileItem(id, group, first.Title ?? first.Stem, description, creator, thumb, url, subs);
            return new CollectionEntry
            {
                MediaId = item.MediaId!, Url = url, Title = item.Title, Duration = item.Duration, Thumbnail = thumb, Index = i, Resolved = item
            };
        }).ToList();

        return MediaDiscoveryResult.FromCollection(new MediaCollection
        {
            SourceUrl = request.Url, Provider = "internet-archive", CollectionId = id, Title = title,
            Kind = mediaType is "etree" or "audio" ? CollectionKind.Album : CollectionKind.Playlist,
            TotalCount = groups.Count, Entries = entries, IsComplete = entries.Count == groups.Count
        });
    }

    private static List<IGrouping<string, IaFile>> GroupFiles(List<IaFile> files)
    {
        var originals = files.Where(f => f.Source != "derivative" && f.Original is null)
            .ToLookup(f => f.Stem, StringComparer.Ordinal);
        return files.GroupBy(file =>
        {
            if (file.Original is not null) return file.Original;
            var sameStem = originals[file.Stem].ToArray();
            if (sameStem.Length == 1) return sameStem[0].Name;
            if (sameStem.Length == 0 && file.Source == "derivative") return file.Stem;
            return file.Name;
        }, StringComparer.Ordinal).ToList();
    }

    private static MediaItem FileItem(string id, IGrouping<string, IaFile> group, string title, string? description,
        string? creator, Uri thumb, Uri url, List<SubtitleTrack> subtitles)
    {
        bool audio = AudioExt.Contains(group.First().Ext);
        string[] stems = group.Select(f => f.Stem).Distinct(StringComparer.Ordinal).ToArray();
        return new MediaItem
        {
            SourceUrl = url, Provider = "internet-archive", MediaId = $"{id}/{group.Key}",
            Title = title, Description = description, Uploader = creator, Thumbnail = thumb,
            Duration = group.Select(f => f.Length).FirstOrDefault(l => l is not null),
            Kind = audio ? MediaKind.Audio : MediaKind.Video,
            Representations = group.Select(f => ToRep(id, f, audio)).ToList(),
            Subtitles = audio ? [] : subtitles.Where(subtitle =>
            {
                string name = Uri.UnescapeDataString(subtitle.Url.AbsolutePath);
                string prefix = "/download/" + id + "/";
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
                string relative = name[prefix.Length..];
                string captionStem = relative[..^Path.GetExtension(relative).Length];
                if (stems.Contains(captionStem, StringComparer.Ordinal)) return true;
                int dot = captionStem.LastIndexOf('.');
                return dot > 0 && captionStem.Length - dot - 1 is 2 or 3
                    && stems.Contains(captionStem[..dot], StringComparer.Ordinal);
            }).ToList()
        };
    }

    private static Uri DownloadUrl(string id, string name) =>
        new($"https://archive.org/download/{Uri.EscapeDataString(id)}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}");

    private static MediaRepresentation ToRep(string id, IaFile f, bool audio) => new()
    {
        Id = "ia-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Name))),
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
