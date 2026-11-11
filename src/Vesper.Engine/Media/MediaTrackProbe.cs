using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

/// <summary>
/// Bounded, seek-based track-presence guard for plain HTTP media handoffs. Prevents an observed adaptive video URL
/// being published as an audio/video download just because its MIME type is video/mp4 or video/webm.
/// This checks container layout/track declarations, not full codec decodability. No media payload is buffered.
/// </summary>
internal static class MediaTrackProbe
{
    private readonly record struct IsoBox(string Type, int Payload, int End);
    internal static void RequireAudio(PartialFile file, string format, CancellationToken ct)
    {
        bool audio = format == "webm" ? WebmHasAudio(file, ct) : Mp4HasAudio(file, ct);
        if (!audio) throw new MediaContainerException("The downloaded representation is video-only or has no supported audio track. No silent output was published; choose another format.");
    }

    private static bool Mp4HasAudio(PartialFile file, CancellationToken ct)
    {
        long offset = 0;
        byte[]? moov = null;
        bool media = false;
        for (int count = 0; offset < file.Length; count++)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= 100_000 || file.Length - offset < 8) throw Bad("Invalid MP4 top-level structure.");
            var header = Read(file, offset, (int)Math.Min(16, file.Length - offset));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header); int prefix = 8;
            if (size == 1)
            {
                if (header.Length < 16) throw Bad("Truncated extended MP4 size.");
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); prefix = 16;
            }
            else if (size == 0) size = (ulong)(file.Length - offset);
            if (size < (ulong)prefix || size > (ulong)(file.Length - offset)) throw Bad("An MP4 box exceeds the downloaded file.");
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "moov")
            {
                if (moov is not null || size > 8 * 1024 * 1024) throw Bad("Duplicate or oversized MP4 metadata requires another processing path.");
                moov = Read(file, offset + prefix, (int)size - prefix);
            }
            if (type == "mdat" && size > (ulong)prefix) media = true;
            if (type == "pssh") throw Bad("Protected MP4 initialization is unsupported.");
            offset += (long)size;
        }
        if (moov is null || !media) throw Bad("The MP4 file has no movie metadata or media payload.");
        var movie = IsoBoxes(moov, 0, moov.Length);
        if (movie.Any(b => b.Type == "pssh")) throw Bad("Protected MP4 movie metadata is unsupported.");
        bool audio = false;
        foreach (var trak in movie.Where(b => b.Type == "trak"))
        {
            var mdia = One(IsoBoxes(moov, trak.Payload, trak.End), "mdia");
            var md = IsoBoxes(moov, mdia.Payload, mdia.End);
            var handler = One(md, "hdlr");
            if (handler.End - handler.Payload < 12) throw Bad("Truncated MP4 track handler.");
            string kind = Encoding.ASCII.GetString(moov, handler.Payload + 8, 4);
            if (kind is not ("soun" or "vide")) continue;
            var minf = One(md, "minf");
            var stbl = One(IsoBoxes(moov, minf.Payload, minf.End), "stbl");
            var stsd = One(IsoBoxes(moov, stbl.Payload, stbl.End), "stsd");
            if (stsd.End - stsd.Payload < 8) throw Bad("Truncated MP4 sample descriptions.");
            var entries = IsoBoxes(moov, stsd.Payload + 8, stsd.End);
            if (entries.Count == 0 || entries.Count > 16 || BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stsd.Payload + 4)) != entries.Count)
                throw Bad("Invalid MP4 sample-description count.");
            if (entries.Any(b => b.Type is "enca" or "encv")) throw Bad("Encrypted MP4 samples are unsupported.");
            if (kind == "soun") audio = true;
        }
        return audio;
    }

    private static bool WebmHasAudio(PartialFile file, CancellationToken ct)
    {
        long position = 0;
        var ebml = FileElement(file, ref position, file.Length);
        if (ebml.Id != 0x1a45dfa3) throw Bad("Missing EBML header.");
        position = ebml.End;
        var segment = FileElement(file, ref position, file.Length);
        if (segment.Id != 0x18538067) throw Bad("Missing WebM segment.");
        for (int count = 0; position < segment.End && count < 100_000; count++)
        {
            ct.ThrowIfCancellationRequested();
            var element = FileElement(file, ref position, segment.End);
            if (element.Id == 0x1654ae6b)
            {
                if (element.End - position > 4 * 1024 * 1024) throw Bad("Oversized WebM track metadata.");
                byte[] tracks = Read(file, position, (int)(element.End - position));
                int p = 0; bool audio = false;
                while (p < tracks.Length)
                {
                    var track = MemoryElement(tracks, ref p, tracks.Length);
                    if (track.Id == 0xae)
                    {
                        int q = p; ulong? kind = null; string? codec = null;
                        while (q < track.End)
                        {
                            var property = MemoryElement(tracks, ref q, (int)track.End);
                            if (property.Id == 0x6d80) throw Bad("Encoded/encrypted WebM tracks are unsupported.");
                            if (property.Id == 0x83)
                            {
                                if (property.End - q is < 1 or > 8) throw Bad("Invalid WebM track type.");
                                ulong value = 0; for (int i = q; i < property.End; i++) value = value * 256 + tracks[i]; kind = value;
                            }
                            if (property.Id == 0x86 && property.End - q <= 128) codec = Encoding.ASCII.GetString(tracks, q, (int)property.End - q);
                            q = (int)property.End;
                        }
                        if (kind == 2 && codec is "A_OPUS" or "A_VORBIS" or "A_AAC" or "A_MPEG/L3" or "A_FLAC") audio = true;
                    }
                    p = (int)track.End;
                }
                return audio;
            }
            position = element.End;
        }
        throw Bad("No bounded WebM track table was found.");
    }

    private readonly record struct Element(ulong Id, long End);
    private static Element FileElement(PartialFile file, ref long position, long parentEnd)
    {
        if (parentEnd - position < 2) throw Bad("Truncated EBML element.");
        var header = Read(file, position, (int)Math.Min(16, parentEnd - position));
        int p = 0; ulong id = Vint(header, ref p, true, out _); ulong length = Vint(header, ref p, false, out bool unknown);
        position += p;
        if (!unknown && length > (ulong)(parentEnd - position)) throw Bad("EBML element exceeds its container.");
        return new(id, unknown ? parentEnd : position + (long)length);
    }
    private static Element MemoryElement(byte[] bytes, ref int p, int end)
    {
        ulong id = Vint(bytes.AsSpan(0, end), ref p, true, out _), length = Vint(bytes.AsSpan(0, end), ref p, false, out bool unknown);
        if (unknown || length > (ulong)(end - p)) throw Bad("Unsupported or truncated EBML metadata.");
        return new(id, p + (long)length);
    }
    private static ulong Vint(ReadOnlySpan<byte> bytes, ref int p, bool identifier, out bool unknown)
    {
        if (p >= bytes.Length) throw Bad("Truncated EBML integer.");
        byte first = bytes[p++]; int length = 1; byte marker = 0x80;
        while (marker != 0 && (first & marker) == 0) { marker >>= 1; length++; }
        if (marker == 0 || length > (identifier ? 4 : 8) || bytes.Length - p < length - 1) throw Bad("Invalid EBML integer.");
        ulong value = identifier ? first : (ulong)(first & (marker - 1));
        for (int i = 1; i < length; i++) value = value * 256 + bytes[p++];
        unknown = !identifier && value == ((1UL << (length * 7)) - 1);
        return value;
    }
    private static List<IsoBox> IsoBoxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<IsoBox>();
        while (start < end)
        {
            if (end - start < 8 || boxes.Count >= 100_000) throw Bad("Truncated MP4 metadata.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));
            if (size < 8 || size > end - start) throw Bad("Invalid MP4 metadata box size.");
            boxes.Add(new(Encoding.ASCII.GetString(bytes, start + 4, 4), start + 8, start + (int)size)); start += (int)size;
        }
        return boxes;
    }
    private static IsoBox One(List<IsoBox> boxes, string type)
    {
        var matches = boxes.Where(b => b.Type == type).ToArray();
        return matches.Length == 1 ? matches[0] : throw Bad($"Missing or duplicate MP4 {type} box.");
    }
    private static byte[] Read(PartialFile file, long offset, int size)
    {
        var bytes = new byte[size]; int done = 0;
        while (done < size)
        {
            int count = file.Read(bytes.AsSpan(done), offset + done);
            if (count <= 0) throw Bad("The file ended during media track validation.");
            done += count;
        }
        return bytes;
    }
    private static MediaContainerException Bad(string text) => new(text);
}
