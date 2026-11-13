using System.Buffers;
using System.Text;

namespace Vesper.Engine;

internal enum Mp3MetadataWriteResult { Written, ExistingTags, UnsupportedAudio, NoTags }

internal static class Mp3MetadataWriter
{
    private const int MaxTagBytes = 1_000_000;
    private const int MaxFrameCount = 4096;
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    internal static async Task<Mp3MetadataWriteResult> TryWriteAsync(Stream source, Stream destination, MediaMergeMetadata metadata,
        MediaThumbnail? cover, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek || !destination.CanWrite || !destination.CanSeek)
            return Mp3MetadataWriteResult.UnsupportedAudio;

        bool hasExistingTags = HasId3v2Tag(source);
        long audioOffset = 0;
        List<byte[]>? existingFrames = null;
        if (hasExistingTags && !TryReadExistingTag(source, out audioOffset, out existingFrames))
            return Mp3MetadataWriteResult.ExistingTags;

        if (!IsMpegLayer3(source, audioOffset))
            return hasExistingTags ? Mp3MetadataWriteResult.ExistingTags : Mp3MetadataWriteResult.UnsupportedAudio;

        List<byte[]> newFrames = BuildFrames(metadata, cover);
        if (newFrames.Count == 0)
            return hasExistingTags ? Mp3MetadataWriteResult.ExistingTags : Mp3MetadataWriteResult.NoTags;

        var replacedTextFrames = new HashSet<string>(newFrames.Select(GetFrameId), StringComparer.Ordinal);
        bool replaceCover = replacedTextFrames.Remove("APIC");
        var frames = new List<byte[]>((existingFrames?.Count ?? 0) + newFrames.Count);
        if (existingFrames is not null)
        {
            foreach (byte[] frame in existingFrames)
            {
                string id = GetFrameId(frame);
                if (replacedTextFrames.Contains(id)) continue;
                if (replaceCover && id == "APIC")
                {
                    if (!TryIsFrontCover(frame, out bool frontCover))
                        return Mp3MetadataWriteResult.ExistingTags;
                    if (frontCover) continue;
                }
                frames.Add(frame);
            }
        }
        frames.AddRange(newFrames);

        byte[]? tag = BuildTag(frames);
        if (tag is null)
            return hasExistingTags ? Mp3MetadataWriteResult.ExistingTags : Mp3MetadataWriteResult.NoTags;

        source.Position = audioOffset;
        destination.Position = 0;
        destination.SetLength(0);
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

    private static List<byte[]> BuildFrames(MediaMergeMetadata metadata, MediaThumbnail? cover)
    {
        var frames = new List<byte[]>();
        if (!MediaMergePlan.ValidMetadata(metadata)) return frames;

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

        return frames;
    }

    private static bool TryReadExistingTag(Stream source, out long audioOffset, out List<byte[]> frames)
    {
        audioOffset = 0;
        frames = [];
        if (source.Length < 10) return false;

        Span<byte> header = stackalloc byte[10];
        MuxIo.ReadExact(source, 0, header);
        if (!header[..3].SequenceEqual("ID3"u8) || header[3] != 4 || header[4] != 0 || header[5] != 0
            || !TryReadSynchsafe(header[6..10], out int tagLength) || tagLength > MaxTagBytes
            || tagLength > source.Length - 10)
            return false;

        byte[] body = new byte[tagLength];
        if (body.Length > 0) MuxIo.ReadExact(source, 10, body);
        int offset = 0;
        while (offset < body.Length)
        {
            if (body[offset] == 0)
            {
                for (int i = offset; i < body.Length; i++)
                    if (body[i] != 0) return false;
                break;
            }
            if (body.Length - offset < 10 || !IsFrameId(body.AsSpan(offset, 4))
                || !TryReadSynchsafe(body.AsSpan(offset + 4, 4), out int frameLength)
                || frameLength > body.Length - offset - 10)
                return false;

            frames.Add(body.AsSpan(offset, frameLength + 10).ToArray());
            if (frames.Count > MaxFrameCount) return false;
            offset += frameLength + 10;
        }

        audioOffset = 10L + tagLength;
        return true;
    }

    private static byte[]? BuildTag(IReadOnlyList<byte[]> frames)
    {
        using var frameData = new MemoryStream();
        foreach (byte[] frame in frames) frameData.Write(frame);
        if (frameData.Length == 0 || frameData.Length > MaxTagBytes) return null;

        using var tag = new MemoryStream(checked((int)frameData.Length + 10));
        tag.Write("ID3"u8);
        tag.WriteByte(4); // ID3v2.4
        tag.WriteByte(0);
        tag.WriteByte(0);
        WriteSynchsafe(tag, checked((int)frameData.Length));
        frameData.Position = 0;
        frameData.CopyTo(tag);
        return tag.ToArray();
    }

