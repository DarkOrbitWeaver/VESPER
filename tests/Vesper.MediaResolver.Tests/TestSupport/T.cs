using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Tests.TestSupport;

/// <summary>Tiny builders so tests read as scenarios.</summary>
public static class T
{
    public static ResolverOptions Options() => new() { CacheTtl = TimeSpan.Zero };

    public static DefaultMediaResolver Resolver(FakeHttpHandler handler, ResolverOptions? options = null) =>
        new(options ?? Options(), handler);

    public static MediaDiscoveryRequest Req(string url, CollectionIntent intent = CollectionIntent.Auto, string? pageToken = null) => new()
    {
        Url = new Uri(url),
        Options = new DiscoveryOptions { CollectionIntent = intent, PageToken = pageToken }
    };

    public static MediaRepresentation Video(
        string id, int height, string codec = "h264", bool withAudio = true, int kbps = 1000, double fps = 30,
        string container = "mp4", long? size = null, HdrKind hdr = HdrKind.Sdr, StreamProtocol protocol = StreamProtocol.Http,
        string? audioGroup = null) => new()
    {
        Id = id,
        Url = new Uri($"https://cdn.example.com/{id}.{container}"),
        Protocol = protocol,
        Content = withAudio ? StreamContent.VideoAndAudio : StreamContent.VideoOnly,
        Container = container,
        VideoCodec = codec,
        AudioCodec = withAudio ? "aac" : null,
        Height = height,
        Width = height * 16 / 9,
        FrameRate = fps,
        BitrateKbps = kbps,
        FileSize = size,
        Hdr = hdr,
        AudioGroupId = audioGroup,
        ManifestUrl = protocol == StreamProtocol.Http ? null : new Uri("https://cdn.example.com/master.m3u8"),
        Requirements = protocol == StreamProtocol.Hls ? ProcessingRequirement.HlsSegmentAssembly : ProcessingRequirement.None
    };

    public static MediaRepresentation Audio(
        string id, string codec = "aac", int kbps = 128, string? lang = "en", int channels = 2,
        bool isDefault = false, string container = "m4a", long? size = null) => new()
    {
        Id = id,
        Url = new Uri($"https://cdn.example.com/{id}.{container}"),
        Content = StreamContent.AudioOnly,
        Container = container,
        AudioCodec = codec,
        BitrateKbps = kbps,
        AudioLanguage = lang,
        AudioChannels = channels,
        IsDefaultAudio = isDefault,
        FileSize = size,
        AudioTrackId = lang
    };

    public static MediaItem Item(params MediaRepresentation[] reps) => new()
    {
        SourceUrl = new Uri("https://example.com/item"),
        Provider = "test",
        MediaId = "t1",
        Title = "Test item",
        Duration = TimeSpan.FromMinutes(5),
        Representations = reps
    };
}
