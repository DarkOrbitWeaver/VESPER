using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Bounded browser-byte transport. Receives only a user-selected complete file, not an HTTP replay or a Chrome
/// download record. Each chunk is verified before its acknowledgement and written exactly once, straight into the
/// destination's staging partial (preallocated, so a full disk fails at the start rather than at 90 %).
/// <para>Crash consistency: a journal in the data directory names every partial before it is created. "receiving"
/// entries are removed at startup only with proof that no job owns the same file; "committing" entries may be owned, so
/// their files are kept. An incomplete transfer never becomes a published file or an engine job.</para>
/// </summary>
internal sealed class BrowserBodyReceiver : IDisposable
{
    internal const int ChunkBytes = 512 * 1024;
    internal const long MaxBytes = 16L * 1024 * 1024 * 1024;
    internal const string JournalFileName = "browser-inbox.json";
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
    private readonly string _journal;
    private readonly Func<string, long, string?> _destination;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Transfer> _active = new(StringComparer.Ordinal);
    private bool _disposed;
    private Timer? _expiry;

    /// <param name="destination">Resolves (file name, length) to a ready destination folder with enough free space, or null.</param>
    internal BrowserBodyReceiver(string dataDirectory, Func<string, long, string?> destination)
    {
        _journal = Path.Combine(Path.GetFullPath(dataDirectory), JournalFileName);
        _destination = destination;
    }

    internal sealed record Reply(bool Ok, string Code, long Offset = 0, Guid? JobId = null);

    private sealed record JournalEntry(string Id, string Path, string State, string? Identity = null);

    internal sealed class Transfer(string origin, string name, long length, string folder, PartialFile file, string? identity, bool verifyMp4) : IDisposable
    {
        internal string Origin { get; } = origin;
        internal string Name { get; } = name;
        internal long Length { get; } = length;
        internal bool VerifyMp4 { get; } = verifyMp4;
        internal string Folder { get; } = folder;
        internal PartialFile File { get; } = file;
        internal string? Identity { get; } = identity;
        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Offset { get; set; }
        internal int LastLength { get; set; }
        internal string? LastHash { get; set; }
        internal DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>Set by the engine once durable state may own the partial. A claimed partial is never deleted here.</summary>
        internal bool Claimed { get; set; }
        public void Dispose() { try { File.Dispose(); } finally { Hash.Dispose(); } }
    }