    private static void AddTextFrame(List<byte[]> frames, string id, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Contains('\0')) return;
        byte[] text = Encoding.UTF8.GetBytes(value);
        var payload = new byte[text.Length + 1];
        payload[0] = 3; // UTF-8
        text.CopyTo(payload, 1);
        frames.Add(BuildFrame(id, payload));
    }

    private static void AddPictureFrame(List<byte[]> frames, MediaThumbnail cover)
    {
        byte[] mime = Encoding.ASCII.GetBytes(cover.MediaType);
        var payload = new byte[checked(1 + mime.Length + 1 + 1 + 1 + cover.Data.Length)];
        payload[0] = 3; // UTF-8 description
        mime.CopyTo(payload, 1);
        int position = mime.Length + 2;
        payload[position++] = 3; // front cover
        payload[position++] = 0; // empty description
        cover.Data.CopyTo(payload, position);
        frames.Add(BuildFrame("APIC", payload));
    }

    private static byte[] BuildFrame(string id, byte[] payload)
    {
        using var frame = new MemoryStream(payload.Length + 10);
        frame.Write(Encoding.ASCII.GetBytes(id));
        WriteSynchsafe(frame, payload.Length);
        frame.WriteByte(0);
        frame.WriteByte(0);
        frame.Write(payload);
        return frame.ToArray();
    }

    private static bool TryIsFrontCover(byte[] frame, out bool frontCover)
    {
        frontCover = false;
        if (frame.Length < 13 || GetFrameId(frame) != "APIC" || frame[9] != 0) return false;
        ReadOnlySpan<byte> payload = frame.AsSpan(10);
        byte encoding = payload[0];
        if (encoding > 3) return false;
        int mimeLength = payload[1..].IndexOf((byte)0);
        if (mimeLength <= 0) return false;
        int pictureTypeOffset = mimeLength + 2;
        if (pictureTypeOffset >= payload.Length || payload[pictureTypeOffset] > 20) return false;
        int descriptionOffset = pictureTypeOffset + 1;
        if (!TrySkipApicDescription(payload, encoding, descriptionOffset, out int imageOffset)
            || imageOffset >= payload.Length)
            return false;

        frontCover = payload[pictureTypeOffset] == 3;
        return true;
    }

    private static bool TrySkipApicDescription(ReadOnlySpan<byte> payload, byte encoding, int offset, out int imageOffset)
    {
        imageOffset = 0;
        if (encoding is 0 or 3)
        {
            int terminator = payload[offset..].IndexOf((byte)0);
            if (terminator < 0) return false;
            imageOffset = offset + terminator + 1;
            return true;
        }

        int searchOffset = offset;
        if (encoding == 1)
        {
            if (payload.Length - offset < 2 || !((payload[offset] == 0xFF && payload[offset + 1] == 0xFE)
                || (payload[offset] == 0xFE && payload[offset + 1] == 0xFF)))
                return false;
            searchOffset += 2;
        }

        for (int i = searchOffset; i + 1 < payload.Length; i += 2)
        {
            if (payload[i] != 0 || payload[i + 1] != 0) continue;
            imageOffset = i + 2;
            return true;
        }
        return false;
    }

    private static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        if (id.Length != 4) return false;
        foreach (byte value in id)
            if ((value < 'A' || value > 'Z') && (value < '0' || value > '9')) return false;
        return true;
    }

    private static string GetFrameId(byte[] frame) => Encoding.ASCII.GetString(frame, 0, 4);

    private static bool TryReadSynchsafe(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.Length != 4) return false;
        foreach (byte b in bytes)
        {
            if ((b & 0x80) != 0) return false;
            value = (value << 7) | b;
        }
        return true;
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

    internal static bool IsMpegLayer3(Stream source, long offset = 0)
    {
        if (offset < 0 || source.Length - offset < 4) return false;
        Span<byte> header = stackalloc byte[4];
        MuxIo.ReadExact(source, offset, header);
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
        return source.Length - offset >= frameLength;
    }

    internal static bool HasId3v2Tag(Stream source)
    {
        if (source.Length < 3) return false;
        Span<byte> signature = stackalloc byte[3];
        MuxIo.ReadExact(source, 0, signature);
        return signature.SequenceEqual("ID3"u8);
    }
}
