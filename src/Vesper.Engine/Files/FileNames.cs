using System.Text;
using Vesper.Shared;

namespace Vesper.Engine;

/// <summary>
/// Turns untrusted names (URL segments, Content-Disposition, browser metadata) into safe Windows file names.
/// A name that comes out of here can never contain a directory component.
/// </summary>
public static class FileNames
{
    private const int MaxLength = 150;

    public static string Sanitize(string? raw, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        // Keep only the last path component, whichever separator was used.
        string name = raw.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        // Windows rules on every platform; a lone surrogate comes out of EnumerateRunes as U+FFFD.
        var builder = new StringBuilder(name.Length);
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (FileNameRules.IsHidden(rune)) continue;         // control codes, zero-width marks, bidi overrides
            if (FileNameRules.IsWindowsInvalid(rune)) builder.Append('_');
            else builder.Append(rune.ToString());
        }

        name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c == '.' || c == '_')) return fallback;

        int firstDot = name.IndexOf('.');
        string firstSegment = firstDot < 0 ? name : name[..firstDot];
        if (FileNameRules.IsReservedStem(firstSegment)) name = "_" + name;

        if (name.Length > MaxLength)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 16) ext = ext[..16];
            string stem = name[..(MaxLength - ext.Length)];
            if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];   // dont cut an emoji in half
            name = stem.TrimEnd('.', ' ') + ext;
        }
        return name;
    }

    public static string FromUrl(Uri url, string fallback = "download")
    {
        string segment = url.Segments.LastOrDefault() ?? "";
        try { segment = Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { /* keep the escaped text; it is sanitized below */ }
        return Sanitize(segment.TrimEnd('/'), fallback);
    }

    /// <summary>Adds an extension from the Content-Type when the name has none.</summary>
    public static string WithExtensionFor(string name, string? contentType)
    {
        if (Path.HasExtension(name) || contentType is null) return name;
        string? ext = contentType.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4", "video/webm" => ".webm", "video/x-matroska" => ".mkv",
            "audio/mpeg" => ".mp3", "audio/mp4" or "audio/x-m4a" => ".m4a", "audio/ogg" => ".ogg", "audio/flac" => ".flac",
            "audio/wav" or "audio/x-wav" => ".wav", "audio/webm" => ".weba",
            "application/zip" => ".zip", "application/pdf" => ".pdf", "application/x-7z-compressed" => ".7z",
            "application/vnd.rar" or "application/x-rar-compressed" => ".rar", "application/gzip" => ".gz",
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif",
            "application/x-iso9660-image" => ".iso", "application/epub+zip" => ".epub",
            _ => null
        };
        return ext is null ? name : name + ext;
    }

    /// <summary>First path in <paramref name="folder"/> that does not exist: "a.zip", "a (1).zip", "a (2).zip"…</summary>
    public static string UniquePath(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        string candidate = Path.Combine(folder, name);
        if (!exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException("Could not find a free file name.");
    }

    public static string CategoryFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".ts" or ".m4v" => "Video",
        ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".weba" => "Audio",
        ".zip" or ".rar" or ".7z" or ".gz" or ".tar" or ".xz" or ".bz2" => "Archives",
        ".iso" or ".img" => "Disc images",
        ".exe" or ".msi" or ".msix" or ".appx" => "Programs",
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".epub" or ".txt" => "Documents",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" => "Images",
        _ => "Other"
    };
}
