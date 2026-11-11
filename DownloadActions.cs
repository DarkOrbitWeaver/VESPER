using System.Diagnostics;
using Vesper.Engine;

namespace VESPER;

/// <summary>
/// User-initiated operations on a download, shared by the row menu, batch toolbar and Details dialog.
/// Every destructive action is confirmed here, revalidated by the engine, and reports what really happened.
/// </summary>
internal sealed class DownloadActions(IWin32Window owner, DownloadEngine engine, Func<VesperPreferences> preferences)
{
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".scr", ".com", ".lnk", ".hta", ".jar", ".reg", ".msix", ".appx"
    };

    private VesperPalette Colors => UiKit.Palette(preferences());

    private void Info(string text, string title = "VESPER", MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.OK, icon);

    private bool Confirm(string text, string title, MessageBoxIcon icon = MessageBoxIcon.Question) =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    // ---- opening --------------------------------------------------------------------------------------------------

    /// <summary>Revalidates the path, asks before launching anything runnable, and never runs anything unprompted.</summary>
    public void Open(JobSnapshot s)
    {
        if (!TryGetFile(s, out string path)) return;
        if (RunnableExtensions.Contains(Path.GetExtension(path)) &&
            !Confirm($"{Path.GetFileName(path)} is a program or script. Windows will treat it as downloaded from the internet.\n\nRun it?", "Open file", MessageBoxIcon.Warning))
            return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        { Info("The file could not be opened: " + ex.Message, "Open file", MessageBoxIcon.Warning); }
    }

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

    public void Locate(JobSnapshot s)
    {
        using var picker = new OpenFileDialog { Title = "Locate " + s.FileName, FileName = s.FileName, CheckFileExists = true };
        if (Directory.Exists(s.Destination)) picker.InitialDirectory = s.Destination;
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        DeleteResult result = engine.LocateFile(s.Id, picker.FileName);
        if (!result.Deleted) Info(result.Message, "Locate file", MessageBoxIcon.Warning);
    }

    // ---- recovery -------------------------------------------------------------------------------------------------

    public void ReplaceLink(JobSnapshot s)
    {
        using var form = new RescueForm(engine, s, preferences());
        form.ShowDialog(owner);
    }

    public void ApproveUnverified(JobSnapshot s)
    {
        if (Confirm("This server gives no way to confirm that the file is unchanged.\n\nIf it changed, the finished file could be a mix of two versions and may be corrupt.\n\nResume anyway?",
            "Resume without verification", MessageBoxIcon.Warning))
            engine.ApproveUnverifiedResume(s.Id);
    }

    public void StartOver(JobSnapshot s)
    {
        string saved = s.CommittedBytes > 0 ? $"{Formatting.Bytes(s.CommittedBytes)} of saved data" : "the partial file";
        if (Confirm($"This permanently deletes {saved} for {s.FileName} and starts again from zero.\n\nThis cannot be undone. Continue?", "Start over", MessageBoxIcon.Warning))
            if (!engine.RestartFromScratch(s.Id)) Info("The partial data could not be removed. Nothing was changed.", "Start over", MessageBoxIcon.Warning);
    }

    // ---- remove / delete --------------------------------------------------------------------------------------------

    public void Remove(IReadOnlyList<JobSnapshot> items)
    {
        if (items.Count == 0) return;
        int partial = items.Count(i => i.State != JobState.Completed && i.CommittedBytes > 0);
        string text = $"Remove {items.Count} {(items.Count == 1 ? "entry" : "entries")} from the list?\n\nFiles on disk are NOT deleted."
            + (partial > 0 ? $"\nPartially downloaded data ({partial}) stays in the destination folder." : "");
        if (!Confirm(text, "Remove from list")) return;

        var blocked = new List<string>();
        foreach (JobSnapshot item in items)
        {
            RemoveResult result = engine.RemoveFromList(item.Id);
            if (!result.Removed) blocked.Add($"{item.FileName}: {result.Message}");
        }
        if (blocked.Count > 0) Info(string.Join("\n", blocked), "Remove from list", MessageBoxIcon.Information);
    }

    public void DeleteFile(JobSnapshot s)
    {
        if (!Confirm($"Move this file to the Recycle Bin?\n\n{s.FinalPath}\n\nThe entry stays in the list as \"file missing\".", "Delete downloaded file", MessageBoxIcon.Warning)) return;
        DeleteResult result = engine.DeleteDownloadedFile(s.Id, EngineHost.RecycleFile);
        Info(result.Deleted ? $"Moved to the Recycle Bin:\n{result.Path}" : result.Message, "Delete downloaded file",
            result.Deleted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ---- information -----------------------------------------------------------------------------------------------

    public async void Verify(JobSnapshot s)
    {
        owner.Cursor(Cursors.WaitCursor);
        try
        {
            VerifyResult result = await engine.VerifyIntegrityAsync(s.Id);
            Info(result.Message, "Verify integrity", result.Matches == false ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
        string hint = s.State == JobState.Completed
            ? "Renames the entry in VESPER only. The file on disk keeps its name."
            : "Used as the file name when the download finishes.";
        string? name = AskForText("Rename entry", "Name", s.FileName, hint);
        if (!string.IsNullOrWhiteSpace(name)) engine.Rename(s.Id, name);
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
        engine.SetSpeedLimit(s.Id, kb * 1000L);
    }

    // ---- small input dialog in the shared popup style ----------------------------------------------------------------

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
