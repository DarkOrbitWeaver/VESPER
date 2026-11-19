using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>Validates PCM WAVE and copies an exact, whole-sample interval without another decode.</summary>
internal static class WaveAudio
{
    internal sealed record Info(int SampleRate, short Channels, short Bits, long DataOffset, long DataBytes)
    {
        public int BlockAlign => Channels * Bits / 8;
        public long SampleCount => DataBytes / BlockAlign;
        public double Duration => SampleCount / (double)SampleRate;
    }

    internal static Info Read(Stream source)
    {
        source.Position = 0;
        Span<byte> header = stackalloc byte[12]; source.ReadExactly(header);
        bool rf64 = header[..4].SequenceEqual("RF64"u8);
        if ((!rf64 && !header[..4].SequenceEqual("RIFF"u8)) || !header[8..].SequenceEqual("WAVE"u8))
            throw new MediaContainerException("The WAV output has an invalid RIFF length.");

        ulong rf64Length = 0, rf64DataBytes = 0, rf64Samples = 0;
        if (rf64)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != uint.MaxValue)
                throw new MediaContainerException("The RF64 output has an invalid size marker.");
            Span<byte> ds64 = stackalloc byte[36]; source.ReadExactly(ds64);
            if (!ds64[..4].SequenceEqual("ds64"u8) || BinaryPrimitives.ReadUInt32LittleEndian(ds64[4..]) != 28
                || BinaryPrimitives.ReadUInt32LittleEndian(ds64[32..]) != 0)
                throw new MediaContainerException("The RF64 size table is unsupported or invalid.");
            rf64Length = BinaryPrimitives.ReadUInt64LittleEndian(ds64[8..]);
            rf64DataBytes = BinaryPrimitives.ReadUInt64LittleEndian(ds64[16..]);
            rf64Samples = BinaryPrimitives.ReadUInt64LittleEndian(ds64[24..]);
            if (rf64Length != (ulong)(source.Length - 8))
                throw new MediaContainerException("The RF64 output has an invalid length.");
        }
        else if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != source.Length - 8)
            throw new MediaContainerException("The WAV output has an invalid RIFF length.");
        int rate = 0; short channels = 0, bits = 0, align = 0;
        long dataOffset = 0, dataBytes = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (source.Position <= source.Length - 8)
        {
            source.ReadExactly(chunk);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long size = chunkSize;
            if (rf64 && chunk[..4].SequenceEqual("data"u8) && chunkSize == uint.MaxValue)
            {
                if (rf64DataBytes > long.MaxValue) throw new MediaContainerException("The RF64 data is too large.");
                size = (long)rf64DataBytes;
            }
            else if (rf64 && chunkSize == uint.MaxValue)
                throw new MediaContainerException("An RF64 chunk has no supported size entry.");
            long end = checked(source.Position + size);
            if (end > source.Length) throw new MediaContainerException("A WAV chunk is truncated.");
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new MediaContainerException("A WAV format is truncated.");
                source.ReadExactly(format);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1)
                    throw new MediaContainerException("The WAV output is not integer PCM.");
                channels = BinaryPrimitives.ReadInt16LittleEndian(format[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
                align = BinaryPrimitives.ReadInt16LittleEndian(format[12..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(format[14..]);
                if (channels is < 1 or > 8 || rate is < 8000 or > 384000 || bits is not (16 or 24 or 32)
                    || align != channels * bits / 8 || BinaryPrimitives.ReadInt32LittleEndian(format[8..]) != rate * align)
                    throw new MediaContainerException("The WAV PCM format is inconsistent.");
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (dataOffset != 0) throw new MediaContainerException("Multiple WAV data chunks are unsupported.");
                dataOffset = source.Position; dataBytes = size;
            }
            source.Position = end + (size & 1);
        }
        if (rate == 0 || dataOffset == 0 || dataBytes == 0 || dataBytes % align != 0
            || (rf64 && rf64Samples != (ulong)(dataBytes / align)))
            throw new MediaContainerException("The WAV output has no complete PCM samples.");
        return new Info(rate, channels, bits, dataOffset, dataBytes);
    }

    internal static async Task WriteAsync(Stream source, Stream output, MediaTimeRange? range,
        MediaMergeMetadata? metadata, CancellationToken ct, bool forceRf64 = false)
    {
        var info = Read(source);
        long first = range is null ? 0 : checked((long)Math.Round(range.StartSeconds * info.SampleRate, MidpointRounding.AwayFromZero));
        long last = range is null ? info.SampleCount : checked((long)Math.Round(range.EndSeconds * info.SampleRate, MidpointRounding.AwayFromZero));
        if (first < 0 || last > info.SampleCount || last <= first)
            throw new MediaContainerException("The requested clip is outside the decoded audio timeline.");
        long count = checked((last - first) * info.BlockAlign);

        using var tags = new MemoryStream(); tags.Write("INFO"u8);
        void Tag(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(value + '\0');
            tags.Write(Encoding.ASCII.GetBytes(key));
            using var tagWriter = new BinaryWriter(tags, Encoding.UTF8, true);
            tagWriter.Write((uint)bytes.Length); tags.Write(bytes);
            if ((bytes.Length & 1) != 0) tags.WriteByte(0);
        }
        Tag("INAM", metadata?.Title); Tag("IART", metadata?.Artist ?? metadata?.Uploader);
        Tag("IPRD", metadata?.Album); Tag("ICRD", metadata?.ReleaseDate);

        long totalLength = checked(44 + count + (count & 1) + (tags.Length > 4 ? 8 + tags.Length : 0));
        bool rf64 = forceRf64 || totalLength - 8 > uint.MaxValue;
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(rf64 ? "RF64"u8 : "RIFF"u8); writer.Write(rf64 ? uint.MaxValue : 0u); writer.Write("WAVE"u8);
        if (rf64)
        {
            writer.Write("ds64"u8); writer.Write(28u); writer.Write(0ul); writer.Write((ulong)count);
            writer.Write((ulong)(count / info.BlockAlign)); writer.Write(0u);
        }
        writer.Write("fmt "u8); writer.Write(16u);
        writer.Write((short)1); writer.Write(info.Channels); writer.Write(info.SampleRate);
        writer.Write(info.SampleRate * info.BlockAlign); writer.Write((short)info.BlockAlign); writer.Write(info.Bits);
        writer.Write("data"u8); writer.Write(rf64 ? uint.MaxValue : (uint)count);
        source.Position = info.DataOffset + first * info.BlockAlign;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int got = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (got == 0) throw new EndOfStreamException("The decoded WAV was truncated.");
                await output.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false); remaining -= got;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        if ((count & 1) != 0) output.WriteByte(0);
        if (tags.Length > 4) { writer.Write("LIST"u8); writer.Write((uint)tags.Length); tags.Position = 0; tags.CopyTo(output); }
        long end = output.Position;
        if (rf64)
        {
            output.Position = 20; writer.Write((ulong)(end - 8));
            output.Position = 28; writer.Write((ulong)count);
            output.Position = 36; writer.Write((ulong)(count / info.BlockAlign));
        }
        else
        {
            output.Position = 4; writer.Write(checked((uint)(end - 8)));
        }
        writer.Flush(); output.Position = end;
        output.Position = 0;
        if (Read(output).SampleCount != last - first) throw new MediaContainerException("The WAV clip lost PCM samples.");
    }
}
