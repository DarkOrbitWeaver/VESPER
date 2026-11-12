using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VESPER;

namespace Vesper.Setup;

internal static class Program
{
    private const string AppMutex = @"Local\VESPER.SingleInstance";

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
            using var window = new SetupWindow(mode, paths, palette, (progress, ct) =>
            {
                if (mode == SetupMode.Uninstall) transaction.Run(mode, null, null, progress, ct);
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

    private static IDisposable AcquireAppLock()
    {
        var mutex = new Mutex(true, AppMutex, out bool acquired);
        if (acquired) return new AppLock(mutex);
        mutex.Dispose();
        throw new IOException("Exit VESPER from its tray menu, then try again.");
    }

    private sealed class AppLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    internal static bool MotionEnabled => !SystemInformation.HighContrast
        && (!SystemParametersInfo(0x1042, 0, out bool enabled, 0) || enabled);

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
