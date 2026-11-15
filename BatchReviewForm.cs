using Vesper.Engine;

namespace VESPER;

/// <summary>Native confirmation for browser-proposed links. Destinations remain desktop-owned.</summary>
internal sealed class BatchReviewForm : VesperPopupForm
{
    private readonly IReadOnlyList<BridgeItem> _items;
    private readonly CheckedListBox _files = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _trustPageChecksums = new() { AutoSize = true };
    private readonly TextBox _checksumDetails = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical };
    private readonly VesperButton _download = new() { Text = "Download", AutoSize = true, Primary = true };
    private readonly VesperButton _queue = new() { Text = "Queue", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false };
    public QuickDownloadForm.Outcome Result { get; private set; }
    public string Destination => PathSafety.TryGetFullPath(_folder.Text, out string full, out _) ? full : _folder.Text.Trim();
    public IReadOnlySet<int> SelectedIndices => _files.CheckedIndices.Cast<int>().ToHashSet();
    public IReadOnlySet<int> TrustedChecksumIndices => _trustPageChecksums.Checked
        ? _files.CheckedIndices.Cast<int>().Where(index => _items[index].Sha256 is not null).ToHashSet()
        : new HashSet<int>();

    public BatchReviewForm(VesperPreferences preferences, IReadOnlyList<BridgeItem> items)
    {
        var colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Review files — VESPER";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 530);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = colors.Surface; ForeColor = colors.Text;
        _items = items;
        _folder.Text = preferences.DefaultFolder;
        _folder.AccessibleName = "Destination folder";
        _files.Name = "filesToReview";
        _files.AccessibleName = "Files to download; Space changes selection";
        _files.BackColor = colors.Surface; _files.ForeColor = colors.Text;
        foreach (var item in items)
        {
            var uri = new Uri(item.Url);
            string checksum = item.Sha256 is null ? "" : "  ·  page SHA-256 available";
            _files.Items.Add($"{item.FileName ?? FileNames.FromUrl(uri)}  ·  {uri.Host}{checksum}", true);
        }
        bool hasChecksums = items.Any(item => item.Sha256 is not null);
        _trustPageChecksums.Name = "trustPageChecksums";
        _trustPageChecksums.Text = "Use the page-supplied SHA-256 for selected files";
        _trustPageChecksums.AccessibleName = _trustPageChecksums.Text;
        _trustPageChecksums.AccessibleDescription = "VESPER will verify each selected file before publishing it. Mismatches are retained and marked for review.";
        _trustPageChecksums.Enabled = false;
        _checksumDetails.Name = "pageChecksumDetails";
        _checksumDetails.AccessibleName = "Page-supplied checksum details";
        _checksumDetails.BackColor = colors.Secondary;
        _checksumDetails.ForeColor = colors.Text;
        _checksumDetails.Height = 52;
        _checksumDetails.Text = "Select a file with a page checksum to inspect the full value.";

        var checksumPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 8, 0, 4), Visible = hasChecksums
        };
        checksumPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        checksumPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        checksumPanel.Controls.Add(_trustPageChecksums, 0, 0);
        checksumPanel.Controls.Add(_checksumDetails, 0, 1);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            if (PathSafety.TryGetFullPath(Destination, out string selected, out _) && Directory.Exists(selected))
                picker.SelectedPath = selected;
            if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath;
        };
        destination.Controls.Add(browse, 2, 0); layout.Controls.Add(destination, 0, 2);
        layout.Controls.Add(checksumPanel, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new VesperButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (var button in new[] { _download, _queue, cancel }) { button.ApplyPalette(colors); buttons.Controls.Add(button); }
        layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
        _download.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Download);
        _queue.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Queue);
        _files.ItemCheck += (_, _) => BeginInvoke(UpdateSelection);
        _files.SelectedIndexChanged += (_, _) => UpdateChecksumDetails();
        _trustPageChecksums.CheckedChanged += (_, _) => UpdateChecksumDetails();
        Shown += (_, _) => { UpdateSelection(); UpdateChecksumDetails(); };
        AcceptButton = _download; CancelButton = cancel;
        VesperDialogChrome.Attach(this, "VESPER  /  Review files", colors, VesperIcon.Download);
    }

    private void UpdateSelection()
    {
        if (IsDisposed) return;
        int count = _files.CheckedItems.Count;
        _status.Text = $"{count} of {_files.Items.Count} selected";
        _download.Enabled = _queue.Enabled = count > 0;
        UpdateChecksumDetails();
    }

    private void UpdateChecksumDetails()
    {
        int index = _files.SelectedIndex;
        BridgeItem? item = index >= 0 && index < _items.Count ? _items[index] : null;
        _checksumDetails.Text = item?.Sha256 is { } hash
            ? $"{PageHost(item.Referer)} supplies SHA-256:\r\n{hash}\r\nCheck the publisher and this value before enabling it."
            : "Select a file with a page checksum to inspect the full value.";

        bool hasSelectedChecksum = _files.CheckedIndices.Cast<int>().Any(i => _items[i].Sha256 is not null);
        _trustPageChecksums.Enabled = hasSelectedChecksum;
        if (!hasSelectedChecksum) _trustPageChecksums.Checked = false;
    }

    private static string PageHost(string? referer) => UrlPolicy.TryParse(referer, out Uri uri, out _)
        ? $"{uri.Scheme}://{uri.Authority}" : "The page";

    private void Accept(QuickDownloadForm.Outcome outcome)
    {
        if (_files.CheckedItems.Count == 0) return;
        if (!PathSafety.TryResolveFolder(Destination, create: true, out string resolved, out string error))
        { _status.Text = error; return; }
        _folder.Text = resolved;
        Result = outcome; DialogResult = DialogResult.OK;
    }
}
