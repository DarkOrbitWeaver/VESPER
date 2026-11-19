using Vesper.Engine;

namespace Vesper.Tests;

public sealed class WebmDemuxMalformedInputTests
{
    [Fact]
    public async Task WebmDemuxer_HandlesDeterministicTruncationsAndBitFlips()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "muxed.webm");
        byte[] fixture = File.ReadAllBytes(fixturePath);
        Assert.NotEmpty(WebmDemuxer.Read(new MemoryStream(fixture), fixture.Length, CancellationToken.None));

        var random = new Random(1959);
        var truncationLengths = new HashSet<int>();
        while (truncationLengths.Count < 96)
            truncationLengths.Add(random.Next(fixture.Length));

        var bitFlips = new HashSet<(int Offset, byte Mask)>();
        while (bitFlips.Count < 96)
            bitFlips.Add((random.Next(fixture.Length), (byte)(1 << random.Next(8))));

        Task sweep = Task.Run(() =>
        {
            foreach (int length in truncationLengths)
                AssertParsesOrRefuses(fixture.AsSpan(0, length).ToArray());

            foreach ((int offset, byte mask) in bitFlips)
            {
                byte[] damaged = (byte[])fixture.Clone();
                damaged[offset] ^= mask;
                AssertParsesOrRefuses(damaged);
            }
        });

        await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        await sweep;
    }

    private static void AssertParsesOrRefuses(byte[] input)
    {
        using var stream = new MemoryStream(input, writable: false);
        try
        {
            Assert.NotEmpty(WebmDemuxer.Read(stream, stream.Length, CancellationToken.None));
        }
        catch (MediaContainerException)
        {
            // A malformed container is a normal refusal, not an unhandled parser failure.
        }
    }
}
