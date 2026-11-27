using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Bounded, crash-journaled browser-byte receiver supporting:
/// 1. Known-length preallocated browser copies (blob:, data:, HTTP files),
/// 2. Unknown-length browser transfers with bounded staging,
/// 3. Multi-track (video + audio) browser-body transfers routed directly into VESPER's native lossless remuxer,
/// 4. Up to <see cref="MaxConcurrentTransfers"/> concurrent streams without head-of-line blocking.
/// </summary>
internal sealed class BrowserBodyReceiver : IDisposable
{
    internal const int ChunkBytes = 512 * 1024;
    internal const long MaxBytes = 16L * 1024 * 1024 * 1024;
    internal const int MaxConcurrentTransfers = 4;
    internal const long StreamingMinReserve = 64L * 1024 * 1024;
    internal const string JournalFileName = "browser-inbox.json";
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
    private readonly string _journal;
    private readonly string _partialsFolder;
    private readonly Func<string, long, string?> _destination;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Transfer> _active = new(StringComparer.Ordinal);
    private bool _disposed;
    private Timer? _expiry;

    internal BrowserBodyReceiver(string dataDirectory, Func<string, long, string?> destination)
    {
        _journal = Path.Combine(Path.GetFullPath(dataDirectory), JournalFileName);
        _partialsFolder = PartialFile.PrivateFolder(dataDirectory);
        _destination = destination;
    }

    internal sealed record Reply(bool Ok, string Code, long Offset = 0, Guid? JobId = null);

    private sealed record JournalEntry(string Id, string Path, string State, string? Identity = null);

    internal sealed class Transfer(
        Guid jobId,
        string origin,
        string name,
        long? expectedLength,
        string folder,
        PartialFile file,
        string? identity,
        bool verifyMp4,
        IReadOnlyList<BrowserBodyTrackSpec>? tracks = null,
        string? pageUrl = null,
        string? outputContainer = null) : IDisposable
    {
        internal Guid JobId { get; } = jobId;
        internal string Origin { get; } = origin;
        internal string? PageUrl { get; } = pageUrl;
        internal string Name { get; } = name;
        internal long? ExpectedLength { get; } = expectedLength;
        internal bool VerifyMp4 { get; } = verifyMp4;
        internal string Folder { get; } = folder;
        internal PartialFile File { get; } = file;
        internal string? Identity { get; } = identity;
        internal IReadOnlyList<BrowserBodyTrackSpec>? Tracks { get; } = tracks;
        internal string? OutputContainer { get; } = outputContainer;
        internal long[] TrackLengths { get; } = new long[Math.Max(1, tracks?.Count ?? 1)];
        internal int CurrentTrackIndex { get; set; }
        internal IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Offset { get; set; }
        internal int LastLength { get; set; }
        internal string? LastHash { get; set; }
        internal DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    internal async Task<Reply> BeginAsync(
        string id,
        string origin,
        string name,
        long? expectedLength,
        CancellationToken ct,
        bool exclusiveSession = false,
        bool verifyMp4 = false,
        IReadOnlyList<BrowserBodyTrackSpec>? tracks = null,
        string? pageUrl = null,
        string? outputContainer = null)
    {
        if (!BridgeProtocol.IsRequestId(id) || origin.Length > 512 || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsoluteUri != uri.GetLeftPart(UriPartial.Authority) + "/"
            || expectedLength is < 1 or > MaxBytes || name.Length is < 1 or > 255)
            return new(false, "invalid");
        name = FileNames.Sanitize(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "closed");
            Prune();
            if (_active.TryGetValue(id, out Transfer? existing))
            {
                if (exclusiveSession) return new(false, "busy");
                if (existing.Origin != origin || existing.Name != name || existing.ExpectedLength != expectedLength || existing.VerifyMp4 != verifyMp4
                    || existing.PageUrl != pageUrl || existing.OutputContainer != outputContainer
                    || !(existing.Tracks ?? []).SequenceEqual(tracks ?? []))
                    return new(false, "conflict");
                Renew(existing);
                return new(true, "ok", existing.Offset);
            }
            if (_active.Count >= MaxConcurrentTransfers) return new(false, "busy");
            long spaceNeeded = expectedLength ?? StreamingMinReserve;
            if (tracks is { Count: 2 }) spaceNeeded = spaceNeeded * 2; // reserve staging + remux headroom
            if (_destination(name, spaceNeeded) is not { } folder) return new(false, "storage");
            PartialFile.PreparePrivateFolder(_partialsFolder);
            Guid jobId = Guid.NewGuid();
            string path = Path.Combine(_partialsFolder, name + "." + jobId.ToString("N")[..8] + ".vpart");
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
                RememberJournal(new JournalEntry(id, path, "receiving", identity));
                if (expectedLength is long knownLength) file.EnsureLength(knownLength);
            }
            catch
            {
                try { file.DeleteUnclaimedNewFile(); } finally { file.Dispose(); }
                ForgetJournal(id);
                throw;
            }
            var transfer = new Transfer(jobId, origin, name, expectedLength, folder, file, identity, verifyMp4, tracks, pageUrl, outputContainer);
            _active.Add(id, transfer);
            Renew(transfer);
            return new(true, "ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task<Reply> AppendAsync(string id, long offset, byte[] bytes, string digest, CancellationToken ct, int trackIndex = 0)
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
            if (trackIndex == transfer.CurrentTrackIndex && offset == transfer.Offset - transfer.LastLength && bytes.Length == transfer.LastLength
                && digest.Equals(transfer.LastHash, StringComparison.OrdinalIgnoreCase))
            {
                Renew(transfer);
                return new(true, "ok", transfer.Offset);
            }
            int maxTrack = (transfer.Tracks?.Count ?? 1) - 1;
            if (trackIndex < transfer.CurrentTrackIndex || trackIndex > maxTrack || trackIndex > transfer.CurrentTrackIndex + 1)
                return new(false, "invalid-track", transfer.Offset);
            if (trackIndex == 1 && transfer.CurrentTrackIndex == 0
                && (transfer.TrackLengths[0] <= 0 || transfer.Tracks?[0].Length is long firstLength
                    && firstLength != transfer.TrackLengths[0]))
                return new(false, "incomplete-track", transfer.Offset);
            if (transfer.Tracks?[trackIndex].Length is long trackLength
                && bytes.Length > trackLength - transfer.TrackLengths[trackIndex])
                return new(false, "track-length", transfer.Offset);

            long limit = transfer.ExpectedLength ?? MaxBytes;
            if (offset != transfer.Offset || bytes.Length > limit - transfer.Offset)
                return new(false, "offset", transfer.Offset);
            try
            {
                await transfer.File.WriteAsync(bytes, offset, ct).ConfigureAwait(false);
                transfer.Hash.AppendData(bytes);
                transfer.Offset += bytes.Length;
                transfer.CurrentTrackIndex = trackIndex;
                transfer.TrackLengths[trackIndex] += bytes.Length;
                transfer.LastLength = bytes.Length;
                transfer.LastHash = digest;
                Renew(transfer);
                return new(true, "ok", transfer.Offset);
            }
            catch
            {
                Remove(id);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "storage"); }
        finally { _gate.Release(); }
    }

