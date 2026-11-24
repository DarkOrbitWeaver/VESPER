using System.Buffers.Binary;
using System.Text;
using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Vesper.MediaResolver.Tests.DiscoveryTests;

public sealed class Mp4HeaderFactsTests
{
    [Fact]
    public void A_web_optimised_file_reports_its_tracks_size_codecs_and_length()
    {
        byte[] file = Join(Box("ftyp", Ascii("isom")), Box("moov", Mvhd(1000, 93_500),
            Track("vide", "avc1", 1280, 720), Track("soun", "mp4a", 0, 0)), Box("mdat", new byte[64]));

        DirectMediaFacts? facts = Mp4HeaderFacts.Read(file);

        Assert.NotNull(facts);
        Assert.True(facts.HasVideo);
        Assert.True(facts.HasAudio);
        Assert.Equal((1280, 720), (facts.Width, facts.Height));
        Assert.NotNull(facts.VideoCodec);
        Assert.NotNull(facts.AudioCodec);
        Assert.Equal(93.5, facts.DurationSeconds);
        Assert.False(facts.Encrypted);
    }

    [Fact]
    public void A_video_track_alone_is_reported_as_silent_instead_of_assumed_to_have_sound()
    {
        byte[] file = Join(Box("ftyp", Ascii("iso5")), Box("moov", Mvhd(1000, 0), Track("vide", "avc1", 720, 1280)));

        DirectMediaFacts? facts = Mp4HeaderFacts.Read(file);

        Assert.NotNull(facts);
        Assert.True(facts.HasVideo);
        Assert.False(facts.HasAudio);
        Assert.Null(facts.DurationSeconds);
    }

    [Fact]
    public void An_encrypted_sample_entry_is_reported()
    {
        byte[] file = Join(Box("ftyp", Ascii("iso6")), Box("moov", Mvhd(1000, 5000), Track("vide", "encv", 1920, 1080)));

        Assert.True(Mp4HeaderFacts.Read(file)!.Encrypted);
    }

    [Fact]
    public void A_track_table_outside_the_bytes_read_leaves_everything_unknown()
    {
        byte[] whole = Join(Box("ftyp", Ascii("isom")), Box("moov", Mvhd(1000, 5000), Track("vide", "avc1", 640, 360)));
        byte[] moovAtEnd = Join(Box("ftyp", Ascii("isom")), Box("mdat", new byte[256]));

        Assert.Null(Mp4HeaderFacts.Read(whole.AsSpan(0, whole.Length - 10)));
        Assert.Null(Mp4HeaderFacts.Read(moovAtEnd));
        Assert.Null(Mp4HeaderFacts.Read(Encoding.ASCII.GetBytes("<!doctype html><html>not a video</html>")));
        Assert.Null(Mp4HeaderFacts.Read(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_box_that_claims_more_bytes_than_exist_is_not_followed()
    {
        byte[] file = Join(Box("ftyp", Ascii("isom")), Box("moov", Mvhd(1000, 5000), Track("vide", "avc1", 640, 360)));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), uint.MaxValue);

        Assert.Null(Mp4HeaderFacts.Read(file));
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("wrong-tag", false)]
    [InlineData("wrong-size", false)]
    [InlineData("ignored-range", false)]
    [InlineData("init-only", false)]
    public async Task Observed_partial_response_requires_the_same_complete_muxed_representation(string mode, bool success)
    {
        const string url = "https://cdn.example.com/opaque";
        const string page = "https://video.example.com/watch";
        byte[] file = Join(Box("ftyp", Ascii("isom")), Box("moov", Mvhd(1000, mode == "init-only" ? 0u : 15_000u),
            Track("vide", "avc1", 1280, 720), Track("soun", "mp4a", 0, 0)), Box("mdat", new byte[64]));
        using var resolver = new DefaultMediaResolver(handler: new RangeFixture(file, mode));
        var result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new Uri(page), Options = new DiscoveryOptions { ExactBrowserSource = true },
            Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page,
                Candidates = [new BrowserMediaCandidate { Url = url, Kind = "direct", MimeType = "video/mp4",
                    ObservedLength = file.Length + (mode == "wrong-size" ? 1 : 0), ObservedETag = "\"original\"" }]
            }
        }, CancellationToken.None);
        Assert.Equal(success, result.IsSuccess);
        if (!success) return;
        var rep = Assert.Single(result.Item!.Representations);
        Assert.True(rep.BrowserRangeVerified);
        Assert.Equal("\"original\"", rep.ResourceETag);
        Assert.Equal(file.Length, rep.FileSize);
        Assert.True(rep.HasAudio && rep.HasVideo);
    }

    private sealed class RangeFixture(byte[] bytes, string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.Single();
            int start = (int)(range?.From ?? 0);
            int end = (int)Math.Min(range?.To ?? bytes.Length - 1, bytes.Length - 1);
            bool partial = mode != "ignored-range" && range is not null;
            var result = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(partial ? bytes[start..(end + 1)] : bytes)
            };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            if (partial) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            result.Headers.ETag = new EntityTagHeaderValue(mode == "wrong-tag" ? "\"changed\"" : "\"original\"");
            return Task.FromResult(result);
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] Box(string type, params byte[][] body)
    {
        byte[] payload = Join(body);
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Ascii(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Mvhd(uint scale, uint ticks)
    {
        byte[] body = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), scale);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), ticks);
        return Box("mvhd", body);
    }

    private static byte[] Track(string handler, string format, uint width, uint height)
    {
        byte[] tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), height << 16);
        byte[] hdlr = new byte[25];
        Ascii(handler).CopyTo(hdlr, 8);
        byte[] stsd = new byte[8 + 86];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(8), 86);
        Ascii(format).CopyTo(stsd, 12);
        return Box("trak", Box("tkhd", tkhd),
            Box("mdia", Box("hdlr", hdlr), Box("minf", Box("stbl", Box("stsd", stsd)))));
    }
}
