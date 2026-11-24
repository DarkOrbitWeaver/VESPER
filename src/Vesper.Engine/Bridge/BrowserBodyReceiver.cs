using System.Security.Cryptography;

namespace Vesper.Engine;

/// <summary>
/// Bounded browser-byte transport. Receives only a user-selected complete file, not
/// an HTTP replay or a Chrome download record. Each chunk is verified before ack.
/// An incomplete transfer never becomes a published file or an engine job.
/// </summary>
internal sealed class BrowserBodyReceiver : IDisposable
{
    internal const int ChunkBytes = 256 * 1024;
    internal const long MaxBytes = 16L * 1024 * 1024 * 1024;
    private readonly string _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Transfer> _active = new(StringComparer.Ordinal);
    private bool _disposed;
    private Timer? _expiry;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    internal BrowserBodyReceiver(string folder) => _folder = Path.GetFullPath(folder);

    internal sealed record Reply(bool Ok, string Code, long Offset = 0, Guid? JobId = null);
    private sealed class Transfer(string origin, string name, long length, FileStream file) : IDisposable
    {
        internal string Origin { get; } = origin;
        internal string Name { get; } = name;
        internal long Length { get; } = length;
        internal FileStream File { get; } = file;
        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Offset { get; set; }
        internal int LastLength { get; set; }
        internal string? LastHash { get; set; }
        internal DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
        public void Dispose() { try { File.Dispose(); } finally { Hash.Dispose(); } }
    }

    private void Remove(string id)
    {
        if (_active.Remove(id, out Transfer? transfer))
        {
            try { transfer.Dispose(); }
            catch (IOException) { /* Delete-on-close still owns the private temporary file. */ }
        }
        if (_active.Count == 0) _expiry?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - Idle;
        foreach (string id in _active.Where(pair => pair.Value.LastActivity < cutoff).Select(pair => pair.Key).ToArray())
            Remove(id);
    }

    private void Renew(Transfer transfer)
    {
        transfer.LastActivity = DateTimeOffset.UtcNow;
        _expiry ??= new Timer(_ =>
        {
            _gate.Wait();
            try
            {
                if (_disposed) return;
                Prune();
                if (_active.Count != 0)
                {
                    TimeSpan remaining = _active.Values.First().LastActivity + Idle - DateTimeOffset.UtcNow;
                    _expiry!.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
                }
            }
            finally { _gate.Release(); }
        });
        _expiry.Change(Idle, Timeout.InfiniteTimeSpan);
    }

    internal async Task<Reply> BeginAsync(string id, string origin, string name, long length, CancellationToken ct,
        bool exclusiveSession = false)
    {
        if (!BridgeProtocol.IsRequestId(id) || origin.Length > 512 || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsoluteUri != uri.GetLeftPart(UriPartial.Authority) + "/"
            || length is < 1 or > MaxBytes || name.Length is < 1 or > 255)
            return new(false, "invalid");
        name = FileNames.Sanitize(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "closed");
            Prune();
            if (_active.TryGetValue(id, out Transfer? existing))
            {
                // A second persistent pipe must not attach to an active inbox:
                // its disconnect cleanup could otherwise abort the first pipe.
                if (exclusiveSession) return new(false, "busy");
                if (existing.Origin != origin || existing.Name != name || existing.Length != length)
                    return new(false, "conflict");
                Renew(existing);
                return new(true, "ok", existing.Offset);
            }
            // One stream is enough for the initial acquisition route. The cap limits
            // memory, file handles and temporary disk usage even for hostile input.
            if (_active.Count != 0) return new(false, "busy");
            if (!PathSafety.TryCheckUnlinkedDirectory(_folder, out _)) return new(false, "storage");
            string path = Path.Combine(_folder, "browser-body-" + Guid.NewGuid().ToString("N") + ".tmp");
            // This file is private to the process; Windows deletes it on close, even
            // if the browser disconnects or the desktop process terminates.
            using var directory = OperatingSystem.IsWindows() ? PathSafety.OpenTemporaryDirectoryHandle(_folder) : null;
            var handle = PathSafety.OpenTemporaryHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose, allowDelete: true);
            FileStream file;
            try { file = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: true); }
            catch { handle.Dispose(); throw; }
            var transfer = new Transfer(origin, name, length, file);
            _active.Add(id, transfer);
            Renew(transfer);
            return new(true, "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task<Reply> AppendAsync(string id, long offset, byte[] bytes, string digest, CancellationToken ct)
    {
        if (offset < 0 || bytes.Length is < 1 or > ChunkBytes || digest.Length != 64
            || !digest.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            return new(false, "chunk-integrity");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "closed");
            Prune();
            if (!_active.TryGetValue(id, out Transfer? transfer)) return new(false, "expired");
            // A lost ACK may retry exactly the last chunk; never append it twice.
            if (offset == transfer.Offset - transfer.LastLength && bytes.Length == transfer.LastLength
                && digest.Equals(transfer.LastHash, StringComparison.OrdinalIgnoreCase))
            {
                Renew(transfer);
                return new(true, "ok", transfer.Offset);
            }
            if (offset != transfer.Offset || bytes.Length > transfer.Length - transfer.Offset)
                return new(false, "offset", transfer.Offset);
            try
            {
                await transfer.File.WriteAsync(bytes, ct).ConfigureAwait(false);
                transfer.Hash.AppendData(bytes);
                transfer.Offset += bytes.Length;
                transfer.LastLength = bytes.Length;
                transfer.LastHash = digest;
                Renew(transfer);
                return new(true, "ok", transfer.Offset);
            }
            catch
            {
                // A write can be partial on cancellation or disk failure. Never
                // guess its extent or resume this volatile inbox.
                Remove(id);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task<Reply> FinishAsync(string id,
        Func<string, string, long, string, Stream, CancellationToken, Task<AddResult>> accept,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "closed");
            Prune();
            if (!_active.TryGetValue(id, out Transfer? transfer)) return new(false, "expired");
            if (transfer.Offset != transfer.Length) return new(false, "incomplete", transfer.Offset);
            try
            {
                await transfer.File.FlushAsync(ct).ConfigureAwait(false);
                transfer.File.Position = 0;
                string digest = Convert.ToHexString(transfer.Hash.GetHashAndReset());
                AddResult result = await accept(transfer.Origin, transfer.Name, transfer.Length, digest, transfer.File, ct)
                    .ConfigureAwait(false);
                return result.Ok ? new(true, "accepted", transfer.Offset, result.JobId) : new(false, "rejected");
            }
            finally { Remove(id); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task AbortAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { Remove(id); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (string id in _active.Keys.ToArray()) Remove(id);
            _expiry?.Dispose();
            _expiry = null;
        }
        finally { _gate.Release(); }
    }
}
