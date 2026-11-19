using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>Small fingerprint of the decoder configurations carried by a clear HLS TS segment.</summary>
internal static class HlsTransportCodecProfile
{
    private sealed record TrackProfile(TrackKind Kind, string Codec, string Language, int Width, int Height,
        int SampleRate, int Channels, string CodecPrivate);

    internal static string? TryRead(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length % 188 != 0) return null;
        try
        {
            using var input = new MemoryReader(bytes);
            using var payloads = new CountingStream();
            List<DemuxedTrack> tracks = MpegTsDemuxer.Read(input, input.Length, payloads, ct);
            var profiles = new List<TrackProfile>(tracks.Count);
            foreach (DemuxedTrack track in tracks)
            {
                if (track.CodecPrivate is null) return null;
                profiles.Add(new TrackProfile(track.Kind, track.Codec, track.Language,
                    track.Width, track.Height, track.SampleRate, track.Channels,
                    Convert.ToHexString(track.CodecPrivate)));
            }
            if (profiles.Count == 0) return null;
            TrackProfile[] ordered = [.. profiles.OrderBy(track => track.Kind)
                .ThenBy(track => track.Language, StringComparer.Ordinal).ThenBy(track => track.Codec, StringComparer.Ordinal)];
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ordered)));
        }
        catch (MediaContainerException ex) when (ex.Message is
            "The H.264 stream has no complete sequence and picture configuration."
            or "H.264 media arrived before its decoder configuration."
            or "The transport output is missing required program tables, audio/video tracks or elementary media data."
            or "A declared transport track contains no complete media samples."
            or "The transport stream contains no supported H.264 or AAC track.")
        {
            // Many valid HLS segments do not repeat decoder configuration. They are usable
            // in the current epoch, but cannot prove compatibility at the next boundary.
            return null;
        }
    }

    private sealed class MemoryReader(ReadOnlyMemory<byte> bytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => _position = value is >= 0 && value <= bytes.Length
                ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(Span<byte> buffer)
        {
            int length = (int)Math.Min(buffer.Length, bytes.Length - _position);
            if (length <= 0) return 0;
            bytes.Span.Slice((int)_position, length).CopyTo(buffer);
            _position += length;
            return length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(bytes.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStream : Stream
    {
        private long _position;
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void SetLength(long value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _length = value;
            if (_position > value) _position = value;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _position = checked(_position + buffer.Length);
            _length = Math.Max(_length, _position);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
