using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Reflection;
using System.Runtime.Versioning;

namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    private NetworkCostWatcher? _networkCostWatcher;
    private int _networkCostStatus = (int)NetworkCostStatus.Unknown;

    /// <summary>Call after the saved metered-network preference changes.</summary>
    public void MeteredNetworkPreferenceChanged()
    {
        if (!Options.PauseOnMeteredNetwork)
        {
            if (OperatingSystem.IsWindows()) _networkCostWatcher?.Dispose();
            _networkCostWatcher = null;
            Volatile.Write(ref _networkCostStatus, (int)NetworkCostStatus.Unknown);
            return;
        }

        if (Options.NetworkCostProvider is not null)
        {
            NetworkCostStatus status;
            try { status = Options.NetworkCostProvider(); }
            catch { status = NetworkCostStatus.Unknown; }
            Volatile.Write(ref _networkCostStatus, (int)status);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Volatile.Write(ref _networkCostStatus, (int)NetworkCostStatus.Unknown);
            return;
        }

        if (_networkCostWatcher is null)
        {
            try { _networkCostWatcher = new NetworkCostWatcher(OnNetworkCostChanged); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _networkCostWatcher = null;
                Volatile.Write(ref _networkCostStatus, (int)NetworkCostStatus.Unknown);
                Log(null, "network-cost", "Windows could not report connection cost: " + ex.Message);
                return;
            }
        }

        Volatile.Write(ref _networkCostStatus, (int)_networkCostWatcher.Status);
    }

    internal void RefreshMeteredNetworkStatus()
    {
        NetworkCostStatus status = ReadNetworkCostStatus();
        OnNetworkCostChanged(status);
    }

    private NetworkCostStatus ReadNetworkCostStatus()
    {
        if (Options.NetworkCostProvider is { } provider)
        {
            try { return provider(); }
            catch { return NetworkCostStatus.Unknown; }
        }

        return (NetworkCostStatus)Volatile.Read(ref _networkCostStatus);
    }

    private void OnNetworkCostChanged(NetworkCostStatus status)
    {
        int previous = Interlocked.Exchange(ref _networkCostStatus, (int)status);
        if (previous == (int)status) return;

        try { ScheduleNext(); }
        catch (LocalStorageException ex) { Log(null, "network-cost", ex.Message); }
    }

    private bool UpdateMeteredNetworkWaitsLocked()
    {
        if (!Options.PauseOnMeteredNetwork)
        {
            WakeMeteredNetworkJobsLocked();
            return false;
        }

        NetworkCostStatus status = ReadNetworkCostStatus();
        if (status == NetworkCostStatus.Unmetered)
        {
            WakeMeteredNetworkJobsLocked();
            return false;
        }

        string reason = status == NetworkCostStatus.Metered ? "metered-network" : "network-cost-unknown";
        string message = MeteredNetworkMessage(status);
        foreach (DownloadJob job in _jobs.Values.Where(job => !_sessions.ContainsKey(job.Id)
            && !_forced.Contains(job.Id) && !_cancelling.Contains(job.Id)
            && (job.State == JobState.Queued || job.State == JobState.WaitingForNetwork
                && (job.Reason is "metered-network" or "network-cost-unknown"))))
        {
            if (job.State == JobState.WaitingForNetwork && job.Reason == reason
                && job.Message == message && job.NextRetryUtc is null) continue;

            try
            {
                PersistUiMutationLocked(job, pending =>
                {
                    pending.State = JobState.WaitingForNetwork;
                    pending.Reason = reason;
                    pending.Message = message;
                    pending.NextRetryUtc = null;
                });
                Changed(job);
            }
            catch (LocalStorageException ex) { Log(job.Id, "network-cost", ex.Message); }
        }

        return true;
    }

    private void WakeMeteredNetworkJobsLocked()
    {
        foreach (DownloadJob job in _jobs.Values.Where(job => !_sessions.ContainsKey(job.Id)
            && job.State == JobState.WaitingForNetwork
            && (job.Reason is "metered-network" or "network-cost-unknown")))
        {
            try
            {
                PersistUiMutationLocked(job, pending =>
                {
                    pending.State = JobState.Queued;
                    pending.Reason = "";
                    pending.Message = "";
                    pending.NextRetryUtc = null;
                });
                Changed(job);
            }
            catch (LocalStorageException ex) { Log(job.Id, "network-cost", ex.Message); }
        }
    }

    private static string MeteredNetworkMessage(NetworkCostStatus status) => status switch
    {
        NetworkCostStatus.Metered => "Windows reports a metered connection. Choose Start now for this download or turn off the metered-network setting.",
        _ => "Windows cannot report this connection's cost. Choose Start now for this download or turn off the metered-network setting."
    };

    internal RunOutcome MeteredNetworkStopOutcome()
    {
        NetworkCostStatus status = ReadNetworkCostStatus();
        if (!Options.PauseOnMeteredNetwork)
            return new RunOutcome(JobState.Queued, "network-policy-disabled", "The metered-network pause was turned off. The download will resume.");
        if (status == NetworkCostStatus.Unmetered)
            return new RunOutcome(JobState.Queued, "network-cost-changed", "Windows reports an unmetered connection. The download will resume.");

        string reason = status == NetworkCostStatus.Metered ? "metered-network" : "network-cost-unknown";
        return new RunOutcome(JobState.WaitingForNetwork, reason, MeteredNetworkMessage(status));
    }

    private void DisposeMeteredNetworkWatcher()
    {
        if (OperatingSystem.IsWindows()) _networkCostWatcher?.Dispose();
        _networkCostWatcher = null;
    }

    [SupportedOSPlatform("windows")]
    private sealed class NetworkCostWatcher : IDisposable
    {
        private static readonly Guid NetworkListManagerClassId = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
        private static readonly Guid CostEventsId = new("DCB00009-570F-4A9B-8D69-199FDBA5723B");
        private readonly INetworkCostManager _manager;
        private readonly IConnectionPoint _connectionPoint;
        private readonly CostEventSink _sink;
        private readonly int _cookie;
        private readonly Action<NetworkCostStatus> _changed;
        private readonly object _gate = new();
        private int _status;
        private int _disposed;

        public NetworkCostStatus Status => (NetworkCostStatus)Volatile.Read(ref _status);

        public NetworkCostWatcher(Action<NetworkCostStatus> changed)
        {
            _changed = changed;
            Type managerType = Type.GetTypeFromCLSID(NetworkListManagerClassId, throwOnError: true)!;
            object instance = Activator.CreateInstance(managerType)
                ?? throw new COMException("Network List Manager could not be created.");
            IConnectionPoint? point = null;
            CostEventSink? sink = null;
            int cookie = 0;
            bool advised = false;
            try
            {
                _manager = (INetworkCostManager)instance;
                Guid eventsId = CostEventsId;
                ((IConnectionPointContainer)instance).FindConnectionPoint(ref eventsId, out point);
                if (point is null) throw new COMException("Network cost notifications are unavailable.");
                sink = new CostEventSink(QueueRefresh);
                RefreshStatus(out _);
                point.Advise(sink, out cookie);
                advised = true;
                _connectionPoint = point;
                _sink = sink;
                _cookie = cookie;
                RefreshStatus(out _);
            }
            catch
            {
                if (point is not null)
                {
                    if (advised)
                    {
                        try { point.Unadvise(cookie); }
                        catch (COMException) { }
                    }
                    if (Marshal.IsComObject(point)) Marshal.ReleaseComObject(point);
                }
                if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
                throw;
            }
        }

        private bool RefreshStatus(out NetworkCostStatus status)
        {
            lock (_gate)
            {
                int result = _manager.GetCost(out uint cost, IntPtr.Zero);
                status = result < 0 ? NetworkCostStatus.Unknown : ClassifyCost(cost);
                int previous = _status;
                Volatile.Write(ref _status, (int)status);
                return previous != (int)status;
            }
        }

        private void QueueRefresh()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                NetworkCostStatus next;
                bool changed;
                try { changed = RefreshStatus(out next); }
                catch
                {
                    next = NetworkCostStatus.Unknown;
                    changed = SetUnknownStatus();
                }
                if (Volatile.Read(ref _disposed) != 0) return;

                if (changed) _changed(next);
            });
        }

        private bool SetUnknownStatus()
        {
            lock (_gate)
            {
                int previous = _status;
                Volatile.Write(ref _status, (int)NetworkCostStatus.Unknown);
                return previous != (int)NetworkCostStatus.Unknown;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate)
            {
                try { _connectionPoint.Unadvise(_cookie); }
                catch (COMException) { }
                if (Marshal.IsComObject(_connectionPoint)) Marshal.ReleaseComObject(_connectionPoint);
                if (Marshal.IsComObject(_manager)) Marshal.ReleaseComObject(_manager);
            }
        }

        private static NetworkCostStatus ClassifyCost(uint cost)
        {
            const uint unrestricted = 0x00000001;
            const uint fixedCost = 0x00000002;
            const uint variableCost = 0x00000004;
            const uint overDataLimit = 0x00010000;
            const uint roaming = 0x00040000;
            const uint approachingDataLimit = 0x00080000;
            const uint meteredFlags = fixedCost | variableCost | overDataLimit | roaming | approachingDataLimit;

            if ((cost & meteredFlags) != 0) return NetworkCostStatus.Metered;
            return (cost & unrestricted) != 0 ? NetworkCostStatus.Unmetered : NetworkCostStatus.Unknown;
        }

        [ComVisible(true)]
        [Guid("DCB00009-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkCostManagerEvents
        {
            [PreserveSig] int CostChanged(uint newCost, IntPtr destinationAddress);
            [PreserveSig] int DataPlanStatusChanged(IntPtr destinationAddress);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class CostEventSink(Action refresh) : INetworkCostManagerEvents
        {
            public int CostChanged(uint newCost, IntPtr destinationAddress)
            {
                refresh();
                return 0;
            }

            public int DataPlanStatusChanged(IntPtr destinationAddress)
            {
                refresh();
                return 0;
            }
        }

        [ComImport]
        [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkCostManager
        {
            [PreserveSig] int GetCost(out uint cost, IntPtr destinationAddress);
            [PreserveSig] int GetDataPlanStatus(IntPtr status, IntPtr destinationAddress);
            [PreserveSig] int SetDestinationAddresses(uint count, IntPtr addresses, [MarshalAs(UnmanagedType.VariantBool)] bool append);
        }
    }
}
