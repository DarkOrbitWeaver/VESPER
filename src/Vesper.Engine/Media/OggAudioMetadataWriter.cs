using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

internal enum OggAudioMetadataWriteResult { Written, NoChanges, UnsupportedAudio }

internal static class OggAudioMetadataWriter
{
    private const int MaxHeaderPacketBytes = 16 * 1024 * 1024;
    private const int MaxCommentPacketBytes = 8 * 1024 * 1024;
    private const int MaxCommentCount = 16_384;
    private const int MaxCommentBytes = 1024 * 1024;
    private const int MaxPageBodyBytes = 255 * 255;
    private const int MaxHeaderPages = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private enum Codec { Vorbis, Opus }

    private sealed record Header(Codec Codec, uint Serial, uint LastSequence, long EndOffset,
        byte[] Identification, byte[] Comments, byte[]? Setup);

    private sealed record Page(byte[] Bytes, byte Flags, long Granule, uint Serial, uint Sequence,
        byte[] Lacing, int BodyOffset, int BodyLength);

    private sealed record Comment(string Key, string Value);

    internal static async Task<OggAudioMetadataWriteResult> TryWriteAsync(Stream source, Stream destination,
        MediaMergeMetadata metadata, MediaThumbnail? cover, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek || !destination.CanWrite || !destination.CanSeek
            || !MediaMergePlan.ValidMetadata(metadata))
            return OggAudioMetadataWriteResult.UnsupportedAudio;

        source.Position = 0;
        destination.Position = 0;
        if (!TryReadHeaders(source, out Header header)) return OggAudioMetadataWriteResult.UnsupportedAudio;
        if (!HasValues(metadata) && cover is null) return OggAudioMetadataWriteResult.NoChanges;
        if (!TryUpdateComments(header.Codec, header.Comments, metadata, cover, out byte[] comments))
            return OggAudioMetadataWriteResult.UnsupportedAudio;
        if (comments.AsSpan().SequenceEqual(header.Comments)) return OggAudioMetadataWriteResult.NoChanges;

        try
        {
            uint sequence = 0;
            sequence = await WriteHeaderPacketAsync(destination, header.Identification, header.Serial, sequence, first: true, ct)
                .ConfigureAwait(false);
            sequence = await WriteHeaderPacketAsync(destination, comments, header.Serial, sequence, first: false, ct)
                .ConfigureAwait(false);
            if (header.Setup is not null)
                sequence = await WriteHeaderPacketAsync(destination, header.Setup, header.Serial, sequence, first: false, ct)
                    .ConfigureAwait(false);

            source.Position = header.EndOffset;
            uint sourceSequence = unchecked(header.LastSequence + 1);
            bool previousContinues = false;
            bool ended = false;
            bool sawAudio = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Page? page = ReadPage(source);
                if (page is null) break;
                if (page.Serial != header.Serial || page.Sequence != sourceSequence || ended
                    || (page.Flags & 0x02) != 0 || ((page.Flags & 0x01) != 0) != previousContinues)
                    return OggAudioMetadataWriteResult.UnsupportedAudio;

                if (page.BodyLength > 0) sawAudio = true;
                BinaryPrimitives.WriteUInt32LittleEndian(page.Bytes.AsSpan(18, 4), sequence++);
                WriteChecksum(page.Bytes);
                await destination.WriteAsync(page.Bytes, ct).ConfigureAwait(false);
                sourceSequence = unchecked(sourceSequence + 1);
                previousContinues = page.Lacing.Length > 0 && page.Lacing[^1] == byte.MaxValue;
                ended = (page.Flags & 0x04) != 0;
            }

