using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

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
    internal string PartialsFolder { get; }
    internal RateLimiter GlobalLimiter { get; } = new();
    private readonly SegmentBufferLimiter _segmentBuffers = new();

    private readonly JobStore _store;
    private readonly HttpTransport _http;
    private readonly RetryPolicy _retry;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _runningTasks = [];
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private readonly Dictionary<Guid, DownloadJob> _jobs = [];
    private readonly MediaDeliveryHistory _mediaDeliveryHistory = new();
    private readonly Dictionary<Guid, JobSession> _sessions = [];
    private readonly Dictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, RequestCredentials> _credentials = [];
    private readonly HashSet<Guid> _forced = [];
    private readonly HashSet<Guid> _missing = [];
    private readonly ConcurrentQueue<JobSnapshot> _outbox = new();
    private readonly object _outboxLock = new();
    private readonly Dictionary<Guid, PendingRunOutcome> _pendingRunOutcomes = [];
    private Timer? _wakeTimer;
    private Timer? _pumpTimer;
    private volatile bool _crashed;
    private bool _disposed;
    private bool _wasActive;
    private readonly Dictionary<DuplicateKey, List<DownloadJob>> _duplicateCandidates = [];

    private readonly record struct DuplicateKey(string Url, MediaOutput Output);
    private sealed record PendingRunOutcome(DownloadJob Job, DownloadJob Expected, RunOutcome Outcome, bool Progressed,
        DateTime RetryAt, int Failures);

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
        PartialsFolder = PartialFile.PrivateFolder(options.DataDirectory);
        JobStore store = JobStore.Open(Path.Combine(options.DataDirectory, "vesper.db"));
        HttpTransport? http = null;
        try
        {
            http = new HttpTransport(options);
            _store = store;
            _http = http;
            _retry = new RetryPolicy(options.Retry);
            GlobalLimiter.SetRate(options.GlobalLimitBytesPerSecond);
            foreach (DownloadJob job in store.LoadAll())
            {
                _jobs[job.Id] = job;
                AddDuplicateCandidateLocked(job);
            }
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
        try
        {
            engine.Recover();
            lock (engine._lock)
                foreach (DownloadJob job in engine._jobs.Values) engine._mediaDeliveryHistory.Observe(job);
            engine.NormalizeQueueOrdersOnStartup();
            engine.MeteredNetworkPreferenceChanged();
            engine.ScheduleNext();
            _ = engine.RefreshAvailabilityAsync();
            return engine;
        }
        catch
        {
            // A recovery write can fail after the constructor has subscribed to network events.
            // Failed startup must not leave SQLite open or a half-started engine subscribed.
            engine.Dispose();
            throw;
        }
    }

    public bool RecoveredFromCorruptDatabase => _store.RecoveredFromCorruption;
    public string? CorruptDatabaseBackup => _store.CorruptBackupPath;

    internal ValueTask<SegmentBufferLease> AcquireSegmentBufferAsync(long bytes, CancellationToken ct) =>
        _segmentBuffers.AcquireAsync(bytes, ct);

    // snapshots

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
            Id = job.Id, Kind = job.Kind, Output = job.Output, FileName = job.FileName, SourceUrl = job.SourceUrl, Destination = job.DestinationFolder,
            OriginalPageUrl = job.OriginalPageUrl, MediaProvider = job.MediaProvider, MediaId = job.MediaId,
            MediaQuality = job.MediaQuality, MediaFormatId = job.MediaFormatId, MediaThumbnailUrl = job.MediaThumbnailUrl, MediaExpiresAt = job.MediaExpiresAt,
            PartialPath = job.PartialPath, FinalPath = job.FinalPath,
            // Completion is durable already, but actions must not race the runner's final cleanup.
            State = job.State == JobState.Completed && session is not null ? JobState.Finalizing : job.State,
            Reason = job.Reason, Message = job.Message,
            IsUnacceptedBrowserCapture = job.BrowserProof is { } proof
                && (proof.AwaitingConfirmation || job.Reason == "browser-fallback"),
            HasNonPortableMediaSelection = job.MediaPlanJson is not null || job.MediaMetadataJson is not null,
            TotalBytes = job.TotalBytes, CommittedBytes = committed, ProgressBytes = progress,
            SpeedBytesPerSecond = downloading && estimate?.WaitingForData != true ? session?.SpeedBytesPerSecond ?? 0 : 0,
            Eta = estimate?.Remaining, EstimatingEta = estimate?.Estimating == true, WaitingForData = estimate?.WaitingForData == true,
            Connections = session?.Connections ?? 0,
            Priority = job.Priority, SpeedLimitBytes = job.SpeedLimitBytes, QueueOrder = job.QueueOrder,
            CreatedUtc = job.CreatedUtc, CompletedUtc = job.CompletedUtc, NextRetryUtc = job.NextRetryUtc, RetryCount = job.RetryCount,
            FileMissing = _missing.Contains(job.Id), Identity = job.Identity, AcceptsRanges = job.AcceptsRanges,
            IsLiveRecording = job.State != JobState.Completed && HlsTransfer.IsLiveState(job.MediaState),
            ContentType = job.ContentType, Category = job.Category, Rescue = job.RescueMode,
            HasExpectedHash = job.ExpectedSha256 is not null, HasPieceMap = job.PieceMapJson is not null, Sha256Verified = job.Sha256Verified
        };
    }

    private void Changed(DownloadJob job)
    {
        _mediaDeliveryHistory.Observe(job);
        _outbox.Enqueue(SnapshotLocked(job));
    }

    /// <summary>Advisory route preference from recent native outcomes; never evidence of media identity.</summary>
    public JobKind? PreferredMediaDelivery(string provider, IReadOnlyCollection<JobKind> available, long? expectedBytes = null)
    {
        lock (_lock) return _mediaDeliveryHistory.Preferred(provider, available, DateTime.UtcNow, expectedBytes);
    }

    private static bool IsDuplicateCandidate(DownloadJob job) =>
        job.Kind is not (JobKind.BrowserFile or JobKind.BrowserStream) && job.State is not (JobState.Completed or JobState.Cancelled);

    private static bool TryGetDuplicateKey(DownloadJob job, out DuplicateKey key)
    {
        if (Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out Uri? uri))
        {
            key = new DuplicateKey(UrlPolicy.Normalize(uri), job.Output);
            return true;
        }

        key = default;
        return false;
    }

    private bool HasConflictingDownloadLocked(DownloadJob job)
    {
        if (!TryGetDuplicateKey(job, out DuplicateKey key)
            || !_duplicateCandidates.TryGetValue(key, out List<DownloadJob>? candidates))
            return false;
        var request = new AddRequest(job.SourceUrl, Kind: job.Kind, MediaPlanJson: job.MediaPlanJson, Output: job.Output);
        return candidates.Any(candidate => candidate.Id != job.Id && IsDuplicateCandidate(candidate)
            && SameRequestPlan(candidate, request));
    }

    private void AddDuplicateCandidateLocked(DownloadJob job)
    {
        if (!IsDuplicateCandidate(job) || !TryGetDuplicateKey(job, out DuplicateKey key)) return;
        if (!_duplicateCandidates.TryGetValue(key, out List<DownloadJob>? candidates))
            _duplicateCandidates.Add(key, candidates = []);
        if (!candidates.Contains(job)) candidates.Add(job);
    }

    private void RemoveDuplicateCandidateLocked(DownloadJob job, string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri)) return;
        var key = new DuplicateKey(UrlPolicy.Normalize(uri), job.Output);
        if (!_duplicateCandidates.TryGetValue(key, out List<DownloadJob>? candidates)) return;
        candidates.Remove(job);
        if (candidates.Count == 0) _duplicateCandidates.Remove(key);
    }

    private void ReconcileDuplicateCandidateLocked(DownloadJob job, bool wasCandidate, string previousSourceUrl)
    {
        bool isCandidate = IsDuplicateCandidate(job);
        bool sourceChanged = !string.Equals(previousSourceUrl, job.SourceUrl, StringComparison.Ordinal);
        if (wasCandidate && (!isCandidate || sourceChanged))
            RemoveDuplicateCandidateLocked(job, previousSourceUrl);
        if (isCandidate && (!wasCandidate || sourceChanged))
            AddDuplicateCandidateLocked(job);
    }

    /// <summary>Raises queued change events outside every lock so handlers may call back into the engine.</summary>
    private void FlushOutbox()
    {
        lock (_outboxLock)
        {
            while (_outbox.TryDequeue(out JobSnapshot? snapshot)) JobChanged?.Invoke(snapshot);
            bool active;
            lock (_lock) active = _sessions.Count > 0;
            if (active != _wasActive) { _wasActive = active; ActivityChanged?.Invoke(active); }
        }
    }

    // internal services used by runners

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

    /// <summary>
    /// Persist a transfer metadata change before publishing it to the same DownloadJob
    /// referenced by its active runner. The mutator is invoked only on the detached copy.
    /// </summary>
    internal void UpdateJob(DownloadJob job, Action<DownloadJob> mutate)
    {
        lock (_lock)
        {
            bool wasCandidate = IsDuplicateCandidate(job);
            string previousSourceUrl = job.SourceUrl;
            DownloadJob staged = job.CopyForUpdate();
            mutate(staged);
            SaveLocked(staged);
            job.ApplyPersistedState(staged);
            ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
            Changed(job);
        }
        FlushOutbox();
    }

    internal void SetPhase(DownloadJob job, JobState state) => UpdateJob(job, j => j.State = state);

    /// <summary>
    /// Saves flushed bytes before making them visible. Retire the batch under the same lock
    /// so snapshots cannot count it twice and metadata writers see the committed version.
    /// </summary>
    internal DownloadJob CommitCheckpoint(DownloadJob job, ExtentSet next, string? mediaState, Action? retirePending = null,
        long? totalBytes = null, string? partialIdentity = null)
    {
        lock (_lock)
        {
            if (job.Kind == JobKind.Hls && mediaState is not null)
                mediaState = HlsTransfer.PreserveLiveStop(job.MediaState, mediaState);
            DownloadJob copy = job.CloneForCommit(next, mediaState ?? job.MediaState);
            if (totalBytes is not null) copy.TotalBytes = totalBytes;
            if (partialIdentity is not null) copy.PartialIdentity = partialIdentity;
            SaveLocked(copy);
            job.Extents = copy.Extents;
            job.Generation = copy.Generation;
            job.MediaState = copy.MediaState;
            job.TotalBytes = copy.TotalBytes;
            job.PartialIdentity = copy.PartialIdentity;
            retirePending?.Invoke();
            return copy;
        }
    }

    internal void WriteManifest(DownloadJob committed, SafeFileHandle? livePartial = null)
    {
        try { RecoveryManifest.Write(committed, livePartial); }
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

    // scheduling

    /// <summary>Starts as many queued jobs as the limits allow. Safe to call at any time, from any thread.</summary>
    internal void ScheduleNext()
    {
        lock (_lock)
        {
            if (_disposed || _crashed) return;

            bool networkCostBlocked = UpdateMeteredNetworkWaitsLocked();

            foreach (Guid id in _forced.ToArray())
            {
                if (_cancelling.Contains(id)) continue;
                if (_jobs.TryGetValue(id, out DownloadJob? job) && job.State == JobState.Queued && !_sessions.ContainsKey(id)) StartSession(job);
                else if (!_sessions.ContainsKey(id)) _forced.Remove(id);
            }

            if (!networkCostBlocked && Options.Schedule.IsOpen(DateTime.Now))
            {
                while (_sessions.Count(s => !_forced.Contains(s.Key)) < Options.MaxConcurrentJobs && PickNext() is { } next)
                    StartSession(next);
            }
            else
            {
                foreach (JobSession session in _sessions.Values.Where(s => !_forced.Contains(s.Job.Id)
                    && s.Intent == StopIntent.None
                    && s.Job.State is not (JobState.Verifying or JobState.Converting or JobState.Finalizing)))
                {
                    session.Intent = networkCostBlocked ? StopIntent.MeteredNetwork : StopIntent.Window;
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
            .Where(j => j.State == JobState.Queued && !_sessions.ContainsKey(j.Id) && !_cancelling.Contains(j.Id))
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
        JobState oldState = job.State;
        string oldMessage = job.Message;
        DateTime? oldRetry = job.NextRetryUtc;
        job.State = JobState.Resolving;
        job.Message = "";
        job.NextRetryUtc = null;
        try { SaveLocked(job); }
        catch
        {
            // A refused SQLite write must not leave a phantom active job or its CTS alive.
            job.State = oldState;
            job.Message = oldMessage;
            job.NextRetryUtc = oldRetry;
            _sessions.Remove(job.Id);
            session.Dispose();
            throw;
        }
        Changed(job);
        EnsurePump();
        Task running = session.RunTask = Task.Run(() => RunSessionAsync(session));
        _runningTasks.Add(running);
        _ = running.ContinueWith(task => { lock (_lock) _runningTasks.Remove(task); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunSessionAsync(JobSession session)
    {
        RunOutcome outcome;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long localRevision = session.Limiter.Revision, globalRevision = GlobalLimiter.Revision;
        bool timingEligible = session.CommittedAtStart == 0 && session.Job.RetryCount == 0
            && string.IsNullOrEmpty(session.Job.PreviousUrls) && session.Limiter.BytesPerSecond == 0
            && GlobalLimiter.BytesPerSecond == 0;
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
        TimeSpan? elapsed = timingEligible && session.Limiter.Revision == localRevision
            && GlobalLimiter.Revision == globalRevision ? System.Diagnostics.Stopwatch.GetElapsedTime(started) : null;
        OnRunFinished(session, outcome, elapsed);
    }

    private void OnRunFinished(JobSession session, RunOutcome outcome, TimeSpan? elapsed)
    {
        DownloadJob job = session.Job;
        try
        {
            lock (_lock)
            {
                _sessions.Remove(job.Id);
                _forced.Remove(job.Id);
                bool progressed = job.Extents.TotalBytes > session.CommittedAtStart;
                if (session.Intent == StopIntent.BrowserFallback && outcome.State != JobState.Completed
                    && job.PendingFinalPath is null)
                    outcome = BrowserFallbackOutcome;
                if (session.Intent == StopIntent.Cancel && outcome.State != JobState.Completed
                    && job.State != JobState.Completed && job.PendingFinalPath is null)
                    outcome = new RunOutcome(JobState.Cancelled, "cancelled", "Cancelling download.");
                bool wasCandidate = IsDuplicateCandidate(job);
                string previousSourceUrl = job.SourceUrl;
                DownloadJob updated = job.CopyForUpdate();
                ApplyOutcome(updated, outcome, progressed);
                try
                {
                    SaveLocked(updated);
                    job.ApplyPersistedState(updated);
                    ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
                    Changed(job);
                    if (elapsed is { } duration && outcome.State == JobState.Completed)
                        _mediaDeliveryHistory.ObserveTiming(job, duration);
                    Log(job.Id, "run-ended", $"{job.State} {job.Reason} {outcome.Message}");
                }
                catch (LocalStorageException ex)
                {
                    // Keep the live record at its last committed state. Startup will recover
                    // the durable active state if the database stays unavailable.
                    Log(job.Id, "save-failed", ex.Message);
                    if (!_disposed && !_crashed)
                    {
                        _pendingRunOutcomes[job.Id] = new PendingRunOutcome(job, job.CopyForUiMutation(), outcome,
                            progressed, DateTime.UtcNow.AddSeconds(1), Failures: 0);
                        ArmWakeTimer();
                    }
                }
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
                    DateTime now = DateTime.UtcNow;
                    job.NextRetryUtc = now.AddTicks(Math.Min(delay.Ticks, DateTime.MaxValue.Ticks - now.Ticks));
                    job.Message = $"{outcome.Message} Retrying in {Math.Ceiling(delay.TotalSeconds)} s.";
                }
                break;
            case JobState.WaitingForNetwork:
                job.State = JobState.WaitingForNetwork;
                job.NextRetryUtc = outcome.Reason is "metered-network" or "network-cost-unknown"
                    ? null : DateTime.UtcNow.AddMinutes(2);   // safety net if the network event is missed
                break;
            default:
                job.State = outcome.State;
                job.NextRetryUtc = null;
                break;
        }
    }

    // timers (one wake-up timer, one progress pump; neither runs when there is nothing to do)

    private void ArmWakeTimer()
    {
        if (_disposed) return;
        DateTime? next = _jobs.Values.Where(j => j.State is JobState.WaitingForRetry or JobState.WaitingForNetwork && j.NextRetryUtc is not null)
            .Select(j => j.NextRetryUtc!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        if (next == DateTime.MaxValue) next = null;
        DateTime? pendingRunOutcome = _pendingRunOutcomes.Values.Select(pending => pending.RetryAt)
            .DefaultIfEmpty(DateTime.MaxValue).Min();
        if (pendingRunOutcome != DateTime.MaxValue && (next is null || pendingRunOutcome < next))
            next = pendingRunOutcome;
        if (Options.Schedule.NextBoundary(DateTime.Now) is { } boundary)
        {
            DateTime boundaryUtc = boundary.ToUniversalTime();
            next = next is null || boundaryUtc < next ? boundaryUtc : next;
        }

        _wakeTimer ??= new Timer(_ => OnWake(), null, Timeout.Infinite, Timeout.Infinite);
        // A far-off retry is revisited later within the range supported by Timer.Change.
        _wakeTimer.Change(next is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(
            Math.Clamp((next.Value - DateTime.UtcNow).TotalMilliseconds, 50, TimeSpan.FromDays(1).TotalMilliseconds)),
            Timeout.InfiniteTimeSpan);
    }

    private void RetrySchedulingSoonLocked()
    {
        if (_disposed || _crashed) return;
        _wakeTimer ??= new Timer(_ => OnWake(), null, Timeout.Infinite, Timeout.Infinite);
        _wakeTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void OnWake()
    {
        lock (_lock)
        {
            if (_disposed || _crashed) return;

            DateTime now = DateTime.UtcNow;
            RetryPendingRunOutcomesLocked(now);
            foreach (DownloadJob job in _jobs.Values.Where(j => j.State is JobState.WaitingForRetry or JobState.WaitingForNetwork
                && j.NextRetryUtc is { } due && due <= now && !_sessions.ContainsKey(j.Id)))
            {
                try
                {
                    PersistUiMutationLocked(job, static pending =>
                    {
                        pending.State = JobState.Queued;
                        pending.NextRetryUtc = null;
                    });
                    Changed(job);
                }
                catch (LocalStorageException ex) { Log(job.Id, "wake-save-failed", ex.Message); }
            }
        }

        try { ScheduleNext(); }
        catch (LocalStorageException ex)
        {
            lock (_lock)
            {
                if (_disposed || _crashed) return;
                Log(null, "wake-start-failed", ex.Message);
                // A queued job has no retry timestamp after waking. Re-arm the one-shot timer.
                RetrySchedulingSoonLocked();
            }
            FlushOutbox();
        }
    }

    private void RetryPendingRunOutcomesLocked(DateTime now)
    {
        foreach ((Guid id, PendingRunOutcome pending) in _pendingRunOutcomes
            .Where(pair => pair.Value.RetryAt <= now).ToArray())
        {
            if (!_jobs.TryGetValue(id, out DownloadJob? job) || !ReferenceEquals(job, pending.Job)
                || _sessions.ContainsKey(id) || _cancelling.Contains(id) || IsCleanupPending(job)
                || !SamePendingRunState(job, pending.Expected))
            {
                _pendingRunOutcomes.Remove(id);
                continue;
            }

            DownloadJob updated = job.CopyForUpdate();
            ApplyOutcome(updated, pending.Outcome, pending.Progressed);
            try { SaveLocked(updated); }
            catch (LocalStorageException ex)
            {
                int failures = Math.Min(pending.Failures + 1, 6);
                int seconds = Math.Min(60, 1 << failures);
                _pendingRunOutcomes[id] = pending with { RetryAt = now.AddSeconds(seconds), Failures = failures };
                Log(id, "run-outcome-save-retry", ex.Message);
                continue;
            }

            bool wasCandidate = IsDuplicateCandidate(job);
            string previousSourceUrl = job.SourceUrl;
            _pendingRunOutcomes.Remove(id);
            job.ApplyPersistedState(updated);
            ReconcileDuplicateCandidateLocked(job, wasCandidate, previousSourceUrl);
            Changed(job);
            Log(id, "run-ended", $"{job.State} {job.Reason} {pending.Outcome.Message}");
        }
    }

    private static bool SamePendingRunState(DownloadJob current, DownloadJob expected) =>
        current.State == expected.State && current.Reason == expected.Reason
        && current.SourceUrl == expected.SourceUrl && current.FinalResourceIdentity == expected.FinalResourceIdentity
        && ReferenceEquals(current.Extents, expected.Extents) && current.Generation == expected.Generation
        && current.TotalBytes == expected.TotalBytes && current.PartialIdentity == expected.PartialIdentity
        && current.MediaPlanJson == expected.MediaPlanJson && current.MediaState == expected.MediaState
        && current.MediaExpiresAt == expected.MediaExpiresAt && current.ExpectedSha256 == expected.ExpectedSha256
        && current.PieceMapJson == expected.PieceMapJson && current.Output == expected.Output
        && current.PendingFinalPath == expected.PendingFinalPath
        && current.PendingConversionBytes == expected.PendingConversionBytes
        && current.PendingConversionDigest == expected.PendingConversionDigest
        && current.PendingConversionIdentity == expected.PendingConversionIdentity
        && current.PublicationCopyIdentity == expected.PublicationCopyIdentity
        && current.RetryCount == expected.RetryCount && current.NextRetryUtc == expected.NextRetryUtc;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) WakeWaiting(includeRetry: false);
    }

    /// <summary>Wakes jobs that wait for connectivity (and optionally those in backoff), e.g. after sleep or a network change.</summary>
    public void WakeWaiting(bool includeRetry = true)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (DownloadJob job in _jobs.Values.Where(j => !_sessions.ContainsKey(j.Id)
                && (j.State == JobState.WaitingForNetwork || (includeRetry && j.State == JobState.WaitingForRetry))))
            {
                try
                {
                    PersistUiMutationLocked(job, static pending =>
                    {
                        pending.State = JobState.Queued;
                        pending.NextRetryUtc = null;
                    });
                    Changed(job);
                }
                catch (LocalStorageException ex) { Log(job.Id, "wake-save-failed", ex.Message); }
            }
        }
        try { ScheduleNext(); }
        catch (LocalStorageException ex)
        {
            lock (_lock)
            {
                if (_disposed || _crashed) return;
                Log(null, "wake-start-failed", ex.Message);
                RetrySchedulingSoonLocked();
            }
            FlushOutbox();
        }
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

    private OperationLease EnterOperation(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var lease = new OperationLease(this, CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token));
            _activeOperations++;
            return lease;
        }
    }

    private sealed class OperationLease(DownloadEngine engine, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            cancellation.Dispose();
            lock (engine._lock)
            {
                if (--engine._activeOperations == 0)
                    engine._operationsDrained?.TrySetResult();
            }
        }
    }

    /// <summary>Stop transfers before releasing their network and storage resources.</summary>
    public void Dispose()
    {
        Task[] running;
        Task operations;
        Task wake;
        Task pump;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (JobSession session in _sessions.Values)
            {
                session.Intent = StopIntent.Shutdown;
                session.Cts.Cancel();
            }
            running = [.. _runningTasks];
            operations = _activeOperations == 0 ? Task.CompletedTask
                : (_operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            wake = _wakeTimer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            pump = _pumpTimer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            _wakeTimer = null;
            _pumpTimer = null;
        }
        _shutdown.Cancel();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisposeMeteredNetworkWatcher();
        Task drain = Task.WhenAll([.. running, operations, wake, pump]);
        // Do not close SQLite or the transport underneath an operation that is still unwinding.
        Task release = drain.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _browserBodies?.Dispose();
            _conversionSlot.Dispose();
            _remuxSlots.Dispose();
            foreach (SemaphoreSlim gate in _hostGates.Values) gate.Dispose();
            _http.Dispose();
            _store.Dispose();
            _shutdown.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                System.Diagnostics.Trace.WriteLine("Download shutdown is still draining.", "VESPER");
        }
        catch (AggregateException ex) { System.Diagnostics.Trace.WriteLine(ex.Message, "VESPER shutdown"); }
    }
}
