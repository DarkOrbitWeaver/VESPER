using System.Buffers.Binary;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;
using Vesper.Shared;
using Vesper.MediaResolver.Formats;

namespace Vesper.MediaResolver.Discovery;

/// <summary>
/// Facts from an MP4's bounded track table, at the start or a verified top-level tail boundary.
/// </summary>
internal sealed record DirectMediaFacts(bool HasVideo, bool HasAudio, int? Width, int? Height,
    string? VideoCodec, string? AudioCodec, double? DurationSeconds, bool Encrypted, bool CompleteFileCandidate = true, long? MaxChunkOffset = null);

internal static class Mp4HeaderFacts
{

    // Unresolved prefixes are not positive evidence. Veto only contradictory
    // structure, an empty resource, or an identifiable non-media response.
    internal static MediaError? DirectFileProblem(Uri url, ProbeResult probe, ReadOnlySpan<byte> prefix,
        out bool layoutUnverified)
    {
        var layout = probe.ContentLength is long total ? Mp4TopLevelExtent.Inspect(prefix, total) : Mp4Layout.Unresolved;
        layoutUnverified = layout == Mp4Layout.Unresolved;
        string? reason = probe.InvalidExtent ? "the server returned an invalid media extent"
            : probe.ContentLength == 0 ? "the response is empty" : layout switch
        {
            Mp4Layout.Invalid => "the MP4 box structure contradicts the HTTP resource extent",
            Mp4Layout.Initialization => "this is an MP4 initialization segment without samples",
            Mp4Layout.Fragment => "this is an MP4 media fragment without movie metadata",
            _ => LooksLikeHtml(prefix) ? "the response contains HTML, not an MP4" : null,
        };
        return reason is null ? null : MediaErrors.NoMedia($"{UrlRedactor.Redact(url)}: {reason}.", browserMayHelp: true);
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> prefix)
    {
        prefix = prefix[..Math.Min(prefix.Length, 256)];
        if (prefix.StartsWith("\uFEFF"u8)) prefix = prefix[3..];
        while (!prefix.IsEmpty && prefix[0] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') prefix = prefix[1..];
        return prefix.Length >= 14 && System.Text.Ascii.EqualsIgnoreCase(prefix[..14], "<!doctype html"u8)
            || prefix.Length >= 6 && System.Text.Ascii.EqualsIgnoreCase(prefix[..5], "<html"u8)
                && prefix[5] is (byte)'>' or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t';
    }

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
        long? maxChunk = null;

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
                if (!ReadTrack(body, out string? handler, out string? format, out int? w, out int? h, out bool protectedTrack, out long? trackChunk))
                    return null;
                encrypted |= protectedTrack;
                if (trackChunk is long chunk && (maxChunk is null || chunk > maxChunk)) maxChunk = chunk;
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
        return video || audio ? new(video, audio, width, height, videoCodec, audioCodec, duration, encrypted, CompleteFileCandidate: !fragmented, MaxChunkOffset: maxChunk) : null;
    }

    private static bool ReadTrack(ReadOnlySpan<byte> trak, out string? handler, out string? format,
        out int? width, out int? height, out bool encrypted, out long? maxChunkOffset)
    {
        handler = format = null;
        width = height = null;
        encrypted = false;
        maxChunkOffset = null;
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
        maxChunkOffset = MaxChunkOffset(stbl);
        return true;
    }

    // Chunk offsets are absolute file positions. An offset at or beyond the
    // declared resource end is positive evidence that this is not a whole file.
    internal static bool SamplesOutsideExtent(DirectMediaFacts? facts, long? extent) =>
        facts is { MaxChunkOffset: long offset } && extent is long length && offset >= length;

    private static long? MaxChunkOffset(ReadOnlySpan<byte> stbl)
    {
        bool wide = false;
        if (!TryFind(stbl, "stco", out var table))
        {
            if (!TryFind(stbl, "co64", out table)) return null;
            wide = true;
        }
        if (table.Length < 8) return null;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(table[4..]);
        int width = wide ? 8 : 4;
        if (count == 0 || count > (table.Length - 8) / width) return null;
        long max = 0;
        for (int i = 0; i < (int)count; i++)
        {
            var entry = table[(8 + i * width)..];
            long offset = wide ? (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(entry), (ulong)long.MaxValue)
                : BinaryPrimitives.ReadUInt32BigEndian(entry);
            max = Math.Max(max, offset);
        }
        return max;
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
