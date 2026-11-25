using System.Buffers.Binary;

namespace Vesper.Shared;

// A bounded structural check, not a proof of sample integrity or player identity.
internal enum Mp4Layout { Unresolved, Progressive, Fragmented, Initialization, Fragment, Invalid }

internal static class Mp4TopLevelExtent
{
    internal enum BoxStatus { Known, NeedMoreBytes, Invalid }

    internal static BoxStatus ReadBox(ReadOnlySpan<byte> bytes, long remaining, out int header, out long size)
    {
        header = 8;
        size = 0;
        if (remaining < header) return BoxStatus.Invalid;
        if (bytes.Length < header) return BoxStatus.NeedMoreBytes;
        ulong length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (length == 1)
        {
            header = 16;
            if (remaining < header) return BoxStatus.Invalid;
            if (bytes.Length < header) return BoxStatus.NeedMoreBytes;
            length = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        }
        else if (length == 0) length = (ulong)remaining;
        if (length < (ulong)header || length > (ulong)remaining) return BoxStatus.Invalid;
        size = (long)length;
        return BoxStatus.Known;
    }

    internal static Mp4Layout Inspect(ReadOnlySpan<byte> prefix, long total)
    {
        if (prefix.Length < 8 || total < prefix.Length || !KnownStart(prefix[4..8])) return Mp4Layout.Unresolved;
        bool movie = false, media = false, fragment = false;
        long offset = 0;
        for (int count = 0; offset < total; count++)
        {
            if (count >= 100_000 || offset >= prefix.Length) return Mp4Layout.Unresolved;
            var bytes = prefix[(int)offset..];
            var status = ReadBox(bytes, total - offset, out int header, out long size);
            if (status == BoxStatus.Invalid) return Mp4Layout.Invalid;
            if (status != BoxStatus.Known) return Mp4Layout.Unresolved;
            var type = bytes[4..8];
            if (type.SequenceEqual("moov"u8))
            {
                if (movie) return Mp4Layout.Invalid;
                movie = true;
            }
            else if (type.SequenceEqual("mdat"u8) && size > header) media = true;
            else if (type.SequenceEqual("moof"u8)) fragment = true;
            offset += size;
        }
        if (!media) return movie && !fragment ? Mp4Layout.Initialization : Mp4Layout.Invalid;
        if (!movie) return fragment ? Mp4Layout.Fragment : Mp4Layout.Invalid;
        return fragment ? Mp4Layout.Fragmented : Mp4Layout.Progressive;
    }

    private static bool KnownStart(ReadOnlySpan<byte> type) => type.SequenceEqual("ftyp"u8) || type.SequenceEqual("styp"u8)
        || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("moof"u8)
        || type.SequenceEqual("sidx"u8) || type.SequenceEqual("wide"u8) || type.SequenceEqual("free"u8)
        || type.SequenceEqual("skip"u8);
}
