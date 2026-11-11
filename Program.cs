using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace VESPER
{
    internal static class Program
    {
        private const string ShowEventName = @"Local\VESPER.Show";

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase))
            {
                ApplicationConfiguration.Initialize();
                BotanicalPreview.Run(args);
                return;
            }

            bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            // Background launches can race while Chrome sends several requests.
            using var mutex = new Mutex(true, @"Local\VESPER.SingleInstance", out bool firstInstance);
            if (!firstInstance)
            {
                if (!background) SignalRunningInstance();
                return;
            }

            ApplicationConfiguration.Initialize();
            VesperPreferences preferences = SettingsStore.Load();
            if (SettingsStore.LoadWarning is { } warning)
                MessageBox.Show(warning, "VESPER settings", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (NativeHostSetup.HostPresent && (NativeHostSetup.IsInstalledApp || NativeHostSetup.HasDevelopmentRegistration)
                && !NativeHostSetup.IsRegistered)
                _ = NativeHostSetup.Register(); // Troubleshooting > Repair helper is available if registration fails.

            DownloadEngine engine;
            try
            {
                engine = EngineHost.Start(preferences);
            }
            catch (StoreTooNewException ex)
            {
                MessageBox.Show(ex.Message + "\n\nInstall the newer VESPER, or restore the backup next to the database.",
                    "VESPER cannot start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("VESPER could not open its download database:\n" + ex.Message, "VESPER cannot start",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (engine.RecoveredFromCorruptDatabase)
            {
                MessageBox.Show("The download database was damaged. It was kept as " + Path.GetFileName(engine.CorruptDatabaseBackup) +
                    " and a new one was created. Partial downloads that have recovery files appear as \"Needs a new link\".",
                    "VESPER recovered", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            var form = new Form1(preferences, engine, startHidden: background);

            // Event-driven (no polling): a second launch signals this wait handle.
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => form.RequestShow(), null, Timeout.Infinite, executeOnlyOnce: false);

            Application.Run(form);   // Form1 disposes the engine when it closes
        }

        private static void SignalRunningInstance()
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first instance has not finished starting; it will show its own window.
            }
        }
    }
}
