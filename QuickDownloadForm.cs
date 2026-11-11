using Vesper.Engine;

namespace VESPER;

/// <summary>
/// The compact window shown when the browser sends a download and the user chose "ask". It shows only what is
/// known: the file name, its real type and size when the server reported them, and where it will be saved.
/// Footer: Cancel on the left, Queue and Download on the right.
/// </summary>
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class QuickDownloadForm : VesperPopupForm
{
    public enum Outcome { Cancel, Queue, Download }

    private readonly VesperPalette _colors;
    private readonly TextBox _folder;
    private readonly Label _warning;
    private readonly long? _sizeBytes;

    public Outcome Result { get; private set; } = Outcome.Cancel;
    public string Destination => _folder.Text.Trim();

    public QuickDownloadForm(VesperPreferences preferences, string title, string detailLine, long? sizeBytes)
    {
        _colors = UiKit.Palette(preferences);
        _sizeBytes = sizeBytes;
        VesperWindowChrome.Brand(this);
        Text = "Quick download — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(508, 218 + VesperDialogChrome.HeaderHeight);
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        var name = Label_(title, 11F, bold: true);
        name.SetBounds(23, 16, 462, 44);
        name.AutoEllipsis = true;
        var detail = Label_(detailLine, 8.75F, muted: true);
        detail.SetBounds(23, 62, 462, 20);

        var saveLabel = Label_("Save to", 9, bold: true);
        saveLabel.SetBounds(23, 96, 462, 20);
        var frame = new VesperInputFrame { Location = new Point(23, 119), Size = new Size(351, 36), BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen };
        _folder = new TextBox { Text = preferences.DefaultFolder, BorderStyle = BorderStyle.None, Location = new Point(10, 9), Width = 331, BackColor = _colors.Surface, ForeColor = _colors.Text };
        _folder.GotFocus += (_, _) => { frame.Active = true; frame.Invalidate(); };
        _folder.LostFocus += (_, _) => { frame.Active = false; frame.Invalidate(); };
        _folder.TextChanged += (_, _) => CheckSpace();
        frame.Controls.Add(_folder);

        var browse = new VesperButton { Text = "Browse…", Width = 102, Height = 36 };
        browse.ApplyPalette(_colors);
        browse.SetBounds(383, 119, 102, 36);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Folder for this download", UseDescriptionForTitle = true };
            if (Directory.Exists(_folder.Text)) picker.SelectedPath = _folder.Text;
            if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath;
        };
        _warning = Label_("", 8.5F);
        _warning.ForeColor = _colors.Red;
        _warning.SetBounds(23, 160, 462, 38);
        _warning.AutoEllipsis = false;

        var footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        footer.Paint += (_, e) => { using var pen = new Pen(_colors.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var cancel = new VesperButton { Text = "Cancel", Width = 85, Height = 34, DialogResult = DialogResult.Cancel };
        var queue = new VesperButton { Text = "Queue", Width = 91, Height = 34, Symbol = VesperIcon.Queue };
        var download = new VesperButton { Text = "Download", Width = 122, Height = 34, Primary = true, Symbol = VesperIcon.Download };
        foreach (VesperButton b in new[] { cancel, queue, download }) b.ApplyPalette(_colors);
        queue.Click += (_, _) => Accept(Outcome.Queue);
        download.Click += (_, _) => Accept(Outcome.Download);
        footer.Controls.AddRange([cancel, queue, download]);
        footer.Resize += (_, _) =>
        {
            cancel.SetBounds(23, 14, 85, 34);
            download.SetBounds(footer.Width - 23 - 122, 14, 122, 34);
            queue.SetBounds(download.Left - 8 - 91, 14, 91, 34);
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        body.Controls.AddRange([name, detail, saveLabel, frame, browse, _warning]);
        Controls.Add(body);
        Controls.Add(footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Quick download", _colors, VesperIcon.Download);
        CancelButton = cancel;
        AcceptButton = download;
        TopMost = true;
        CheckSpace();
    }

    private Label Label_(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false, BackColor = Color.Transparent, ForeColor = muted ? _colors.Muted : _colors.Text,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft
    };

    /// <summary>Warns before the download starts; the engine still enforces it when the transfer begins.</summary>
    private void CheckSpace()
    {
        _warning.Text = "";
        if (_sizeBytes is not long size || !Path.IsPathFullyQualified(_folder.Text)) return;
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_folder.Text)) ?? "";
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < size) _warning.Text = $"Not enough free space on {root}: {Formatting.Bytes(size)} needed, {Formatting.Bytes(free)} available.";
        }
        catch (ArgumentException) { /* unusual path: nothing to check */ }
        catch (IOException) { /* drive not ready */ }
    }

    private void Accept(Outcome outcome)
    {
        if (!PathSafety.TryResolveFolder(_folder.Text, create: false, out _, out string problem) && !problem.Contains("does not exist"))
        {
            _warning.Text = problem;
            return;
        }
        Result = outcome;
        DialogResult = DialogResult.OK;
        Close();
    }
}
