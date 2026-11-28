using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaBodyReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65537)]
    [InlineData(2097152)]
    public async Task Exact_length_body_is_returned_byte_for_byte(int length)
    {
        byte[] bytes = Payloads.Make(length, 17);
        using var body = new MemoryStream(bytes);
        using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] result = await MediaBodyReader.ReadKnownAsync(body, length, stall, TimeSpan.FromSeconds(5), default, "short");
        Assert.Equal(bytes, result);
        Assert.Equal(length, body.Position);
    }

    [Theory]
    [InlineData(100, 99)]
    [InlineData(100, 101)]
    [InlineData(0, 1)]
    public async Task Incorrect_length_never_returns_partial_or_truncated_data(int declared, int actual)
    {
        using var body = new MemoryStream(new byte[actual]);
        using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TransientNetworkException>(() => MediaBodyReader.ReadKnownAsync(body,
            declared, stall, TimeSpan.FromSeconds(5), default, "short"));
    }

    [Fact]
    public async Task Cancellation_prevents_returning_a_buffer()
    {
        using var body = new MemoryStream(new byte[32]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaBodyReader.ReadKnownAsync(body,
            32, stall, TimeSpan.FromSeconds(5), cancelled.Token, "short"));
    }
}
