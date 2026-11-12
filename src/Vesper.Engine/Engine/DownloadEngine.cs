using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Microsoft.Data.Sqlite;

namespace Vesper.Engine;

/// <summary>
/// The coordinator. It is the only writer of the <see cref="JobStore"/> and the only owner of durable job
/// state; the UI and the browser bridge ask it to do things and receive immutable snapshots back.
/// Locking: <c>_lock</c> guards the job tables and every store write; a session's own gate guards its
/// pending extents. Code holding a session gate never takes <c>_lock</c>.
/// </summary>
public sealed partial class DownloadEngine : IDisposable
{
    public EngineOptions Options { get; }
    internal RateLimiter GlobalLimiter { get; } = new();

    private readonly JobStore _store;
    private readonly HttpTransport _http;
    private readonly RetryPolicy _retry;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DownloadJob> _jobs = [];
    private readonly Dictionary<Guid, JobSession> _sessions = [];
    private readonly Dictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, RequestCredentials> _credentials = [];
    private readonly HashSet<Guid> _forced = [];
    private readonly HashSet<Guid> _missing = [];
    private readonly ConcurrentQueue<JobSnapshot> _outbox = new();
    private Timer? _wakeTimer;
    private Timer? _pumpTimer;
    private volatile bool _crashed;
    private bool _disposed;
    private bool _wasActive;

    /// <summary>Raised after any durable or visible change of one job (add, state change, rename…).</summary>
    public event Action<JobSnapshot>? JobChanged;
    public event Action<Guid>? JobRemoved;
    /// <summary>Raised roughly 5 times a second, and only while at least one job is running.</summary>
    public event Action<IReadOnlyList<JobSnapshot>>? Progress;
    /// <summary>True when any job is running. Used for sleep prevention and the tray icon.</summary>
    public event Action<bool>? ActivityChanged;

