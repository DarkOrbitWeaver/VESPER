using Microsoft.Win32.SafeHandles;

namespace Vesper.Engine;

/// <summary>A local storage failure (disk full, locked file, permissions). Never treated as a network error.</summary>
public sealed class LocalStorageException(string message, Exception inner) : IOException(message, inner)
{
    public bool IsDiskFull => ErrorClassifier.IsDiskFull(InnerException!);
}

/// <summary>
/// The private staging file. Writes are positional and may arrive out of order from several
/// connections; the coordinator guarantees they never overlap.
/// </summary>
internal sealed class PartialFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    public string Path { get; }

    private PartialFile(string path, SafeFileHandle handle) { Path = path; _handle = handle; }

    public static PartialFile Open(string path)
    {
        try
        {
            // FileShare.Read lets a virus scanner read the file but blocks deletion or truncation by others.
            SafeFileHandle handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            return new PartialFile(path, handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalStorageException("The partial file could not be opened: " + ex.Message, ex);
        }
    }

    public long Length => RandomAccess.GetLength(_handle);

    /// <summary>Reserves the final size up front so a full disk is discovered before hours of transfer.</summary>
    public void EnsureLength(long length)
    {
        try { if (Length < length) RandomAccess.SetLength(_handle, length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("Could not reserve disk space: " + ex.Message, ex); }
    }

    public void Truncate(long length)
    {
        try { RandomAccess.SetLength(_handle, length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("Could not resize the partial file: " + ex.Message, ex); }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        try { await RandomAccess.WriteAsync(_handle, data, offset, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("Writing to disk failed: " + ex.Message, ex); }
    }

    public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(_handle, buffer, offset);

    /// <summary>Everything whose write has completed before this call is on stable storage afterwards.</summary>
    public void Flush()
    {
        try { RandomAccess.FlushToDisk(_handle); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("Flushing to disk failed: " + ex.Message, ex); }
    }

    public void Dispose() => _handle.Dispose();
}
