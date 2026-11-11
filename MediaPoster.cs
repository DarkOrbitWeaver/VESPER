using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace VESPER;

internal static class MediaPoster
{
    internal static bool TryUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static async Task<Bitmap?> LoadAsync(SecureHttpClient http, Uri uri, int width, int height, CancellationToken token)
    {
        try
        {
            byte[]? bytes = await http.GetThumbnailAsync(uri, token).ConfigureAwait(false);
            if (bytes is null) return null;
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream, false, true);
                if ((long)image.Width * image.Height > 4_000_000) return null;
                double scale = Math.Min(1, Math.Min(width / (double)image.Width, height / (double)image.Height));
                return new Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or MediaResolverException
            or OperationCanceledException or HttpRequestException or ObjectDisposedException
            or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }
}
