using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

internal enum FlacMetadataWriteResult { Written, NoChanges, UnsupportedAudio }

internal static class FlacMetadataWriter
{
    private const int MaxMetadataBlocks = 256;
    private const int MaxCommentBytes = 4 * 1024 * 1024;
    private const int MaxCommentCount = 16_384;
    private const int MaxBlockBytes = 0x00FF_FFFF;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record Block(byte Type, long DataOffset, int Length, uint? PictureType = null);
    private sealed record Comments(byte[] Vendor, List<byte[]> Values);

    internal static async Task<FlacMetadataWriteResult> TryWriteAsync(Stream source, Stream destination,
        MediaMergeMetadata metadata, MediaThumbnail? cover, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek || !destination.CanWrite || !destination.CanSeek
            || !MediaMergePlan.ValidMetadata(metadata)) return FlacMetadataWriteResult.UnsupportedAudio;

        Dictionary<string, string> replacements = TagValues(metadata);
        MediaThumbnail? validCover = cover is not null && MediaThumbnail.TryCreate(cover.Data) is { } checkedCover
            && checkedCover.MediaType == cover.MediaType ? checkedCover : null;
        if (replacements.Count == 0 && validCover is null) return FlacMetadataWriteResult.NoChanges;

        if (!TryReadBlocks(source, out List<Block> blocks, out long audioOffset, out Comments? oldComments))
            return FlacMetadataWriteResult.UnsupportedAudio;

        byte[]? commentBlock = null;
        if (replacements.Count > 0)
        {
            Comments comments = oldComments ?? new Comments(Encoding.UTF8.GetBytes("VESPER"), []);
            List<byte[]> values = UpdateComments(comments.Values, replacements);
            commentBlock = BuildCommentBlock(comments.Vendor, values);
            if (commentBlock is null) return FlacMetadataWriteResult.UnsupportedAudio;
        }

        byte[]? pictureBlock = validCover is null ? null : BuildPictureBlock(validCover);
        if (validCover is not null && pictureBlock is null) return FlacMetadataWriteResult.UnsupportedAudio;

        int frontCoverCount = validCover is null ? 0 : blocks.Count(block => block.Type == 6 && block.PictureType == 3);
        int outputCount = blocks.Count
            + (oldComments is null && commentBlock is not null ? 1 : 0)
            + (validCover is not null && frontCoverCount == 0 ? 1 : 0)
            - Math.Max(0, frontCoverCount - 1);
        if (outputCount > MaxMetadataBlocks) return FlacMetadataWriteResult.UnsupportedAudio;

        source.Position = 0;
        destination.SetLength(0);
        destination.Position = 0;
        await destination.WriteAsync("fLaC"u8.ToArray(), ct).ConfigureAwait(false);

        int written = 0;
        bool commentWritten = false;
        bool coverWritten = false;
        foreach (Block block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block.Type == 4 && commentBlock is not null)
            {
                WriteBlockHeader(destination, block.Type, commentBlock.Length, ++written == outputCount);
                await destination.WriteAsync(commentBlock, ct).ConfigureAwait(false);
                commentWritten = true;
                continue;
            }

            if (block.Type == 6 && block.PictureType == 3 && pictureBlock is not null)
            {
                if (!coverWritten)
                {
                    WriteBlockHeader(destination, block.Type, pictureBlock.Length, ++written == outputCount);
                    await destination.WriteAsync(pictureBlock, ct).ConfigureAwait(false);
                    coverWritten = true;
                }
                continue;
            }

