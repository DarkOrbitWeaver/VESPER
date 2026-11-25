using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

/// <summary>
/// Collapses equivalent representations (same content, dimensions, fps, HDR, codecs, audio track and requirements)
/// into the single most convenient one: plain HTTP over HLS over DASH, then highest bitrate, then a known size.
/// Audio-only streams of genuinely different bitrates are kept.
/// </summary>
public static class FormatDeduplicator
{
    private readonly record struct FormatKey(
        StreamContent Content,
        string? DeliveryId,
        int? Width,
        int? Height,
        double? FrameRate,
        HdrKind Hdr,
        string? VideoCodec,
        string? AudioCodec,
        string? Container,
        string? AudioLanguage,
        int? AudioChannels,
        int? AudioSampleRate,
        string? AudioTrackId,
        bool IsDefaultAudio,
        bool IsAudioDescription,
        bool IsDrc,
        string? AudioGroupId,
        bool AudioUnverified,
        bool StandaloneVideo,
        bool MutedLoop,
        string? ContentIdentity,
        ProcessingRequirement Requirements,
        MediaRequestContext Context,
        int? AudioBitrateBucket);

    public static IReadOnlyList<MediaRepresentation> Deduplicate(IEnumerable<MediaRepresentation> reps)
    {
        var order = new List<FormatKey>();
        var winners = new Dictionary<FormatKey, MediaRepresentation>();

        foreach (var r in reps)
        {
            var key = KeyOf(r);
            if (!winners.TryGetValue(key, out var current))
            {
                winners[key] = r;
                order.Add(key);
            }
            else if (IsBetter(r, current))
            {
                winners[key] = r;
            }
        }
        return order.Select(k => winners[k]).ToList();
    }

    private static FormatKey KeyOf(MediaRepresentation r)
    {
        int? audioBitrate = r.Content == StreamContent.AudioOnly ? (r.BitrateKbps ?? 0) / 16 : null;
        var requirements = r.Requirements & ~(ProcessingRequirement.HlsSegmentAssembly | ProcessingRequirement.DashSegmentAssembly);
        return new(r.Content, r.DeliveryId, r.Width, r.Height, r.FrameRate, r.Hdr, r.VideoCodec, r.AudioCodec, r.Container,
            r.AudioLanguage, r.AudioChannels, r.AudioSampleRate, r.AudioTrackId, r.IsDefaultAudio,
            r.IsAudioDescription, r.IsDrc, r.AudioGroupId, r.AudioUnverified, r.StandaloneVideo, r.MutedLoop, r.ContentIdentity,
            requirements, r.Context, audioBitrate);
    }

    private static bool IsBetter(MediaRepresentation candidate, MediaRepresentation current)
    {
        if (candidate.Mp4LayoutVerified == true && current.NeedsMp4Verification) return true;
        if (current.Mp4LayoutVerified == true && candidate.NeedsMp4Verification) return false;
        int p = ((int)candidate.Protocol).CompareTo((int)current.Protocol);
        if (p != 0) return p < 0;
        int b = (candidate.BitrateKbps ?? 0).CompareTo(current.BitrateKbps ?? 0);
        if (b != 0) return b > 0;
        bool cs = candidate.FileSize is not null, us = current.FileSize is not null;
        if (cs != us) return cs;
        return string.CompareOrdinal(candidate.Id, current.Id) < 0;
    }
}
