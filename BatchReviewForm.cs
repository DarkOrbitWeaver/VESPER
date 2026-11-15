using Vesper.Engine;

namespace VESPER;

/// <summary>Native confirmation for browser-proposed links. Destinations remain desktop-owned.</summary>
internal sealed class BatchReviewForm : VesperPopupForm
{
    private readonly CheckedListBox _files = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly VesperButton _download = new() { Text = "Download", AutoSize = true, Primary = true };
    private readonly VesperButton _queue = new() { Text = "Queue", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false };
    public QuickDownloadForm.Outcome Result { get; private set; }
    public string Destination => _folder.Text.Trim();
    public IReadOnlySet<int> SelectedIndices => _files.CheckedIndices.Cast<int>().ToHashSet();

    public BatchReviewForm(VesperPreferences preferences, IReadOnlyList<BridgeItem> items)
    {
        var colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Review files — VESPER";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = colors.Surface; ForeColor = colors.Text;
        _folder.Text = preferences.DefaultFolder;
        _folder.AccessibleName = "Destination folder";
        _files.AccessibleName = "Files to download; Space changes selection";
        _files.BackColor = colors.Surface; _files.ForeColor = colors.Text;
        foreach (var item in items)
        {
            var uri = new Uri(item.Url);
            _files.Items.Add($"{item.FileName ?? FileNames.FromUrl(uri)}  ·  {uri.Host}", true);
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Choose the files VESPER should own. Unchecked links are not submitted.", AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        layout.Controls.Add(_files, 0, 1);
        var destination = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 12, 0, 8) };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.Controls.Add(new Label { Text = "Save to", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        destination.Controls.Add(_folder, 1, 0);
        var browse = new VesperButton { Text = "Browse…", AutoSize = true };
        browse.ApplyPalette(colors);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Folder for selected files", UseDescriptionForTitle = true };
            if (Directory.Exists(Destination)) picker.SelectedPath = Destination;
            if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath;
        };
        destination.Controls.Add(browse, 2, 0); layout.Controls.Add(destination, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new VesperButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (var button in new[] { _download, _queue, cancel }) { button.ApplyPalette(colors); buttons.Controls.Add(button); }
        layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        _download.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Download);
        _queue.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Queue);
        _files.ItemCheck += (_, _) => BeginInvoke(UpdateSelection);
        Shown += (_, _) => UpdateSelection();
        AcceptButton = _download; CancelButton = cancel;
        VesperDialogChrome.Attach(this, "VESPER  /  Review files", colors, VesperIcon.Download);
    }

    private void UpdateSelection()
    {
        if (IsDisposed) return;
        int count = _files.CheckedItems.Count;
        _status.Text = $"{count} of {_files.Items.Count} selected";
        _download.Enabled = _queue.Enabled = count > 0;
    }

    private void Accept(QuickDownloadForm.Outcome outcome)
    {
        if (_files.CheckedItems.Count == 0) return;
        if (!PathSafety.TryResolveFolder(Destination, create: true, out _, out string error))
        { _status.Text = error; return; }
        Result = outcome; DialogResult = DialogResult.OK;
    }
}
