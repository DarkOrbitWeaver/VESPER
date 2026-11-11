using System.Buffers;
using System.Security.Cryptography;

namespace Vesper.Engine;

/// <summary>Bounded local verification shared by streaming transfers. No scheduled checks or remote spot checks.</summary>
internal static class MediaPartialIntegrity
{
    internal static void HashPrefix(PartialFile file, long length, IncrementalHash hash, CancellationToken ct, TransportStreamValidator? transport = null)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(188 * 512);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(188 * 512, length - offset), n = 0;
                while (n < want)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = file.Read(buffer.AsSpan(n, want - n), offset + n);
                    if (read <= 0) throw new MediaContainerException("The partial file ended while checking its committed prefix.");
                    n += read;
                }
                hash.AppendData(buffer, 0, n); transport?.Validate(buffer.AsSpan(0, n)); offset += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static byte[] ReadInitialization(PartialFile file, int length, CancellationToken ct)
    {
        if (length is < 8 or > 8 * 1024 * 1024) throw new MediaContainerException("Invalid saved initialization length.");
        var bytes = new byte[length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = file.Read(bytes.AsSpan(offset), offset);
            if (n <= 0) throw new MediaContainerException("The saved initialization segment is incomplete.");
            offset += n;
        }
        return bytes;
    }

    internal static void ValidateTransportStream(ReadOnlySpan<byte> bytes)
    {
        var transport = new TransportStreamValidator(); transport.Validate(bytes); transport.Finish();
    }
}