    private DownloadEngine(EngineOptions options)
    {
        Options = options;
        Directory.CreateDirectory(options.DataDirectory);
        JobStore store = JobStore.Open(Path.Combine(options.DataDirectory, "vesper.db"));
        HttpTransport? http = null;
        try
        {
            http = new HttpTransport(options);
            _store = store;
            _http = http;
            _retry = new RetryPolicy(options.Retry);
            GlobalLimiter.SetRate(options.GlobalLimitBytesPerSecond);
            foreach (DownloadJob job in store.LoadAll()) _jobs[job.Id] = job;
        }
        catch
        {
            http?.Dispose();
            store.Dispose();
            throw;
        }
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    /// <summary>Opens the store, reconciles state left by a previous run, and starts what is queued.</summary>
    public static DownloadEngine Start(EngineOptions options)
    {
        var engine = new DownloadEngine(options);
        engine.Recover();
        engine.ScheduleNext();
        _ = Task.Run(engine.RefreshAvailability);
        return engine;
    }

    public bool RecoveredFromCorruptDatabase => _store.RecoveredFromCorruption;
    public string? CorruptDatabaseBackup => _store.CorruptBackupPath;

    // ---- snapshots ---------------------------------------------------------------------------------------------

    public IReadOnlyList<JobSnapshot> Snapshots()
    {
        lock (_lock) return [.. _jobs.Values.OrderBy(j => j.QueueOrder).Select(SnapshotLocked)];
    }

    public JobSnapshot? Snapshot(Guid id)
    {
        lock (_lock) return _jobs.TryGetValue(id, out DownloadJob? job) ? SnapshotLocked(job) : null;
    }

    private JobSnapshot SnapshotLocked(DownloadJob job)
    {
        _sessions.TryGetValue(job.Id, out JobSession? session);
        long committed;
        long progress;
        if (session is not null)
        {
            lock (session.Gate)
            {
                committed = job.Extents.TotalBytes;
                progress = committed + session.PendingBytes;
            }
        }
        else
        {
            committed = job.Extents.TotalBytes;
            progress = job.State == JobState.Completed ? job.TotalBytes ?? committed : committed;
        }

        bool downloading = job.State == JobState.Downloading;
        TransferEstimate? estimate = downloading ? session?.Estimate : null;

        return new JobSnapshot
        {
            Id = job.Id, Kind = job.Kind, FileName = job.FileName, SourceUrl = job.SourceUrl, Destination = job.DestinationFolder,
            OriginalPageUrl = job.OriginalPageUrl, MediaProvider = job.MediaProvider, MediaId = job.MediaId,
            MediaQuality = job.MediaQuality, MediaFormatId = job.MediaFormatId, MediaThumbnailUrl = job.MediaThumbnailUrl, MediaExpiresAt = job.MediaExpiresAt,
            PartialPath = job.PartialPath, FinalPath = job.FinalPath,
            // Completion is durable already, but actions must not race the runner's final cleanup.
            State = job.State == JobState.Completed && session is not null ? JobState.Finalizing : job.State,
            Reason = job.Reason, Message = job.Message,
            TotalBytes = job.TotalBytes, CommittedBytes = committed, ProgressBytes = progress,
            SpeedBytesPerSecond = downloading && estimate?.WaitingForData != true ? session?.SpeedBytesPerSecond ?? 0 : 0,
            Eta = estimate?.Remaining, EstimatingEta = estimate?.Estimating == true, WaitingForData = estimate?.WaitingForData == true,
            Connections = session?.Connections ?? 0,
            Priority = job.Priority, SpeedLimitBytes = job.SpeedLimitBytes, QueueOrder = job.QueueOrder,
            CreatedUtc = job.CreatedUtc, CompletedUtc = job.CompletedUtc, NextRetryUtc = job.NextRetryUtc, RetryCount = job.RetryCount,
            FileMissing = _missing.Contains(job.Id), Identity = job.Identity, AcceptsRanges = job.AcceptsRanges,
            ContentType = job.ContentType, Category = job.Category, Rescue = job.RescueMode,
            HasExpectedHash = job.ExpectedSha256 is not null, HasPieceMap = job.PieceMapJson is not null, Sha256Verified = job.Sha256Verified
        };
    }

    private void Changed(DownloadJob job) => _outbox.Enqueue(SnapshotLocked(job));

    /// <summary>Raises queued change events outside every lock so handlers may call back into the engine.</summary>
    private void FlushOutbox()
    {
        while (_outbox.TryDequeue(out JobSnapshot? snapshot)) JobChanged?.Invoke(snapshot);
        bool active;
        lock (_lock) active = _sessions.Count > 0;
        if (active != _wasActive) { _wasActive = active; ActivityChanged?.Invoke(active); }
    }

    // ---- internal services used by runners ------------------------------------------------------------------------

    internal void Failpoint(string name)
    {
        if (_crashed) throw new FailpointCrash("(after crash) " + name);
        Options.Failpoint?.Invoke(name);
    }

    internal SemaphoreSlim HostGate(string host)
    {
        lock (_lock)
        {
            if (!_hostGates.TryGetValue(host, out SemaphoreSlim? gate))
                _hostGates[host] = gate = new SemaphoreSlim(Math.Max(1, Options.MaxConnectionsPerHost));
            return gate;
        }
    }

    internal RequestCredentials? CredentialsFor(Guid id)
    {
        lock (_lock) return _credentials.GetValueOrDefault(id);
    }

    internal void Log(Guid? jobId, string kind, string detail)
    {
        try { _store.LogEvent(jobId, kind, Diagnostics.Scrub(detail)); }
        catch (SqliteException) { /* logging must never take a download down */ }
    }

    internal void UpdateJob(DownloadJob job, Action<DownloadJob> mutate)
    {
        lock (_lock)
        {
            mutate(job);
            SaveLocked(job);
            Changed(job);
        }
        FlushOutbox();
    }

    internal void SetPhase(DownloadJob job, JobState state) => UpdateJob(job, j => j.State = state);

    /// <summary>The durability point of a checkpoint. Nothing in memory has changed if this throws.</summary>
    internal DownloadJob CommitCheckpoint(DownloadJob job, ExtentSet next, string? mediaState)
    {
        lock (_lock)
        {
            DownloadJob copy = job.CloneForCommit(next, mediaState ?? job.MediaState);
            SaveLocked(copy);
            return copy;
        }
    }

    internal void WriteManifest(DownloadJob committed)
    {
        try { RecoveryManifest.Write(committed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(committed.Id, "manifest", "Recovery file could not be written: " + ex.Message);   // auxiliary: never fatal
        }
    }

    private void SaveLocked(DownloadJob job, string? submissionId = null)
    {
        if (_crashed) throw new FailpointCrash("(after crash) save");
        try { _store.Save(job, submissionId); }
        catch (SqliteException ex) { throw new LocalStorageException("The download database could not be written: " + ex.Message, ex); }
    }

    // ---- scheduling ---------------------------------------------------------------------------------------------

    /// <summary>Starts as many queued jobs as the limits allow. Safe to call at any time, from any thread.</summary>
    internal void ScheduleNext()
    {
        lock (_lock)
        {
            if (_disposed || _crashed) return;

            foreach (Guid id in _forced.ToArray())
            {
                if (_jobs.TryGetValue(id, out DownloadJob? job) && job.State == JobState.Queued && !_sessions.ContainsKey(id)) StartSession(job);
                else if (!_sessions.ContainsKey(id)) _forced.Remove(id);
            }

            if (Options.Schedule.IsOpen(DateTime.Now))
            {
                while (_sessions.Count(s => !_forced.Contains(s.Key)) < Options.MaxConcurrentJobs && PickNext() is { } next)
                    StartSession(next);
            }
            else
            {
                foreach (JobSession session in _sessions.Values.Where(s => !_forced.Contains(s.Job.Id)))
                {
                    session.Intent = StopIntent.Window;
                    session.Cts.Cancel();
                }
            }
            ArmWakeTimer();
        }
        FlushOutbox();
    }

    private DownloadJob? PickNext()
    {
        var activePerHost = _sessions.Values.GroupBy(s => s.Host).ToDictionary(g => g.Key, g => g.Count());
        return _jobs.Values
            .Where(j => j.State == JobState.Queued && !_sessions.ContainsKey(j.Id))
            .Where(j => activePerHost.GetValueOrDefault(HostOf(j)) < Options.MaxJobsPerHost)
            // Highest priority first; then the host with the fewest running jobs (fairness); then queue order.
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => activePerHost.GetValueOrDefault(HostOf(j)))
            .ThenBy(j => j.QueueOrder)
            .FirstOrDefault();
    }

    private static string HostOf(DownloadJob job) =>
        Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? uri) ? uri.Host.ToLowerInvariant() : "";

