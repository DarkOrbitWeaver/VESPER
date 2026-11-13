using System.Buffers;
using System.Text;

namespace Vesper.Engine;

internal enum Mp3MetadataWriteResult { Written, ExistingTags, UnsupportedAudio, NoTags }

internal static class Mp3MetadataWriter
{
    private const int MaxTagBytes = 1_000_000;
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    internal static async Task<Mp3MetadataWriteResult> TryWriteAsync(Stream source, Stream destination, MediaMergeMetadata metadata,
        MediaThumbnail? cover, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek || !destination.CanWrite || !destination.CanSeek)
            return Mp3MetadataWriteResult.UnsupportedAudio;
        if (HasId3v2Tag(source)) return Mp3MetadataWriteResult.ExistingTags;
        if (!IsMpegLayer3(source)) return Mp3MetadataWriteResult.UnsupportedAudio;
        byte[]? tag = BuildTag(metadata, cover);
        if (tag is null) return Mp3MetadataWriteResult.NoTags;

        source.Position = 0;
        destination.Position = 0;
        await destination.WriteAsync(tag, ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Mp3MetadataWriteResult.Written;
    }

    private static byte[]? BuildTag(MediaMergeMetadata metadata, MediaThumbnail? cover)
    {
        if (!MediaMergePlan.ValidMetadata(metadata)) return null;
        var frames = new MemoryStream();
        AddTextFrame(frames, "TIT2", metadata.Title, 512);
        AddTextFrame(frames, "TPE1", metadata.Artist, 256);
        AddTextFrame(frames, "TALB", metadata.Album, 512);
        AddTextFrame(frames, "TPE2", metadata.AlbumArtist, 256);
        AddTextFrame(frames, "TRCK", metadata.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture), 4);
        AddTextFrame(frames, "TPOS", metadata.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture), 4);
        AddTextFrame(frames, "TDRC", metadata.ReleaseDate, 10);

        if (cover is not null && MediaThumbnail.TryCreate(cover.Data) is { } validCover
            && validCover.MediaType == cover.MediaType)
            AddPictureFrame(frames, validCover);

        if (frames.Length == 0 || frames.Length > MaxTagBytes) return null;
        var tag = new MemoryStream((int)frames.Length + 10);
        tag.Write("ID3"u8);
        tag.WriteByte(4); // ID3v2.4
        tag.WriteByte(0);
        tag.WriteByte(0);
        WriteSynchsafe(tag, checked((int)frames.Length));
        frames.Position = 0;
        frames.CopyTo(tag);
        return tag.ToArray();
    }

    private static void AddTextFrame(Stream frames, string id, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Contains('\0')) return;
        byte[] text = Encoding.UTF8.GetBytes(value);
        var payload = new byte[text.Length + 1];
        payload[0] = 3; // UTF-8
        text.CopyTo(payload, 1);
        WriteFrame(frames, id, payload);
    }

    private static void AddPictureFrame(Stream frames, MediaThumbnail cover)
    {
        byte[] mime = Encoding.ASCII.GetBytes(cover.MediaType);
        var payload = new byte[checked(1 + mime.Length + 1 + 1 + 1 + cover.Data.Length)];
        payload[0] = 3; // UTF-8 description
        mime.CopyTo(payload, 1);
        int position = mime.Length + 2;
        payload[position++] = 3; // front cover
        payload[position++] = 0; // empty description
        cover.Data.CopyTo(payload, position);
        WriteFrame(frames, "APIC", payload);
    }

    private static void WriteFrame(Stream output, string id, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        WriteSynchsafe(output, payload.Length);
        output.WriteByte(0);
        output.WriteByte(0);
        output.Write(payload);
    }

    private static void WriteSynchsafe(Stream output, int value)
    {
        if (value is < 0 or > 0x0FFFFFFF) throw new ArgumentOutOfRangeException(nameof(value));
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)((value >> 21) & 0x7F);
        bytes[1] = (byte)((value >> 14) & 0x7F);
        bytes[2] = (byte)((value >> 7) & 0x7F);
        bytes[3] = (byte)(value & 0x7F);
        output.Write(bytes);
    }

    internal static bool IsMpegLayer3(Stream source)
    {
        if (source.Length < 4) return false;
        Span<byte> header = stackalloc byte[4];
        MuxIo.ReadExact(source, 0, header);
        if (header[0] != 0xFF || (header[1] & 0xE0) != 0xE0) return false;
        int version = (header[1] >> 3) & 3;
        int layer = (header[1] >> 1) & 3;
        int bitrateIndex = (header[2] >> 4) & 15;
        int sampleRateIndex = (header[2] >> 2) & 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3) return false;

        int sampleRate = sampleRateIndex switch { 0 => 44_100, 1 => 48_000, _ => 32_000 };
        if (version == 2) sampleRate /= 2;
        else if (version == 0) sampleRate /= 4;
        int bitrate = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex] * 1000;
        int frameLength = (version == 3 ? 144 : 72) * bitrate / sampleRate + ((header[2] >> 1) & 1);
        return source.Length >= frameLength;
    }

    internal static bool HasId3v2Tag(Stream source)
    {
        if (source.Length < 3) return false;
        Span<byte> signature = stackalloc byte[3];
        MuxIo.ReadExact(source, 0, signature);
        return signature.SequenceEqual("ID3"u8);
    }
}
