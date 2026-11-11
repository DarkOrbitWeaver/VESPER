using System.Diagnostics;

namespace Vesper.Engine;

/// <summary>
/// Runtime state of one running job, and the single place where received bytes become durable.
///
/// Checkpoint order (never reordered):
///   1. a chunk is written to the partial file, THEN reported as "pending";
///   2. the partial file is flushed to stable storage;
///   3. the database commit records the extents (this is the durability point);
///   4. only afterwards is the in-memory committed state advanced and the recovery manifest rewritten.
/// A byte is therefore never counted as committed unless it was already on disk when the commit happened.
/// </summary>
internal sealed class JobSession : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly ExtentSet _pending = new();
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private string? _pendingMediaState;
    private long _writtenSinceCheckpoint;
    private long _lastCheckpointTicks = Stopwatch.GetTimestamp();
    private long _runBytes;
    private int _connections;

    // speed meter (touched only by the progress pump)
    private long _sampleBytes;
    private long _sampleTicks = Stopwatch.GetTimestamp();
    private double _speed;

    public DownloadJob Job { get; }
    public CancellationTokenSource Cts { get; } = new();
    public RateLimiter Limiter { get; } = new();
    public object Gate { get; } = new();
    public PartialFile? File { get; private set; }
    public volatile StopIntent Intent;
    public long CommittedAtStart { get; }
    public Task? RunTask { get; set; }
    public string Host { get; }

    public JobSession(DownloadEngine engine, DownloadJob job)
    {
        _engine = engine;
        Job = job;
        CommittedAtStart = job.Extents.TotalBytes;
        Host = Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? uri) ? uri.Host.ToLowerInvariant() : "";
        Limiter.SetRate(job.SpeedLimitBytes);
    }

    public int Connections => Volatile.Read(ref _connections);
    public void AddConnection() => Interlocked.Increment(ref _connections);
    public void RemoveConnection() => Interlocked.Decrement(ref _connections);
    public long RunBytes => Interlocked.Read(ref _runBytes);
    public double SpeedBytesPerSecond => _speed;

    public long PendingBytes { get { lock (Gate) return _pending.TotalBytes; } }

    public PartialFile OpenFile() => File ??= PartialFile.Open(Job.PartialPath);

    public void CloseFile()
    {
        File?.Dispose();
        File = null;
    }

    /// <summary>Writes a chunk; with <paramref name="report"/> it also becomes eligible for the next checkpoint.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, bool report, CancellationToken ct)
    {
        await File!.WriteAsync(data, offset, ct).ConfigureAwait(false);
        Interlocked.Add(ref _runBytes, data.Length);
        _engine.Failpoint("write.afterWrite");
        if (report) Report(new ByteRange(offset, offset + data.Length));

        if (report && (Volatile.Read(ref _writtenSinceCheckpoint) >= _engine.Options.CheckpointBytes
            || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCheckpointTicks)) >= _engine.Options.CheckpointInterval))
            await CheckpointAsync(force: false, ct).ConfigureAwait(false);
    }

    public void SetPhase(JobState state) => _engine.SetPhase(Job, state);

    public async ValueTask MaybeCheckpointAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _writtenSinceCheckpoint) >= _engine.Options.CheckpointBytes
            || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCheckpointTicks)) >= _engine.Options.CheckpointInterval)
            await CheckpointAsync(force: false, ct).ConfigureAwait(false);
    }

    /// <summary>Marks a fully WRITTEN, already validated range as pending durability.</summary>
    public void Report(ByteRange range, string? mediaState = null)
    {
        lock (Gate)
        {
            _pending.Add(range);
            _writtenSinceCheckpoint += range.Length;
            if (mediaState is not null) _pendingMediaState = mediaState;
        }
    }

    public async Task CheckpointAsync(bool force, CancellationToken ct = default)
    {
        if (force) await _checkpointLock.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _checkpointLock.WaitAsync(0, ct).ConfigureAwait(false)) return;   // someone else is already doing it
        try
        {
            ByteRange[] batch;
            string? media;
            lock (Gate)
            {
                if (_pending.IsEmpty) { _writtenSinceCheckpoint = 0; return; }
                batch = [.. _pending.Ranges];
                media = _pendingMediaState;
            }

            _engine.Failpoint("checkpoint.beforeFlush");
            File!.Flush();                                  // covers every write that completed before the snapshot
            _engine.Failpoint("checkpoint.afterFlush");

            ExtentSet next = Job.Extents.Clone();
            foreach (ByteRange r in batch) next.Add(r);
            DownloadJob committed = _engine.CommitCheckpoint(Job, next, media);   // throws if the commit failed
            _engine.Failpoint("checkpoint.afterCommit");

            lock (Gate)
            {
                Job.Extents = next;
                Job.Generation = committed.Generation;
                Job.MediaState = committed.MediaState;
                foreach (ByteRange r in batch) _pending.Remove(r);
                _writtenSinceCheckpoint = _pending.TotalBytes;
            }
            Volatile.Write(ref _lastCheckpointTicks, Stopwatch.GetTimestamp());
            _engine.WriteManifest(committed);
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>Called by the progress pump only. Exponentially smoothed so the display does not jump.</summary>
    public void Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (double)(now - _sampleTicks) / Stopwatch.Frequency;
        if (seconds < 0.2) return;
        long bytes = RunBytes;
        double instant = (bytes - _sampleBytes) / seconds;
        _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
        _sampleBytes = bytes;
        _sampleTicks = now;
    }

    public void Dispose()
    {
        CloseFile();
        Cts.Dispose();
        _checkpointLock.Dispose();
    }
}