            WriteBlockHeader(destination, block.Type, block.Length, ++written == outputCount);
            source.Position = block.DataOffset;
            await CopyExactlyAsync(source, destination, block.Length, ct).ConfigureAwait(false);
        }

        if (!commentWritten && commentBlock is not null)
        {
            WriteBlockHeader(destination, 4, commentBlock.Length, ++written == outputCount);
            await destination.WriteAsync(commentBlock, ct).ConfigureAwait(false);
        }
        if (!coverWritten && pictureBlock is not null)
        {
            WriteBlockHeader(destination, 6, pictureBlock.Length, ++written == outputCount);
            await destination.WriteAsync(pictureBlock, ct).ConfigureAwait(false);
        }

        if (written != outputCount) throw new InvalidDataException("The FLAC metadata block count changed while writing.");
        source.Position = audioOffset;
        await CopyExactlyAsync(source, destination, source.Length - audioOffset, ct).ConfigureAwait(false);
        destination.SetLength(destination.Position);
        return FlacMetadataWriteResult.Written;
    }

    private static Dictionary<string, string> TagValues(MediaMergeMetadata metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("TITLE", metadata.Title);
        Add("ARTIST", metadata.Artist);
        Add("ALBUM", metadata.Album);
        Add("ALBUMARTIST", metadata.AlbumArtist);
        Add("TRACKNUMBER", metadata.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("DISCNUMBER", metadata.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("DATE", metadata.ReleaseDate);
        return values;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains('\0')) values[key] = value;
        }
    }

    private static List<byte[]> UpdateComments(List<byte[]> existing, Dictionary<string, string> replacements)
    {
        var values = new List<byte[]>(existing.Count + replacements.Count);
        foreach (byte[] field in existing)
        {
            int equals = Array.IndexOf(field, (byte)'=');
            string key = Encoding.ASCII.GetString(field, 0, equals);
            if (!replacements.ContainsKey(key)) values.Add(field);
        }
        foreach ((string key, string value) in replacements)
        {
            byte[] encoded;
            try { encoded = Utf8.GetBytes(key + "=" + value); }
            catch (EncoderFallbackException) { continue; }
            values.Add(encoded);
        }
        return values;
    }

    private static byte[]? BuildCommentBlock(byte[] vendor, List<byte[]> values)
    {
        if (values.Count > MaxCommentCount) return null;
        long length = 4L + vendor.Length + 4;
        foreach (byte[] value in values) length += 4L + value.Length;
        if (length > MaxCommentBytes || length > MaxBlockBytes) return null;

        using var output = new MemoryStream((int)length);
        WriteLittleEndian(output, checked((uint)vendor.Length));
        output.Write(vendor);
        WriteLittleEndian(output, checked((uint)values.Count));
        foreach (byte[] value in values)
        {
            WriteLittleEndian(output, checked((uint)value.Length));
            output.Write(value);
        }
        return output.ToArray();
    }

    private static byte[]? BuildPictureBlock(MediaThumbnail cover)
    {
        (uint width, uint height, uint depth) = Dimensions(cover);
        byte[] mime = Encoding.ASCII.GetBytes(cover.MediaType);
        const byte pictureType = 3; // front cover
        long length = 4L + 4 + mime.Length + 4 + 4 + 4 + 4 + 4 + cover.Data.Length;
        if (length > MaxBlockBytes) return null;

        using var output = new MemoryStream((int)length);
        WriteBigEndian(output, pictureType);
        WriteBigEndian(output, checked((uint)mime.Length));
        output.Write(mime);
        WriteBigEndian(output, 0); // empty description
        WriteBigEndian(output, width);
        WriteBigEndian(output, height);
        WriteBigEndian(output, depth);
        WriteBigEndian(output, 0); // indexed colors
        WriteBigEndian(output, checked((uint)cover.Data.Length));
        output.Write(cover.Data);
        return output.ToArray();
    }

    private static (uint Width, uint Height, uint Depth) Dimensions(MediaThumbnail cover)
    {
        ReadOnlySpan<byte> bytes = cover.Data;
        if (cover.MediaType == "image/png" && bytes.Length >= 26)
        {
            uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            uint channels = bytes[25] switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
            return (width, height, (uint)(bytes[24] * channels));
        }

        if (cover.MediaType == "image/jpeg")
        {
            int position = 2;
            while (position < bytes.Length - 4 && bytes[position++] == 0xFF)
            {
                while (position < bytes.Length && bytes[position] == 0xFF) position++;
                if (position >= bytes.Length) break;
                byte marker = bytes[position++];
                if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7) continue;
                if (position + 2 > bytes.Length) break;
                int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
                if (size < 2 || size > bytes.Length - position) break;
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    if (size >= 8)
                    {
                        uint height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]);
                        uint width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]);
                        uint depth = (uint)(bytes[position + 2] * bytes[position + 7]);
                        return (width, height, depth);
                    }
                    break;
                }
                position += size;
                if (marker == 0xDA) break;
            }
        }
        return (0, 0, 0);
    }

    private static bool TryReadBlocks(Stream source, out List<Block> blocks, out long audioOffset, out Comments? comments)
    {
        blocks = [];
        audioOffset = 0;
        comments = null;
        if (source.Length < 42) return false;
        source.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        if (!ReadExactly(source, signature) || !signature.SequenceEqual("fLaC"u8)) return false;

        bool last;
        bool foundStreamInfo = false;
        Span<byte> header = stackalloc byte[4];
        Span<byte> typeBytes = stackalloc byte[4];
        do
        {
            if (blocks.Count >= MaxMetadataBlocks) return false;
            if (!ReadExactly(source, header)) return false;
            byte type = (byte)(header[0] & 0x7F);
            last = (header[0] & 0x80) != 0;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            long offset = source.Position;
            if (type == 127) return false;
            if (length > source.Length - offset) return false;
            if (blocks.Count == 0 && (type != 0 || length != 34)) return false;
            if (type == 0)
            {
                if (foundStreamInfo || blocks.Count != 0 || length != 34) return false;
                foundStreamInfo = true;
            }

            uint? pictureType = null;
            if (type == 4)
            {
                if (comments is not null || length > MaxCommentBytes) return false;
                byte[] data = new byte[length];
                if (!ReadExactly(source, data) || !TryParseComments(data, out comments)) return false;
            }
            else if (type == 6)
            {
                if (length < 4) return false;
                if (!ReadExactly(source, typeBytes)) return false;
                pictureType = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
            }

            blocks.Add(new Block(type, offset, length, pictureType));
            source.Position = offset + length;
        } while (!last);

        audioOffset = source.Position;
        return foundStreamInfo && audioOffset < source.Length;
    }

    private static bool TryParseComments(byte[] data, out Comments? comments)
    {
        comments = null;
        int position = 0;
        if (!TryReadLittleEndian(data, ref position, out uint vendorLength) || vendorLength > data.Length - position) return false;
        byte[] vendor = data.AsSpan(position, (int)vendorLength).ToArray();
        try
        {
            string decodedVendor = Utf8.GetString(vendor);
            if (decodedVendor.Contains('\0')) return false;
        }
        catch (DecoderFallbackException) { return false; }
        position += (int)vendorLength;
        if (!TryReadLittleEndian(data, ref position, out uint count) || count > MaxCommentCount) return false;

        var values = new List<byte[]>((int)count);
        for (uint index = 0; index < count; index++)
        {
            if (!TryReadLittleEndian(data, ref position, out uint length) || length > data.Length - position) return false;
            ReadOnlySpan<byte> field = data.AsSpan(position, (int)length);
            int equals = field.IndexOf((byte)'=');
            if (equals <= 0 || field.Contains((byte)0)) return false;
            for (int i = 0; i < equals; i++)
            {
                if (field[i] is < 0x20 or > 0x7D) return false;
            }
            try { _ = Utf8.GetString(field); }
            catch (DecoderFallbackException) { return false; }
            values.Add(field.ToArray());
            position += (int)length;
        }
        if (position != data.Length) return false;
        comments = new Comments(vendor, values);
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer) => ReadExactly(stream, buffer.AsSpan());

    private static bool TryReadLittleEndian(byte[] data, ref int position, out uint value)
    {
        value = 0;
        if (position > data.Length - 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        position += 4;
        return true;
    }

    private static void WriteBlockHeader(Stream output, byte type, int length, bool last)
    {
        if (length is < 0 or > MaxBlockBytes) throw new InvalidDataException("The FLAC metadata block is too large.");
        output.WriteByte((byte)(type | (last ? 0x80 : 0)));
        output.WriteByte((byte)(length >> 16));
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
    }

    private static void WriteLittleEndian(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteBigEndian(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long bytes, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (bytes > 0)
            {
                ct.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, bytes);
                int read = await source.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The FLAC source ended before its metadata or audio data was copied.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytes -= read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
