using System.Buffers.Binary;
using System.Text;

namespace Vesper.Engine;

internal sealed record TransportStreamInfo(int Pid, byte StreamType, string Language)
{
    internal bool IsAudio => StreamType is 0x03 or 0x04 or 0x0f or 0x11 or 0x81 or 0x87;
    internal bool IsVideo => StreamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x24;
}

internal sealed record TransportPes(TransportStreamInfo Stream, byte[] Data);

/// <summary>
/// Bounded single-program MPEG-TS structural validation: 188-byte packets, clear payloads, CRC-checked PAT/PMT,
/// recognized audio/video PIDs and PES starts. PSI sections may span packets. Not a codec decoder or remuxer.
/// </summary>
internal sealed class TransportStreamValidator
{
    private sealed class Section
    {
        internal readonly byte[] Bytes = new byte[1024];
        internal int Count, Expected;
        internal void Reset() { Count = Expected = 0; }
    }
    private readonly Section _pat = new(), _pmt = new();
    private int? _program, _pmtPid;
    private readonly Dictionary<int, TransportStreamInfo> _media = [];
    private readonly HashSet<int> _observed = [];
    private readonly Dictionary<int, MemoryStream> _pendingPes = [];
    private readonly Action<TransportPes>? _onPes;
    private long _pendingPesBytes;

    internal TransportStreamValidator(Action<TransportPes>? onPes = null) => _onPes = onPes;

