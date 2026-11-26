using System.Buffers.Binary;
using System.Text;

namespace Vesper.MediaResolver.Discovery;

// Reads only complete, bounded EBML metadata from the already fetched prefix.
// A track declaration is not proof of a full file; silent standalone delivery
// additionally needs finite duration and an exact declared Segment extent.
internal static class WebmHeaderFacts
{
    public static DirectMediaFacts? Read(ReadOnlySpan<byte> data, long? length)
    {
        int p = 0;
        if (!Header(data, ref p, out ulong id, out ulong size) || id != 0x1a45dfa3
            || size > 4096 || size > (ulong)(data.Length - p)) return null;
        var ebml = data.Slice(p, (int)size);
        int q = 0;
        bool webm = false;
        while (q < ebml.Length)
        {
            if (!Element(ebml, ref q, out id, out var value)) return null;
            if (id == 0x4282) webm = value.SequenceEqual("webm"u8);
        }
        if (!webm) return null;
        p += (int)size;
        if (!Header(data, ref p, out id, out size) || id != 0x18538067) return null;
        bool finite = length is > 0 && size != ulong.MaxValue && size == (ulong)(length.Value - p);
        int end = size <= (ulong)(data.Length - p) ? p + (int)size : data.Length;
        bool audio = false, video = false, encrypted = false, tracks = false;
        int? width = null, height = null;
        string? vc = null, ac = null;
        double? duration = null;
        ulong scale = 1_000_000;
        double ticks = 0;
        for (int count = 0; p < end && count < 4096; count++)
        {
            if (!Element(data[..end], ref p, out id, out var body)) break;
            if (id == 0x1549a966)
            {
                int i = 0;
                while (i < body.Length)
                {
                    if (!Element(body, ref i, out var tag, out var val)) return null;
                    if (tag == 0x2ad7b1) scale = UInt(val);
                    if (tag == 0x4489) ticks = val.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(val)
                        : val.Length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(val) : 0;
                }
                double seconds = ticks * scale / 1_000_000_000d;
                if (double.IsFinite(seconds) && seconds is > 0 and <= 31_536_000) duration = seconds;
            }
            else if (id == 0x1654ae6b)
            {
                if (tracks) return null;
                tracks = true;
                int i = 0;
                while (i < body.Length)
                {
                    if (!Element(body, ref i, out var tag, out var entry)) return null;
                    if (tag != 0xae) continue;
                    int j = 0;
                    ulong kind = 0;
                    string? codec = null;
                    int? w = null, h = null;
                    while (j < entry.Length)
                    {
                        if (!Element(entry, ref j, out var field, out var val)) return null;
                        if (field == 0x83) kind = UInt(val);
                        else if (field == 0x86 && val.Length <= 128) codec = Encoding.ASCII.GetString(val);
                        else if (field == 0x6d80) encrypted = true;
                        else if (field == 0xe0)
                        {
                            int k = 0;
                            while (k < val.Length)
                            {
                                if (!Element(val, ref k, out var dim, out var number)) return null;
                                ulong n = UInt(number);
                                if (n is 0 or > 65535) continue;
                                if (dim == 0xb0) w = (int)n;
                                if (dim == 0xba) h = (int)n;
                            }
                        }
                    }
                    if (kind == 1)
                    {
                        if (video) return null;
                        video = true; width = w; height = h;
                        vc = codec switch { "V_VP8" => "vp8", "V_VP9" => "vp9", "V_AV1" => "av1", _ => null };
                    }
                    else if (kind == 2)
                    {
                        if (audio) return null;
                        audio = true;
                        ac = codec switch { "A_OPUS" => "opus", "A_VORBIS" => "vorbis", _ => null };
                    }
                    else if (kind == 0) return null;
                }
            }
            if (tracks && duration is not null) break;
        }
        bool complete = finite && duration is not null && (!video || vc is not null) && (!audio || ac is not null);
        return tracks && (video || audio) ? new(video, audio, width, height, vc, ac, duration, encrypted, complete) : null;
    }

    private static ulong UInt(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 8) return 0;
        ulong value = 0;
        foreach (byte b in data) value = (value << 8) | b;
        return value;
    }

    private static bool Element(ReadOnlySpan<byte> data, ref int p, out ulong id, out ReadOnlySpan<byte> body)
    {
        body = default;
        if (!Header(data, ref p, out id, out ulong size) || size > (ulong)(data.Length - p)) return false;
        body = data.Slice(p, (int)size);
        p += (int)size;
        return true;
    }

    private static bool Header(ReadOnlySpan<byte> data, ref int p, out ulong id, out ulong size)
    {
        size = 0;
        return Vint(data, ref p, true, out id) && Vint(data, ref p, false, out size);
    }

    private static bool Vint(ReadOnlySpan<byte> data, ref int p, bool identifier, out ulong value)
    {
        value = 0;
        if (p >= data.Length) return false;
        int first = data[p++], marker = 128, count = 1;
        while (marker != 0 && (first & marker) == 0) { marker >>= 1; count++; }
        if (marker == 0 || count > (identifier ? 4 : 8) || count - 1 > data.Length - p) return false;
        value = (ulong)(identifier ? first : first & (marker - 1));
        for (int i = 1; i < count; i++) value = (value << 8) | data[p++];
        if (!identifier && value == (1UL << (count * 7)) - 1) value = ulong.MaxValue;
        return true;
    }
}