            if (!sawAudio) return OggAudioMetadataWriteResult.UnsupportedAudio;
            return OggAudioMetadataWriteResult.Written;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException or ArgumentException or OverflowException)
        {
            return OggAudioMetadataWriteResult.UnsupportedAudio;
        }
    }

    private static bool TryReadHeaders(Stream source, out Header header)
    {
        header = null!;
        source.Position = 0;
        using var packet = new MemoryStream();
        var packets = new List<byte[]>(3);
        Codec? codec = null;
        int requiredPackets = 0;
        uint serial = 0;
        uint expectedSequence = 0;
        bool previousContinues = false;
        bool firstPage = true;
        int pageCount = 0;

        while (true)
        {
            if (++pageCount > MaxHeaderPages) return false;
            Page? page;
            try { page = ReadPage(source); }
            catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException) { return false; }
            if (page is null || page.Sequence != expectedSequence
                || ((page.Flags & 0x01) != 0) != previousContinues)
                return false;

            if (firstPage)
            {
                if (page.Sequence != 0 || (page.Flags & 0x02) == 0 || (page.Flags & 0x04) != 0)
                    return false;
                serial = page.Serial;
                firstPage = false;
            }
            else if (page.Serial != serial || (page.Flags & (0x02 | 0x04)) != 0)
            {
                return false;
            }

            int bodyPosition = 0;
            bool packetCompletedOnPage = false;
            for (int index = 0; index < page.Lacing.Length; index++)
            {
                if (packets.Count >= requiredPackets && requiredPackets != 0) return false;
                int segmentBytes = page.Lacing[index];
                if (packet.Length + segmentBytes > MaxHeaderPacketBytes) return false;
                packet.Write(page.Bytes, page.BodyOffset + bodyPosition, segmentBytes);
                bodyPosition += segmentBytes;

                if (segmentBytes == byte.MaxValue) continue;

                byte[] completed = packet.ToArray();
                packet.SetLength(0);
                packetCompletedOnPage = true;
                if (packets.Count == 0)
                {
                    if (IsOpusHead(completed))
                    {
                        codec = Codec.Opus;
                        requiredPackets = 2;
                    }
                    else if (IsVorbisIdentification(completed))
                    {
                        codec = Codec.Vorbis;
                        requiredPackets = 3;
                    }
                    else return false;
                }
                else if (packets.Count == 1 && codec is { } value
                    && !IsCommentPacket(value, completed)) return false;
                else if (packets.Count == 2 && codec == Codec.Vorbis && !IsVorbisSetup(completed)) return false;

                packets.Add(completed);
                if (packets.Count == requiredPackets)
                {
                    if (index != page.Lacing.Length - 1 || bodyPosition != page.BodyLength || page.Granule != 0) return false;
                    header = new Header(codec!.Value, serial, page.Sequence, source.Position,
                        packets[0], packets[1], codec == Codec.Vorbis ? packets[2] : null);
                    return true;
                }
            }

            long expectedGranule = packetCompletedOnPage ? 0 : -1;
            if (bodyPosition != page.BodyLength || (page.Flags & 0x04) != 0 || page.Granule != expectedGranule) return false;
            previousContinues = page.Lacing.Length > 0 && page.Lacing[^1] == byte.MaxValue;
            expectedSequence = unchecked(expectedSequence + 1);
        }
    }

    private static bool IsOpusHead(byte[] packet) => packet.Length >= 19
        && packet.AsSpan(0, 8).SequenceEqual("OpusHead"u8) && packet[8] == 1 && packet[9] > 0;

    private static bool IsVorbisIdentification(byte[] packet) => packet.Length == 30
        && packet[0] == 1 && packet.AsSpan(1, 6).SequenceEqual("vorbis"u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7, 4)) == 0
        && packet[11] > 0 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)) > 0;

    private static bool IsCommentPacket(Codec codec, byte[] packet) => codec switch
    {
        Codec.Opus => packet.Length >= 8 && packet.AsSpan(0, 8).SequenceEqual("OpusTags"u8),
        Codec.Vorbis => packet.Length >= 7 && packet[0] == 3 && packet.AsSpan(1, 6).SequenceEqual("vorbis"u8),
        _ => false
    };

    private static bool IsVorbisSetup(byte[] packet) => packet.Length >= 7
        && packet[0] == 5 && packet.AsSpan(1, 6).SequenceEqual("vorbis"u8);

    private static bool HasValues(MediaMergeMetadata metadata) =>
        HasText(metadata.Title) || HasText(metadata.Artist) || HasText(metadata.Album)
        || HasText(metadata.AlbumArtist) || metadata.TrackNumber is not null || metadata.DiscNumber is not null
        || HasText(metadata.ReleaseDate);

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');

    private static bool TryUpdateComments(Codec codec, byte[] packet, MediaMergeMetadata metadata,
        MediaThumbnail? cover, out byte[] updated)
    {
        updated = [];
        if (!TryParseComments(codec, packet, out string vendor, out List<Comment> comments, out byte[] trailingData)) return false;
        List<Comment> values = comments;

        var replacements = new List<Comment>();
        AddReplacement(replacements, "TITLE", metadata.Title);
        AddReplacement(replacements, "ARTIST", metadata.Artist);
        AddReplacement(replacements, "ALBUM", metadata.Album);
        AddReplacement(replacements, "ALBUMARTIST", metadata.AlbumArtist);
        AddReplacement(replacements, "TRACKNUMBER", metadata.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddReplacement(replacements, "DISCNUMBER", metadata.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddReplacement(replacements, "DATE", metadata.ReleaseDate);
        if (cover is not null)
        {
            if (!TryBuildPictureComment(cover, out string picture)) return false;
            replacements.Add(new Comment("METADATA_BLOCK_PICTURE", picture));
        }

        foreach (Comment replacement in replacements)
        {
            int insertion = values.FindIndex(comment => string.Equals(comment.Key, replacement.Key, StringComparison.OrdinalIgnoreCase));
            values.RemoveAll(comment => string.Equals(comment.Key, replacement.Key, StringComparison.OrdinalIgnoreCase));
            if (insertion < 0 || insertion > values.Count) insertion = values.Count;
            values.Insert(insertion, replacement);
        }

        if (!TryWriteCommentPacket(codec, vendor, values, trailingData, out updated)) return false;
        return true;
    }

    private static void AddReplacement(List<Comment> replacements, string key, string? value)
    {
        if (HasText(value)) replacements.Add(new Comment(key, value!));
    }

    private static bool TryParseComments(Codec codec, byte[] packet, out string vendor, out List<Comment> comments,
        out byte[] trailingData)
    {
        vendor = "";
        comments = null!;
        trailingData = [];
        int offset;
        if (codec == Codec.Opus)
        {
            if (packet.Length < 8 || !packet.AsSpan(0, 8).SequenceEqual("OpusTags"u8)) return false;
            offset = 8;
        }
        else
        {
            if (packet.Length < 7 || packet[0] != 3 || !packet.AsSpan(1, 6).SequenceEqual("vorbis"u8)) return false;
            offset = 7;
        }

        if (!ReadString(packet, ref offset, MaxCommentBytes, out vendor) || vendor.Contains('\0')
            || !TryReadUInt32(packet, ref offset, out uint count) || count > MaxCommentCount) return false;
        var values = new List<Comment>((int)count);
        for (uint index = 0; index < count; index++)
        {
            if (!ReadString(packet, ref offset, MaxCommentBytes, out string value)) return false;
            int separator = value.IndexOf('=');
            if (separator is < 1 or > 256 || value.Contains('\0')) return false;
            string key = value[..separator];
            if (key.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
                return false;
            values.Add(new Comment(key, value[(separator + 1)..]));
        }

        if (codec == Codec.Vorbis)
        {
            if (offset >= packet.Length || packet[offset++] != 1) return false;
            if (offset != packet.Length) return false;
        }
        else
        {
            int extraBytes = packet.Length - offset;
            if (extraBytes > MaxCommentPacketBytes) return false;
            trailingData = packet.AsSpan(offset).ToArray();
        }
        comments = values;
        return true;
    }

    private static bool ReadString(byte[] packet, ref int offset, int maxBytes, out string value)
    {
        value = "";
        if (!TryReadUInt32(packet, ref offset, out uint length) || length > maxBytes || length > packet.Length - offset)
            return false;
        try { value = StrictUtf8.GetString(packet, offset, (int)length); }
        catch (DecoderFallbackException) { return false; }
        offset += (int)length;
        return true;
    }

    private static bool TryWriteCommentPacket(Codec codec, string vendor, List<Comment> comments,
        byte[] trailingData, out byte[] packet)
    {
        packet = [];
        try
        {
            byte[] vendorBytes = StrictUtf8.GetBytes(vendor);
            if (vendorBytes.Length > MaxCommentBytes || comments.Count > MaxCommentCount) return false;
            using var output = new MemoryStream();
            if (codec == Codec.Opus) output.Write("OpusTags"u8);
            else
            {
                output.WriteByte(3);
                output.Write("vorbis"u8);
            }
            WriteLengthAndBytes(output, vendorBytes);
            WriteUInt32(output, (uint)comments.Count);
            foreach (Comment comment in comments)
            {
                if (comment.Key.Length is < 1 or > 256 || comment.Key.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                    || comment.Value.Contains('\0')) return false;
                byte[] bytes = StrictUtf8.GetBytes(comment.Key + "=" + comment.Value);
                if (bytes.Length > MaxCommentBytes) return false;
                WriteLengthAndBytes(output, bytes);
                if (output.Length > MaxCommentPacketBytes - 1) return false;
            }
            if (codec == Codec.Vorbis) output.WriteByte(1);
            else output.Write(trailingData);
            if (output.Length > MaxCommentPacketBytes) return false;
            packet = output.ToArray();
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool TryBuildPictureComment(MediaThumbnail cover, out string value)
    {
        value = "";
        if (cover.Data.Length is < 16 or > MediaThumbnail.MaxBytes
            || cover.MediaType is not ("image/png" or "image/jpeg")) return false;

        using var picture = new MemoryStream(cover.Data.Length + 32);
        WriteBigEndianUInt32(picture, 3); // front cover
        byte[] mime = Encoding.ASCII.GetBytes(cover.MediaType);
        WriteBigEndianUInt32(picture, (uint)mime.Length);
        picture.Write(mime);
        WriteBigEndianUInt32(picture, 0); // description
        WriteBigEndianUInt32(picture, 0); // width unknown
        WriteBigEndianUInt32(picture, 0); // height unknown
        WriteBigEndianUInt32(picture, 0); // color depth unknown
        WriteBigEndianUInt32(picture, 0); // indexed colors
        WriteBigEndianUInt32(picture, (uint)cover.Data.Length);
        picture.Write(cover.Data);
        value = Convert.ToBase64String(picture.ToArray());
        return true;
    }

    private static async Task<uint> WriteHeaderPacketAsync(Stream output, byte[] packet, uint serial,
        uint sequence, bool first, CancellationToken ct)
    {
        if (packet.Length == 0 || packet.Length > MaxHeaderPacketBytes) throw new IOException("An Ogg header packet has an unsupported size.");
        var lacing = new List<byte>((packet.Length / 255) + 1);
        int remaining = packet.Length;
        while (remaining >= byte.MaxValue)
        {
            lacing.Add(byte.MaxValue);
            remaining -= byte.MaxValue;
        }
        lacing.Add((byte)remaining);

        int laceOffset = 0;
        int packetOffset = 0;
        while (laceOffset < lacing.Count)
        {
            int laceCount = Math.Min(255, lacing.Count - laceOffset);
            int bodyBytes = 0;
            for (int index = 0; index < laceCount; index++) bodyBytes += lacing[laceOffset + index];
            byte[] pageLacing = lacing.Skip(laceOffset).Take(laceCount).ToArray();
            byte[] body = packet.AsSpan(packetOffset, bodyBytes).ToArray();
            byte flags = first ? (byte)0x02 : packetOffset > 0 ? (byte)0x01 : (byte)0;
            long granule = laceOffset + laceCount == lacing.Count ? 0 : -1;
            byte[] page = BuildPage(flags, granule, serial, sequence, pageLacing, body);
            await output.WriteAsync(page, ct).ConfigureAwait(false);
            sequence = unchecked(sequence + 1);
            first = false;
            laceOffset += laceCount;
            packetOffset += bodyBytes;
        }
        return sequence;
    }

    private static byte[] BuildPage(byte flags, long granule, uint serial, uint sequence,
        byte[] lacing, byte[] body)
    {
        if (lacing.Length > 255 || body.Length > MaxPageBodyBytes) throw new IOException("An Ogg page is too large.");
        byte[] page = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Length);
        WriteChecksum(page);
        return page;
    }

    private static Page? ReadPage(Stream input)
    {
        Span<byte> header = stackalloc byte[27];
        int first = ReadAtMost(input, header);
        if (first == 0) return null;
        if (first != header.Length || !header[..4].SequenceEqual("OggS"u8) || header[4] != 0 || (header[5] & ~0x07) != 0)
            throw new IOException("The Ogg page header is invalid.");

        int segmentCount = header[26];
        var lacing = new byte[segmentCount];
        ReadExactly(input, lacing);
        int bodyLength = 0;
        foreach (byte segment in lacing) bodyLength = checked(bodyLength + segment);
        var page = new byte[27 + segmentCount + bodyLength];
        header.CopyTo(page);
        lacing.CopyTo(page, 27);
        ReadExactly(input, page.AsSpan(27 + segmentCount, bodyLength));

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22, 4));
        page.AsSpan(22, 4).Clear();
        uint actual = OggCrc(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), expected);
        if (expected != actual) throw new IOException("The Ogg page checksum is invalid.");

        return new Page(page, header[5], BinaryPrimitives.ReadInt64LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[14..]), BinaryPrimitives.ReadUInt32LittleEndian(header[18..]),
            lacing, 27 + segmentCount, bodyLength);
    }

    private static void WriteChecksum(byte[] page)
    {
        page.AsSpan(22, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), OggCrc(page));
    }

    private static uint OggCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    private static int ReadAtMost(Stream input, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static void ReadExactly(Stream input, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read == 0) throw new IOException("The Ogg stream ended in a page.");
            total += read;
        }
    }

    private static bool TryReadUInt32(byte[] bytes, ref int offset, out uint value)
    {
        value = 0;
        if (offset > bytes.Length - 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return true;
    }

    private static void WriteLengthAndBytes(Stream output, byte[] bytes)
    {
        WriteUInt32(output, (uint)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteBigEndianUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
