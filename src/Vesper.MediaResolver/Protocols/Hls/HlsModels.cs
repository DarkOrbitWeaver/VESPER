using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Protocols.Hls;

public enum HlsProtection
{
    None = 0,
    /// <summary>Whole-segment AES-128-CBC with a key fetched over plain HTTPS: supported.</summary>
    Aes128 = 1,
    /// <summary>SAMPLE-AES with identity key format: standard but needs elementary-stream decryption: unsupported.</summary>
    SampleAes = 2,
    /// <summary>FairPlay / Widevine / PlayReady / CENC / non-http key URIs: DRM.</summary>
    Drm = 3
}

public sealed record HlsKey(string Method, Uri? KeyUri, string? Iv, string? KeyFormat, HlsProtection Protection);

public sealed record HlsMap(Uri Url, ByteRange? Range);

public sealed record HlsSegment(Uri Url, double Duration, ByteRange? Range, bool DiscontinuityBefore, HlsKey? Key, HlsMap? Map);

public sealed record HlsVariant(
    Uri Url, int Bandwidth, int? AverageBandwidth, int? Width, int? Height,
    double? FrameRate, string? Codecs, string? AudioGroup, string? SubtitleGroup, string? VideoRange);

public sealed record HlsRendition(
    string Type, string GroupId, string? Name, string? Language, Uri? Url,
    bool IsDefault, bool AutoSelect, string? Channels, string? Characteristics);

public sealed class HlsMasterPlaylist
{
    public List<HlsVariant> Variants { get; } = new();
    public List<HlsRendition> Renditions { get; } = new();
    public HlsProtection SessionProtection { get; set; }
}

public sealed class HlsMediaPlaylist
{
    public int Version { get; set; }
    public double TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool IsEndList { get; set; }
    public string? PlaylistType { get; set; }
    public List<HlsSegment> Segments { get; } = new();
    public double TotalDuration { get; set; }
    public bool HasByteRanges { get; set; }
    public bool HasDiscontinuities { get; set; }
    public HlsProtection Protection { get; set; }

    public bool HasInitSegment => Segments.Count > 0 && Segments[0].Map is not null;

    /// <summary>A playlist is live until it is closed with ENDLIST.</summary>
    public bool IsLive => !IsEndList;
}

public sealed class HlsParseResult
{
    public HlsMasterPlaylist? Master { get; init; }
    public HlsMediaPlaylist? Media { get; init; }
    public bool IsMaster => Master is not null;
}
