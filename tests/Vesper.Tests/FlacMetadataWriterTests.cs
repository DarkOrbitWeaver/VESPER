using System.Buffers.Binary;
using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class FlacMetadataWriterTests
{
    private static byte[] AudioTail => [0xFF, 0xF8, 0x99, 0x18, 0, 1, 2, 3, 4, 5, 6, 7];
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task UpdatesSourceTagsAndFrontCoverWhilePreservingOtherBlocksAndAudio()
    {
        byte[] originalComment = Comments("Test encoder", "TITLE=Old Song", "GENRE=Jazz", "COMMENT=keep this");
        byte[] application = [0x56, 0x45, 0x53, 0x50, 0x45, 0x52, 0x01];
        byte[] frontCover = Picture(3, [1, 2, 3]);
        byte[] duplicateFrontCover = Picture(3, [7, 8, 9]);
        byte[] backCover = Picture(4, [4, 5, 6]);
        byte[] original = Flac((2, application), (4, originalComment), (6, frontCover), (6, duplicateFrontCover), (6, backCover));
        byte[] audio = AudioTail;
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));
        var metadata = new MediaMergeMetadata
        {
            Title = "New Song", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output, metadata, cover, default);

        Assert.Equal(FlacMetadataWriteResult.Written, result);
        ParsedFlac parsed = Parse(output.ToArray());
        Assert.Equal(audio, parsed.Audio);
        Assert.Equal(application, parsed.Blocks.Single(block => block.Type == 2).Data);
        string[] comments = ParseComments(parsed.Blocks.Single(block => block.Type == 4).Data, out string vendor);
        Assert.Equal("Test encoder", vendor);
        Assert.Equal("GENRE=Jazz", comments.Single(value => value.StartsWith("GENRE=", StringComparison.Ordinal)));
        Assert.Equal("COMMENT=keep this", comments.Single(value => value.StartsWith("COMMENT=", StringComparison.Ordinal)));
        Assert.Equal("TITLE=New Song", comments.Single(value => value.StartsWith("TITLE=", StringComparison.Ordinal)));
        Assert.DoesNotContain(comments, value => value == "TITLE=Old Song");
        Assert.Equal("Fixture Band", comments.Single(value => value.StartsWith("ARTIST=", StringComparison.Ordinal))[7..]);
        Assert.Contains("ALBUM=Fixture Album", comments);
        Assert.Contains("ALBUMARTIST=Fixture Band", comments);
        Assert.Contains("TRACKNUMBER=2", comments);
        Assert.Contains("DISCNUMBER=1", comments);
        Assert.Contains("DATE=2024-03-12", comments);

        var pictures = parsed.Blocks.Where(block => block.Type == 6).ToArray();
        Assert.Equal(2, pictures.Length);
        ParsedPicture newCover = ParsePicture(pictures[0].Data);
        Assert.Equal("image/png", newCover.Mime);
        Assert.Equal((1u, 1u, 16u), (newCover.Width, newCover.Height, newCover.Depth));
        Assert.Equal(TinyPng(), newCover.Image);
        Assert.Equal((uint)4, ParsePicture(pictures[1].Data).Type);
        AssertLastBlockIsUnique(parsed.Blocks);
    }

    [Fact]
    public async Task AddsCommentAndPictureBlocksWhenMissing()
    {
        byte[] original = Flac();
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One", Artist = "Fixture Band" }, cover, default);

        Assert.Equal(FlacMetadataWriteResult.Written, result);
        ParsedFlac parsed = Parse(output.ToArray());
        Assert.Equal(AudioTail, parsed.Audio);
        Assert.Equal(3, parsed.Blocks.Count);
        string[] comments = ParseComments(parsed.Blocks.Single(block => block.Type == 4).Data, out string vendor);
        Assert.Equal("VESPER", vendor);
        Assert.Contains("TITLE=Song One", comments);
        Assert.Contains("ARTIST=Fixture Band", comments);
        Assert.Equal(TinyPng(), ParsePicture(parsed.Blocks.Single(block => block.Type == 6).Data).Image);
        AssertLastBlockIsUnique(parsed.Blocks);
    }

    [Fact]
    public async Task RealFlacFixtureKeepsEncodedFramesAndCanBeInspectedByExternalPlayers()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "quiet-track.flac"));
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output, metadata, cover, default);

        Assert.Equal(FlacMetadataWriteResult.Written, result);
        ParsedFlac before = Parse(original);
        byte[] tagged = output.ToArray();
        ParsedFlac after = Parse(tagged);
        Assert.Equal(before.Audio, after.Audio);
        string[] comments = ParseComments(after.Blocks.Single(block => block.Type == 4).Data, out _);
        Assert.Contains("TITLE=Song One", comments);
        Assert.Contains("ARTIST=Fixture Band", comments);
        Assert.Contains("ALBUM=Fixture Album", comments);
        Assert.Equal(TinyPng(), ParsePicture(after.Blocks.Single(block => block.Type == 6).Data).Image);

        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_FLAC");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, tagged);
        }
    }

    [Fact]
    public async Task KeepsExistingCoverAndTagsWhenNoSourceMetadataIsAvailable()
    {
        byte[] original = Flac((4, Comments("Encoder", "TITLE=Existing")), (6, Picture(3, [9, 8, 7])));
        using var source = new MemoryStream(original);
        using var output = new MemoryStream([0xAA, 0xBB]);

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output, new MediaMergeMetadata(), null, default);

        Assert.Equal(FlacMetadataWriteResult.NoChanges, result);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, output.ToArray());
        Assert.Equal(original, source.ToArray());
    }

    [Fact]
    public async Task MalformedFlacOrVorbisCommentsLeaveDestinationUntouched()
    {
        byte[] malformedComment = [1, 0, 0, 0, (byte)'x', 1, 0, 0, 0, 100, 0, 0, 0, (byte)'T'];
        byte[] malformed = Flac((4, malformedComment));
        using var source = new MemoryStream(malformed);
        using var output = new MemoryStream([0xAA, 0xBB]);

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One" }, null, default);

        Assert.Equal(FlacMetadataWriteResult.UnsupportedAudio, result);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, output.ToArray());
        Assert.Equal(malformed, source.ToArray());
    }

    [Fact]
    public async Task RejectsNonNativeFlacWithoutTruncatingDestination()
    {
        byte[] ogg = "OggS-not-native-flac"u8.ToArray();
        using var source = new MemoryStream(ogg);
        using var output = new MemoryStream([0xAA, 0xBB]);

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One" }, null, default);

        Assert.Equal(FlacMetadataWriteResult.UnsupportedAudio, result);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, output.ToArray());
        Assert.Equal(ogg, source.ToArray());
    }

    [Fact]
    public async Task RejectsReservedMetadataBlockTypeWithoutTruncatingDestination()
    {
        byte[] reserved = Flac((127, [1]));
        using var source = new MemoryStream(reserved);
        using var output = new MemoryStream([0xAA, 0xBB]);

        FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One" }, null, default);

        Assert.Equal(FlacMetadataWriteResult.UnsupportedAudio, result);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, output.ToArray());
        Assert.Equal(reserved, source.ToArray());
    }

    private static byte[] Flac(params (byte Type, byte[] Data)[] additionalBlocks)
    {
        using var output = new MemoryStream();
        output.Write("fLaC"u8);
        var blocks = new List<(byte Type, byte[] Data)> { (0, new byte[34]) };
        blocks.AddRange(additionalBlocks);
        for (int index = 0; index < blocks.Count; index++)
        {
            (byte type, byte[] data) = blocks[index];
            output.WriteByte((byte)(type | (index == blocks.Count - 1 ? 0x80 : 0)));
            output.WriteByte((byte)(data.Length >> 16));
            output.WriteByte((byte)(data.Length >> 8));
            output.WriteByte((byte)data.Length);
            output.Write(data);
        }
        output.Write(AudioTail);
        return output.ToArray();
    }

    private static byte[] Comments(string vendor, params string[] values)
    {
        using var output = new MemoryStream();
        WriteLittleEndian(output, (uint)Encoding.UTF8.GetByteCount(vendor));
        output.Write(Encoding.UTF8.GetBytes(vendor));
        WriteLittleEndian(output, (uint)values.Length);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteLittleEndian(output, (uint)bytes.Length);
            output.Write(bytes);
        }
        return output.ToArray();
    }

    private static byte[] Picture(uint type, byte[] image)
    {
        using var output = new MemoryStream();
        WriteBigEndian(output, type);
        WriteBigEndian(output, 9);
        output.Write("image/png"u8);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, (uint)image.Length);
        output.Write(image);
        return output.ToArray();
    }

    private static string[] ParseComments(byte[] data, out string vendor)
    {
        int position = 0;
        uint vendorLength = ReadLittleEndian(data, ref position);
        vendor = Encoding.UTF8.GetString(data, position, (int)vendorLength);
        position += (int)vendorLength;
        uint count = ReadLittleEndian(data, ref position);
        var values = new string[count];
        for (int index = 0; index < values.Length; index++)
        {
            uint length = ReadLittleEndian(data, ref position);
            values[index] = Encoding.UTF8.GetString(data, position, (int)length);
            position += (int)length;
        }
        Assert.Equal(data.Length, position);
        return values;
    }

    private sealed record ParsedPicture(uint Type, string Mime, uint Width, uint Height, uint Depth, byte[] Image);

    private static ParsedPicture ParsePicture(byte[] data)
    {
        int position = 0;
        uint type = ReadBigEndian(data, ref position);
        uint mimeLength = ReadBigEndian(data, ref position);
        string mime = Encoding.ASCII.GetString(data, position, (int)mimeLength);
        position += (int)mimeLength;
        uint descriptionLength = ReadBigEndian(data, ref position);
        position += (int)descriptionLength;
        uint width = ReadBigEndian(data, ref position);
        uint height = ReadBigEndian(data, ref position);
        uint depth = ReadBigEndian(data, ref position);
        _ = ReadBigEndian(data, ref position); // indexed colors
        uint imageLength = ReadBigEndian(data, ref position);
        Assert.Equal(data.Length, position + imageLength);
        return new ParsedPicture(type, mime, width, height, depth, data.AsSpan(position, (int)imageLength).ToArray());
    }

    private sealed record MetadataBlock(byte Type, bool IsLast, byte[] Data);
    private sealed record ParsedFlac(List<MetadataBlock> Blocks, byte[] Audio);

    private static ParsedFlac Parse(byte[] data)
    {
        Assert.Equal("fLaC", Encoding.ASCII.GetString(data, 0, 4));
        int position = 4;
        var blocks = new List<MetadataBlock>();
        bool last;
        do
        {
            byte flags = data[position++];
            byte type = (byte)(flags & 0x7F);
            last = (flags & 0x80) != 0;
            int length = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
            position += 3;
            blocks.Add(new MetadataBlock(type, last, data.AsSpan(position, length).ToArray()));
            position += length;
        } while (!last);
        return new ParsedFlac(blocks, data[position..]);
    }

    private static void AssertLastBlockIsUnique(List<MetadataBlock> blocks)
    {
        Assert.True(blocks[^1].IsLast);
        Assert.Equal(1, blocks.Count(block => block.IsLast));
    }

    private static uint ReadLittleEndian(byte[] data, ref int position)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        position += 4;
        return value;
    }

    private static uint ReadBigEndian(byte[] data, ref int position)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
        position += 4;
        return value;
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
}
