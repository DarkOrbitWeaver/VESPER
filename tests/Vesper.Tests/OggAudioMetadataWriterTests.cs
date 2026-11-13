using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class OggAudioMetadataWriterTests
{
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task OpusTagsPreserveVendorUnknownCommentsAndAudioPackets()
    {
        byte[] audio = [0xF8, 0xFF, 0xFE];
        byte[] trailingData = [1, 0xA5, 0x5A];
        byte[] input = BuildStream(Codec.Opus, audio, trailingData);
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(SmallPng));
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, output,
            Metadata(), cover, default);

        Assert.Equal(OggAudioMetadataWriteResult.Written, result);
        List<TestPage> pages = ReadPages(output.ToArray());
        List<byte[]> packets = ReadPackets(pages);
        Assert.NotEqual(0, pages[0].Flags & (byte)PageFlags.BeginningOfStream);
        Assert.DoesNotContain(pages.Skip(1), page => (page.Flags & (byte)PageFlags.BeginningOfStream) != 0);
        Assert.Equal(BuildOpusHead(), packets[0]);
        Assert.Equal(audio, packets[^1]);
        Assert.True(packets[1].AsSpan().EndsWith(trailingData));
        Assert.Equal(960, pages[^1].Granule);

        (string vendor, List<string> comments) = ReadComments(Codec.Opus, packets[1]);
        Assert.Equal("Original encoder", vendor);
        Assert.Contains("COMMENT=keep this field", comments);
        Assert.Contains("TITLE=Song One", comments);
        Assert.Contains("ARTIST=Fixture Band", comments);
        Assert.Contains("ALBUM=Fixture Album", comments);
        Assert.Contains("ALBUMARTIST=Fixture Band", comments);
        Assert.Contains("TRACKNUMBER=2", comments);
        Assert.Contains("DISCNUMBER=1", comments);
        Assert.Contains("DATE=2024-03-12", comments);
        string picture = Assert.Single(comments, value => value.StartsWith("METADATA_BLOCK_PICTURE=", StringComparison.Ordinal));
        Assert.Equal(SmallPng, ReadPicture(Convert.FromBase64String(picture[(picture.IndexOf('=') + 1)..])));
    }

    [Fact]
    public async Task VorbisCommentsKeepAllThreeHeadersAndAudioPacketBytes()
    {
        byte[] audio = [0x00, 0x02, 0x04];
        byte[] input = BuildStream(Codec.Vorbis, audio);
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, output,
            Metadata(), null, default);

        Assert.Equal(OggAudioMetadataWriteResult.Written, result);
        List<TestPage> pages = ReadPages(output.ToArray());
        List<byte[]> packets = ReadPackets(pages);
        Assert.Equal(4, packets.Count);
        Assert.Equal(BuildVorbisIdentification(), packets[0]);
        Assert.Equal(BuildVorbisSetup(), packets[2]);
        Assert.Equal(audio, packets[3]);
        Assert.Equal(9_000, pages[^1].Granule);
        (string vendor, List<string> comments) = ReadComments(Codec.Vorbis, packets[1]);
        Assert.Equal("Original encoder", vendor);
        Assert.Contains("COMMENT=keep this field", comments);
        Assert.Contains("TITLE=Song One", comments);
        Assert.Contains("ALBUM=Fixture Album", comments);
    }

    [Theory]
    [InlineData("quiet-track.ogg", "Vorbis")]
    [InlineData("quiet-track.opus", "Opus")]
    public async Task RealOggSamplesKeepAudioPacketsAndCarryInspectableTagsAndArtwork(string fileName, string codecName)
    {
        Codec codec = Enum.Parse<Codec>(codecName);
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", fileName));
        int headerPackets = codec == Codec.Opus ? 2 : 3;
        List<byte[]> inputPackets = ReadPackets(ReadPages(original));
        Assert.True(inputPackets.Count > headerPackets);
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(SmallPng));
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();

        OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, output,
            Metadata(), cover, default);

        Assert.Equal(OggAudioMetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        List<TestPage> pages = ReadPages(tagged);
        List<byte[]> packets = ReadPackets(pages);
        Assert.Equal(inputPackets.Count, packets.Count);
        for (int index = headerPackets; index < inputPackets.Count; index++)
            Assert.Equal(inputPackets[index], packets[index]);
        (string vendor, List<string> comments) = ReadComments(codec, packets[1]);
        Assert.NotEmpty(vendor);
        Assert.Contains("TITLE=Song One", comments);
        Assert.Contains("ARTIST=Fixture Band", comments);
        Assert.Contains("ALBUM=Fixture Album", comments);
        string picture = Assert.Single(comments, value => value.StartsWith("METADATA_BLOCK_PICTURE=", StringComparison.Ordinal));
        Assert.Equal(SmallPng, ReadPicture(Convert.FromBase64String(picture[(picture.IndexOf('=') + 1)..])));

        string? qaPath = Environment.GetEnvironmentVariable(codec == Codec.Opus ? "VESPER_MEDIA_QA_OPUS" : "VESPER_MEDIA_QA_VORBIS");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, tagged);
        }
    }

    [Fact]
    public async Task LargeOpusCoverSpansPagesAndResequencesAudioWithoutChangingIt()
    {
        byte[] audio = Enumerable.Range(0, 512).Select(index => (byte)(index % 251)).ToArray();
        byte[] input = BuildStream(Codec.Opus, audio, splitAudioPacket: true);
        byte[] png = BuildLargePng();
        Assert.True(png.Length > 65_025 && png.Length < MediaThumbnail.MaxBytes);
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(png));
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, output,
            Metadata(), cover, default);

        Assert.Equal(OggAudioMetadataWriteResult.Written, result);
        List<TestPage> inputPages = ReadPages(input);
        List<TestPage> outputPages = ReadPages(output.ToArray());
        List<byte[]> packets = ReadPackets(outputPages);
        Assert.True(outputPages.Count > inputPages.Count);
        Assert.Equal(Enumerable.Range(0, outputPages.Count).Select(index => (uint)index), outputPages.Select(page => page.Sequence));
        Assert.Equal(inputPages[^1].Granule, outputPages[^1].Granule);
        Assert.Equal(audio, packets[^1]);
        Assert.Contains(outputPages, page => (page.Flags & (byte)PageFlags.ContinuedPacket) != 0);
        Assert.All(outputPages.Where(page => page.Sequence != outputPages[^3].Sequence
                && page.Sequence != outputPages[^1].Sequence
                && (page.Flags & (byte)PageFlags.ContinuedPacket) != 0),
            page => Assert.Equal(-1, page.Granule));
        Assert.Equal(0, outputPages[^3].Granule);
        Assert.Equal(-1, outputPages[^2].Granule);
        Assert.NotEqual(0, outputPages[^1].Flags & (byte)PageFlags.ContinuedPacket);
        foreach (TestPage page in outputPages) Assert.True(HasValidChecksum(page.Bytes));

        using var reread = new MemoryStream(output.ToArray());
        using var rewritten = new MemoryStream();
        OggAudioMetadataWriteResult second = await OggAudioMetadataWriter.TryWriteAsync(reread, rewritten,
            Metadata() with { Title = "Updated title" }, null, default);
        Assert.Equal(OggAudioMetadataWriteResult.Written, second);
        Assert.Equal(audio, ReadPackets(ReadPages(rewritten.ToArray()))[^1]);
    }

    [Fact]
    public async Task InvalidPageChecksumLeavesTheDestinationEmpty()
    {
        byte[] input = BuildStream(Codec.Opus, [0xF8, 0xFF, 0xFE]);
        input[22] ^= 1;
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, output,
            Metadata(), null, default);

        Assert.Equal(OggAudioMetadataWriteResult.UnsupportedAudio, result);
        Assert.Empty(output.ToArray());
    }

    private enum Codec { Vorbis, Opus }
    [Flags]
    private enum PageFlags : byte { ContinuedPacket = 1, BeginningOfStream = 2, EndOfStream = 4 }
    private sealed record TestPage(byte[] Bytes, byte Flags, long Granule, uint Sequence, byte[] Lacing, int BodyOffset);

    private static MediaMergeMetadata Metadata() => new()
    {
        Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
        TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
    };

    private static byte[] BuildStream(Codec codec, byte[] audio, byte[]? opusCommentTail = null,
        bool splitAudioPacket = false)
    {
        List<byte[]> packets = codec == Codec.Opus
            ? [BuildOpusHead(), BuildComments(codec, ["COMMENT=keep this field"], opusCommentTail), audio]
            : [BuildVorbisIdentification(), BuildComments(codec, ["COMMENT=keep this field"]), BuildVorbisSetup(), audio];
        using var stream = new MemoryStream();
        uint sequence = 0;
        for (int index = 0; index < packets.Count; index++)
        {
            long granule = index == packets.Count - 1 ? codec == Codec.Opus ? 960 : 9_000 : 0;
            if (splitAudioPacket && index == packets.Count - 1)
            {
                byte[] firstPart = packets[index].AsSpan(0, 255).ToArray();
                byte[] finalPart = packets[index].AsSpan(255).ToArray();
                stream.Write(BuildPage(0, -1, 0x534F4E47, sequence++, [255], firstPart));
                byte[] finalLacing = new byte[finalPart.Length / 255 + 1];
                int completeSegments = finalPart.Length / 255;
                Array.Fill(finalLacing, (byte)255, 0, completeSegments);
                finalLacing[^1] = (byte)(finalPart.Length % 255);
                stream.Write(BuildPage((byte)(PageFlags.ContinuedPacket | PageFlags.EndOfStream), granule,
                    0x534F4E47, sequence++, finalLacing, finalPart));
                continue;
            }
            byte flags = index == 0 ? (byte)PageFlags.BeginningOfStream : index == packets.Count - 1 ? (byte)PageFlags.EndOfStream : (byte)0;
            byte[] page = BuildSinglePacketPage(packets[index], flags, granule, 0x534F4E47, sequence++);
            stream.Write(page);
        }
        return stream.ToArray();
    }

    private static byte[] BuildPage(byte flags, long granule, uint serial, uint sequence, byte[] lacing, byte[] body)
    {
        byte[] page = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Length);
        SetChecksum(page);
        return page;
    }

    private static byte[] BuildOpusHead() =>
        [.. "OpusHead"u8, 1, 2, 0, 0, 0x80, 0xBB, 0, 0, 0, 0, 0];

    private static byte[] BuildVorbisIdentification()
    {
        byte[] packet = new byte[30];
        packet[0] = 1;
        "vorbis"u8.CopyTo(packet.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), 0);
        packet[11] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 44_100);
        packet[28] = 0xB8;
        packet[29] = 1;
        return packet;
    }

    private static byte[] BuildVorbisSetup() => [5, .. "vorbis"u8, 1, 2, 3];

    private static byte[] BuildComments(Codec codec, string[] comments, byte[]? trailingData = null)
    {
        using var output = new MemoryStream();
        if (codec == Codec.Opus) output.Write("OpusTags"u8);
        else
        {
            output.WriteByte(3);
            output.Write("vorbis"u8);
        }
        WriteCommentString(output, "Original encoder");
        WriteUInt32(output, (uint)comments.Length);
        foreach (string comment in comments) WriteCommentString(output, comment);
        if (codec == Codec.Vorbis) output.WriteByte(1);
        else if (trailingData is not null) output.Write(trailingData);
        return output.ToArray();
    }

    private static void WriteCommentString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] BuildSinglePacketPage(byte[] packet, byte flags, long granule, uint serial, uint sequence)
    {
        if (packet.Length > 65_025) throw new ArgumentOutOfRangeException(nameof(packet));
        var lacing = new List<byte>();
        int remaining = packet.Length;
        while (remaining >= 255)
        {
            lacing.Add(255);
            remaining -= 255;
        }
        lacing.Add((byte)remaining);
        byte[] page = new byte[27 + lacing.Count + packet.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        packet.CopyTo(page, 27 + lacing.Count);
        SetChecksum(page);
        return page;
    }

    private static List<TestPage> ReadPages(byte[] bytes)
    {
        var pages = new List<TestPage>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(bytes.Length - offset >= 27);
            ReadOnlySpan<byte> header = bytes.AsSpan(offset, 27);
            Assert.True(header[..4].SequenceEqual("OggS"u8));
            int segmentCount = header[26];
            Assert.True(bytes.Length - offset >= 27 + segmentCount);
            byte[] lacing = bytes.AsSpan(offset + 27, segmentCount).ToArray();
            int bodyLength = lacing.Sum(value => (int)value);
            int pageLength = 27 + segmentCount + bodyLength;
            Assert.True(bytes.Length - offset >= pageLength);
            byte[] page = bytes.AsSpan(offset, pageLength).ToArray();
            Assert.True(HasValidChecksum(page));
            pages.Add(new TestPage(page, page[5], BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(6)),
                BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(18)), lacing, 27 + segmentCount));
            offset += pageLength;
        }
        Assert.Equal(Enumerable.Range(0, pages.Count).Select(index => (uint)index), pages.Select(page => page.Sequence));
        return pages;
    }

    private static List<byte[]> ReadPackets(List<TestPage> pages)
    {
        var packets = new List<byte[]>();
        using var packet = new MemoryStream();
        foreach (TestPage page in pages)
        {
            int bodyOffset = 0;
            foreach (byte lace in page.Lacing)
            {
                packet.Write(page.Bytes, page.BodyOffset + bodyOffset, lace);
                bodyOffset += lace;
                if (lace < 255)
                {
                    packets.Add(packet.ToArray());
                    packet.SetLength(0);
                }
            }
        }
        Assert.Equal(0, packet.Length);
        return packets;
    }

    private static (string Vendor, List<string> Comments) ReadComments(Codec codec, byte[] packet)
    {
        int offset;
        if (codec == Codec.Opus)
        {
            Assert.True(packet.AsSpan(0, 8).SequenceEqual("OpusTags"u8));
            offset = 8;
        }
        else
        {
            Assert.Equal(3, packet[0]);
            Assert.True(packet.AsSpan(1, 6).SequenceEqual("vorbis"u8));
            offset = 7;
        }
        string vendor = ReadCommentString(packet, ref offset);
        uint count = ReadUInt32(packet, ref offset);
        var comments = new List<string>((int)count);
        for (uint index = 0; index < count; index++) comments.Add(ReadCommentString(packet, ref offset));
        if (codec == Codec.Vorbis)
        {
            Assert.Equal(1, packet[offset++]);
            Assert.Equal(packet.Length, offset);
        }
        else Assert.True(offset <= packet.Length);
        return (vendor, comments);
    }

    private static string ReadCommentString(byte[] packet, ref int offset)
    {
        uint length = ReadUInt32(packet, ref offset);
        string value = Encoding.UTF8.GetString(packet, offset, (int)length);
        offset += (int)length;
        return value;
    }

    private static byte[] ReadPicture(byte[] picture)
    {
        int offset = 0;
        Assert.Equal(3u, ReadBigEndianUInt32(picture, ref offset));
        uint mimeLength = ReadBigEndianUInt32(picture, ref offset);
        offset += (int)mimeLength;
        uint descriptionLength = ReadBigEndianUInt32(picture, ref offset);
        offset += (int)descriptionLength;
        for (int field = 0; field < 4; field++) ReadBigEndianUInt32(picture, ref offset);
        uint imageLength = ReadBigEndianUInt32(picture, ref offset);
        return picture.AsSpan(offset, (int)imageLength).ToArray();
    }

    private static byte[] BuildLargePng()
    {
        const int width = 256;
        const int height = 256;
        byte[] scanlines = new byte[height * (width * 4 + 1)];
        var random = new Random(1234);
        for (int row = 0; row < height; row++)
        {
            int offset = row * (width * 4 + 1);
            scanlines[offset] = 0;
            random.NextBytes(scanlines.AsSpan(offset + 1, width * 4));
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(scanlines);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        stream.Write(length);
        byte[] kind = Encoding.ASCII.GetBytes(type);
        stream.Write(kind);
        stream.Write(payload);
        uint crc = PngCrc(kind, payload);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
    }

    private static uint PngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = UpdatePngCrc(crc, value);
        foreach (byte value in payload) crc = UpdatePngCrc(crc, value);
        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    private static bool HasValidChecksum(byte[] page)
    {
        byte[] copy = page.ToArray();
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(22));
        copy.AsSpan(22, 4).Clear();
        return expected == OggCrc(copy);
    }

    private static void SetChecksum(byte[] page)
    {
        page.AsSpan(22, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggCrc(page));
    }

    private static uint OggCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    private static uint ReadUInt32(byte[] bytes, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
