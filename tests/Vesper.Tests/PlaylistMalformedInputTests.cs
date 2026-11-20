using Vesper.Engine;
using Vesper.Tests.Shared;

namespace Vesper.Tests;

/// <summary>
/// Seeded damaged-text sweeps for the engine's HLS playlist parser, which reads whatever a server sends on
/// every refresh. Its contract is to report trouble through <see cref="HlsMedia.Unsupported"/>, so any
/// exception here would reach the job as "Unexpected error". A hang fails too, and names the case.
/// VESPER_FUZZ_CASES raises the per-mutation count for a deeper local run; 96 keeps the suite quick.
/// </summary>
public sealed class PlaylistMalformedInputTests
{
    private static readonly int Cases =
        int.TryParse(Environment.GetEnvironmentVariable("VESPER_FUZZ_CASES"), out int cases) && cases > 0 ? cases : 96;

    private static readonly Uri Base = new("https://cdn.example.com/hls/index.m3u8");

    private const string Master = "#EXTM3U\n#EXT-X-VERSION:6\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",URI=\"audio/en.m3u8\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=1280x720,CODECS=\"avc1.64001f,mp4a.40.2\"\nvideo/720.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=640000,RESOLUTION=640x360,CODECS=\"avc1.4d401e\",AUDIO=\"aud\"\nvideo/360.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=96000,CODECS=\"mp4a.40.2\"\nhttps://other.example.com/audio.m3u8\n";

    private const string Media = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:12\n"
        + "#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"\n"
        + "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.com/k1\",IV=0x000102030405060708090a0b0c0d0e0f\n"
        + "#EXTINF:6.000,\n#EXT-X-BYTERANGE:1000@720\nmedia.mp4\n#EXTINF:5.5,\n#EXT-X-BYTERANGE:900\nmedia.mp4\n"
        + "#EXT-X-KEY:METHOD=NONE\n#EXT-X-DISCONTINUITY\n#EXTINF:4,\nsegment-14.m4s\n#EXT-X-ENDLIST\n";

    /// <summary>Returns true when the parser produced entries. Seeds are fixed; never renumber them.</summary>
    private sealed record Target(int Seed, string Fixture, Func<string, bool> Parse);

    private static readonly Dictionary<string, Target> Targets = new()
    {
        ["hls-master"] = new(2101, Master, text => HlsParser.ParseMaster(text, Base).Count > 0),
        ["hls-media"] = new(2102, Media, text => HlsParser.ParseMedia(text, Base).Segments.Count > 0),
        ["hls-media-timeline-changes"] = new(2103, Media,
            text => HlsParser.ParseMedia(text, Base, allowTimelineChanges: true).Segments.Count > 0),
    };

    public static TheoryData<string> TargetNames()
    {
        var names = new TheoryData<string>();
        foreach (string name in Targets.Keys) names.Add(name);
        return names;
    }

    [Fact]
    public void Master_SkipsAQualityWhoseAddressCannotBeResolved()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000000\nhttps://[broken/high.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=640000\nvideo/360.m3u8\n";

        HlsVariant usable = Assert.Single(HlsParser.ParseMaster(master, Base));

        Assert.Equal("https://cdn.example.com/hls/video/360.m3u8", usable.Uri);
    }

    [Theory]
    [MemberData(nameof(TargetNames))]
    public async Task Playlist_NeverThrowsOnDamagedText(string name)
    {
        Target target = Targets[name];
        Assert.True(target.Parse(target.Fixture), "The undamaged playlist must yield entries, or the sweep proves nothing.");

        var failures = new List<string>();
        int tried = 0;
        Task sweep = Task.Run(() =>
        {
            foreach ((string label, string text) in DamagedText.Cases(target.Fixture, target.Seed, Cases))
            {
                tried++;
                try
                {
                    target.Parse(text);
                }
                catch (Exception ex)
                {
                    failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        try
        {
            await sweep.WaitAsync(TimeSpan.FromSeconds(10 + Cases / 8));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{name} (seed {target.Seed}) did not finish; it was on case {tried}.");
        }

        Assert.True(failures.Count == 0,
            $"{name} (seed {target.Seed}, {Cases} cases per mutation): {failures.Count} of {tried} damaged playlists "
            + $"escaped as exceptions.\n{string.Join('\n', failures.Take(12))}");
    }
}