    private void StartSession(DownloadJob job)
    {
        var session = new JobSession(this, job);
        _sessions[job.Id] = session;
        job.State = JobState.Resolving;
        job.Message = "";
        job.NextRetryUtc = null;
        SaveLocked(job);
        Changed(job);
        EnsurePump();
        session.RunTask = Task.Run(() => RunSessionAsync(session));
    }

    private async Task RunSessionAsync(JobSession session)
    {
        RunOutcome outcome;
        try
        {
            outcome = await new JobRunner(this, session, _http).RunAsync().ConfigureAwait(false);
        }
        catch (FailpointCrash)
        {
            // Simulated process death: persist nothing more, exactly like a real crash.
            lock (_lock) { _crashed = true; _sessions.Remove(session.Job.Id); }
            session.Dispose();
            return;
        }
        catch (Exception ex)
        {
            outcome = RunOutcome.NeedsAttention("unexpected", "Unexpected error: " + ex.Message);
        }
        OnRunFinished(session, outcome);
    }

    private void OnRunFinished(JobSession session, RunOutcome outcome)
    {
        DownloadJob job = session.Job;
        try
        {
            lock (_lock)
            {
                _sessions.Remove(job.Id);
                _forced.Remove(job.Id);
                bool progressed = job.Extents.TotalBytes > session.CommittedAtStart;
                if (session.Intent == StopIntent.BrowserFallback && outcome.State != JobState.Completed)
                    outcome = BrowserFallbackOutcome;
                ApplyOutcome(job, outcome, progressed);
                SaveLocked(job);
                Changed(job);
                Log(job.Id, "run-ended", $"{job.State} {job.Reason} {outcome.Message}");
            }
        }
        catch (FailpointCrash) { _crashed = true; }
        catch (LocalStorageException ex) { Log(job.Id, "save-failed", ex.Message); }
        finally { session.Dispose(); }

        ScheduleNext();
        FlushOutbox();
        StopPumpIfIdle();
    }

