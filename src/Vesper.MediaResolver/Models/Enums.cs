namespace Vesper.MediaResolver.Models;

public enum MediaKind { Unknown, Video, Audio, Collection }

public enum StreamProtocol
{
    /// <summary>A single file fetched with ordinary HTTP(S) GET (Range-capable or not).</summary>
    Http,
    /// <summary>HLS media playlist; the transfer engine downloads and assembles the segments.</summary>
    Hls,
    /// <summary>DASH representation with an explicit segment plan.</summary>
    Dash
}

/// <summary>What a single representation physically contains. Never guess: a video-only stream is VideoOnly.</summary>
public enum StreamContent { VideoAndAudio, VideoOnly, AudioOnly }

public enum HdrKind { Sdr, Hdr10, Hlg, DolbyVision }

/// <summary>Post-transfer work the caller must perform to produce the requested output.</summary>
[Flags]
public enum ProcessingRequirement
{
    None = 0,
    /// <summary>Separate video and audio tracks must be joined. VESPER's engine handles compatible clear HTTP tracks.</summary>
    MuxAudioVideo = 1,
    /// <summary>HLS segments must be concatenated in order (TS or fMP4 with init segment; no re-encoding).</summary>
    HlsSegmentAssembly = 2,
    /// <summary>DASH init + media segments must be concatenated in order (single representation, fragmented MP4/WebM).</summary>
    DashSegmentAssembly = 4,
    /// <summary>Segments are AES-128 (CBC) encrypted with a key reachable by plain HTTPS; decrypt with the platform AES implementation.</summary>
    DecryptAes128 = 8,
    /// <summary>Only a stream with audio and video exists; the caller must extract audio for an audio-only output.</summary>
    ExtractAudio = 16
}

public enum CollectionKind { Playlist, Album, Channel, DynamicMix, Feed, ShortFormFeed }

/// <summary>Tells an adapter whether the user means the single item in the URL or the surrounding collection.</summary>
public enum CollectionIntent { Auto, ItemOnly, CollectionOnly }

public enum DiscoveryMode { Native, BrowserAssisted }

public enum DiscoveryStatus { Success, Partial, Failed }
