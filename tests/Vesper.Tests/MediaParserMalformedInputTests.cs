using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

/// <summary>
/// Seeded damaged-input sweeps for the engine parsers that read bytes VESPER did not write.
/// Each target gets truncations, single-bit flips and saturated four-byte windows, which is how a
/// length field usually goes wrong. A parser may accept the input or refuse it the way its callers
/// expect. Anything else, including a hang, fails and names the case so it can be replayed.
/// VESPER_FUZZ_CASES raises the per-mutation count for a deeper local run; 96 keeps the suite quick.
/// </summary>
public sealed class MediaParserMalformedInputTests
{
    private static readonly int Cases =
        int.TryParse(Environment.GetEnvironmentVariable("VESPER_FUZZ_CASES"), out int cases) && cases > 0 ? cases : 96;

    private static readonly MediaMergeMetadata Tags = new() { Title = "Evening", Artist = "Vesper" };
    private static readonly byte[] AvInit = Media("av-init.mp4");
    private static readonly byte[] AvFragment = Media("av-1.m4s");

    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nFirst line\r\n\r\n"
        + "2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond <i>line</i>\r\nwraps\r\n\r\n"
        + "3\r\n00:00:05,250 --> 00:00:09,000\r\nThird\r\n";

    private const string Vtt = "WEBVTT\n\nNOTE a comment\n\n00:00:01.000 --> 00:00:02.500\nFirst line\n\n"
        + "intro\n00:00:03.000 --> 00:00:04.000\nSecond line\nwraps\n\n00:05.250 --> 00:09.000\nThird\n";

    /// <summary>Returns true when the parser accepted the input. Seeds are fixed; never renumber them.</summary>
    private sealed record Target(int Seed, Func<byte[]> Fixture, Func<byte[], bool> Parse);

