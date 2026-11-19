using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VESPER;
using Vesper.Shared;

namespace Vesper.Setup;

internal static class Program
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static readonly TimeSpan ExitRequestConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitRequestReplyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstanceReleaseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstancePollInterval = TimeSpan.FromMilliseconds(100);

    internal static string InstanceMutexName => WindowsObjectNames.LocalNameForCurrentUser("VESPER.SingleInstance");

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var mode = args.Contains("--uninstall") ? SetupMode.Uninstall : args.Contains("--repair") ? SetupMode.Repair : SetupMode.Install;
        try
        {
            var paths = InstallPaths.ForUser();
            string executable = Environment.ProcessPath ?? throw new IOException("Windows could not locate setup.");
            if (executable.Equals(paths.Setup, StringComparison.OrdinalIgnoreCase))
            {
                StartMaintenanceWorker(executable, mode);
                return;
            }
            if (args.Length >= 3 && args[^2] == "--wait-for" && int.TryParse(args[^1], out int parent))
                WaitForLauncher(parent);
            VesperPalette palette = UiKit.Palette(UiKit.IsSystemDark(), botanical: true);
            VesperWindowChrome.ApplyNativeColors(palette);
            var transaction = new InstallTransaction(paths, new WindowsInstallRegistration(paths), AcquireAppLock);
            using var window = new SetupWindow(mode, paths, palette, (progress, ct, deleteUserData) =>
            {
                if (mode == SetupMode.Uninstall) transaction.Run(mode, null, null, progress, ct, deleteUserData);
                else
                {
                    using var package = InstallPayload.Resource("Vesper.Setup.Payload.zip");
                    transaction.Run(mode, new InstallPayload(package, InstallPayload.ReadManifest()), executable, progress, ct);
                }
            });
            Application.Run(window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(ex.Message, "VESPER Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static IDisposable AcquireAppLock(CancellationToken ct) => AcquireAppLock(InstanceMutexName,
        token => SetupExitProtocol.RequestExitAsync(ExitRequestConnectTimeout, ExitRequestReplyTimeout, token),
        InstanceReleaseTimeout, ct);

    internal static IDisposable AcquireAppLock(string mutexName, Func<CancellationToken, Task<bool>> requestExit,
        TimeSpan releaseTimeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (TryAcquireAppLock(mutexName) is { } appLock) return appLock;

        bool accepted;
        try { accepted = requestExit(ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException
            || ex.GetType() == typeof(InvalidDataException))
        {
            if (TryAcquireAppLock(mutexName) is { } releasedLock) return releasedLock;
            throw new IOException("Setup could not confirm a cooperative VESPER shutdown. Close VESPER from its tray menu, then try again.", ex);
        }

        ct.ThrowIfCancellationRequested();
        if (!accepted)
            throw new IOException("VESPER is still running. Accept its exit prompt or close VESPER from its tray menu, then try setup again.");

        Stopwatch wait = Stopwatch.StartNew();
        do
        {
            if (ct.WaitHandle.WaitOne(InstancePollInterval)) ct.ThrowIfCancellationRequested();
            if (TryAcquireAppLock(mutexName) is { } releasedLock) return releasedLock;
        }
        while (wait.Elapsed < releaseTimeout);

        throw new IOException("VESPER accepted the exit request but has not released its running lock. Wait for VESPER to finish closing, then try setup again.");
    }

    private static AppLock? TryAcquireAppLock(string mutexName)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when the previous owner exited without releasing.
                acquired = true;
            }
            if (acquired) return new AppLock(mutex);
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class AppLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    internal static bool MotionEnabled
    {
        get
        {
            bool readAnimationSetting = SystemParametersInfo(SpiGetClientAreaAnimation, 0, out bool animationsEnabled, 0);
            return MotionEnabledFor(SystemInformation.HighContrast, readAnimationSetting, animationsEnabled);
        }
    }

    internal static bool MotionEnabledFor(bool highContrast, bool animationSettingRead, bool animationsEnabled) =>
        !highContrast && animationSettingRead && animationsEnabled;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    // Running outside the install directory lets Windows rename it during repair or uninstall.
    private static void StartMaintenanceWorker(string executable, SetupMode mode)
    {
        string cache = Path.Combine(Path.GetTempPath(), "VESPER Maintenance");
        InstallTransaction.CheckPath(cache);
        Directory.CreateDirectory(cache);
        using var source = File.OpenRead(executable);
        string digest = Convert.ToHexString(SHA256.HashData(source));
        string worker = Path.Combine(cache, digest + ".exe");
        InstallTransaction.CheckPath(worker);
        if (!File.Exists(worker) || FileDigest(worker) != digest)
        {
            string temp = worker + $".{Guid.NewGuid():N}.tmp";
            try
            {
                source.Position = 0;
                using (var copy = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(copy);
                if (FileDigest(temp) != digest) throw new IOException("Setup could not verify the temporary maintenance copy.");
                File.Move(temp, worker, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, WorkingDirectory = cache };
        start.ArgumentList.Add(mode == SetupMode.Uninstall ? "--uninstall" : "--repair");
        start.ArgumentList.Add("--wait-for");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Setup could not open maintenance.");
    }

    private static string FileDigest(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void WaitForLauncher(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var launcher = Process.GetProcessById(pid);
            if (!launcher.WaitForExit(5000)) throw new IOException("Close the other setup window and try again.");
        }
        catch (ArgumentException) { }
    }
}
