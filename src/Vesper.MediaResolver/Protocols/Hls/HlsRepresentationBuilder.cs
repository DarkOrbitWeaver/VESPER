using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Protocols.Hls;

/// <summary>Converts parsed HLS playlists into typed representations without inventing any information.</summary>
public static class HlsRepresentationBuilder
{
    /// <summary>Build representations from the source master and an optional media-playlist probe.</summary>
    /// <param name="master">Parsed master playlist.</param>
    /// <param name="masterUrl">Final master URL used to resolve relative references.</param>
    /// <param name="ctx">Authorized request context for manifests and media.</param>
    /// <param name="probe">One fetched media playlist used to learn container type, protection and duration (may be null).</param>
    public static ManifestResult FromMaster(HlsMasterPlaylist master, Uri masterUrl, MediaRequestContext ctx, HlsMediaPlaylist? probe)
    {
        bool fmp4 = probe?.HasInitSegment == true;
        string? videoContainer = probe is null ? null : fmp4 ? "mp4" : "ts";
        string? audioContainer = probe is null ? null : fmp4 ? "m4a" : "ts";
        TimeSpan? duration = probe is { TotalDuration: > 0 } ? TimeSpan.FromSeconds(probe.TotalDuration) : null;

        var requirements = ProcessingRequirement.HlsSegmentAssembly;
        if (probe?.Protection == HlsProtection.Aes128) requirements |= ProcessingRequirement.DecryptAes128;

        var audioGroups = master.Renditions
            .Where(r => r.Type == "AUDIO")
            .GroupBy(r => r.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var reps = new List<MediaRepresentation>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        // ---- variants ----
        foreach (var v in master.Variants)
        {
            CodecInfo.Split(v.Codecs, out var vCodec, out var aCodec, out var hdr);
            if (v.VideoRange?.ToUpperInvariant() == "PQ") hdr = HdrKind.Hdr10;
            else if (v.VideoRange?.ToUpperInvariant() == "HLG") hdr = HdrKind.Hlg;

            bool externalAudio = v.AudioGroup is not null
                && audioGroups.TryGetValue(v.AudioGroup, out var grp) && grp.Any(r => r.Url is not null);

            StreamContent content;
            bool audioUnverified = false;
            if (v.Codecs is not null && vCodec is null && v.Height is null && aCodec is not null) content = StreamContent.AudioOnly;
            else if (externalAudio) content = StreamContent.VideoOnly;
            else if (v.Codecs is null) { content = StreamContent.VideoAndAudio; audioUnverified = true; }
            else content = aCodec is not null ? StreamContent.VideoAndAudio : StreamContent.VideoOnly;

            int kbps = (v.AverageBandwidth ?? v.Bandwidth) / 1000;
            long? size = v.AverageBandwidth is > 0 && duration is { } d ? (long)(v.AverageBandwidth.Value / 8.0 * d.TotalSeconds) : null;

            string idBase = content == StreamContent.AudioOnly ? $"hls-audio-{kbps}k" : $"hls-{(v.Height is null ? "x" : v.Height + "p")}-{kbps}k";
            reps.Add(new MediaRepresentation
            {
                Id = Unique(usedIds, idBase),
                Url = v.Url,
                ManifestUrl = masterUrl,
                Protocol = StreamProtocol.Hls,
                Content = content,
                Container = videoContainer,
                VideoCodec = content == StreamContent.AudioOnly ? null : vCodec,
                AudioCodec = aCodec,
                RawCodecs = v.Codecs,
                Width = v.Width,
                Height = v.Height,
                FrameRate = v.FrameRate,
                Hdr = hdr,
                BitrateKbps = kbps > 0 ? kbps : null,
                AudioGroupId = externalAudio ? v.AudioGroup : null,
                AudioUnverified = audioUnverified,
                FileSize = size,
                FileSizeIsEstimate = size is not null,
                Context = ctx,
                Requirements = requirements
            });
        }

        // ---- audio renditions (one representation per distinct playlist) ----
        var audioTracks = new List<AudioTrack>();
        var seenAudioUrls = new HashSet<string>(StringComparer.Ordinal);
        int audioIndex = 0;
        foreach (var r in master.Renditions.Where(r => r.Type == "AUDIO" && r.Url is not null))
        {
            if (!seenAudioUrls.Add(r.Url!.AbsoluteUri)) continue;
            string? codec = master.Variants.Where(v => v.AudioGroup == r.GroupId)
                .Select(v => { CodecInfo.Split(v.Codecs, out _, out var a, out _); return a; })
                .FirstOrDefault(a => a is not null);
            int? channels = int.TryParse(r.Channels?.Split('/')[0], out var ch) ? ch : null;
            string lang = TextUtil.NormalizeLang(r.Language);
            string trackId = $"{r.GroupId}:{r.Name ?? lang}";
            bool describes = r.Characteristics?.Contains("describes-video", StringComparison.OrdinalIgnoreCase) == true;

            audioTracks.Add(new AudioTrack(trackId, r.Language is null ? null : lang, r.Name, r.IsDefault, channels, codec));
            reps.Add(new MediaRepresentation
            {
                Id = Unique(usedIds, $"hls-audio-{r.GroupId}-{lang}-{audioIndex++}"),
                Url = r.Url,
                ManifestUrl = masterUrl,
                Protocol = StreamProtocol.Hls,
                Content = StreamContent.AudioOnly,
                Container = audioContainer,
                AudioCodec = codec,
                AudioChannels = channels,
                AudioLanguage = r.Language is null ? null : lang,
                AudioTrackId = trackId,
                AudioGroupId = r.GroupId,
                IsDefaultAudio = r.IsDefault,
                IsAudioDescription = describes,
                Label = r.Name,
                Context = ctx,
                Requirements = requirements
            });
        }

        // ---- subtitle renditions ----
        var subs = master.Renditions
            .Where(r => r.Type == "SUBTITLES" && r.Url is not null)
            .Select(r => new SubtitleTrack(r.Url!, TextUtil.NormalizeLang(r.Language), r.Name, "hls-webvtt"))
            .ToList();

        return new ManifestResult(reps, audioTracks, subs, duration);
    }

    /// <summary>A media playlist opened directly: one representation with a complete segment plan.</summary>
    public static ManifestResult FromMedia(HlsMediaPlaylist p, Uri url, MediaRequestContext ctx)
    {
        var requirements = ProcessingRequirement.HlsSegmentAssembly;
        if (p.Protection == HlsProtection.Aes128) requirements |= ProcessingRequirement.DecryptAes128;

        var first = p.Segments[0];
        var segments = p.Segments.Select(s => new MediaSegment(s.Url, s.Duration, s.Range, s.DiscontinuityBefore)).ToList();
        var plan = new SegmentPlan
        {
            InitializationUrl = first.Map?.Url,
            InitializationRange = first.Map?.Range,
            Segments = segments,
            TotalDurationSeconds = p.TotalDuration,
            IsFragmentedMp4 = p.HasInitSegment,
            HasDiscontinuities = p.HasDiscontinuities
        };

        var rep = new MediaRepresentation
        {
            Id = "hls-media",
            Url = url,
            ManifestUrl = url,
            Protocol = StreamProtocol.Hls,
            Content = StreamContent.VideoAndAudio,
            AudioUnverified = true, // a bare media playlist carries no codec information
            Container = p.HasInitSegment ? "mp4" : "ts",
            Segments = plan,
            Context = ctx,
            Requirements = requirements
        };
        return new ManifestResult([rep], [], [], TimeSpan.FromSeconds(p.TotalDuration));
    }

    private static string Unique(HashSet<string> used, string id)
    {
        string candidate = id;
        for (int i = 2; !used.Add(candidate); i++) candidate = $"{id}-{i}";
        return candidate;
    }
}