    private void ApplyOutcome(DownloadJob job, RunOutcome outcome, bool progressed)
    {
        job.Reason = outcome.Reason;
        job.Message = outcome.Message;
        if (progressed) job.RetryCount = 0;

        switch (outcome.State)
        {
            case JobState.Completed:
                break;   // FinalizeAsync already wrote the Completed state
            case JobState.WaitingForRetry:
                job.RetryCount++;
                if (_retry.ShouldGiveUp(job.RetryCount))
                {
                    job.State = JobState.Failed;
                    job.NextRetryUtc = null;
                    job.Message = $"Stopped after {job.RetryCount} failed attempts. {outcome.Message} Saved data was kept; resume to try again.";
                }
                else
                {
                    TimeSpan delay = _retry.Delay(job.RetryCount - 1, outcome.RetryAfter);
                    job.State = JobState.WaitingForRetry;
                    job.NextRetryUtc = DateTime.UtcNow + delay;
                    job.Message = $"{outcome.Message} Retrying in {Math.Ceiling(delay.TotalSeconds)} s.";
                }
                break;
            case JobState.WaitingForNetwork:
                job.State = JobState.WaitingForNetwork;
                job.NextRetryUtc = DateTime.UtcNow.AddMinutes(2);   // safety net if the network event is missed
                break;
            default:
                job.State = outcome.State;
                job.NextRetryUtc = null;
                break;
        }
    }

    // ---- timers (one wake-up timer, one progress pump; neither runs when there is nothing to do) -----------------------

    private void ArmWakeTimer()
    {
        DateTime? next = _jobs.Values.Where(j => j.State is JobState.WaitingForRetry or JobState.WaitingForNetwork && j.NextRetryUtc is not null)
            .Select(j => j.NextRetryUtc!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        if (next == DateTime.MaxValue) next = null;
        if (Options.Schedule.NextBoundary(DateTime.Now) is { } boundary)
        {
            DateTime boundaryUtc = boundary.ToUniversalTime();
            next = next is null || boundaryUtc < next ? boundaryUtc : next;
        }

        _wakeTimer ??= new Timer(_ => OnWake(), null, Timeout.Infinite, Timeout.Infinite);
        _wakeTimer.Change(next is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(Math.Max(50, (next.Value - DateTime.UtcNow).TotalMilliseconds)),
            Timeout.InfiniteTimeSpan);
    }

    private void OnWake()
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            foreach (DownloadJob job in _jobs.Values.Where(j => j.State is JobState.WaitingForRetry or JobState.WaitingForNetwork
                && j.NextRetryUtc is { } due && due <= now && !_sessions.ContainsKey(j.Id)))
            {
                job.State = JobState.Queued;
                job.NextRetryUtc = null;
                SaveLocked(job);
                Changed(job);
            }
        }
        ScheduleNext();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) WakeWaiting(includeRetry: false);
    }

    /// <summary>Wakes jobs that wait for connectivity (and optionally those in backoff), e.g. after sleep or a network change.</summary>
    public void WakeWaiting(bool includeRetry = true)
    {
        lock (_lock)
        {
            foreach (DownloadJob job in _jobs.Values.Where(j => !_sessions.ContainsKey(j.Id)
                && (j.State == JobState.WaitingForNetwork || (includeRetry && j.State == JobState.WaitingForRetry))))
            {
                job.State = JobState.Queued;
                job.NextRetryUtc = null;
                SaveLocked(job);
                Changed(job);
            }
        }
        ScheduleNext();
    }

    private void EnsurePump()
    {
        _pumpTimer ??= new Timer(_ => PumpTick(), null, Options.ProgressInterval, Options.ProgressInterval);
    }

    private void StopPumpIfIdle()
    {
        lock (_lock)
        {
            if (_sessions.Count > 0 || _pumpTimer is null) return;
            _pumpTimer.Dispose();
            _pumpTimer = null;
        }
    }

    private void PumpTick()
    {
        List<JobSnapshot> batch;
        lock (_lock)
        {
            if (_sessions.Count == 0) return;
            foreach (JobSession session in _sessions.Values) session.Sample();
            batch = [.. _sessions.Values.Select(s => SnapshotLocked(s.Job))];
        }
        Progress?.Invoke(batch);
    }

    // ---- shutdown -------------------------------------------------------------------------------------------------

    /// <summary>Stops running jobs, makes their progress durable, and releases everything. Jobs resume next start.</summary>
    public void Dispose()
    {
        Task[] running;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            running = [.. _sessions.Values.Select(s => { s.Intent = StopIntent.Shutdown; s.Cts.Cancel(); return s.RunTask ?? Task.CompletedTask; })];
        }
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        try { Task.WaitAll(running, TimeSpan.FromSeconds(20)); }
        catch (AggregateException) { /* each run reports its own failure through OnRunFinished */ }

        _wakeTimer?.Dispose();
        _pumpTimer?.Dispose();
        _http.Dispose();
        _store.Dispose();
    }
}
