using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Vesper.Engine;

internal sealed record MediaThumbnail(byte[] Data, string MediaType, string FileName, string Sha256)
{
    internal const int MaxBytes = 768 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static async Task<MediaThumbnail?> FetchAsync(string? address, HttpTransport http, bool allowPrivate,
        CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(address, out Uri uri, out _)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            UrlPolicy.EnsureAllowed(uri, allowPrivate);
            using HttpResponseMessage response = await http.SendAsync(uri, new RequestSpec(), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength is long size && size > MaxBytes)
                return null;

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var data = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int read = await body.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (data.Length + read > MaxBytes) return null;
                data.Write(buffer, 0, read);
            }
            return TryCreate(data.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is RedirectException or BlockedAddressException || ErrorClassifier.IsTransient(ex))
        { return null; }
    }

    internal static MediaThumbnail? TryCreate(byte[] data)
    {
        if (data.Length is < 16 or > MaxBytes) return null;
        ReadOnlySpan<byte> bytes = data;
        string mediaType, fileName;
        bool valid;
        if (bytes.StartsWith(PngSignature))
        {
            valid = ValidPng(bytes);
            mediaType = "image/png";
            fileName = "cover.png";
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            valid = ValidJpeg(bytes);
            mediaType = "image/jpeg";
            fileName = "cover.jpg";
        }
        else return null;

        return valid ? new MediaThumbnail(data, mediaType, fileName, Convert.ToHexString(SHA256.HashData(bytes))) : null;
    }

    private static bool ValidPng(ReadOnlySpan<byte> bytes)
    {
        int pos = PngSignature.Length;
        bool header = false, imageData = false;
        while (pos <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[pos..]);
            if (length > MaxBytes || length > bytes.Length - pos - 12) return false;
            ReadOnlySpan<byte> type = bytes.Slice(pos + 4, 4);
            ReadOnlySpan<byte> data = bytes.Slice(pos + 8, (int)length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 8 + (int)length, 4));
            if (PngCrc(type, data) != crc) return false;

            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return false;
                uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
                uint height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (!ValidDimensions(width, height) || data[10] != 0 || data[11] != 0 || data[12] > 1) return false;
                header = true;
            }
            else if (type.SequenceEqual("IHDR"u8)) return false;

            if (type.SequenceEqual("IDAT"u8)) imageData = true;
            if (type.SequenceEqual("IEND"u8))
                return length == 0 && imageData && pos + 12 == bytes.Length;

            pos += checked((int)length + 12);
        }
        return false;
    }

    private static bool ValidJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[^2] != 0xFF || bytes[^1] != 0xD9) return false;
        int pos = 2;
        bool dimensions = false, scan = false;
        while (pos < bytes.Length - 2 && !scan)
        {
            if (bytes[pos++] != 0xFF) return false;
            while (pos < bytes.Length && bytes[pos] == 0xFF) pos++;
            if (pos >= bytes.Length) return false;
            byte marker = bytes[pos++];
            if (marker == 0x01) continue;
            if (marker is 0xD8 or 0xD9 or 0x00 || marker is >= 0xD0 and <= 0xD7) return false;
            if (pos + 2 > bytes.Length) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[pos..]);
            if (length < 2 || length > bytes.Length - pos) return false;
            int data = pos + 2;
            if (IsStartOfFrame(marker))
            {
                if (length < 7) return false;
                uint height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(data + 1)..]);
                uint width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(data + 3)..]);
                if (!ValidDimensions(width, height)) return false;
                dimensions = true;
            }
            scan = marker == 0xDA;
            pos += length;
        }
        return dimensions && scan;
    }

    private static bool IsStartOfFrame(byte marker) => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
        or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static bool ValidDimensions(uint width, uint height) =>
        width is > 0 and <= 8192 && height is > 0 and <= 8192 && (ulong)width * height <= 32_000_000;

    private static uint PngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
