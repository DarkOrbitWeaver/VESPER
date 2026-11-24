using System.Security.Cryptography;
using System.Text;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols.Hls;
using Xunit;

namespace Vesper.MediaResolver.Tests.ProtocolTests;

public class ManifestPlaybackEvidenceTests
{
    private static string Hash(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    private static readonly Uri Playlist = new("https://cdn.example.com/movie/master.m3u8");

    [Fact]
    public void HlsMembershipMatchesExactSignedMediaSegmentsNotInitOrKeys()
    {
        const string manifest = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:4
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key",IV=0x00000000000000000000000000000001
            #EXTINF:4,
            seg.m4s?token=one
            #EXTINF:4,
            seg.m4s?token=two
            #EXT-X-ENDLIST
            """;
        var result = HlsRepresentationBuilder.FromMedia(HlsParser.Parse(manifest, Playlist).Media!, Playlist, MediaRequestContext.Empty);
        var witnesses = new[] {
            Hash("https://cdn.example.com/movie/seg.m4s?token=one"),
            Hash("https://cdn.example.com/movie/seg.m4s?token=one").ToLowerInvariant(),
            Hash("https://cdn.example.com/movie/seg.m4s?token=wrong"),
            Hash("https://cdn.example.com/movie/init.mp4"),
            Hash("https://keys.example.com/key")
        };
        Assert.Equal(1, ManifestPlaybackEvidence.CountMatches(result.Representations, witnesses));
    }

    [Fact]
    public void MasterEvidenceBelongsOnlyToTheProbedVariant()
    {
        var master = HlsParser.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhigh.m3u8\n", Playlist).Master!;
        var chosen = master.Variants[1].Url;
        var probe = HlsParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nclip-high.ts\n#EXT-X-ENDLIST\n", chosen).Media!;
        var result = HlsRepresentationBuilder.FromMaster(master, Playlist, MediaRequestContext.Empty, probe, chosen);
        var digest = Hash("https://cdn.example.com/movie/clip-high.ts");
        Assert.Equal(0, ManifestPlaybackEvidence.CountMatches(result.Representations.Where(r => r.Url == master.Variants[0].Url), [digest]));
        Assert.Equal(1, ManifestPlaybackEvidence.CountMatches(result.Representations.Where(r => r.Url == chosen), [digest]));
        Assert.All(result.Representations, r => Assert.Null(r.Segments));
    }

    [Fact]
    public void DashMembershipChecksDeclaredSegmentsAndDoesNotCountDuplicateRangesTwice()
    {
        var url = new Uri("https://cdn.example.com/a.mp4?token=video");
        var rep = new MediaRepresentation {
            Id = "v", Url = Playlist, Protocol = StreamProtocol.Dash,
            Segments = new SegmentPlan {
                InitializationUrl = new Uri("https://cdn.example.com/init.mp4"),
                Segments = [new(url, 4, new ByteRange(0, 100)), new(url, 4, new ByteRange(100, 100))]
            }
        };
        Assert.Equal(1, ManifestPlaybackEvidence.CountMatches([rep,rep], [Hash(url.AbsoluteUri)]));
        Assert.Equal(0, ManifestPlaybackEvidence.CountMatches([rep], [Hash(rep.Segments.InitializationUrl!.AbsoluteUri)]));
        Assert.Equal(0, ManifestPlaybackEvidence.CountMatches([rep with {Protocol = StreamProtocol.Http}], [Hash(url.AbsoluteUri)]));
        Assert.Equal(0, ManifestPlaybackEvidence.CountMatches([rep], Enumerable.Repeat(Hash(url.AbsoluteUri), 13).ToArray()));
    }
}
