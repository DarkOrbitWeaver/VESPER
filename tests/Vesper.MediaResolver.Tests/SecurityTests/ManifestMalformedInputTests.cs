using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Playlists;
using Vesper.MediaResolver.Protocols.Dash;
using Vesper.MediaResolver.Protocols.Hls;
using Vesper.MediaResolver.Tests.TestSupport;
using Vesper.Tests.Shared;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

/// <summary>
/// Seeded damaged-text sweeps for the resolver parsers that read a server's reply or a pasted list.
/// A parser may accept the text or refuse it with MediaResolverException, which discovery already shows
/// as a plain message. Anything else, including a hang, fails and names the case so it can be replayed.
/// VESPER_FUZZ_CASES raises the per-mutation count for a deeper local run; 96 keeps the suite quick.
/// </summary>
public sealed class ManifestMalformedInputTests
{
    private static readonly int Cases =
        int.TryParse(Environment.GetEnvironmentVariable("VESPER_FUZZ_CASES"), out int cases) && cases > 0 ? cases : 96;

    private static readonly Uri Source = new("https://cdn.example.com/media/source");

    private const string UrlList = "# evening queue\nhttps://example.com/files/one.zip\n"
        + "https://example.com/watch?v=abc123&t=42 | Second title\n\nhttp://198.51.100.7:8080/three.mp4\n"
        + "example.com/four.pdf\nhttps://example.com/files/one.zip\n";

    /// <summary>Returns true when the parser accepted the text. Seeds are fixed; never renumber them.</summary>
    private sealed record Target(int Seed, Func<string> Fixture, Func<string, bool> Parse);

    private static readonly Dictionary<string, Target> Targets = new()
    {
        ["hls-master"] = new(3001, () => Fixture.Load("hls_master.m3u8"),
            text => HlsParser.Parse(text, Source) is not null),
        ["hls-media-vod"] = new(3002, () => Fixture.Load("hls_media_vod.m3u8"),
            text => HlsParser.Parse(text, Source) is not null),
        ["hls-media-aes-byterange"] = new(3003, () => Fixture.Load("hls_media_aes_byterange.m3u8"),
            text => HlsParser.Parse(text, Source) is not null),
        ["dash-manifest"] = new(3004, () => Fixture.Load("reddit_dash.mpd"),
            text => DashParser.Parse(text, Source).Representations.Count > 0),
        ["url-list"] = new(3006, () => UrlList,
            text => UrlListParser.Parse(text) is not null),
    };

    public static TheoryData<string> TargetNames()
    {
        var names = new TheoryData<string>();
        foreach (string name in Targets.Keys) names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(TargetNames))]
    public async Task Parser_AcceptsOrRefusesDamagedText(string name)
    {
        Target target = Targets[name];
        string fixture = target.Fixture();
        Assert.True(target.Parse(fixture), "The undamaged fixture must be accepted, or the sweep proves nothing.");

        var failures = new List<string>();
        int tried = 0;
        Task sweep = Task.Run(() =>
        {
            foreach ((string label, string text) in DamagedText.Cases(fixture, target.Seed, Cases))
            {
                tried++;
                try
                {
                    target.Parse(text);
                }
                catch (MediaResolverException)
                {
                    // The documented refusal; discovery reports it instead of offering a download.
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
            $"{name} (seed {target.Seed}, {Cases} cases per mutation): {failures.Count} of {tried} damaged texts "
            + $"escaped as unexpected exceptions.\n{string.Join('\n', failures.Take(12))}");
    }
}
