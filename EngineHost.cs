using System.Runtime.InteropServices;
using Vesper.Engine;

namespace VESPER;

/// <summary>Maps the user's preferences to engine options and applies later changes without disturbing running jobs.</summary>
internal static class EngineHost
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VESPER");

    public static DownloadEngine Start(VesperPreferences p) => DownloadEngine.Start(Build(p));

    private static EngineOptions Build(VesperPreferences p)
    {
        var options = new EngineOptions
        {
            DataDirectory = DataDirectory,
            AllowPrivateNetworks = p.AllowPrivateNetworks,
            Proxy = Enum.TryParse(p.ProxyMode, out ProxyMode mode) ? mode : ProxyMode.Direct,
            ProxyAddress = string.IsNullOrWhiteSpace(p.ProxyAddress) ? null : p.ProxyAddress
        };
        ApplyLive(options, p);
        return options;
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
