namespace Vesper.Engine;

/// <summary>Rebases compatible MPEG-TS HLS epochs into one playable transport timeline.</summary>
internal sealed class HlsTransportTimeline
{
    private const int PacketBytes = 188;
    private const ulong TimestampModulus = 1UL << 33;
    private const ulong PcrModulus = TimestampModulus * 300;
    private sealed record CounterState(int Version, bool Normalize, Dictionary<int, byte> Output,
        Dictionary<int, byte> Input, Dictionary<int, byte> Offsets);

    private readonly Dictionary<int, byte> _lastOutputCounter = [];
    private readonly Dictionary<int, byte> _lastInputCounter = [];
    private readonly Dictionary<int, byte> _counterOffset = [];
    private bool _normalizeCounters;

    internal static long? FirstPresentationTimestamp(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length % PacketBytes != 0)
            throw Bad("Expected complete 188-byte MPEG transport packets.");
        for (int position = 0; position < bytes.Length; position += PacketBytes)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> packet = bytes.Slice(position, PacketBytes);
            if (!TryGetPayload(packet, out _, out int payloadOffset, out bool start) || !start)
                continue;
            ReadOnlySpan<byte> payload = packet[payloadOffset..];
            if (!IsMediaPes(payload)) continue;
            int headerLength = payload[8];
            if (payload.Length < 9 + headerLength)
                throw Bad("A PES optional header spans multiple transport payloads and cannot be rebased safely.");
            int flags = (payload[7] >> 6) & 3;
            if (flags == 0) continue;
            int requiredHeaderLength = flags switch { 2 => 5, 3 => 10, _ => int.MaxValue };
            if (headerLength < requiredHeaderLength || flags is not (2 or 3))
                throw Bad("A transport packet has an invalid or truncated presentation timestamp.");
            return (long)ReadTimestamp(payload.Slice(9, 5), flags == 3 ? 3 : 2);
        }
        return null;
    }

    internal void ObservePrefix(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length % PacketBytes != 0)
            throw Bad("Expected complete 188-byte MPEG transport packets.");
        for (int position = 0; position < bytes.Length; position += PacketBytes)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> packet = bytes.Slice(position, PacketBytes);
            if (!TryGetPayload(packet, out int pid, out _, out _))
                throw Bad("The saved HLS prefix has an invalid transport packet.");
            _lastOutputCounter[pid] = (byte)(packet[3] & 0x0f);
        }
    }

    internal string SnapshotCounters() => System.Text.Json.JsonSerializer.Serialize(new CounterState(1,
        _normalizeCounters, new Dictionary<int, byte>(_lastOutputCounter),
        new Dictionary<int, byte>(_lastInputCounter), new Dictionary<int, byte>(_counterOffset)));

    internal bool TryRestoreCounters(string? json)
    {
        if (json is not { Length: > 0 and <= 16_384 }) return false;
        CounterState? state;
        try { state = System.Text.Json.JsonSerializer.Deserialize<CounterState>(json); }
        catch (System.Text.Json.JsonException) { return false; }
        if (state is not { Version: 1, Output: not null, Input: not null, Offsets: not null }
            || state.Output.Count > 8192 || state.Input.Count > 8192 || state.Offsets.Count > 8192
            || state.Output.Any(pair => !ValidCounter(pair))
            || state.Input.Any(pair => !ValidCounter(pair))
            || state.Offsets.Any(pair => !ValidCounter(pair))
            || state.Input.Keys.Any(pid => !state.Offsets.ContainsKey(pid))
            || state.Offsets.Keys.Any(pid => !state.Input.ContainsKey(pid)))
            return false;

        if (_lastOutputCounter.Count != state.Output.Count
            || _lastOutputCounter.Any(pair => !state.Output.TryGetValue(pair.Key, out byte saved) || saved != pair.Value))
            return false;
        if (!state.Normalize && (state.Input.Count > 0 || state.Offsets.Count > 0)) return false;

        _normalizeCounters = state.Normalize;
        _lastInputCounter.Clear();
        _counterOffset.Clear();
        foreach (var pair in state.Input) _lastInputCounter.Add(pair.Key, pair.Value);
        foreach (var pair in state.Offsets) _counterOffset.Add(pair.Key, pair.Value);
        return true;
    }

    internal ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> source, long timestampOffset, bool startEpoch,
        CancellationToken ct)
    {
        if (startEpoch)
        {
            _normalizeCounters = true;
            _lastInputCounter.Clear();
            _counterOffset.Clear();
        }

        if (!_normalizeCounters && timestampOffset == 0)
        {
            ObservePrefix(source.Span, ct);
            return source;
        }

        byte[] result = source.ToArray();
        if (result.Length == 0 || result.Length % PacketBytes != 0)
            throw Bad("Expected complete 188-byte MPEG transport packets.");
        for (int position = 0; position < result.Length; position += PacketBytes)
        {
            ct.ThrowIfCancellationRequested();
            Span<byte> packet = result.AsSpan(position, PacketBytes);
            if (!TryGetPayload(packet, out int pid, out int payloadOffset, out bool start, out bool hasPayload))
                throw Bad("A transport segment contains an invalid packet header.");

            if (_normalizeCounters)
                RebaseCounter(packet, pid, hasPayload);
            else
                _lastOutputCounter[pid] = (byte)(packet[3] & 0x0f);

            int control = (packet[3] >> 4) & 3;
            if ((control & 2) != 0)
            {
                int adaptationLength = packet[4];
                if (adaptationLength > 0)
                {
                    byte flags = packet[5];
                    int fieldOffset = 6;
                    if ((flags & 0x10) != 0)
                    {
                        if (fieldOffset + 6 > 5 + adaptationLength)
                            throw Bad("A PCR field is truncated.");
                        RebasePcr(packet.Slice(fieldOffset, 6), timestampOffset);
                        fieldOffset += 6;
                    }
                    if ((flags & 0x08) != 0)
                    {
                        if (fieldOffset + 6 > 5 + adaptationLength)
                            throw Bad("An OPCR field is truncated.");
                        RebasePcr(packet.Slice(fieldOffset, 6), timestampOffset);
                    }
                }
            }

            if (!hasPayload || !start) continue;
            Span<byte> payload = packet[payloadOffset..];
            if (!IsMediaPes(payload)) continue;
            int headerLength = payload[8];
            if (payload.Length < 9 + headerLength)
                throw Bad("A PES optional header spans multiple transport payloads and cannot be rebased safely.");
            int ptsDts = (payload[7] >> 6) & 3;
            if (ptsDts == 0) continue;
            int requiredHeaderLength = ptsDts switch { 2 => 5, 3 => 10, _ => int.MaxValue };
            if (headerLength < requiredHeaderLength || ptsDts is not (2 or 3))
                throw Bad("A transport packet has an invalid or truncated PES timestamp.");
            ulong pts = ReadTimestamp(payload.Slice(9, 5), ptsDts == 3 ? 3 : 2);
            WriteTimestamp(payload.Slice(9, 5), ptsDts == 3 ? 3 : 2, AddTimestamp(pts, timestampOffset));
            if (ptsDts == 3)
            {
                ulong dts = ReadTimestamp(payload.Slice(14, 5), 1);
                WriteTimestamp(payload.Slice(14, 5), 1, AddTimestamp(dts, timestampOffset));
            }
        }
        return result;
    }

    private void RebaseCounter(Span<byte> packet, int pid, bool hasPayload)
    {
        byte input = (byte)(packet[3] & 0x0f);
        byte offset;
        if (_lastInputCounter.TryGetValue(pid, out byte previousInput))
        {
            byte expected = (byte)((previousInput + (hasPayload ? 1 : 0)) & 0x0f);
            if (input != expected)
                throw Bad("A transport continuity counter changed inside an HLS discontinuity epoch.");
            offset = _counterOffset[pid];
        }
        else
        {
            int nextOutput = _lastOutputCounter.TryGetValue(pid, out byte previousOutput)
                ? (previousOutput + (hasPayload ? 1 : 0)) & 0x0f
                : input;
            offset = (byte)((nextOutput - input) & 0x0f);
            _counterOffset[pid] = offset;
        }

        byte output = (byte)((input + offset) & 0x0f);
        packet[3] = (byte)((packet[3] & 0xf0) | output);
        _lastInputCounter[pid] = input;
        _lastOutputCounter[pid] = output;
    }

    private static bool ValidCounter(KeyValuePair<int, byte> pair) => pair.Key is >= 0 and <= 0x1fff && pair.Value <= 0x0f;

    private static void RebasePcr(Span<byte> value, long timestampOffset)
    {
        if ((value[4] & 0x7e) != 0x7e)
            throw Bad("A transport clock has invalid reserved bits.");

        ulong baseValue = ((ulong)value[0] << 25) | ((ulong)value[1] << 17)
            | ((ulong)value[2] << 9) | ((ulong)value[3] << 1) | ((ulong)value[4] >> 7);
        ulong extension = (ulong)(((value[4] & 1) << 8) | value[5]);
        if (extension >= 300) throw Bad("A transport clock extension is outside its valid range.");

        Int128 adjusted = ((Int128)(baseValue * 300 + extension) + (Int128)timestampOffset * 300)
            % (Int128)PcrModulus;
        if (adjusted < 0) adjusted += (Int128)PcrModulus;
        ulong rebasedBase = (ulong)(adjusted / 300);
        ushort rebasedExtension = (ushort)(adjusted % 300);
        value[0] = (byte)(rebasedBase >> 25);
        value[1] = (byte)(rebasedBase >> 17);
        value[2] = (byte)(rebasedBase >> 9);
        value[3] = (byte)(rebasedBase >> 1);
        value[4] = (byte)(((rebasedBase & 1) << 7) | 0x7eUL | ((ulong)rebasedExtension >> 8));
        value[5] = (byte)rebasedExtension;
    }

    private static bool IsMediaPes(ReadOnlySpan<byte> payload) => payload.Length >= 9
        && payload[0] == 0 && payload[1] == 0 && payload[2] == 1
        && (payload[3] >= 0xc0 && payload[3] <= 0xef || payload[3] == 0xbd)
        && (payload[6] & 0xc0) == 0x80;

    private static ulong ReadTimestamp(ReadOnlySpan<byte> value, int prefix)
    {
        if (value.Length < 5 || value[0] >> 4 != prefix || (value[0] & 1) == 0
            || (value[2] & 1) == 0 || (value[4] & 1) == 0)
            throw Bad("A PES timestamp has invalid marker bits.");
        return ((ulong)(((uint)value[0] >> 1) & 7) << 30)
            | ((ulong)(uint)value[1] << 22)
            | ((ulong)(((uint)value[2] >> 1) & 0x7f) << 15)
            | ((ulong)(uint)value[3] << 7)
            | (ulong)(((uint)value[4] >> 1) & 0x7f);
    }

    private static void WriteTimestamp(Span<byte> value, int prefix, ulong timestamp)
    {
        value[0] = (byte)((prefix << 4) | (int)(((timestamp >> 30) & 7) << 1) | 1);
        value[1] = (byte)(timestamp >> 22);
        value[2] = (byte)((((timestamp >> 15) & 0x7f) << 1) | 1);
        value[3] = (byte)(timestamp >> 7);
        value[4] = (byte)(((timestamp & 0x7f) << 1) | 1);
    }

    private static ulong AddTimestamp(ulong timestamp, long offset)
    {
        Int128 value = ((Int128)timestamp + offset) % (Int128)TimestampModulus;
        if (value < 0) value += (Int128)TimestampModulus;
        return (ulong)value;
    }

    private static bool TryGetPayload(ReadOnlySpan<byte> packet, out int pid, out int payloadOffset, out bool start)
    {
        pid = 0; payloadOffset = 188; start = false;
        if (packet.Length != PacketBytes || packet[0] != 0x47) return false;
        int control = (packet[3] >> 4) & 3;
        if (control == 0) return false;
        pid = ((packet[1] & 0x1f) << 8) | packet[2];
        start = (packet[1] & 0x40) != 0;
        payloadOffset = 4;
        if ((control & 2) != 0)
        {
            int length = packet[4];
            payloadOffset += 1 + length;
            if (payloadOffset > PacketBytes) return false;
        }
        return true;
    }

    private static bool TryGetPayload(ReadOnlySpan<byte> packet, out int pid, out int payloadOffset,
        out bool start, out bool hasPayload)
    {
        bool valid = TryGetPayload(packet, out pid, out payloadOffset, out start);
        int control = valid ? (packet[3] >> 4) & 3 : 0;
        hasPayload = valid && (control & 1) != 0 && payloadOffset < PacketBytes;
        return valid;
    }

    private static MediaContainerException Bad(string reason) => new(reason);
}
