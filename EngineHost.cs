using System.Runtime.InteropServices;
using System.Diagnostics;
using Vesper.Engine;
using Vesper.MediaResolver.Core;

namespace VESPER;

/// <summary>Maps the user's preferences to engine options and applies later changes without disturbing running jobs.</summary>
internal static class EngineHost
{
    public static string DataDirectory
    {
        get
        {
            string? testDirectory = Environment.GetEnvironmentVariable("VESPER_TEST_DATA_DIRECTORY");
            return string.IsNullOrWhiteSpace(testDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VESPER")
                : Path.GetFullPath(testDirectory);
        }
    }

    private static EngineOptions? _activeNetwork;

    public static DownloadEngine Start(VesperPreferences p)
    {
        var engine = DownloadEngine.Start(Build(p));
        Volatile.Write(ref _activeNetwork, engine.Options);
        return engine;
    }

    // Proxy changes take effect on restart for both discovery and the transfer engine.
    internal static ResolverOptions MediaNetwork(int maxConcurrent = 4, EngineOptions? options = null, bool persistRouteHistory = false)
    {
        options ??= Volatile.Read(ref _activeNetwork);
        Uri? proxy = options?.Proxy == ProxyMode.Custom && Uri.TryCreate(options.ProxyAddress, UriKind.Absolute, out Uri? address)
            ? address : null;
        var cooldown = options?.Cooldown;
        return new ResolverOptions
        {
            UseSystemProxy = options?.Proxy == ProxyMode.System,
            ProxyAddress = proxy,
            MaxConcurrentRequests = maxConcurrent,
            RulePackDirectory = Path.Combine(DataDirectory, "rules"),
            RouteHistoryPath = persistRouteHistory && options is not null
                ? Path.Combine(options.DataDirectory, "intelligence", "route-history.json") : null,
            RulePackPublicKeySpki = Environment.GetEnvironmentVariable("VESPER_RULE_PACK_PUBLIC_KEY"),
            LocalSemanticEndpoint = Uri.TryCreate(Environment.GetEnvironmentVariable("VESPER_LOCAL_SEMANTIC_ENDPOINT"),
                UriKind.Absolute, out Uri? semanticEndpoint) ? semanticEndpoint : null,
            OriginRetryAt = cooldown is null ? null : cooldown.RetryAt,
            OriginThrottled = cooldown is null ? null : cooldown.Remember,
            Diagnostics = options is null ? null : message => System.Diagnostics.Trace.WriteLine(message, $"Media discovery ({options.Proxy})")
        };
    }

    private static EngineOptions Build(VesperPreferences p)
    {
        var options = new EngineOptions
        {
            DataDirectory = DataDirectory,
            DiagnosticBuild = Vesper.Shared.BuildInfo.Version(typeof(EngineHost).Assembly),
            BrowserDownloadsFolder = WindowsDownloadsFolder(),
            AllowPrivateNetworks = p.AllowPrivateNetworks,
            PauseOnMeteredNetwork = p.PauseOnMeteredNetwork,
            Proxy = Enum.TryParse(p.ProxyMode, out ProxyMode mode) ? mode : ProxyMode.Direct,
            ProxyAddress = string.IsNullOrWhiteSpace(p.ProxyAddress) ? null : p.ProxyAddress
        };
        ApplyLive(options, p);
        return options;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);

    private static string? WindowsDownloadsFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        Guid downloads = new("374de290-123f-4565-9164-39c4925e467b");
        int result = SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out IntPtr path);
        try { return result == 0 ? Marshal.PtrToStringUni(path) : null; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    /// <summary>Settings that can change while jobs run.</summary>
    private static void ApplyLive(EngineOptions options, VesperPreferences p)
    {
        options.DefaultFolder = PathSafety.TryGetFullPath(p.DefaultFolder, out string folder, out _)
            ? folder : p.DefaultFolder;
        options.MaxConcurrentJobs = Math.Max(1, p.ConcurrentTransfers);
        options.MaxConnectionsPerJob = Math.Clamp(p.MaxConnectionsPerJob, 1, 8);
        options.GlobalLimitBytesPerSecond = p.GlobalRateLimitKbs * 1000L;
        options.CategorySubfolders = p.CategoryFolders;
        options.FileNameTemplate = p.FileNameTemplate;
        options.PreventSleepWhileActive = p.PreventSleep;
        options.PauseOnMeteredNetwork = p.PauseOnMeteredNetwork;
        options.Schedule = TimeOnly.TryParse(p.ScheduleStart, out TimeOnly start) && TimeOnly.TryParse(p.ScheduleEnd, out TimeOnly end)
            ? new SchedulePolicy(p.ScheduleEnabled, start, end) : SchedulePolicy.Always;
    }

    public static void Apply(DownloadEngine engine, VesperPreferences p)
    {
        ApplyLive(engine.Options, p);
        engine.MeteredNetworkPreferenceChanged();
        engine.SettingsChanged();
    }

    // sleep prevention: only while jobs run and only if the user asked for it

    [Flags]
    private enum ExecutionState : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    /// <summary>Must be called from one consistent thread (the UI thread).</summary>
    public static void HoldAwake(bool hold) =>
        SetThreadExecutionState(hold ? ExecutionState.Continuous | ExecutionState.SystemRequired : ExecutionState.Continuous);

    /// <summary>Requests normal Windows sleep. The caller must obtain an explicit user choice first.</summary>
    internal static bool SleepComputer() =>
        OperatingSystem.IsWindows() && SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);

