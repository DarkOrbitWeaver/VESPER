namespace Vesper.Engine;

public readonly record struct FileCheck(bool Exists, long Length, bool IsReparsePoint, bool IsCloudPlaceholder, bool IsDirectory);

/// <summary>Path validation and file inspection that precede every destructive or opening action.</summary>
public static class PathSafety
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>
    /// Resolves a user-chosen folder to a full path. Never creates the volume root: if a removable or
    /// network drive is gone, creating "E:\Downloads" would silently write to the wrong place.
    /// </summary>
    public static bool TryResolveFolder(string? folder, bool create, out string full, out string error)
    {
        full = "";
        error = "";
        if (string.IsNullOrWhiteSpace(folder)) { error = "Choose a destination folder."; return false; }
        folder = Environment.ExpandEnvironmentVariables(folder.Trim());
        if (!Path.IsPathFullyQualified(folder)) { error = "The destination must be a full path."; return false; }
        if (folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        { error = "The destination cannot contain '..'."; return false; }

        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { error = "The destination path is not valid."; return false; }

        string? root = Path.GetPathRoot(full);
        if (root is null || !Directory.Exists(root)) { error = $"The drive or share '{root}' is not available."; return false; }

        if (File.Exists(full)) { error = "The destination is a file, not a folder."; return false; }
        if (!Directory.Exists(full))
        {
            if (!create) { error = "The destination folder does not exist."; return false; }
            try { Directory.CreateDirectory(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error = "The destination folder could not be created: " + ex.Message; return false; }
        }
        return true;
    }

    public static bool IsInside(string root, string path)
    {
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(path);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static FileCheck Inspect(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            long length = directory ? 0 : new FileInfo(path).Length;
            bool reparse = attributes.HasFlag(FileAttributes.ReparsePoint);
            bool cloud = (attributes & (RecallOnDataAccess | RecallOnOpen)) != 0;
            return new FileCheck(true, length, reparse, cloud, directory);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileCheck(false, 0, false, false, false);
        }
    }

    /// <summary>User-readable form: the profile path is shown as %USERPROFILE%. Never used for filesystem work.</summary>
    public static string ForDisplay(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }
}
