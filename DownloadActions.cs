using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vesper.Engine;

namespace VESPER;

/// <summary>
/// User-initiated operations on a download, shared by the row menu, batch toolbar and Details dialog.
/// File deletion, removal from the list, and restart prompt before invoking engine operations.
/// </summary>
internal sealed class DownloadActions(IWin32Window owner, DownloadEngine engine, Func<VesperPreferences> preferences)
{
    private const string SaveFailureMessage = "The change could not be saved. Your previous download settings were kept.";

    // Anything Windows can execute, open with a handler that runs code, or mount as a disk image.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".mst", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".scr", ".com", ".lnk", ".hta", ".jar", ".reg", ".msix", ".appx", ".appinstaller",
        ".application", ".appref-ms", ".url", ".scf", ".cpl", ".chm", ".pif", ".xll", ".one", ".library-ms",
        ".search-ms", ".settingcontent-ms", ".iso", ".img", ".vhd", ".vhdx"
    };

    private VesperPalette Colors => UiKit.Palette(preferences());

    private void Info(string text, string title = "VESPER", MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        if (owner is Control { IsDisposed: true }) return;
        VesperMessageBox.Show(owner, text, title, MessageBoxButtons.OK, icon);
    }

    private bool Confirm(string text, string title, MessageBoxIcon icon = MessageBoxIcon.Question,
        string? action = null, VesperIcon? symbol = null) =>
        VesperMessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2,
            action, symbol) == DialogResult.Yes;

    // opening

    /// <summary>Revalidates the path, asks before launching anything runnable, and never runs anything unprompted.</summary>
    public async void Open(JobSnapshot s)
    {
        if (!TryGetFile(s, out string path)) return;
        if (RequiresOpenConfirmation(path) &&
            !Confirm(OpenConfirmationText(path), "Open file", MessageBoxIcon.Warning, "Open file"))
            return;

        if (owner is Control control) control.UseWaitCursor = true;
        OpenCompletedFileResult result;
        try { result = await engine.OpenCompletedFileAsync(s.Id, path); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Info("Could not open this file: " + ex.Message, "Open file", MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            if (owner is Control { IsDisposed: false } current) current.UseWaitCursor = false;
        }

        if (result.Lease is null)
        {
            string message = result.Message.StartsWith("VESPER could not save the file identity:", StringComparison.Ordinal)
                ? "The file could not be opened because its identity could not be saved. The entry was kept unchanged."
                : result.Message;
            Info(message, "Open file", MessageBoxIcon.Warning);
            return;
        }

        using (result.Lease)
        {
            if (owner is Control { IsDisposed: true }) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            { Info("The file could not be opened: " + ex.Message, "Open file", MessageBoxIcon.Warning); }
        }
    }

    internal static bool RequiresOpenConfirmation(string finalPath) =>
        RunnableExtensions.Contains(Path.GetExtension(finalPath));

    internal static string OpenConfirmationText(string finalPath) =>
        $"{Path.GetFileName(finalPath)} can run code, open a shortcut, or mount a disk image.\n\nOnly open it if you trust the source.";

    public void ShowFolder(JobSnapshot s)
    {
        string path = s.State == JobState.Completed ? s.FinalPath ?? "" : s.PartialPath;
        FileCheck check = PathSafety.Inspect(path);
        try
        {
            if (check.Exists) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(s.Destination)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.Destination}\"") { UseShellExecute = false });
            else Info("The folder is not available right now.", "Show folder", MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Info("Explorer could not be started: " + ex.Message, "Show folder", MessageBoxIcon.Warning); }
    }

    private bool TryGetFile(JobSnapshot s, out string path)
    {
        path = s.FinalPath ?? "";
        FileCheck check = PathSafety.Inspect(path);
        if (s.State != JobState.Completed || !check.Exists || check.IsDirectory)
        {
            Info("The file is not available at its saved location.\n\nUse Locate file if it was moved.", "Open file", MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    public async void Locate(JobSnapshot s)
    {
        using var picker = new OpenFileDialog { Title = "Locate " + s.FileName, FileName = s.FileName, CheckFileExists = true };
        if (Directory.Exists(s.Destination)) picker.InitialDirectory = s.Destination;
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        string path = picker.FileName;
        owner.Cursor(Cursors.WaitCursor);
        try
        {
            DeleteResult result = await engine.LocateFileAsync(s.Id, path);
            if (owner is not Control { IsDisposed: true } && !result.Deleted)
            {
                string message = result.Message.StartsWith("The file action could not finish:", StringComparison.Ordinal)
                    ? "Could not update the saved file location. The previous file location was kept."
                    : result.Message;
                Info(message, "Locate file", MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Info("Could not locate this file: " + ex.Message, "Locate file", MessageBoxIcon.Warning); }
        finally { owner.Cursor(Cursors.Default); }
    }

    // recovery

    public void ReplaceLink(JobSnapshot s)
    {
        using var form = new RescueForm(engine, s, preferences());
        form.ShowDialog(owner);
    }

    public void ApproveUnverified(JobSnapshot s)
    {
        if (Confirm("This server gives no way to confirm that the file is unchanged.\n\nIf it changed, the finished file could be a mix of two versions and may be corrupt.\n\nResume anyway?",
            "Resume without verification", MessageBoxIcon.Warning))
        {
            try { engine.ApproveUnverifiedResume(s.Id); }
            catch (LocalStorageException)
            { Info("The download could not be resumed. Check its current status before trying again.", "Resume without verification", MessageBoxIcon.Warning); }
        }
    }

    public void StartOver(JobSnapshot s)
    {
        string saved = s.CommittedBytes > 0 ? $"{Formatting.Bytes(s.CommittedBytes)} of saved data" : "the partial file";
        if (!Confirm($"This permanently deletes {saved} for {s.FileName} and starts again from zero.\n\nThis cannot be undone. Continue?", "Start over", MessageBoxIcon.Warning))
            return;

        try
        {
            if (engine.RestartFromScratch(s.Id)) return;
            JobSnapshot? latest = engine.Snapshot(s.Id);
            string message = latest is { IsCleanupPending: true, State: JobState.NeedsAttention }
                ? latest.Message : "This download could not be restarted. Check its current status and try again.";
            Info(message, "Start over", MessageBoxIcon.Warning);
        }
        catch (LocalStorageException)
        {
            Info(SaveFailureMessage, "Start over", MessageBoxIcon.Warning);
        }
    }

    // cancel / remove / delete

    public Task CancelAsync(JobSnapshot snapshot) => CancelAsync([snapshot]);

    public async Task CancelAsync(IReadOnlyList<JobSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;

        var latest = new List<JobSnapshot>(snapshots.Count);
        var seen = new HashSet<Guid>();
        var blocked = new List<string>();
        foreach (JobSnapshot requested in snapshots)
        {
            if (!seen.Add(requested.Id)) continue;

            JobSnapshot? current = engine.Snapshot(requested.Id);
            if (current is null)
            {
                blocked.Add($"{requested.FileName}: This download is no longer available.");
                continue;
            }

            latest.Add(current);
        }

        JobSnapshot[] cancellable = latest.Where(CanCancel).ToArray();
        JobSnapshot[] savedPartials = cancellable.Where(s => !s.IsCleanupPending && s.CommittedBytes > 0).ToArray();
        if (savedPartials.Length > 0 &&
            !Confirm(CancelConfirmationText(cancellable.Length, savedPartials), "Cancel download", MessageBoxIcon.Warning,
                "Cancel download", VesperIcon.Close))
            return;

        int cancelled = 0;
        foreach (JobSnapshot snapshot in latest)
        {
            if (!CanCancel(snapshot))
            {
                blocked.Add($"{snapshot.FileName}: This download can no longer be cancelled.");
                continue;
            }

            try
            {
                CancelDownloadResult result = await engine.CancelDownloadAsync(snapshot.Id);
                if (result.Cancelled) cancelled++;
                else blocked.Add($"{snapshot.FileName}: {result.Message}");
            }
            catch (LocalStorageException)
            {
                blocked.Add($"{snapshot.FileName}: The change could not be saved. The saved partial was kept, and the entry remains in the list.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                blocked.Add($"{snapshot.FileName}: Could not cancel this download: {ex.Message}");
            }
        }

        if (blocked.Count > 0)
        {
            string summary = seen.Count > 1 ? $"Cancelled {cancelled} of {seen.Count} downloads.\n\n" : "";
            Info(summary + string.Join("\n", blocked), "Cancel download", MessageBoxIcon.Warning);
        }
    }

    private static bool CanCancel(JobSnapshot snapshot) =>
        snapshot.Kind != JobKind.BrowserFile && snapshot.State is not
            (JobState.Completed or JobState.Verifying or JobState.Finalizing or JobState.Converting);

    internal static string CancelConfirmationText(int downloadCount, IReadOnlyList<JobSnapshot> savedPartials)
    {
        long retainedBytes = 0;
        foreach (JobSnapshot snapshot in savedPartials)
        {
            long bytes = Math.Max(0, snapshot.CommittedBytes);
            retainedBytes = bytes > long.MaxValue - retainedBytes ? long.MaxValue : retainedBytes + bytes;
        }

        string question = downloadCount == 1
            ? "Cancel this download and delete its saved partial?"
            : $"Cancel {downloadCount} downloads and delete their saved partials?";
        string partialLabel = savedPartials.Count == 1 ? "partial" : "partials";
        return $"{question}\n\nSaved data to be deleted: {Formatting.Bytes(retainedBytes)} across {savedPartials.Count} {partialLabel}.";
    }

    public void Remove(IReadOnlyList<JobSnapshot> items)
    {
        if (items.Count == 0) return;
        int partial = items.Count(i => i.State != JobState.Completed && i.CommittedBytes > 0);
        string text = (items.Count == 1 ? "Remove this download from the list?" : $"Remove {items.Count} downloads from the list?")
            + "\n\nYour files stay in their folders."
            + (partial > 0 ? $"\nSaved partial files ({partial}) will be kept." : "");
        if (!Confirm(text, "Remove from list", action: "Remove", symbol: VesperIcon.RemoveList)) return;

        var blocked = new List<string>();
        int removed = 0;
        foreach (JobSnapshot item in items)
        {
            try
            {
                RemoveResult result = engine.RemoveFromList(item.Id);
                if (result.Removed) removed++;
                else blocked.Add($"{item.FileName}: {result.Message}");
            }
            catch (Exception ex) when (ex is SqliteException or LocalStorageException)
            {
                blocked.Add($"{item.FileName}: The change could not be saved. The entry remains in the list. No files were deleted.");
            }
        }
        if (blocked.Count > 0)
        {
            string summary = items.Count > 1 ? $"Removed {removed} of {items.Count} entries.\n\n" : "";
            Info(summary + string.Join("\n", blocked), "Remove from list", MessageBoxIcon.Warning);
        }
    }

    public async void DeleteFile(JobSnapshot s)
    {
        if (!Confirm($"Move this file to the Recycle Bin?\n\n{s.FinalPath}\n\nVESPER marks the entry as File missing.",
                "Delete downloaded file", MessageBoxIcon.Warning, "Move to Recycle Bin", VesperIcon.Trash)) return;
        owner.Cursor(Cursors.WaitCursor);
        try
        {
            DeleteResult result = await engine.DeleteDownloadedFileAsync(s.Id, EngineHost.RecycleFile, s.FinalPath);
            if (owner is not Control { IsDisposed: true })
                Info(result.Deleted ? $"Moved to the Recycle Bin:\n{result.Path}" : result.Message, "Delete downloaded file",
                    result.Deleted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Info("Could not delete this file: " + ex.Message, "Delete downloaded file", MessageBoxIcon.Warning); }
        finally { owner.Cursor(Cursors.Default); }
    }

    // information

    public async void Verify(JobSnapshot s)
    {
        owner.Cursor(Cursors.WaitCursor);
        try
        {
            VerifyResult result = await engine.VerifyIntegrityAsync(s.Id);
            Info(result.Message, "Verify integrity", result.Matches == false ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (LocalStorageException)
        {
            Info("The verification result could not be saved. The file was kept.", "Verify integrity", MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Info("Verification failed: " + ex.Message, "Verify integrity", MessageBoxIcon.Warning);
        }
        finally { owner.Cursor(Cursors.Default); }
    }

    public void CopyUrl(JobSnapshot s)
    {
        try { Clipboard.SetText(s.SourceUrl); }
        catch (System.Runtime.InteropServices.ExternalException) { Info("The clipboard is unavailable right now.", "Clipboard"); }
    }

    public void Rename(JobSnapshot s)
    {
        string extension = Path.GetExtension(s.FileName);
        bool sourceIsTaggedAudio = s.MediaProvider is not null
            && extension.ToLowerInvariant() is (".mp3" or ".flac" or ".ogg" or ".opus");
        string hint = s.State == JobState.Completed
            ? sourceIsTaggedAudio
                ? $"Renames the entry in VESPER only. The file on disk keeps its name and {extension} extension."
                : "Renames the entry in VESPER only. The file on disk keeps its name."
            : sourceIsTaggedAudio
                ? $"You can change its name, but the {extension} extension stays to match the audio format."
                : "Used as the file name when the download finishes.";
        string? name = AskForText("Rename entry", "Name", s.FileName, hint);
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { engine.Rename(s.Id, name); }
            catch (LocalStorageException)
            { Info(SaveFailureMessage, "Rename entry", MessageBoxIcon.Warning); }
        }
    }

    public void CustomLimit(JobSnapshot s)
    {
        string? text = AskForText("Custom bandwidth", "Maximum KB/s", s.SpeedLimitBytes > 0 ? (s.SpeedLimitBytes / 1000).ToString() : "1024",
            "Applies to this download only.");
        if (text is null) return;
        if (!int.TryParse(text, out int kb) || kb is < 1 or > 10_000_000)
        {
            Info("Enter a number between 1 and 10,000,000 KB/s.", "Invalid limit");
            return;
        }
        try { engine.SetSpeedLimit(s.Id, kb * 1000L); }
        catch (LocalStorageException)
        { Info(SaveFailureMessage, "Custom bandwidth", MessageBoxIcon.Warning); }
    }

    // small input dialog in the shared popup style

    public string? AskForText(string title, string caption, string initial, string hintText)
    {
        VesperPalette colors = Colors;
        using var dialog = new VesperPopupForm
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, AutoScaleDimensions = new SizeF(96, 96),
            AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(442, 193 + VesperDialogChrome.HeaderHeight),
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, Font = new Font("Segoe UI", 9F),
            BackColor = colors.Surface, ForeColor = colors.Text
        };
        VesperWindowChrome.Brand(dialog);
        var heading = new Label { Text = caption, Location = new Point(22, 21), Size = new Size(400, 26), ForeColor = colors.Text, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        var hint = new Label { Text = hintText, Location = new Point(22, 48), Size = new Size(398, 20), ForeColor = colors.Muted, Font = new Font("Segoe UI", 8F) };
        var frame = new VesperInputFrame { Location = new Point(22, 79), Size = new Size(398, 37), BorderColor = colors.Border, FocusColor = colors.MutedGreen, BackColor = colors.Surface };
        var input = new TextBox { Text = initial, BorderStyle = BorderStyle.None, Location = new Point(10, 10), Width = 377, BackColor = colors.Surface, ForeColor = colors.Text };
        input.GotFocus += (_, _) => { frame.Active = true; frame.Invalidate(); };
        input.LostFocus += (_, _) => { frame.Active = false; frame.Invalidate(); };
        frame.Controls.Add(input);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = colors.Surface };
        footer.Paint += (_, e) => { using var pen = new Pen(colors.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var cancel = new VesperButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var save = new VesperButton { Text = "Save", Primary = true, DialogResult = DialogResult.OK };
        cancel.ApplyPalette(colors);
        save.ApplyPalette(colors);
        cancel.SetBounds(242, 13, 82, 34);
        save.SetBounds(333, 13, 86, 34);
        footer.Controls.AddRange([cancel, save]);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = colors.Surface };
        body.Controls.AddRange([heading, hint, frame]);
        dialog.Controls.Add(body);
        dialog.Controls.Add(footer);
        VesperDialogChrome.Attach(dialog, "VESPER  /  " + title, colors, VesperIcon.Pencil);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Focus();
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}

internal static class OwnerExtensions
{
    /// <summary>Sets the cursor of a window owner that may be a Form or any other window wrapper.</summary>
    public static void Cursor(this IWin32Window owner, Cursor cursor)
    {
        if (owner is Control control) control.Cursor = cursor;
    }
}