    private void Remove(string id)
    {
        if (_active.Remove(id, out Transfer? transfer))
        {
            try
            {
                if (!transfer.Claimed) transfer.File.DeleteUnclaimedNewFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The journal entry is kept below. */ }
            finally
            {
                try { transfer.Dispose(); }
                catch (IOException) { }
            }
            if (transfer.Claimed || !System.IO.File.Exists(transfer.File.Path)) ForgetJournal(id);
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
                    TimeSpan remaining = _active.Values.Min(entry => entry.LastActivity) + Idle - DateTimeOffset.UtcNow;
                    _expiry!.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
                }
            }
            finally { _gate.Release(); }
        });
        _expiry.Change(Idle, Timeout.InfiniteTimeSpan);
    }

    internal async Task<Reply> BeginAsync(string id, string origin, string name, long length, CancellationToken ct,
        bool exclusiveSession = false, bool verifyMp4 = false)
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
                if (existing.Origin != origin || existing.Name != name || existing.Length != length || existing.VerifyMp4 != verifyMp4)
                    return new(false, "conflict");
                Renew(existing);
                return new(true, "ok", existing.Offset);
            }
            // One stream at a time limits memory, handles and disk usage even for hostile input.
            if (_active.Count != 0) return new(false, "busy");
            if (_destination(name, length) is not { } folder) return new(false, "storage");
            string staging = PartialFile.PrepareStagingFolder(folder);
            string path = Path.Combine(staging, name + "." + Guid.NewGuid().ToString("N")[..8] + ".vpart");
            // Journal first: a crash after the file exists still leaves a record that names it.
            RememberJournal(new JournalEntry(id, path, "receiving"));
            PartialFile file;
            try { file = PartialFile.Open(path, FileMode.CreateNew, allowDelete: true); }
            catch { ForgetJournal(id); throw; }
            string? identity;
            try
            {
                identity = file.CaptureIdentity();
                if (OperatingSystem.IsWindows() && identity is null)
                    throw new IOException("Cannot establish the browser file identity.");
                // Record the actual file identity before its path becomes eligible for crash cleanup.
                RememberJournal(new JournalEntry(id, path, "receiving", identity));
                file.EnsureLength(length);
            }
            catch
            {
                try { file.DeleteUnclaimedNewFile(); } finally { file.Dispose(); }
                ForgetJournal(id);
                throw;
            }
            var transfer = new Transfer(origin, name, length, folder, file, identity, verifyMp4);
            _active.Add(id, transfer);
            Renew(transfer);
            return new(true, "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
                await transfer.File.WriteAsync(bytes, offset, ct).ConfigureAwait(false);
                transfer.Hash.AppendData(bytes);
                transfer.Offset += bytes.Length;
                transfer.LastLength = bytes.Length;
                transfer.LastHash = digest;
                Renew(transfer);
                return new(true, "ok", transfer.Offset);
            }
            catch
            {
                // A write can be partial on cancellation or disk failure. Never guess its extent or resume this inbox.
                Remove(id);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    /// <param name="accept">Creates the engine job over the finished partial and sets <see cref="Transfer.Claimed"/>.</param>
    internal async Task<Reply> FinishAsync(string id, Func<Transfer, string, CancellationToken, Task<AddResult>> accept,
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
                transfer.File.Flush();
                if (transfer.VerifyMp4)
                {
                    try { MediaTrackProbe.RequireMp4Media(transfer.File, ct); }
                    catch (MediaContainerException) { return new(false, "invalid-media"); }
                }
                string digest = Convert.ToHexString(transfer.Hash.GetHashAndReset());
                RememberJournal(new JournalEntry(id, transfer.File.Path, "committing", transfer.Identity));
                AddResult result = await accept(transfer, digest, ct).ConfigureAwait(false);
                return result.Ok ? new(true, "accepted", transfer.Offset, result.JobId) : new(false, "rejected");
            }
            finally { Remove(id); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task AbortAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { Remove(id); }
        finally { _gate.Release(); }
    }

    // journal

    private List<JournalEntry> ReadJournal() => ReadJournal(_journal);

    private static List<JournalEntry> ReadJournal(string path)
    {
        if (!System.IO.File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<List<JournalEntry>>(System.IO.File.ReadAllText(path)) ?? [];
        return entries.Where(entry => entry is { Id.Length: > 0, Path.Length: > 0, State: "receiving" or "committing" }).ToList();
    }

    private void WriteJournal(List<JournalEntry> entries)
    {
        if (entries.Count == 0)
        {
            System.IO.File.Delete(_journal);
            return;
        }
        string temporary = _journal + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entries);
            stream.Flush(flushToDisk: true);
        }
        System.IO.File.Move(temporary, _journal, overwrite: true);
    }

    private void RememberJournal(JournalEntry entry)
    {
        List<JournalEntry> entries = ReadJournal();
        entries.RemoveAll(existing => existing.Id == entry.Id);
        entries.Add(entry);
        WriteJournal(entries);
    }

    private void ForgetJournal(string id)
    {
        try
        {
            List<JournalEntry> entries = ReadJournal();
            if (entries.RemoveAll(existing => existing.Id == id) > 0) WriteJournal(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Startup cleanup. Runs before any browser copy can begin.</summary>
    internal static void RecoverJournal(string dataDirectory, IReadOnlySet<string> ownedPaths, bool databaseRecovered)
    {
        string journal = Path.Combine(Path.GetFullPath(dataDirectory), JournalFileName);
        try
        {
            foreach (JournalEntry entry in ReadJournal(journal))
                if (!databaseRecovered && entry.State == "receiving") DeleteOrphan(entry, ownedPaths);
            System.IO.File.Delete(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    // Never delete a file based on its name alone, or if a job or recovery manifest may own it.
    private static void DeleteOrphan(JournalEntry entry, IReadOnlySet<string> ownedPaths)
    {
        string full = Path.GetFullPath(entry.Path);
        if (!full.EndsWith(".vpart", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), PartialFile.StagingFolderName, StringComparison.OrdinalIgnoreCase))
            return;
        string manifest = RecoveryManifest.PathFor(full);
        if (ownedPaths.Contains(full) || System.IO.File.Exists(manifest)
            || System.IO.File.Exists(manifest + ".previous") || System.IO.File.Exists(manifest + ".tmp")) return;
        _ = PathSafety.TryDeleteTemporaryFile(full, entry.Identity, out _);
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
