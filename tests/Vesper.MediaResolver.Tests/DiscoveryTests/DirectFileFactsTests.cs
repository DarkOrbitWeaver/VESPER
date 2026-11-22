using System.Buffers.Binary;
using System.Text;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.DiscoveryTests;

/// <summary>Browser-observed direct files are opened once, a bounded amount, to confirm what they contain.</summary>
public sealed class DirectFileFactsTests
{
    private const string Page = "https://unseen-site.example/watch/7";
    private const string VideoUrl = "https://cdn.unseen-site.example/m/7/video";
    private const string AudioUrl = "https://cdn.unseen-site.example/m/7/audio";

    [Fact]
    public async Task A_silent_video_file_and_its_audio_file_are_reported_as_what_they_are()
    {
        var handler = new FakeHttpHandler()
            .Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576)), "video/mp4")
            .Add(AudioUrl, Mp4(("soun", "mp4a", 0, 0)), "video/mp4");

        MediaItem item = await ResolveAsync(handler, VideoUrl, AudioUrl);

        MediaRepresentation video = Assert.Single(item.Representations, r => r.Url.AbsoluteUri == VideoUrl);
        Assert.Equal(StreamContent.VideoOnly, video.Content);
        Assert.True(video.StandaloneVideo);
        Assert.False(video.AudioUnverified);
        Assert.Equal((1024, 576), (video.Width, video.Height));
        Assert.NotNull(video.VideoCodec);
        // The address and content type both said "video". The track table says otherwise.
        MediaRepresentation audio = Assert.Single(item.Representations, r => r.Url.AbsoluteUri == AudioUrl);
        Assert.Equal(StreamContent.AudioOnly, audio.Content);
        Assert.Equal(TimeSpan.FromSeconds(10), item.Duration);
    }

    [Fact]
    public async Task A_file_with_both_tracks_has_confirmed_sound()
    {
        var handler = new FakeHttpHandler()
            .Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576), ("soun", "mp4a", 0, 0)), "video/mp4");

        MediaRepresentation file = Assert.Single((await ResolveAsync(handler, VideoUrl)).Representations);

        Assert.Equal(StreamContent.VideoAndAudio, file.Content);
        Assert.False(file.AudioUnverified);
        Assert.NotNull(file.AudioCodec);
    }

    [Fact]
    public async Task A_file_whose_track_table_cannot_be_read_keeps_its_sound_unverified()
    {
        var handler = new FakeHttpHandler().AddMedia(VideoUrl);

        MediaRepresentation file = Assert.Single((await ResolveAsync(handler, VideoUrl)).Representations);

        Assert.Equal(StreamContent.VideoAndAudio, file.Content);
        Assert.True(file.AudioUnverified);
        Assert.Null(file.Height);
    }

    [Fact]
    public async Task An_inconsistent_range_does_not_change_the_files_track_metadata()
    {
        var handler = new FakeHttpHandler().Add(VideoUrl, Mp4(("vide", "avc1", 1024, 576)), "video/mp4",
            contentHeaders: new() { ["Content-Range"] = "bytes 0-80/81" });

        MediaRepresentation file = Assert.Single((await ResolveAsync(handler, VideoUrl)).Representations);

        Assert.True(file.AudioUnverified);
        Assert.False(file.StandaloneVideo);
        Assert.Null(file.Height);
    }

    [Fact]
    public async Task At_most_three_files_are_opened_and_each_read_is_bounded()
    {
        var handler = new FakeHttpHandler();
        string[] urls = Enumerable.Range(1, 5).Select(i => $"https://cdn.unseen-site.example/m/7/file-{i}.mp4").ToArray();
        foreach (string url in urls) handler.AddMedia(url);

        await ResolveAsync(handler, urls);

        var reads = handler.Requests.Where(r => r.Method == HttpMethod.Get && r.Headers.TryGetValue("Range", out string? range)
            && range != "bytes=0-0").ToList();
        Assert.Equal(3, reads.Count);
        Assert.All(reads, read => Assert.Equal("bytes=0-262143", read.Headers["Range"]));
    }

    private static async Task<MediaItem> ResolveAsync(FakeHttpHandler handler, params string[] urls)
    {
        using var resolver = T.Resolver(handler);
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new(Page), Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = Page, Title = "Observed playback",
                Candidates = urls.Select(url => new BrowserMediaCandidate { Url = url, Kind = "direct", Source = "network" }).ToList()
            }
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Diagnostic);
        return result.Item!;
    }

    // The fake server stores bodies as text, so every byte of this file stays below 0x80.
    private static string Mp4(params (string Handler, string Format, uint Width, uint Height)[] tracks)
    {
        byte[] mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 600);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 6000);
        byte[] moov = Box("moov", [Box("mvhd", mvhd), Box("free", new byte[tracks.Length == 1 ? 32 : 24]),
            .. tracks.Select(track => Track(track.Handler, track.Format, track.Width, track.Height))]);
        byte[] file = [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")), .. moov];
        Assert.All(file, value => Assert.InRange(value, 0, 0x7f));
        return Encoding.ASCII.GetString(file);
    }

    private static byte[] Track(string handler, string format, uint width, uint height)
    {
        byte[] tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), height << 16);
        byte[] hdlr = new byte[25];
        Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        byte[] stsd = new byte[94];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(8), 86);
        Encoding.ASCII.GetBytes(format).CopyTo(stsd, 12);
        return Box("trak", Box("tkhd", tkhd), Box("mdia", Box("hdlr", hdlr), Box("free", new byte[92]),
            Box("minf", Box("stbl", Box("stsd", stsd)))));
    }

    private static byte[] Box(string type, params byte[][] body)
    {
        byte[] payload = body.SelectMany(part => part).ToArray();
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }
}
