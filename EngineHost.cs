using System.Runtime.InteropServices;
using Vesper.Engine;
using Vesper.MediaResolver.Core;

namespace VESPER;

/// <summary>Maps the user's preferences to engine options and applies later changes without disturbing running jobs.</summary>
internal static class EngineHost
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VESPER");

    private static EngineOptions? _activeNetwork;

    public static DownloadEngine Start(VesperPreferences p)
    {
        var engine = DownloadEngine.Start(Build(p));
        Volatile.Write(ref _activeNetwork, engine.Options);
        return engine;
    }

    // Proxy changes take effect on restart for both discovery and the transfer engine.
    internal static ResolverOptions MediaNetwork(int maxConcurrent = 4, EngineOptions? options = null)
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
            BrowserDownloadsFolder = WindowsDownloadsFolder(),
            AllowPrivateNetworks = p.AllowPrivateNetworks,
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
        options.DefaultFolder = p.DefaultFolder;
        options.MaxConcurrentJobs = Math.Max(1, p.ConcurrentTransfers);
        options.MaxConnectionsPerJob = Math.Clamp(p.MaxConnectionsPerJob, 1, 8);
        options.GlobalLimitBytesPerSecond = p.GlobalRateLimitKbs * 1000L;
        options.CategorySubfolders = p.CategoryFolders;
        options.PreventSleepWhileActive = p.PreventSleep;
        options.Schedule = TimeOnly.TryParse(p.ScheduleStart, out TimeOnly start) && TimeOnly.TryParse(p.ScheduleEnd, out TimeOnly end)
            ? new SchedulePolicy(p.ScheduleEnabled, start, end) : SchedulePolicy.Always;
    }

    public static void Apply(DownloadEngine engine, VesperPreferences p)
    {
        ApplyLive(engine.Options, p);
        engine.SettingsChanged();
    }

    // ---- sleep prevention: only while jobs run and only if the user asked for it ------------------------------------

    [Flags]
    private enum ExecutionState : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);

    /// <summary>Must be called from one consistent thread (the UI thread).</summary>
    public static void HoldAwake(bool hold) =>
        SetThreadExecutionState(hold ? ExecutionState.Continuous | ExecutionState.SystemRequired : ExecutionState.Continuous);

    /// <summary>Moves a file to the Recycle Bin so an accidental delete can be undone.</summary>
    public static void RecycleFile(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