    internal async Task<Reply> FinishAsync(string id, Func<Transfer, string, CancellationToken, Task<AddResult>> accept,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(false, "closed");
            Prune();
            if (!_active.TryGetValue(id, out Transfer? transfer)) return new(false, "expired");
            if (transfer.Offset <= 0 || transfer.ExpectedLength is long expected && transfer.Offset != expected)
                return new(false, "incomplete", transfer.Offset);
            if (transfer.Tracks is { } tracks)
                for (int index = 0; index < tracks.Count; index++)
                    if (transfer.TrackLengths[index] <= 0
                        || tracks[index].Length is long length && length != transfer.TrackLengths[index])
                        return new(false, "incomplete", transfer.Offset);
            try
            {
                if (transfer.File.Length != transfer.Offset) transfer.File.Truncate(transfer.Offset);
                transfer.File.Flush();
                if (transfer.VerifyMp4 && (transfer.Tracks is null || transfer.Tracks.Count <= 1))
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

    internal static void RecoverJournal(string dataDirectory, IReadOnlySet<string> ownedPaths, bool databaseRecovered)
    {
        string journal = Path.Combine(Path.GetFullPath(dataDirectory), JournalFileName);
        string partialsFolder = PartialFile.PrivateFolder(dataDirectory);
        try
        {
            foreach (JournalEntry entry in ReadJournal(journal))
                if (!databaseRecovered && entry.State == "receiving") DeleteOrphan(entry, ownedPaths, partialsFolder);
            System.IO.File.Delete(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private static void DeleteOrphan(JournalEntry entry, IReadOnlySet<string> ownedPaths, string partialsFolder)
    {
        string full = Path.GetFullPath(entry.Path);
        string? folder = Path.GetDirectoryName(full);
        if (!full.EndsWith(".vpart", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(folder, partialsFolder, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(folder), PartialFile.StagingFolderName, StringComparison.OrdinalIgnoreCase)))
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
