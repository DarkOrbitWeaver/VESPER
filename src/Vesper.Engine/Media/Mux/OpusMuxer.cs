using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vesper.Engine;

/// <summary>Copies unlaced Opus packets into Ogg, preserving OpusHead pre-skip and channel mapping.</summary>
internal static class OpusMuxer
{
    internal static async Task<string> WriteAsync(DemuxedTrack track, Stream source, Stream output,
        MediaMergeMetadata? metadata, MediaThumbnail? cover, CancellationToken ct)
    {
        if (track.Kind != TrackKind.Audio || track.Codec != "opus" || track.HasLacing || track.Samples.Count == 0
            || track.CodecPrivate is not { Length: >= 19 } head || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)
            || head[8] != 1 || head[9] == 0)
            throw new MediaContainerException("This source does not expose complete, unlaced Opus packets.");
        uint serial = 0x56535052;
        using var digest = SHA256.Create();
        using var hashed = new CryptoStream(output, digest, CryptoStreamMode.Write, leaveOpen: true);
        uint sequence = await OggAudioMetadataWriter.WriteHeaderPacketAsync(hashed, head, serial, 0, true, ct).ConfigureAwait(false);
        sequence = await OggAudioMetadataWriter.WriteHeaderPacketAsync(hashed,
            OggAudioMetadataWriter.OpusComments(metadata, cover), serial, sequence, false, ct).ConfigureAwait(false);
        long granule = 0;
        int packetCount = 0;
        var packets = new List<byte[]>(50);
        int laces = 0, bytes = 0, samplesOnPage = 0;
        void Flush(bool final)
        {
            if (packets.Count == 0) return;
            byte[] page = new byte[27 + laces + bytes];
            "OggS"u8.CopyTo(page); page[5] = final ? (byte)4 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence++);
            page[26] = (byte)laces;
            int lace = 27, payload = 27 + laces;
            foreach (byte[] packet in packets)
            {
                int remaining = packet.Length;
                while (remaining >= 255) { page[lace++] = 255; remaining -= 255; }
                page[lace++] = (byte)remaining;
                packet.CopyTo(page, payload); payload += packet.Length;
            }
            OggAudioMetadataWriter.WriteChecksum(page);
            hashed.Write(page);
            packets.Clear(); laces = bytes = samplesOnPage = 0;
        }
        for (int i = 0; i < track.Samples.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sample = track.Samples[i];
            if (sample.Size is < 1 or > 61_440 || sample.Offset < 0 || sample.Offset > source.Length - sample.Size)
                throw new MediaContainerException("An Opus packet has an invalid size or position.");
            byte[] packet = new byte[sample.Size]; source.Position = sample.Offset; source.ReadExactly(packet);
            int duration = PacketSamples(packet);
            int packetLaces = packet.Length / 255 + 1;
            if (laces + packetLaces > 255 || bytes + packet.Length > 65025 || samplesOnPage >= 48000) Flush(false);
            packets.Add(packet); laces += packetLaces; bytes += packet.Length; samplesOnPage += duration;
            granule = checked(granule + duration); packetCount++;
            if (i == track.Samples.Count - 1) Flush(true);
        }
        if (granule <= BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)))
            throw new MediaContainerException("Opus pre-skip consumes the entire output.");
        await hashed.FlushFinalBlockAsync(ct).ConfigureAwait(false);
        output.Position = 0;
        OggAudioMetadataWriter.ValidateOpus(output, packetCount, granule);
        return Convert.ToHexString(digest.Hash!);
    }

    internal static int PacketSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) throw new MediaContainerException("Empty Opus packet.");
        int configuration = packet[0] >> 3;
        int size = configuration >= 16 ? 120 << (configuration & 3)
            : configuration >= 12 ? 480 << (configuration & 1)
            : (configuration & 3) == 3 ? 2880 : 480 << (configuration & 3);
        int count = (packet[0] & 3) switch
        {
            0 => 1, 1 or 2 => 2,
            _ => packet.Length >= 2 ? packet[1] & 63 : 0
        };
        int samples = size * count;
        if (count == 0 || samples > 5760) throw new MediaContainerException("Invalid Opus packet duration.");
        return samples;
    }
}