    private static readonly Dictionary<string, Target> Targets = new()
    {
        ["mpegts-demux"] = new(2001, () => Media("ts-0.ts"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var payloads = new MemoryStream();
            return MpegTsDemuxer.Read(input, input.Length, payloads, CancellationToken.None).Count > 0;
        }),
        ["mpegts-validate"] = new(2002, () => Media("ts-0.ts"), bytes =>
        {
            var validator = new TransportStreamValidator();
            validator.Validate(bytes);
            validator.Finish();
            return true;
        }),
        ["fmp4-init"] = new(2003, () => AvInit, bytes =>
        {
            FragmentedMp4 mp4 = FragmentedMp4.Open(bytes, captureSamples: true);
            mp4.ValidateFragment(AvFragment);
            mp4.Finish(requireAudio: false, requireVideo: false);
            return true;
        }),
        ["fmp4-fragment"] = new(2004, () => AvFragment, bytes =>
        {
            FragmentedMp4 mp4 = FragmentedMp4.Open(AvInit, captureSamples: true);
            mp4.ValidateFragment(bytes);
            mp4.Finish(requireAudio: false, requireVideo: false);
            return true;
        }),
        ["mp4-demux-audio"] = new(2005, () => Media("quiet-track-aac.m4a"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            return Mp4Demuxer.Read(input, input.Length, CancellationToken.None).Count > 0;
        }),
        ["mp4-tags"] = new(2006, () => Media("quiet-track-aac.m4a"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            Mp4Demuxer.ReadMetadata(input, input.Length);
            Mp4Demuxer.ReadThumbnail(input, input.Length);
            return true;
        }),
        ["webm-demux-audio"] = new(2007, () => Media("quiet-track.webm"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            return WebmDemuxer.Read(input, input.Length, CancellationToken.None).Count > 0;
        }),
        ["webm-demux-video"] = new(2008, () => Media("video-only.webm"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            return WebmDemuxer.Read(input, input.Length, CancellationToken.None).Count > 0;
        }),
        ["webm-tags"] = new(2009, () => Media("muxed.webm"), bytes =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            if (!WebmDemuxer.LooksLikeMatroska(input, input.Length)) return false;
            WebmDemuxer.ReadMetadata(input, input.Length);
            WebmDemuxer.ReadThumbnail(input, input.Length);
            return true;
        }),
        ["flac-tags"] = new(2010, () => Media("quiet-track.flac"), bytes =>
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var output = new MemoryStream();
            return Finished(FlacMetadataWriter.TryWriteAsync(source, output, Tags, null, CancellationToken.None))
                == FlacMetadataWriteResult.Written;
        }),
        ["vorbis-tags"] = new(2011, () => Media("quiet-track.ogg"), bytes =>
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var output = new MemoryStream();
            return Finished(OggAudioMetadataWriter.TryWriteAsync(source, output, Tags, null, CancellationToken.None))
                == OggAudioMetadataWriteResult.Written;
        }),
        ["opus-tags"] = new(2012, () => Media("quiet-track.opus"), bytes =>
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var output = new MemoryStream();
            return Finished(OggAudioMetadataWriter.TryWriteAsync(source, output, Tags, null, CancellationToken.None))
                == OggAudioMetadataWriteResult.Written;
        }),
        ["mp3-tags"] = new(2013, () => Media("quiet-track.mp3"), bytes =>
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var output = new MemoryStream();
            return Finished(Mp3MetadataWriter.TryWriteAsync(source, output, Tags, null, CancellationToken.None))
                == Mp3MetadataWriteResult.Written;
        }),
        ["srt-subtitle"] = new(2014, () => Encoding.UTF8.GetBytes(Srt), bytes =>
            TextSubtitleParser.Parse(bytes, "srt", "en", "English", 60).Track.Samples.Count > 0),
        ["vtt-subtitle"] = new(2015, () => Encoding.UTF8.GetBytes(Vtt), bytes =>
            TextSubtitleParser.Parse(bytes, "vtt", "en", "English", 60).Track.Samples.Count > 0),
    };

    public static TheoryData<string> TargetNames()
    {
        var names = new TheoryData<string>();
        foreach (string name in Targets.Keys) names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(TargetNames))]
    public async Task Parser_AcceptsOrRefusesDamagedInput(string name)
    {
        Target target = Targets[name];
        byte[] fixture = target.Fixture();
        Assert.True(target.Parse(fixture), "The undamaged fixture must be accepted, or the sweep proves nothing.");

        var failures = new List<string>();
        int tried = 0;
        Task sweep = Task.Run(() =>
        {
            foreach ((string label, byte[] input) in Mutations(fixture, target.Seed))
            {
                tried++;
                try
                {
                    target.Parse(input);
                }
                catch (MediaContainerException)
                {
                    // The documented refusal. Conversion and merge callers turn it into a kept-data notice.
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
            $"{name} (seed {target.Seed}, {Cases} cases per mutation): {failures.Count} of {tried} damaged inputs "
            + $"escaped as unexpected exceptions.\n{string.Join('\n', failures.Take(12))}");
    }

    /// <summary>
    /// Found by a 20,000-case run of seed 2003. One flipped size bit splits the stsd payload into two boxes
    /// while its entry count still says one, which used to surface as "Unexpected error: Sequence contains
    /// more than one element" instead of a media refusal.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(420)]
    public void FragmentedMp4_RefusesASampleDescriptionSplitIntoSeveralBoxes(int offset)
    {
        byte[] damaged = (byte[])AvInit.Clone();
        damaged[offset] ^= 1 << 4;

        var refusal = Assert.Throws<MediaContainerException>(() => FragmentedMp4.Open(damaged));

        Assert.Equal("Exactly one clear sample description is required.", refusal.Message);
    }

    private static IEnumerable<(string Label, byte[] Input)> Mutations(byte[] fixture, int seed)
    {
        var random = new Random(seed);

        foreach (int length in Distinct(Math.Min(Cases, fixture.Length), () => random.Next(fixture.Length)))
            yield return ($"truncate to {length}", fixture[..length]);

        foreach ((int offset, int bit) in Distinct(Math.Min(Cases, fixture.Length * 8),
            () => (random.Next(fixture.Length), random.Next(8))))
        {
            byte[] damaged = (byte[])fixture.Clone();
            damaged[offset] ^= (byte)(1 << bit);
            yield return ($"flip bit {bit} at {offset}", damaged);
        }

        int windows = fixture.Length - 3;
        if (windows <= 0) yield break;
        foreach (int offset in Distinct(Math.Min(Cases, windows), () => random.Next(windows)))
        {
            byte[] damaged = (byte[])fixture.Clone();
            damaged.AsSpan(offset, 4).Fill(0xFF);
            yield return ($"saturate 4 bytes at {offset}", damaged);
        }
    }

    private static List<T> Distinct<T>(int count, Func<T> next)
    {
        var seen = new HashSet<T>();
        var ordered = new List<T>(count);
        while (ordered.Count < count)
        {
            T value = next();
            if (seen.Add(value)) ordered.Add(value);
        }
        return ordered;
    }

    // The writers only await in-memory streams here, so the task is already complete.
    private static T Finished<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static byte[] Media(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));
}
