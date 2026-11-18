using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
        if (!TryGetFullPath(folder, out full, out error)) return false;

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

    /// <summary>Expands environment variables and normalizes an absolute path without creating it.</summary>
    public static bool TryGetFullPath(string? path, out string full, out string error)
    {
        full = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Choose a destination folder."; return false; }
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(path)) { error = "The destination must be a full path."; return false; }
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        { error = "The destination cannot contain '..'."; return false; }

        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { error = "The destination path is not valid."; return false; }
        return true;
    }

    public static bool IsInside(string root, string path)
    {
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(path);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryCheckUnlinkedDirectory(string folder, out string error)
    {
        try
        {
            for (string? directory = Path.GetFullPath(folder); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0 || IsCloudPlaceholder(attributes))
                {
                    error = "The download directory or one of its parents is a link, placeholder, or file.";
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The download directory is unavailable.";
            return false;
        }

        error = "";
        return true;
    }

    internal static bool IsCloudPlaceholder(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen)) != 0;

    internal static SafeFileHandle OpenTemporaryHandle(string path, FileMode mode, FileAccess access, FileShare share,
        FileOptions options = FileOptions.None, bool allowDelete = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The temporary file path is invalid.");
        path = Path.GetFullPath(path);
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || Path.GetFileName(path).Contains(':'))
            throw new IOException("The temporary file path is invalid.");
        if (!TryCheckUnlinkedDirectory(folder, out string error)) throw new IOException(error);

        // Inspect the opened object before allowing a write or truncation.
        FileMode openMode = mode == FileMode.Create ? FileMode.OpenOrCreate : mode;
        SafeFileHandle handle = OperatingSystem.IsWindows()
            ? OpenWindowsTemporaryHandle(path, openMode, access, share, options, allowDelete)
            : File.OpenHandle(path, openMode, access, share, options);
        try
        {
            if (!TryCheckUnlinkedDirectory(folder, out error)) throw new IOException(error);
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(handle, out TemporaryFileInfo info))
                    throw FileError(Marshal.GetLastPInvokeError(), path);
                if (!IsPlainFile(info.Attributes) || info.LinkCount != 1)
                    throw new IOException("A temporary path points to a link, folder or cloud placeholder.");
            }
            else if (!IsPlainFile(File.GetAttributes(path)))
                throw new IOException("A temporary path points to a link, folder or cloud placeholder.");

            if (mode == FileMode.Create) RandomAccess.SetLength(handle, 0);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream OpenTemporaryStream(string path, FileMode mode, FileAccess access, FileShare share,
        int bufferSize, FileOptions options = FileOptions.None)
    {
        SafeFileHandle handle = OpenTemporaryHandle(path, mode, access, share, options);
        try { return new FileStream(handle, access, bufferSize, (options & FileOptions.Asynchronous) != 0); }
        catch { handle.Dispose(); throw; }
    }

    private static bool IsPlainFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0
        && !IsCloudPlaceholder(attributes);

    private static SafeFileHandle OpenWindowsTemporaryHandle(string path, FileMode mode, FileAccess access, FileShare share,
        FileOptions options, bool allowDelete)
    {
        const uint read = 0x80000000, write = 0x40000000;
        const uint openReparsePoint = 0x00200000, anonymousSecurity = 0x00100000;
        uint rights = access == FileAccess.Read ? read : read | write;
        if (allowDelete) rights |= 0x00010000; // DELETE
        string nativePath = path.StartsWith(@"\\?\") ? path
            : path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        SafeFileHandle handle = CreateFileW(nativePath, rights, (uint)share, IntPtr.Zero, (uint)mode,
            unchecked((uint)options) | openReparsePoint | anonymousSecurity, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw FileError(error, path);
    }

    private static IOException FileError(int code, string path)
    {
        string message = new Win32Exception(code).Message;
        return code switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | code)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TemporaryFileInfo
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint Volume, SizeHigh, SizeLow, LinkCount, IdHigh, IdLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out TemporaryFileInfo info);

    internal static void DeleteOpenTemporaryFile(SafeFileHandle file, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            return;
        }
        const int fileDispositionInfo = 4;
        byte delete = 1;
        if (!SetFileInformationByHandle(file, fileDispositionInfo, ref delete, sizeof(byte)))
            throw FileError(Marshal.GetLastPInvokeError(), path);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte data, int size);

    internal static bool PathStillNamesFile(string path, FileStream verifiedFile, string digest)
    {
        using SafeFileHandle current = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!OperatingSystem.IsWindows())
        {
            using var reopened = new FileStream(current, FileAccess.Read);
            return reopened.Length == verifiedFile.Length
                && Convert.ToHexString(SHA256.HashData(reopened)).Equals(digest, StringComparison.OrdinalIgnoreCase);
        }

        const int fileIdInfo = 18;
        int size = Marshal.SizeOf<FileIdentity>();
        return GetFileInformationByHandleEx(verifiedFile.SafeFileHandle, fileIdInfo, out FileIdentity verified, size)
            && GetFileInformationByHandleEx(current, fileIdInfo, out FileIdentity found, size)
            && (verified.IdLow != 0 || verified.IdHigh != 0)
            && verified.Volume == found.Volume && verified.IdLow == found.IdLow && verified.IdHigh == found.IdHigh;
    }

    internal static string? TemporaryFileIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const int fileIdInfo = 18;
        if (!GetFileInformationByHandleEx(handle, fileIdInfo, out FileIdentity id, Marshal.SizeOf<FileIdentity>())
            || (id.IdLow == 0 && id.IdHigh == 0))
            throw new IOException("This drive did not provide a stable temporary file ID.");
        long created = File.GetCreationTimeUtc(handle).Ticks;
        return $"{id.Volume:X16}{id.IdHigh:X16}{id.IdLow:X16}{created:X16}";
    }

    internal static void RequireTemporaryIdentity(SafeFileHandle handle, string? expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (expected is null)
            throw new IOException("This partial has no saved file identity. Move it to a safe location in File Explorer to start again.");
        if (!string.Equals(expected, TemporaryFileIdentity(handle), StringComparison.Ordinal))
            throw new IOException("The partial path now contains a different file. Move it to a safe location in File Explorer to start again.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdentity info, int size);

    public static FileCheck Inspect(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            long length = directory ? 0 : new FileInfo(path).Length;
            bool reparse = attributes.HasFlag(FileAttributes.ReparsePoint);
            bool cloud = IsCloudPlaceholder(attributes);
            return new FileCheck(true, length, reparse, cloud, directory);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileCheck(false, 0, false, false, false);
        }
    }
    public static string ForDisplay(string path)
    {
        return TryGetFullPath(path, out string full, out _) ? full : Environment.ExpandEnvironmentVariables(path);
    }
}