    internal void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % 188 != 0) throw Bad("Expected complete 188-byte MPEG transport packets.");
        for (int p = 0; p < bytes.Length; p += 188)
        {
            var packet = bytes.Slice(p, 188);
            if (packet[0] != 0x47 || (packet[1] & 0x80) != 0 || (packet[3] & 0xc0) != 0)
                throw Bad("Invalid transport synchronization, transport error, or scrambled media.");
            int control = (packet[3] >> 4) & 3;
            if (control == 0) throw Bad("Reserved transport adaptation mode.");
            int offset = 4;
            if ((control & 2) != 0)
            {
                int length = packet[4]; offset += 1 + length;
                if (offset > 188 || (control == 2 && offset != 188)) throw Bad("Invalid transport adaptation field.");
            }
            if ((control & 1) == 0 || offset >= 188) continue;
            int pid = ((packet[1] & 0x1f) << 8) | packet[2];
            bool start = (packet[1] & 0x40) != 0;
            var payload = packet[offset..];
            if (pid == 0) Feed(_pat, payload, start, isPat: true);
            else if (pid == _pmtPid) Feed(_pmt, payload, start, isPat: false);
            else if (_media.TryGetValue(pid, out TransportStreamInfo? stream))
            {
                if (start)
                {
                    if (_onPes is null) ValidatePesStart(payload);
                    else
                    {
                        FinishPes(pid, stream);
                        _pendingPes[pid] = new MemoryStream();
                        AppendPes(_pendingPes[pid], payload);
                    }
                    _observed.Add(pid);
                }
                else if (_onPes is not null && _pendingPes.TryGetValue(pid, out MemoryStream? pes)) AppendPes(pes, payload);
            }
        }
    }

    internal void Finish(bool requireAudio = false, bool requireVideo = false)
    {
        foreach (var pair in _pendingPes.ToArray()) FinishPes(pair.Key, _media[pair.Key]);
        if (_media.Count == 0 || _media.Keys.Any(pid => !_observed.Contains(pid))
            || (requireAudio && !_media.Values.Any(stream => stream.IsAudio))
            || (requireVideo && !_media.Values.Any(stream => stream.IsVideo)))
            throw Bad("The transport output is missing required program tables, audio/video tracks or elementary media data.");
    }

    private static void ValidatePesStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            throw Bad("A declared media PID does not begin with a complete PES header.");
        ValidateStreamId(payload[3]);
    }

    private void FinishPes(int pid, TransportStreamInfo stream)
    {
        if (!_pendingPes.Remove(pid, out MemoryStream? pes)) return;
        using (pes)
        {
            byte[] data = pes.ToArray();
            _pendingPesBytes -= pes.Length;
            if (data.Length < 9 || data[0] != 0 || data[1] != 0 || data[2] != 1)
                throw Bad("A declared media PID does not contain a complete PES header.");
            ValidateStreamId(data[3]);
            int packetLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            if (packetLength > 0)
            {
                int expected = 6 + packetLength;
                if (data.Length < expected) throw Bad("A PES packet ended before its declared length.");
                if (data.Length > expected) Array.Resize(ref data, expected);
            }
            if (data.Length < 9 + data[8]) throw Bad("A PES optional header is truncated.");
            _onPes?.Invoke(new TransportPes(stream, data));
        }
    }

    private static void ValidateStreamId(int streamId)
    {
        if (streamId is not (>= 0xc0 and <= 0xef) && streamId != 0xbd)
            throw Bad("Unsupported elementary-stream identifier.");
    }

    private void AppendPes(MemoryStream pes, ReadOnlySpan<byte> payload)
    {
        if (pes.Length > 16 * 1024 * 1024 - payload.Length || _pendingPesBytes > 32 * 1024 * 1024 - payload.Length)
            throw Bad("Buffered PES data exceeds the supported size limit.");
        pes.Write(payload);
        _pendingPesBytes += payload.Length;
    }

    private void Feed(Section state, ReadOnlySpan<byte> data, bool starts, bool isPat)
    {
        if (starts)
        {
            if (data.Length == 0 || data[0] >= data.Length) throw Bad("Invalid PSI pointer.");
            int pointer = data[0];
            if (state.Count > 0 && pointer > 0) Append(state, data.Slice(1, pointer), isPat);
            state.Reset(); data = data[(1 + pointer)..];
        }
        else if (state.Count == 0) return; // pre-existing section before the first observed packet
        while (!data.IsEmpty)
        {
            if (state.Count == 0 && data[0] == 0xff) break; // stuffing
            int consumed = Append(state, data, isPat);
            data = data[consumed..];
            if (consumed == 0) break;
        }
    }

    private int Append(Section state, ReadOnlySpan<byte> data, bool isPat)
    {
        int consumed = 0;
        while (consumed < data.Length)
        {
            if (state.Count >= state.Bytes.Length) throw Bad("Oversized PSI section.");
            state.Bytes[state.Count++] = data[consumed++];
            if (state.Count == 3)
            {
                state.Expected = 3 + (((state.Bytes[1] & 0x0f) << 8) | state.Bytes[2]);
                if ((state.Bytes[1] & 0x80) == 0 || state.Expected is < 12 or > 1024) throw Bad("Invalid PSI section length.");
            }
            if (state.Expected > 0 && state.Count == state.Expected)
            {
                var section = state.Bytes.AsSpan(0, state.Count);
                if (Crc(section) != 0) throw Bad("The program table failed its MPEG-2 CRC check.");
                if ((section[5] & 1) != 0)
                {
                    if (isPat) ReadPat(section); else ReadPmt(section);
                }
                state.Reset(); return consumed;
            }
        }
        return consumed;
    }

    private void ReadPat(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0 || s[6] != 0 || s[7] != 0 || (s.Length - 12) % 4 != 0)
            throw Bad("Only complete single-section program association tables are supported.");
        int? program = null, pid = null;
        for (int i = 8; i < s.Length - 4; i += 4)
        {
            int number = BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
            if (number == 0) continue;
            if (program is not null) throw Bad("Multiple transport programs require explicit selection and are unsupported.");
            program = number; pid = ((s[i + 2] & 0x1f) << 8) | s[i + 3];
        }
        if (program is null || pid is null or 0 or 0x1fff || (_program is not null && (_program != program || _pmtPid != pid)))
            throw Bad("Missing or changed transport program identity.");
        _program = program; _pmtPid = pid;
    }

    private void ReadPmt(ReadOnlySpan<byte> s)
    {
        if (s[0] != 2 || s.Length < 16 || s[6] != 0 || s[7] != 0 || BinaryPrimitives.ReadUInt16BigEndian(s[3..]) != _program)
            throw Bad("Invalid or changed program map table.");
        int offset = 12 + (((s[10] & 0x0f) << 8) | s[11]);
        int end = s.Length - 4;
        if (offset > end) throw Bad("Program descriptors exceed the program table.");
        var media = new Dictionary<int, TransportStreamInfo>();
        while (offset < end)
        {
            if (end - offset < 5) throw Bad("Truncated elementary-stream entry.");
            int type = s[offset], pid = ((s[offset + 1] & 0x1f) << 8) | s[offset + 2];
            int descriptors = ((s[offset + 3] & 0x0f) << 8) | s[offset + 4];
            int descriptorStart = offset + 5, descriptorEnd = descriptorStart + descriptors;
            if (descriptorEnd > end) throw Bad("Elementary descriptors exceed the program table.");
            bool video = type is 0x01 or 0x02 or 0x10 or 0x1b or 0x24;
            bool audio = type is 0x03 or 0x04 or 0x0f or 0x11 or 0x81 or 0x87;
            if (video || audio)
            {
                string language = ReadLanguage(s.Slice(descriptorStart, descriptors));
                if (!media.TryAdd(pid, new TransportStreamInfo(pid, (byte)type, language)) || pid is 0 or 0x1fff)
                    throw Bad("Invalid or duplicate elementary PID.");
            }
            offset = descriptorEnd;
        }
        if (_media.Count > 0 && (_media.Count != media.Count || _media.Any(p =>
            !media.TryGetValue(p.Key, out TransportStreamInfo? stream) || stream.StreamType != p.Value.StreamType)))
            throw Bad("Track layout changed; concatenation would need remuxing.");
        if (media.Count is < 1 or > 16) throw Bad("No supported media or excessive track count in the program table.");
        foreach (var pair in media) _media[pair.Key] = pair.Value;
    }

    private static string ReadLanguage(ReadOnlySpan<byte> descriptors)
    {
        int offset = 0;
        while (offset < descriptors.Length)
        {
            if (descriptors.Length - offset < 2) throw Bad("Truncated program descriptor.");
            int tag = descriptors[offset], length = descriptors[offset + 1];
            offset += 2;
            if (length > descriptors.Length - offset) throw Bad("Program descriptor exceeds its table.");
            if (tag == 0x0a && length >= 4)
            {
                ReadOnlySpan<byte> code = descriptors.Slice(offset, 3);
                if (code.ToArray().All(b => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                    return Encoding.ASCII.GetString(code).ToLowerInvariant();
            }
            offset += length;
        }
        return "und";
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= (uint)b << 24;
            for (int i = 0; i < 8; i++) value = (value << 1) ^ ((value & 0x80000000) != 0 ? 0x04c11db7u : 0u);
        }
        return value;
    }
    private static MediaContainerException Bad(string reason) => new(reason);
}