    /// <summary>Requests a normal Windows shutdown. The caller must obtain an explicit user choice first.</summary>
    internal static bool ShutdownComputer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        const uint tokenAdjustPrivileges = 0x0020;
        const uint tokenQuery = 0x0008;
        const uint privilegeEnabled = 0x00000002;
        const uint shutdown = 0x00000001;
        const uint powerOff = 0x00000008;
        const uint plannedApplicationShutdown = 0x80040000;

        if (!OpenProcessToken(GetCurrentProcess(), tokenAdjustPrivileges | tokenQuery, out IntPtr token))
        {
            TraceShutdownError("OpenProcessToken");
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out Luid luid))
            {
                TraceShutdownError("LookupPrivilegeValue");
                return false;
            }

            int privilegeSize = Marshal.SizeOf<TokenPrivileges>();
            IntPtr previousState = Marshal.AllocHGlobal(privilegeSize);
            IntPtr returnLength = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                var enable = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privilege = new LuidAndAttributes { Luid = luid, Attributes = privilegeEnabled }
                };
                if (!AdjustTokenPrivileges(token, false, ref enable, (uint)privilegeSize, previousState, returnLength))
                {
                    TraceShutdownError("AdjustTokenPrivileges");
                    return false;
                }
                int privilegeError = Marshal.GetLastWin32Error();
                if (privilegeError != 0)
                {
                    Trace.WriteLine($"SeShutdownPrivilege could not be enabled (Win32 error {privilegeError}).", "Queue completion shutdown");
                    return false;
                }

                TokenPrivileges oldState = Marshal.PtrToStructure<TokenPrivileges>(previousState);
                try
                {
                    bool requested = ExitWindowsEx(shutdown | powerOff, plannedApplicationShutdown);
                    int shutdownError = requested ? 0 : Marshal.GetLastWin32Error();
                    if (!requested)
                        Trace.WriteLine($"ExitWindowsEx failed with Win32 error {shutdownError}.", "Queue completion shutdown");
                    return requested;
                }
                finally
                {
                    if (oldState.PrivilegeCount > 0
                        && !AdjustTokenPrivileges(token, false, ref oldState, 0, IntPtr.Zero, IntPtr.Zero))
                        TraceShutdownError("RestoreTokenPrivileges");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(previousState);
                Marshal.FreeHGlobal(returnLength);
            }
        }
        finally { CloseHandle(token); }
    }

    private static void TraceShutdownError(string operation) =>
        Trace.WriteLine($"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}.", "Queue completion shutdown");

    /// <summary>Moves a file to the Recycle Bin so an accidental delete can be undone.</summary>
    public static void RecycleFile(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
