using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    internal static bool TryGetBrowserDownload(BrowserCompletedFile file, out string path, out string? identity)
    {
        path = "";
        identity = null;
        string value = file.Path;
        if (file.Length < 0 || value.Length is < 1 or > 4096 || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value) || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")
            || OperatingSystem.IsWindows() && (value.Length < 3 || !char.IsAsciiLetter(value[0])
                || value[1] != ':' || value[2] is not ('\\' or '/') || value.AsSpan(2).Contains(':')))
            return false;

        try
        {
            string full = Path.GetFullPath(value);
            if (full.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                || OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
                return false;

            using SafeFileHandle saved = OpenTemporaryHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RandomAccess.GetLength(saved) != file.Length) return false;
            identity = TemporaryFileIdentity(saved);
            if (OperatingSystem.IsWindows() && identity is null) return false;

            using SafeFileHandle current = OpenTemporaryHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RandomAccess.GetLength(current) != file.Length
                || identity is not null && !string.Equals(identity, TemporaryFileIdentity(current), StringComparison.Ordinal))
                return false;

            path = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            identity = null;
        }
        return false;
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
        FileOptions options = FileOptions.None, bool allowDelete = false, bool writeAttributes = false)
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
            ? OpenWindowsTemporaryHandle(path, openMode, access, share, options, allowDelete, writeAttributes)
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
        FileOptions options, bool allowDelete, bool writeAttributes)
    {
        const uint read = 0x80000000, write = 0x40000000;
        const uint openReparsePoint = 0x00200000, anonymousSecurity = 0x00100000;
        uint rights = access == FileAccess.Read ? read : read | write;
        if (allowDelete) rights |= 0x00010000; // DELETE
        if (writeAttributes) rights |= 0x00000100; // FILE_WRITE_ATTRIBUTES (not data-write access)
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
        string message = new Win32Exception(code).Message + $" (Windows error {code}).";
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

    internal static bool TryDeleteTemporaryFile(string path, out string error)
    {
        error = "";
        try
        {
            using SafeFileHandle file = OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, allowDelete: true);
            DeleteOpenTemporaryFile(file, path);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryDeleteTemporaryFile(string path, string? expectedIdentity, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            error = "Stable temporary file identity is not supported on this platform.";
            return false;
        }
        if (expectedIdentity is not { Length: 64 } || !expectedIdentity.All(Uri.IsHexDigit))
        {
            error = "The temporary file has no valid saved identity.";
            return false;
        }

        try
        {
            using SafeFileHandle file = OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, allowDelete: true);
            if (!string.Equals(expectedIdentity, TemporaryFileIdentity(file), StringComparison.Ordinal))
            {
                error = "The temporary path now contains a different file.";
                return false;
            }

            DeleteOpenTemporaryFile(file, path);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryMoveTemporaryFile(string sourcePath, string targetPath, string? expectedSourceIdentity, out string error)
        => TryMoveTemporaryFile(sourcePath, targetPath, expectedSourceIdentity, out error, duringMove: null);

    internal static bool TryPublishTemporaryFile(string sourcePath, string targetPath, string? expectedSourceIdentity, out string error)
        => TryMoveTemporaryFile(sourcePath, targetPath, expectedSourceIdentity, out error, duringMove: null, publish: true);

    internal static bool TryMoveTemporaryFile(string sourcePath, string targetPath, string? expectedSourceIdentity,
        out string error, Action? duringMove, bool publish = false)
    {
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            error = "Handle-bound temporary file moves are not supported on this platform.";
            return false;
        }
        if (expectedSourceIdentity is not { Length: 64 } || !expectedSourceIdentity.All(Uri.IsHexDigit))
        {
            error = "The temporary file has no valid saved identity.";
            return false;
        }
        if (!TryGetFullPath(sourcePath, out string source, out error)
            || !TryGetFullPath(targetPath, out string target, out error)) return false;

        string? sourceDirectory = Path.GetDirectoryName(source);
        string? targetDirectory = Path.GetDirectoryName(target);
        string sourceName = Path.GetFileName(source);
        string targetName = Path.GetFileName(target);
        bool sameDirectory = string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase);
        if (sourceDirectory is null || targetDirectory is null
            || (!sameDirectory && (!publish || !PartialFile.IsStagingFolder(sourceDirectory, targetDirectory)))
            || sourceName.Length == 0 || targetName.Length == 0
            || sourceName.Contains(':') || targetName.Contains(':'))
        {
            error = "The temporary file cannot be moved between these folders.";
            return false;
        }
        if (sameDirectory && string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            error = "The temporary source and target names are the same.";
            return false;
        }

        try
        {
            using SafeFileHandle directory = OpenTemporaryDirectoryHandle(sourceDirectory);
            using SafeFileHandle? publishDirectory = sameDirectory ? null : OpenTemporaryDirectoryHandle(targetDirectory);
            using SafeFileHandle sourceFile = OpenTemporaryHandle(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, allowDelete: true, writeAttributes: true);
            if (!string.Equals(expectedSourceIdentity, TemporaryFileIdentity(sourceFile), StringComparison.Ordinal))
            {
                error = "The temporary source path now contains a different file.";
                return false;
            }

            // Recheck the folder after the caller's last async work and before renaming.
            duringMove?.Invoke();
            if (!TryCheckUnlinkedDirectory(sourceDirectory, out error))
            {
                error = "The temporary folder path changed before the file could be moved.";
                return false;
            }

            // Keep the full saved identity until the handle-bound rename begins.
            RequireTemporaryIdentity(sourceFile, expectedSourceIdentity);
            string nativeTarget = target.StartsWith(@"\\?\") ? target
                : target.StartsWith(@"\\") ? @"\\?\UNC\" + target[2..] : @"\\?\" + target;
            byte[] name = Encoding.Unicode.GetBytes(nativeTarget);
            int nameOffset = Marshal.OffsetOf<FileRenameInfoPrefix>(nameof(FileRenameInfoPrefix.FileName)).ToInt32();
            int bufferSize = checked(Marshal.SizeOf<FileRenameInfoPrefix>() + name.Length);
            IntPtr info = Marshal.AllocHGlobal(bufferSize);
            try
            {
                for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(info, i, 0);
                int rootOffset = Marshal.OffsetOf<FileRenameInfoPrefix>(nameof(FileRenameInfoPrefix.RootDirectory)).ToInt32();
                int lengthOffset = Marshal.OffsetOf<FileRenameInfoPrefix>(nameof(FileRenameInfoPrefix.FileNameLength)).ToInt32();
                Marshal.WriteByte(info, 0, 0); // ReplaceIfExists = false
                Marshal.WriteInt32(info, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
                Marshal.WriteIntPtr(info, rootOffset, IntPtr.Zero);
                if (!SetFileInformationByHandle(sourceFile, fileRenameInfo, info, bufferSize))
                    throw FileError(Marshal.GetLastPInvokeError(), target);
            }
            finally { Marshal.FreeHGlobal(info); }

            // NTFS tunneling can inherit the vacated target name's creation time.
            // The original, pinned handle proves which object was renamed. Restore
            // only its saved timestamp, never accept a different volume/file ID.
            RestoreMovedTemporaryIdentity(sourceFile, expectedSourceIdentity);

            using SafeFileHandle movedFile = OpenTemporaryHandle(target, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (!string.Equals(expectedSourceIdentity, TemporaryFileIdentity(movedFile), StringComparison.Ordinal))
            {
                error = "The temporary target path now contains a different file.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static SafeFileHandle OpenTemporaryDirectoryHandle(string path)
    {
        if (!TryCheckUnlinkedDirectory(path, out string error)) throw new IOException(error);
        const uint fileTraverse = 0x00000020, fileReadAttributes = 0x00000080;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000, openNoRecall = 0x00100000;
        string nativePath = path.StartsWith(@"\\?\") ? path
            : path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        SafeFileHandle handle = CreateFileW(nativePath, fileTraverse | fileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero, openExisting,
            openReparsePoint | backupSemantics | openNoRecall, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw FileError(code, path);
        }

        try
        {
            if (!TryCheckUnlinkedDirectory(path, out error)
                || !GetFileInformationByHandle(handle, out TemporaryFileInfo info)
                || (info.Attributes & FileAttributes.Directory) == 0
                || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || IsCloudPlaceholder(info.Attributes))
                throw new IOException(error.Length > 0 ? error : "The temporary folder is not a plain directory.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private const int fileRenameInfo = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfoPrefix
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte data, int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr data, int size);

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

    internal static bool SameFileId(string? expected, string? actual) =>
        expected is { Length: 64 } && actual is { Length: 64 }
        && expected.AsSpan(0, 48).SequenceEqual(actual.AsSpan(0, 48));

    // Only for a proven rename on a pinned handle, or crash recovery after
    // independently verifying the recorded file ID, exact size and SHA-256.
    internal static void RestoreMovedTemporaryIdentity(SafeFileHandle handle, string expected)
    {
        string? actual = TemporaryFileIdentity(handle);
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        if (!SameFileId(expected, actual)
            || !long.TryParse(expected.AsSpan(48), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out long ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new IOException("The moved temporary file no longer has its saved identity.");
        File.SetCreationTimeUtc(handle, new DateTime(ticks, DateTimeKind.Utc));
        RequireTemporaryIdentity(handle, expected);
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
