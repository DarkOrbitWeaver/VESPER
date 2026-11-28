namespace Vesper.Engine;

// Known-length HTTP bodies can be read directly into their final buffer rather
// than copying every chunk through a scratch array and a MemoryStream.
// Callers must validate the framing, limit and reservation before allocation.
internal static class MediaBodyReader
{
    internal static async Task<byte[]> ReadKnownAsync(Stream body, int length,
        CancellationTokenSource stall, TimeSpan timeout, CancellationToken ct,
        string incompleteMessage, RateLimiter? local = null, RateLimiter? global = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        int offset = 0;
        while (offset < result.Length)
        {
            int read = await body.ReadAsync(result.AsMemory(offset, Math.Min(64 * 1024, result.Length - offset)),
                stall.Token).ConfigureAwait(false);
            if (read == 0) throw new TransientNetworkException(incompleteMessage);
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
            if (local is not null) await local.AcquireAsync(read, ct).ConfigureAwait(false);
            if (global is not null) await global.AcquireAsync(read, ct).ConfigureAwait(false);
            offset += read;
            stall.CancelAfter(timeout);
        }
        // HTTP content framing normally makes this an immediate EOF. A custom
        // or chunked stream must not be able to silently discard excess bytes.
        byte[] extra = new byte[1];
        if (await body.ReadAsync(extra.AsMemory(), stall.Token).ConfigureAwait(false) != 0)
            throw new TransientNetworkException("A media object exceeded its declared size.");
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
