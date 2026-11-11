using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Protocols.Dash;

public static class DashRepresentationBuilder
{
    public static ManifestResult Build(DashManifest m, Uri manifestUrl, MediaRequestContext ctx)
    {
        var reps = new List<MediaRepresentation>();
        var audioTracks = new Dictionary<string, AudioTrack>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in m.Representations)
        {
            CodecInfo.Split(r.Codecs, out var vCodec, out var aCodec, out var hdr);
            bool isVideo = r.ContentType == "video";
            var content = isVideo ? (aCodec is not null ? StreamContent.VideoAndAudio : StreamContent.VideoOnly) : StreamContent.AudioOnly;

            string? container = CodecInfo.ContainerFromMime(r.MimeType);
            bool progressive = r.ProgressiveUrl is not null;

            long? size = null;
            if (!progressive && r.Bandwidth is > 0 && m.Duration is { } dur)
                size = (long)(r.Bandwidth.Value / 8.0 * dur.TotalSeconds);

            string idBase = $"dash-{r.ContentType}-{r.Id}";
            string id = idBase;
            for (int i = 2; !used.Add(id); i++) id = $"{idBase}-{r.AdaptationSetKey}-{i}";

            string? lang = isVideo ? null : (r.Lang is null ? null : TextUtil.NormalizeLang(r.Lang));
            string? trackId = isVideo ? null : $"{r.AdaptationSetKey}:{lang ?? "und"}";

            reps.Add(new MediaRepresentation
            {
                Id = id,
                Url = r.ProgressiveUrl ?? manifestUrl,
                ManifestUrl = manifestUrl,
                Protocol = progressive ? StreamProtocol.Http : StreamProtocol.Dash,
                Content = content,
                Container = container,
                VideoCodec = isVideo ? vCodec : null,
                AudioCodec = aCodec,
                RawCodecs = r.Codecs,
                Width = r.Width,
                Height = r.Height,
                FrameRate = r.FrameRate,
                Hdr = hdr,
                BitrateKbps = r.Bandwidth is > 0 ? r.Bandwidth / 1000 : null,
                AudioChannels = r.Channels,
                AudioSampleRate = r.AudioSampleRate,
                AudioLanguage = lang,
                AudioTrackId = trackId,
                IsAudioDescription = r.IsAudioDescription,
                FileSize = size,
                FileSizeIsEstimate = size is not null,
                Context = ctx,
                Segments = r.Segments,
                Requirements = progressive ? ProcessingRequirement.None : ProcessingRequirement.DashSegmentAssembly
            });

            if (!isVideo && trackId is not null && !audioTracks.ContainsKey(trackId))
                audioTracks[trackId] = new AudioTrack(trackId, lang, null, false, r.Channels, aCodec);
        }

        return new ManifestResult(reps, audioTracks.Values.ToList(), m.Subtitles, m.Duration);
    }
}
