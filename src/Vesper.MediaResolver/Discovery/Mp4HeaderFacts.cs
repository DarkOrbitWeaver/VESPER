using System.Buffers.Binary;
using Vesper.MediaResolver.Formats;

namespace Vesper.MediaResolver.Discovery;

/// <summary>
/// Facts from an MP4's bounded track table, at the start or a verified top-level tail boundary.
/// </summary>
internal sealed record DirectMediaFacts(bool HasVideo, bool HasAudio, int? Width, int? Height,
    string? VideoCodec, string? AudioCodec, double? DurationSeconds, bool Encrypted, bool CompleteFileCandidate = true);

internal static class Mp4HeaderFacts
{

    // Follow declared top-level sizes, never search sample bytes for a fake moov signature.
    internal static long? TailOffset(ReadOnlySpan<byte> prefix, long total, int budget)
    {
        if (prefix.Length < 8 || Name(prefix.Slice(4, 4)) != "ftyp") return null;
        long offset = 0;
        while (offset <= prefix.Length - 8)
        {
            var box = prefix[(int)offset..];
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(box);
            int header = 8;
            if (length == 1)
            {
                if (box.Length < 16) return null;
                length = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
                header = 16;
            }
            if (length < (ulong)header || length > (ulong)(total - offset)) return null;
            string type = Name(box.Slice(4, 4));
            long end = offset + (long)length;
            if (end > prefix.Length)
                return type == "mdat" && end < total && total - end <= budget ? end : null;
            if (type is "moov" or "moof" or "styp") return null;
            offset = end;
        }
        return null;
    }

    internal static DirectMediaFacts? ReadTail(ReadOnlySpan<byte> tail)
    {
        while (TryBox(tail, out string type, out _, out int size))
        {
            if (type == "moov") return Read(tail[..size]);
            if (type is not ("free" or "skip" or "wide")) return null;
            tail = tail[size..];
        }
        return null;
    }

    public static DirectMediaFacts? Read(ReadOnlySpan<byte> data)
    {
        if (!TryBox(data, out string first, out _, out _) || first is not ("ftyp" or "styp" or "moov")) return null;
        if (!TryFind(data, "moov", out ReadOnlySpan<byte> moov)) return null;

        bool video = false, audio = false, encrypted = false, fragmented = false;
        int? width = null, height = null;
        string? videoCodec = null, audioCodec = null;
        double? duration = null;

        ReadOnlySpan<byte> rest = moov;
        while (!rest.IsEmpty)
        {
            if (!TryBox(rest, out string type, out ReadOnlySpan<byte> body, out int size)) return null;
            if (type == "mvhd" && body.Length >= 20)
            {
                bool wide = body[0] == 1;
                if (wide && body.Length >= 32)
                {
                    uint scale = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
                    ulong ticks = BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
                    if (scale > 0 && ticks is > 0 and < ulong.MaxValue) duration = ticks / (double)scale;
                }
                else if (body[0] == 0)
                {
                    uint scale = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
                    uint ticks = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                    if (scale > 0 && ticks is > 0 and < uint.MaxValue) duration = ticks / (double)scale;
                }
            }
            else if (type == "mvex") fragmented = true;
            else if (type == "trak")
            {
                if (!ReadTrack(body, out string? handler, out string? format, out int? w, out int? h, out bool protectedTrack))
                    return null;
                encrypted |= protectedTrack;
                if (handler == "vide" && !video)
                {
                    video = true;
                    width = w;
                    height = h;
                    videoCodec = CodecInfo.NormalizeVideo(format);
                }
                else if (handler == "soun" && !audio)
                {
                    audio = true;
                    audioCodec = CodecInfo.NormalizeAudio(format);
                }
            }
            rest = rest[size..];
        }
        if (duration is > 31_536_000) duration = null;
        return video || audio ? new(video, audio, width, height, videoCodec, audioCodec, duration, encrypted, CompleteFileCandidate: !fragmented) : null;
    }

    private static bool ReadTrack(ReadOnlySpan<byte> trak, out string? handler, out string? format,
        out int? width, out int? height, out bool encrypted)
    {
        handler = format = null;
        width = height = null;
        encrypted = false;
        if (TryFind(trak, "tkhd", out ReadOnlySpan<byte> tkhd) && tkhd.Length >= 8)
        {
            // Width and height close the box as 16.16 fixed-point numbers.
            uint w = BinaryPrimitives.ReadUInt32BigEndian(tkhd[^8..]) >> 16;
            uint h = BinaryPrimitives.ReadUInt32BigEndian(tkhd[^4..]) >> 16;
            if (w is > 0 and <= 16384 && h is > 0 and <= 16384) (width, height) = ((int)w, (int)h);
        }
        if (!TryFind(trak, "mdia", out ReadOnlySpan<byte> mdia)
            || !TryFind(mdia, "hdlr", out ReadOnlySpan<byte> hdlr) || hdlr.Length < 12)
            return false;
        handler = Name(hdlr.Slice(8, 4));
        if (handler is not ("vide" or "soun")) return true;
        if (!TryFind(mdia, "minf", out ReadOnlySpan<byte> minf) || !TryFind(minf, "stbl", out ReadOnlySpan<byte> stbl)
            || !TryFind(stbl, "stsd", out ReadOnlySpan<byte> stsd) || stsd.Length < 16)
            return false;
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(stsd[4..]);
        if (entries == 0) return false;
        ReadOnlySpan<byte> descriptions = stsd[8..];
        for (uint i = 0; i < entries; i++)
        {
            if (!TryBox(descriptions, out string entry, out _, out int size)) return false;
            format ??= entry;
            encrypted |= entry is "encv" or "enca";
            descriptions = descriptions[size..];
        }
        return true;
    }

    private static bool TryFind(ReadOnlySpan<byte> boxes, string wanted, out ReadOnlySpan<byte> body)
    {
        while (TryBox(boxes, out string type, out body, out int size))
        {
            if (type == wanted) return true;
            boxes = boxes[size..];
        }
        body = default;
        return false;
    }

    // A box counts only when all of it is inside the bytes read. A cut-off box ends the walk.
    private static bool TryBox(ReadOnlySpan<byte> data, out string type, out ReadOnlySpan<byte> body, out int size)
    {
        type = "";
        body = default;
        size = 0;
        if (data.Length < 8) return false;
        ulong length = BinaryPrimitives.ReadUInt32BigEndian(data);
        int header = 8;
        if (length == 1)
        {
            if (data.Length < 16) return false;
            length = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
            header = 16;
        }
        if (length < (ulong)header || length > (ulong)data.Length) return false;
        type = Name(data.Slice(4, 4));
        size = (int)length;
        body = data.Slice(header, size - header);
        return true;
    }

    private static string Name(ReadOnlySpan<byte> four) => string.Create(4, four.ToArray(), static (chars, bytes) =>
    {
        for (int i = 0; i < 4; i++) chars[i] = bytes[i] is >= 0x20 and < 0x7f ? (char)bytes[i] : '?';
    });
}
